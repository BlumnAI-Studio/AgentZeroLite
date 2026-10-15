using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Common.Mp3;
using Agent.Common.Telemetry;

namespace Agent.Common.Browser;

/// <summary>
/// The JS bridge's op table — the C# end of <c>window.zero.invoke(op, args)</c>
/// (<c>Wasm/common/zero-bridge.js</c>). Moved down from the WPF
/// <c>WebDevBridge</c> so both hosts answer every plugin from one switch: a
/// plugin written against one host runs on the other because there is only
/// one place an op can be defined.
///
/// Wire format (unchanged):
///   request  → { id: number, op: string, args?: object }
///   response → { id: number, ok: boolean, result?: any, error?: string }
///   event    → { op: "event", channel: string, data: any }   (host-pushed)
///
/// The dispatcher knows no transport. A host hands it the raw request text
/// (<see cref="HandleRequestAsync"/>), sends back the response text it returns,
/// and delivers events through the <c>postEvent</c> callback — WPF with
/// <c>PostWebMessageAsJson</c>, Avalonia through a script call into the page.
/// Awaits keep the caller's context (no ConfigureAwait(false)), so a host that
/// calls in on its UI thread gets <see cref="IZeroBridgeHost"/> calls there too.
/// </summary>
public sealed class ZeroBridgeDispatcher
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ZeroBrowserCore _core;
    private readonly IZeroBridgeHost _host;
    private readonly Action<string, object> _postEvent;

    public ZeroBridgeDispatcher(ZeroBrowserCore core, IZeroBridgeHost host, Action<string, object> postEvent)
    {
        _core = core;
        _host = host;
        _postEvent = postEvent;
    }

    /// <summary>
    /// One request envelope in, one response envelope out (JSON text). Never
    /// throws: a failure — unparseable envelope, unknown op, an op that threw —
    /// becomes <c>{ id, ok: false, error }</c>.
    /// </summary>
    public async Task<string> HandleRequestAsync(string? rawJson)
    {
        int id = 0;
        string? op = null;
        try
        {
            using var doc = JsonDocument.Parse(Unwrap(rawJson));
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var parsedId)) id = parsedId;
            if (root.TryGetProperty("op", out var opEl)) op = opEl.GetString();
            JsonElement? args = root.TryGetProperty("args", out var argsEl) ? argsEl : null;

            object? result = await DispatchAsync(op, args);
            return BuildResponse(id, ok: true, result: result, error: null);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] bridge error op={op}: {ex.GetType().Name}: {ex.Message}");
            return BuildResponse(id, ok: false, result: null, error: ex.Message);
        }
    }

    /// <summary>
    /// The request as an object's JSON. WebView2's <c>WebMessageAsJson</c> is that
    /// already; a transport that carries strings (Avalonia's <c>invokeCSharpAction</c>,
    /// WKWebView's message handlers) may hand over the object's JSON wrapped in one
    /// more JSON string, so a string root is unwrapped once.
    /// </summary>
    public static string Unwrap(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return "{}";
        var t = rawJson.TrimStart();
        if (t.Length > 0 && t[0] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(rawJson) ?? "{}"; }
            catch (JsonException) { return rawJson; }
        }
        return rawJson;
    }

    public static string BuildResponse(int id, bool ok, object? result, string? error)
        => JsonSerializer.Serialize(new { id, ok, result, error }, JsonOpts);

    public static string BuildEvent(string channel, object data)
        => JsonSerializer.Serialize(new { op = "event", channel, data }, JsonOpts);

    /// <summary>
    /// Run one op and return its result object (serialised by the caller). Ops the
    /// host implements itself (<see cref="IZeroBridgeHost.TryDispatchHostOp"/>) go
    /// first; an op nobody knows throws, which the envelope turns into a rejected
    /// promise — the same as before the move.
    /// </summary>
    public async Task<object?> DispatchAsync(string? op, JsonElement? args)
    {
        if (op is not null && _host.TryDispatchHostOp(op, args) is { } hostOp)
            return await hostOp;

        // The telemetry ops are synchronous SQLite work over tables that grow without
        // bound (3.7 M token-remaining rows measured). They used to run on whatever
        // thread called in — the web view's UI thread in both hosts — so a slow query
        // froze the window until Windows reported it hung. They touch no UI, so they
        // run on the pool; the response still goes back through the caller's context.
        if (op is not null && op.StartsWith("tokens.", StringComparison.Ordinal))
            return await Task.Run(() => DispatchTokensAsync(op, args));

        switch (op)
        {
            case "version":
                return new { version = _core.GetAppVersion() };

            case "voice.providers":
                return _core.GetVoiceProviders();

            case "tts.speak":
            {
                var text = args?.TryGetProperty("text", out var t) == true ? t.GetString() ?? "" : "";
                var r = await _core.SpeakAsync(text);
                return r;
            }

            case "tts.stop":
                _core.StopSpeaking();
                return new { stopped = true };

            case "chat.status":
                return _core.GetLlmStatus();

            case "chat.send":
            {
                var text = args?.TryGetProperty("text", out var t) == true ? t.GetString() ?? "" : "";
                var r = await _core.ChatSendAsync(text);
                return r;
            }

            case "chat.stream":
            {
                // Fire-and-forget: token + done events go back via PostEvent; the
                // immediate response just acknowledges the streamId so JS can
                // correlate. JS picks up the stream via window.zero.chat.stream(...)
                // which wraps the event flow.
                var text = args?.TryGetProperty("text", out var t) == true ? t.GetString() ?? "" : "";
                var streamId = args?.TryGetProperty("streamId", out var sid) == true
                    ? sid.GetString() ?? Guid.NewGuid().ToString("N")
                    : Guid.NewGuid().ToString("N");
                _ = StreamChatAsync(text, streamId);
                return new { streamId };
            }

            case "chat.reset":
                await _core.ResetChatAsync();
                return new { reset = true };

            // ─── Voice-note plugin surface ─────────────────────────────
            case "note.start":
            {
                // M0024 Phase 3.5e fix — JsonElement.TryGetInt32 throws
                // InvalidOperationException when the element's ValueKind is
                // anything other than Number (Null included). JS sends
                // {sensitivity: null, ...} on first start, so route through
                // the existing TryGetInt helper which gates on ValueKind ==
                // Number first. Same for loopbackChunkSec. Strings get an
                // analogous ValueKind == String guard inline.
                int? sens = TryGetInt(args, "sensitivity");
                string? source = args?.TryGetProperty("source", out var srcEl) == true
                                 && srcEl.ValueKind == JsonValueKind.String
                    ? srcEl.GetString() : null;
                string? loopbackDeviceId = args?.TryGetProperty("loopbackDeviceId", out var ldEl) == true
                                           && ldEl.ValueKind == JsonValueKind.String
                    ? ldEl.GetString() : null;
                int? chunkSec = TryGetInt(args, "loopbackChunkSec");
                // Returns { ok, capturing, sensitivity, threshold, source }
                // so JS can sync its slider + source dropdown to the
                // effective values.
                return await _core.StartNoteCaptureAsync(
                    new NoteStartOptions(sens, source, loopbackDeviceId, chunkSec));
            }
            case "note.stop":
            {
                await _core.StopNoteCaptureAsync();
                return new { ok = true };
            }
            case "note.pause":
                _core.PauseNoteCapture();
                return new { paused = true };
            case "note.resume":
                _core.ResumeNoteCapture();
                return new { paused = false };
            case "note.set-sensitivity":
            {
                int v = args?.TryGetProperty("value", out var sv) == true && sv.TryGetInt32(out var si)
                    ? si : 50;
                _core.SetNoteSensitivity(v);
                return new { sensitivity = v, threshold = _core.CurrentVadThreshold };
            }
            case "note.status":
                return new { capturing = _core.IsNoteCapturing };
            // ─── Agent Band (M0025) — live music classifier surface ─────
            case "music.start":
            {
                string? source = args?.TryGetProperty("source", out var srcEl) == true
                                 && srcEl.ValueKind == JsonValueKind.String
                    ? srcEl.GetString() : null;
                string? deviceId = args?.TryGetProperty("deviceId", out var dEl) == true
                                   && dEl.ValueKind == JsonValueKind.String
                    ? dEl.GetString() : null;
                int? topK = TryGetInt(args, "topK");
                int? cadence = TryGetInt(args, "cadenceMs");
                return await _core.StartMusicAsync(source, deviceId, topK, cadence);
            }
            case "music.stop":
            {
                return await _core.StopMusicAsync();
            }
            case "music.status":
            {
                return _core.GetMusicStatus();
            }
            case "summarize":
            {
                var text = args?.TryGetProperty("text", out var t) == true ? t.GetString() ?? "" : "";
                int maxChars = args?.TryGetProperty("maxChars", out var mc) == true && mc.TryGetInt32(out var mci) && mci > 0
                    ? mci : 6000;
                return await _core.SummarizeAsync(text, maxChars);
            }

            // ─── Agent Band (M0026) — YouTube oEmbed + stateless LLM classify ─
            case "youtube.oembed":
            {
                var vid = args?.TryGetProperty("videoId", out var vEl) == true && vEl.ValueKind == JsonValueKind.String
                    ? vEl.GetString() ?? "" : "";
                return await _core.YouTubeOEmbedAsync(vid);
            }
            case "llm.classify":
            {
                var title = args?.TryGetProperty("title", out var tEl) == true && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString() ?? "" : "";
                string? channel = args?.TryGetProperty("channel", out var cEl) == true && cEl.ValueKind == JsonValueKind.String
                    ? cEl.GetString() : null;
                var cats = new List<string>();
                if (args?.TryGetProperty("categories", out var caEl) == true && caEl.ValueKind == JsonValueKind.Array)
                    foreach (var el in caEl.EnumerateArray())
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            var s = el.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) cats.Add(s!);
                        }
                return await _core.ClassifyAsync(title, channel, cats);
            }

            // ─── Agent Band YouTube playlist (B4) — SQLite-backed persistence ─
            case "youtube.list":
                return await _core.YtListAsync();
            case "youtube.upsert":
            {
                string S(string k) => args?.TryGetProperty(k, out var e) == true && e.ValueKind == JsonValueKind.String
                    ? e.GetString() ?? "" : "";
                return await _core.YtUpsertAsync(
                    S("videoId"), S("title"), S("author"), S("thumbnail"), S("category"), S("by"), S("url"));
            }
            case "youtube.remove":
            {
                var vid = args?.TryGetProperty("videoId", out var e) == true && e.ValueKind == JsonValueKind.String
                    ? e.GetString() ?? "" : "";
                return await _core.YtRemoveAsync(TryGetInt(args, "id") ?? 0, vid);
            }
            case "youtube.clear":
                return await _core.YtClearAsync();

            // ─── Agent Band (M0028) — on-device vision (Florence-2 OD) ───
            // The frame is captured by the HOST (it owns the plugin's web view)
            // because the plugin's cross-origin YouTube iframe can't be read
            // from JS. A host that cannot capture answers capture-unavailable,
            // which the plugin treats like any other ok:false.
            case "vision.status":
                return _core.GetVisionStatus();
            case "vision.analyze":
            {
                var (ax, ay, aw, ah) = ReadRect(args);
                var png = await _host.CaptureFramePngAsync();
                if (png is null) return new { ok = false, error = "capture-unavailable" };
                return await _core.VisionAnalyzeAsync(png, ax, ay, aw, ah);
            }
            case "vision.motion":
            {
                var (mx, my, mw, mh) = ReadRect(args);
                var png = await _host.CaptureFramePngAsync();
                if (png is null) return new { ok = false, error = "capture-unavailable", motion = 0.0 };
                return _core.VisionMotion(png, mx, my, mw, mh);
            }
            case "vision.reset":
                _core.ResetVision();
                return new { ok = true };

            // ─── Agent Band (M0029) — local MP3 playlist (SQLite-backed) ──
            // The host serves the scan root as https://mp3.local/ (told here
            // through UseMp3Folder) so the plugin's <audio> element streams
            // files natively (seek/range included) — audio bytes never cross
            // the postMessage bridge.
            case "mp3.status":
            {
                _host.UseMp3Folder(Mp3SettingsStore.Load().ScanFolder);
                return _core.GetMp3Status();
            }
            case "mp3.pickFolder":
            {
                var current = Mp3SettingsStore.Load().ScanFolder;
                var initial = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current) ? current : null;
                var picked = await _host.PickFolderAsync("Pick an MP3 folder — subfolders are scanned too", initial);
                if (string.IsNullOrWhiteSpace(picked))
                    return new { ok = false, error = "cancelled" };
                return new { ok = true, folder = picked };
            }
            case "mp3.setFolder":
            {
                var folder = args?.TryGetProperty("folder", out var fEl) == true && fEl.ValueKind == JsonValueKind.String
                    ? fEl.GetString() ?? "" : "";
                var r = _core.Mp3SetFolder(folder);
                _host.UseMp3Folder(Mp3SettingsStore.Load().ScanFolder);
                return r;
            }
            case "mp3.scan":
            {
                var cats = ReadStringArray(args, "categories");
                _host.UseMp3Folder(Mp3SettingsStore.Load().ScanFolder);
                return _core.Mp3StartScan(cats);
            }
            case "mp3.scan.cancel":
                return _core.Mp3CancelScan();
            case "mp3.list":
            {
                _host.UseMp3Folder(Mp3SettingsStore.Load().ScanFolder);
                return await _core.Mp3ListAsync(
                    TryGetInt(args, "offset") ?? 0, TryGetInt(args, "limit") ?? 0);
            }
            case "mp3.remove":
            {
                return await _core.Mp3RemoveAsync(TryGetInt(args, "id") ?? 0);
            }
            case "mp3.markPlayed":
            {
                return await _core.Mp3MarkPlayedAsync(TryGetInt(args, "id") ?? 0);
            }
            case "mp3.setInstruments":
            {
                var id = TryGetInt(args, "id") ?? 0;
                var keys = ReadStringArray(args, "instruments");
                return await _core.Mp3SetInstrumentsAsync(id, keys);
            }
            case "mp3.coverGender":
            {
                return await _core.Mp3CoverGenderAsync(TryGetInt(args, "id") ?? 0);
            }
            case "mp3.setMoods":
            {
                var id = TryGetInt(args, "id") ?? 0;
                var keys = ReadStringArray(args, "moods");
                return await _core.Mp3SetMoodsAsync(id, keys);
            }
            case "mp3.cards":
                return await _core.Mp3CardsAsync();
            case "mp3.cardCreate":
                return await _core.Mp3CardCreateAsync();
            case "mp3.cardRemove":
            {
                return await _core.Mp3CardRemoveAsync(TryGetInt(args, "id") ?? 0);
            }

            default:
                throw new InvalidOperationException($"unknown op '{op}'");
        }
    }

    private static async Task<object?> DispatchTokensAsync(string op, JsonElement? args)
    {
        switch (op)
        {
            // ─── Token-monitor plugin surface (read-only) ───────────────
            case "tokens.summary":
            {
                var since = ParseSinceUtc(args);
                var totals  = TokenUsageQueryService.GetTotals(since);
                var byVendor = TokenUsageQueryService.GetByVendor(since);
                var state   = TokenUsageQueryService.GetCollectorState();
                return new {
                    range      = DescribeSince(since),
                    totals,
                    byVendor,
                    collector  = state,
                };
            }
            case "tokens.byVendor":
                return TokenUsageQueryService.GetByVendor(ParseSinceUtc(args));
            case "tokens.byAccount":
                return TokenUsageQueryService.GetByAccount(ParseSinceUtc(args));
            case "tokens.byProject":
            {
                var since = ParseSinceUtc(args);
                var limit = TryGetInt(args, "limit") ?? 50;
                return TokenUsageQueryService.GetByProject(since, limit);
            }
            case "tokens.timeseries":
            {
                int rangeHours    = TryGetInt(args, "rangeHours")    ?? 24;
                int bucketMinutes = TryGetInt(args, "bucketMinutes") ?? 60;
                return TokenUsageQueryService.GetTimeSeries(rangeHours, bucketMinutes);
            }
            case "tokens.sessions":
            {
                var since = ParseSinceUtc(args);
                var limit = TryGetInt(args, "limit") ?? 20;
                return TokenUsageQueryService.GetActiveSessions(since, limit);
            }
            case "tokens.recent":
            {
                var limit = TryGetInt(args, "limit") ?? 50;
                return TokenUsageQueryService.GetRecent(limit);
            }
            case "tokens.refresh":
            {
                var summary = await TokenUsageCollector.Instance.TickNowAsync();
                return new {
                    filesScanned = summary.FilesScanned,
                    rowsInserted = summary.RowsInserted,
                    claudeRows   = summary.ClaudeRows,
                    codexRows    = summary.CodexRows,
                    finishedAt   = summary.FinishedAt,
                    error        = summary.Error,
                };
            }
            case "tokens.status":
                return TokenUsageQueryService.GetCollectorState();
            case "tokens.profiles":
                return TokenUsageQueryService.GetProfiles();
            case "tokens.aliases":
                return TokenUsageQueryService.ListAliases();
            case "tokens.aliases.set":
            {
                var vendor = args?.TryGetProperty("vendor", out var ven) == true ? (ven.GetString() ?? "") : "";
                var key    = args?.TryGetProperty("accountKey", out var ak) == true ? (ak.GetString() ?? "") : "";
                var alias  = args?.TryGetProperty("alias", out var al) == true ? (al.GetString() ?? "") : "";
                return TokenUsageQueryService.SetAlias(vendor, key, alias);
            }
            case "tokens.aliases.remove":
            {
                var vendor = args?.TryGetProperty("vendor", out var ven) == true ? (ven.GetString() ?? "") : "";
                var key    = args?.TryGetProperty("accountKey", out var ak) == true ? (ak.GetString() ?? "") : "";
                return new { removed = TokenUsageQueryService.RemoveAlias(vendor, key) };
            }
            case "tokens.reset":
            {
                var summary = TokenUsageCollector.Instance.ResetData();
                return new { rowsDeleted = summary.RowsDeleted, checkpointsDeleted = summary.CheckpointsDeleted };
            }

            // ─── Token-remaining widget surface (M0011) ────────────────
            case "tokens.remaining.profiles":
            {
                // Discovered Claude profiles + their installed-state.
                // The widget Settings dialog and Install dialog both read this.
                var profiles = StatusLineWrapperInstaller.DiscoverProfiles();
                return profiles.Select(p => new {
                    accountKey            = p.AccountKey,
                    configDir             = p.ConfigDir,
                    settingsJsonPath      = p.SettingsJsonPath,
                    settingsJsonExists    = p.SettingsJsonExists,
                    currentStatusLine     = p.CurrentStatusLineCommand,
                    ourWrapperInstalled   = p.OurWrapperInstalled,
                    claudeHudDetected     = p.ClaudeHudDetected,
                    pipeTarget            = p.PipeTarget,
                }).ToList();
            }
            case "tokens.remaining.accounts":
            {
                // Per-account observation summary (DB rows seen so far),
                // for the "Active account" picker.
                return TokenRemainingQueryService.GetAccountProfiles();
            }
            case "tokens.remaining.observedModels":
            {
                var acct = args?.TryGetProperty("account", out var aEl) == true ? (aEl.GetString() ?? "") : "";
                return TokenRemainingQueryService.GetObservedModels(acct);
            }
            case "tokens.remaining.latest":
            {
                var acct = args?.TryGetProperty("account", out var aEl) == true ? (aEl.GetString() ?? "") : "";
                return new {
                    account = acct,
                    models  = TokenRemainingQueryService.GetLatestForAccount(acct),
                    collector = TokenRemainingQueryService.GetCollectorState(),
                };
            }
            case "tokens.remaining.series":
            {
                var acct  = args?.TryGetProperty("account", out var aEl) == true ? (aEl.GetString() ?? "") : "";
                var model = args?.TryGetProperty("model",   out var mEl) == true ? (mEl.GetString() ?? "") : "";
                var hours = TryGetInt(args, "hours") ?? 24;
                return TokenRemainingQueryService.GetSeries(acct, model, hours);
            }
            case "tokens.remaining.status":
                return TokenRemainingQueryService.GetCollectorState();
            case "tokens.remaining.refresh":
            {
                var summary = await TokenRemainingCollector.Instance.TickNowAsync();
                return new {
                    filesScanned       = summary.FilesScanned,
                    rowsInserted       = summary.RowsInserted,
                    rowsSkippedSamePct = summary.RowsSkippedSamePercent,
                    finishedAt         = summary.FinishedAtUtc,
                    error              = summary.Error,
                };
            }
            case "tokens.remaining.install":
            {
                var acct = args?.TryGetProperty("account", out var aEl) == true ? (aEl.GetString() ?? "") : "";
                return StatusLineWrapperInstaller.Install(acct);
            }
            case "tokens.remaining.uninstall":
            {
                var acct  = args?.TryGetProperty("account", out var aEl) == true ? (aEl.GetString() ?? "") : "";
                var force = args?.TryGetProperty("force",   out var fEl) == true && fEl.ValueKind == JsonValueKind.True;
                return StatusLineWrapperInstaller.Uninstall(acct, force);
            }
            case "tokens.remaining.reset":
            {
                var summary = TokenRemainingCollector.Instance.ResetData();
                return new { rowsDeleted = summary.RowsDeleted, snapshotFilesDeleted = summary.SnapshotFilesDeleted };
            }

            // ─── Active session panel surface (M0012) ──────────────────
            case "tokens.remaining.activeSessions":
            {
                var minutes = TryGetInt(args, "windowMinutes") ?? 5;
                var window = TimeSpan.FromMinutes(Math.Max(1, Math.Min(60, minutes)));
                return new {
                    windowMinutes = (int)window.TotalMinutes,
                    sessions  = SessionHeartbeatQueryService.GetActive(window),
                    collector = SessionHeartbeatQueryService.GetCollectorState(),
                };
            }
            case "tokens.remaining.activeSessions.refresh":
            {
                var summary = await SessionHeartbeatCollector.Instance.TickNowAsync();
                return new {
                    filesScanned = summary.FilesScanned,
                    rowsUpserted = summary.RowsUpserted,
                    rowsPruned   = summary.RowsPruned,
                    finishedAt   = summary.FinishedAtUtc,
                    error        = summary.Error,
                };
            }
            case "tokens.remaining.activeSessions.status":
                return SessionHeartbeatQueryService.GetCollectorState();

            default:
                throw new InvalidOperationException($"unknown op '{op}'");
        }
    }

    private static DateTime? ParseSinceUtc(JsonElement? args)
    {
        if (args is null) return null;
        if (args.Value.TryGetProperty("sinceHours", out var sh) && sh.TryGetInt32(out var hours) && hours > 0)
            return DateTime.UtcNow.AddHours(-hours);
        if (args.Value.TryGetProperty("sinceMinutes", out var sm) && sm.TryGetInt32(out var min) && min > 0)
            return DateTime.UtcNow.AddMinutes(-min);
        return null;
    }

    private static string DescribeSince(DateTime? sinceUtc)
        => sinceUtc is null ? "all-time" : $"since {sinceUtc.Value:O}";

    private static int? TryGetInt(JsonElement? args, string name)
    {
        if (args is null) return null;
        if (args.Value.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
            return v;
        return null;
    }

    // Vision crop rect in device pixels: { x, y, w, h }. Missing → 0 (host clamps).
    private static (int x, int y, int w, int h) ReadRect(JsonElement? args)
        => (TryGetInt(args, "x") ?? 0, TryGetInt(args, "y") ?? 0,
            TryGetInt(args, "w") ?? 0, TryGetInt(args, "h") ?? 0);

    private static List<string> ReadStringArray(JsonElement? args, string name)
    {
        var list = new List<string>();
        if (args?.TryGetProperty(name, out var el) == true && el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                }
        return list;
    }

    private async Task StreamChatAsync(string text, string streamId)
    {
        try
        {
            await foreach (var tok in _core.ChatStreamAsync(text))
                _postEvent("chat.token", new { streamId, token = tok });

            _postEvent("chat.done", new { streamId, ok = true });
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] chat stream failed: {ex.GetType().Name}: {ex.Message}");
            _postEvent("chat.done", new { streamId, ok = false, error = ex.Message });
        }
    }
}

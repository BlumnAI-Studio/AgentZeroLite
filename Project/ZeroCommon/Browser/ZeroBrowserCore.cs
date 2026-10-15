using System.Text.Json;
using System.Text.RegularExpressions;
using Agent.Common.Llm;
using Agent.Common.Voice;

namespace Agent.Common.Browser;

public sealed record SummarizeResult(bool Ok, string? Summary, int InputChars, int Chunks, string? Error);

/// <summary>
/// M0026 — YouTube oEmbed metadata, fetched host-side so the Agent Band
/// plugin avoids browser CORS on <c>youtube.com/oembed</c>. SSRF-safe:
/// the caller passes only an 11-char video id, and the host rebuilds the
/// canonical <c>watch?v=</c> URL itself — no arbitrary host is ever contacted.
/// </summary>
public sealed record OEmbedResult(bool Ok, string? VideoId, string? Title, string? Author, string? Thumbnail, string? Error);

/// <summary>
/// M0026 — result of a stateless one-shot LLM classification. Does NOT
/// touch the persistent <c>chat.*</c> session. <see cref="Category"/> is
/// always one of the caller-supplied categories (host clamps the model's
/// free-text reply to the whitelist; falls back to the last category).
/// </summary>
public sealed record ClassifyResult(bool Ok, string? Category, string? Raw, string? Error);

/// <summary>
/// The native side of the plugin bridge — chat, summarize, classify, oEmbed, the
/// YouTube and MP3 playlists, on-device vision over a frame the host captured, and
/// the voice-note / live-music pipelines over <see cref="Audio.AudioCapture.Platform"/>.
/// Moved down from the WPF <c>WebDevHost</c> so the Avalonia host serves the same
/// <c>window.zero</c> surface from the same code; <c>WebDevHost</c> derives from it
/// and adds only its own TTS playback and STT factory.
///
/// <see cref="SpeakAsync"/> / <see cref="StopSpeaking"/> are virtual: playing
/// audio is the host's business, and a host that cannot says so in the result.
/// </summary>
public partial class ZeroBrowserCore : IZeroBrowser, IDisposable
{
    // M0026 — shared client for host-side YouTube oEmbed lookups. Short
    // timeout so a slow network never wedges the plugin; the video still
    // plays even if metadata never arrives.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // One gate for every LLM call this surface makes (chat, summarize,
    // classify, the MP3 scan's classifier, 느낌 카드) — the local backend
    // runs one inference at a time.
    private readonly SemaphoreSlim _chatLock = new(1, 1);
    private ILocalChatSession? _chatSession;
    private bool _disposed;

    public string GetAppVersion()
    {
        try { return Agent.Common.Module.AppVersionProvider.GetDisplayVersion(); }
        catch { return "v?"; }
    }

    public VoiceProvidersInfo GetVoiceProviders()
    {
        var v = VoiceSettingsStore.Load();
        var llm = TryReadLlmBackendName();
        return new VoiceProvidersInfo(
            Stt: string.IsNullOrWhiteSpace(v.SttProvider) ? "off" : v.SttProvider,
            Tts: string.IsNullOrWhiteSpace(v.TtsProvider) ? "off" : v.TtsProvider,
            LlmBackend: llm);
    }

    public virtual Task<TtsResult> SpeakAsync(string text, CancellationToken ct = default)
        => Task.FromResult(new TtsResult(false, null, 0, null, "TTS playback is not available on this host"));

    public virtual void StopSpeaking() { }

    public LlmStatusInfo GetLlmStatus()
    {
        try
        {
            var s = LlmSettingsStore.Load();
            var available = LlmGateway.IsActiveAvailable();
            var backend = s.ActiveBackend.ToString();
            string model;
            string detail;
            if (s.ActiveBackend == LlmActiveBackend.Local)
            {
                model = LlmModelCatalog.FindById(s.ModelId).DisplayName;
                detail = available
                    ? "Local model loaded — ready."
                    : "Local model not loaded — open Settings → LLM and click Load.";
            }
            else
            {
                model = string.IsNullOrEmpty(s.ResolveExternalModel()) ? "(none)" : s.ResolveExternalModel();
                detail = available
                    ? $"External · {s.External.Provider} ready."
                    : $"External · {s.External.Provider} not configured — open Settings → LLM → External.";
            }
            return new LlmStatusInfo(available, backend, model, detail);
        }
        catch (Exception ex)
        {
            return new LlmStatusInfo(false, "unknown", "?", "Failed to read LLM settings: " + ex.Message);
        }
    }

    public async Task<ChatResult> ChatSendAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ChatResult(false, null, 0, "empty prompt");

        if (!LlmGateway.IsActiveAvailable())
            return new ChatResult(false, null, 0, "Active LLM backend not ready — open Settings → LLM.");

        await _chatLock.WaitAsync(ct);
        try
        {
            var session = EnsureSession();
            var reply = await session.SendAsync(text, ct);
            AppLogger.Log($"[WebDev] Chat OK | turn={session.TurnCount} replyChars={(reply?.Length ?? 0)}");
            return new ChatResult(true, reply, session.TurnCount, null);
        }
        catch (OperationCanceledException) { return new ChatResult(false, null, 0, "cancelled"); }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] Chat failed: {ex.GetType().Name}: {ex.Message}");
            return new ChatResult(false, null, 0, ex.Message);
        }
        finally { _chatLock.Release(); }
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string text,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        if (!LlmGateway.IsActiveAvailable())
            throw new InvalidOperationException("Active LLM backend not ready — open Settings → LLM.");

        await _chatLock.WaitAsync(ct);
        try
        {
            var session = EnsureSession();
            await foreach (var tok in session.SendStreamAsync(text, ct))
                yield return tok;
            AppLogger.Log($"[WebDev] Chat stream OK | turn={session.TurnCount}");
        }
        finally { _chatLock.Release(); }
    }

    public async Task ResetChatAsync()
    {
        await _chatLock.WaitAsync();
        try
        {
            var s = _chatSession;
            _chatSession = null;
            if (s is not null)
            {
                try { await s.DisposeAsync(); } catch { }
            }
            AppLogger.Log("[WebDev] Chat session reset");
        }
        finally { _chatLock.Release(); }
    }

    private ILocalChatSession EnsureSession()
    {
        // Caller holds _chatLock. Lazy + cached for the lifetime of this host.
        return _chatSession ??= LlmGateway.OpenSession();
    }

    /// <summary>
    /// LLM-backed text summarization. No tokenizer in <see cref="LlmGateway"/>,
    /// so chunking uses character length as a proxy: text longer than
    /// <paramref name="maxChars"/> is split in half (on a sentence boundary
    /// when possible), each half summarized recursively, then the partial
    /// summaries are joined and summarized one more time. Default
    /// <paramref name="maxChars"/> is 6000 — well below the 8k-token range
    /// most local backends expose.
    /// </summary>
    public async Task<SummarizeResult> SummarizeAsync(string text, int maxChars = 6000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SummarizeResult(false, null, 0, 0, "empty input");
        if (!LlmGateway.IsActiveAvailable())
            return new SummarizeResult(false, null, text.Length, 0, "Active LLM backend not ready — open Settings → LLM.");

        try
        {
            int chunks = 0;
            var summary = await SummarizeRecursiveAsync(text.Trim(), maxChars, depth: 0, chunkCounter: c => chunks = c, ct);
            AppLogger.Log($"[WebDev:Note] summarize ok | inputChars={text.Length} chunks={chunks} outChars={summary?.Length ?? 0}");
            return new SummarizeResult(true, summary, text.Length, chunks, null);
        }
        catch (OperationCanceledException) { return new SummarizeResult(false, null, text.Length, 0, "cancelled"); }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Note] summarize failed: {ex.GetType().Name}: {ex.Message}");
            return new SummarizeResult(false, null, text.Length, 0, ex.Message);
        }
    }

    private async Task<string> SummarizeRecursiveAsync(string text, int maxChars, int depth, Action<int> chunkCounter, CancellationToken ct)
    {
        if (depth > 6) return text; // hard recursion ceiling
        if (text.Length <= maxChars)
        {
            chunkCounter(1);
            return await SummarizeChunkAsync(text, ct);
        }
        // Split in halves on the closest sentence boundary to the midpoint.
        int mid = text.Length / 2;
        int split = FindSentenceBoundary(text, mid);
        var left  = text.Substring(0, split).Trim();
        var right = text.Substring(split).Trim();

        var sLeft  = await SummarizeRecursiveAsync(left,  maxChars, depth + 1, chunkCounter, ct);
        var sRight = await SummarizeRecursiveAsync(right, maxChars, depth + 1, chunkCounter, ct);
        var merged = sLeft + "\n\n" + sRight;

        // One more pass to consolidate.
        return await SummarizeChunkAsync(merged, ct);
    }

    private async Task<string> SummarizeChunkAsync(string chunk, CancellationToken ct)
    {
        await _chatLock.WaitAsync(ct);
        try
        {
            // Use a one-shot session so prior chat history doesn't pollute
            // the summary. Disposing it here also prevents the cached
            // _chatSession (used by chat.send) from being mutated.
            await using var session = LlmGateway.OpenSession();
            var prompt = "다음 음성노트 텍스트를 핵심만 간결하게 한국어로 요약해줘. 글머리 기호 또는 짧은 단락으로:\n\n" + chunk;
            return (await session.SendAsync(prompt, ct)).Trim();
        }
        finally { _chatLock.Release(); }
    }

    private static int FindSentenceBoundary(string text, int around)
    {
        // Look ±200 chars from `around` for a sentence terminator. Falls
        // back to `around` when the window has no clean break.
        int radius = Math.Min(200, text.Length / 4);
        int lo = Math.Max(0, around - radius);
        int hi = Math.Min(text.Length - 1, around + radius);
        for (int i = around; i <= hi; i++)
            if (text[i] == '.' || text[i] == '!' || text[i] == '?' || text[i] == '\n') return i + 1;
        for (int i = around - 1; i >= lo; i--)
            if (text[i] == '.' || text[i] == '!' || text[i] == '?' || text[i] == '\n') return i + 1;
        return around;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Agent Band (M0026) — YouTube oEmbed + stateless LLM classify
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetch YouTube oEmbed metadata (title / author / thumbnail) for a
    /// video id. Runs host-side so the plugin isn't blocked by the missing
    /// CORS headers on the public oEmbed endpoint. The id is validated and
    /// the request URL is rebuilt from a canonical <c>watch?v=</c> string,
    /// so a malicious "id" can never redirect the fetch to another host.
    /// </summary>
    public async Task<OEmbedResult> YouTubeOEmbedAsync(string videoId, CancellationToken ct = default)
    {
        if (!IsValidVideoId(videoId))
            return new OEmbedResult(false, videoId, null, null, null, "invalid videoId");
        try
        {
            var watch = "https://www.youtube.com/watch?v=" + videoId;
            var url = "https://www.youtube.com/oembed?url=" + Uri.EscapeDataString(watch) + "&format=json";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return new OEmbedResult(false, videoId, null, null, null, $"oembed HTTP {(int)resp.StatusCode}");
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? title  = root.TryGetProperty("title",         out var tEl)  ? tEl.GetString()  : null;
            string? author = root.TryGetProperty("author_name",   out var aEl)  ? aEl.GetString()  : null;
            string? thumb  = root.TryGetProperty("thumbnail_url", out var thEl) ? thEl.GetString() : null;
            AppLogger.Log($"[WebDev] oembed ok | id={videoId} title='{Trunc(title ?? "", 50)}'");
            return new OEmbedResult(true, videoId, title, author, thumb, null);
        }
        catch (OperationCanceledException) { return new OEmbedResult(false, videoId, null, null, null, "cancelled"); }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] oembed failed: {ex.GetType().Name}: {ex.Message}");
            return new OEmbedResult(false, videoId, null, null, null, ex.Message);
        }
    }

    /// <summary>
    /// One-shot LLM classification of a YouTube title into exactly one of
    /// <paramref name="categories"/>. Uses a throwaway session (like
    /// <see cref="SummarizeChunkAsync"/>) so the user's <c>chat.*</c> history
    /// is never polluted. The model's free-text answer is clamped to the
    /// supplied whitelist host-side, so even an adversarial title (prompt
    /// injection) can only ever yield one of the allowed labels — worst case
    /// it lands on the fallback (last category).
    /// </summary>
    public async Task<ClassifyResult> ClassifyAsync(string title, string? channel, IReadOnlyList<string> categories, CancellationToken ct = default)
    {
        var allowed = (categories ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        if (string.IsNullOrWhiteSpace(title))
            return new ClassifyResult(false, null, null, "empty title");
        if (allowed.Count == 0)
            return new ClassifyResult(false, null, null, "no categories");
        if (!LlmGateway.IsActiveAvailable())
        {
            // M0026 후속 #1 — this used to return silently, so an all-"기타"
            // result (LLM off → plugin keyword-fallback) had NO log trail.
            // Log it and return a stable token the plugin keys on to show
            // "LLM 꺼짐 → 키워드 추정" instead of pretending it classified.
            AppLogger.Log("[WebDev] classify skipped — LLM backend not loaded (Settings → LLM); plugin will keyword-fallback.");
            return new ClassifyResult(false, null, null, "llm-not-ready");
        }

        var fallback = allowed[^1];
        await _chatLock.WaitAsync(ct);
        try
        {
            await using var session = LlmGateway.OpenSession();
            var list = string.Join(", ", allowed);
            var ch = string.IsNullOrWhiteSpace(channel) ? "" : $"\n채널: {channel}";
            // Hardened prompt — let the model lean on what it knows about the
            // artist/group/track, give a couple of few-shot anchors, and force
            // a single bare label so MatchCategory lands cleanly.
            var prompt =
                "너는 음악 장르 분류기야. 아래 유튜브 영상을 카테고리 중 정확히 하나로 분류해.\n" +
                "가수·그룹 이름이나 곡 제목을 알면 그 지식으로 장르를 추정해도 좋아.\n" +
                "반드시 카테고리 이름 하나만 답하고, 다른 말은 절대 쓰지 마.\n" +
                $"카테고리: {list}\n" +
                "예) BABYMONSTER - DRIP → K-Pop / 베토벤 교향곡 5번 → 클래식 / Bill Evans Trio → 재즈\n\n" +
                $"제목: {title}{ch}";
            var reply = ((await session.SendAsync(prompt, ct)) ?? "").Trim();
            var match = MatchCategory(reply, allowed) ?? fallback;
            AppLogger.Log($"[WebDev] classify | title='{Trunc(title, 50)}' → {match} (raw='{Trunc(reply, 40)}')");
            return new ClassifyResult(true, match, reply, null);
        }
        catch (OperationCanceledException) { return new ClassifyResult(false, null, null, "cancelled"); }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] classify failed: {ex.GetType().Name}: {ex.Message}");
            return new ClassifyResult(false, null, null, ex.Message);
        }
        finally { _chatLock.Release(); }
    }

    private static bool IsValidVideoId(string id)
        => !string.IsNullOrEmpty(id) && id.Length is >= 8 and <= 16
           && id.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');

    // Extract the 11-char video id from any YouTube URL form (watch / youtu.be
    // / embed / shorts / live), tolerating extra params like &list=…&start_radio=1,
    // or accept a bare id. Mirrors the plugin's JS parseVideoId so the host is
    // self-contained and unit-testable. Returns null when no id is present.
    public static string? ParseYouTubeId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (Regex.IsMatch(s, "^[A-Za-z0-9_-]{11}$")) return s;
        foreach (var pat in new[]
        {
            @"[?&]v=([A-Za-z0-9_-]{11})",
            @"youtu\.be/([A-Za-z0-9_-]{11})",
            @"/embed/([A-Za-z0-9_-]{11})",
            @"/shorts/([A-Za-z0-9_-]{11})",
            @"/live/([A-Za-z0-9_-]{11})",
        })
        {
            var m = Regex.Match(s, pat);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    // Clamp the model's free-text answer to one of the allowed labels.
    // First a direct/contains hit (handles "이 영상은 재즈입니다"), then the
    // reverse (reply is a substring of a label). Returns null when nothing
    // matches so the caller can apply its fallback.
    private static string? MatchCategory(string reply, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var r = reply.ToLowerInvariant();
        foreach (var c in allowed)
            if (r.Contains(c.ToLowerInvariant())) return c;
        foreach (var c in allowed)
            if (r.Length >= 2 && c.ToLowerInvariant().Contains(r)) return c;
        return null;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    private static string TryReadLlmBackendName()
    {
        try
        {
            var s = LlmSettingsStore.Load();
            return s.ActiveBackend.ToString();
        }
        catch { return "unknown"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A derived host releases its own capture pipelines first, then calls this —
    /// the chat gate goes last because those pipelines may still be waiting on it.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        try { DisposeNote(); } catch { }
        try { DisposeMusic(); } catch { }
        try { DisposeVision(); } catch { }
        try { DisposeMp3(); } catch { }
        try { _chatSession?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _chatSession = null;
        _chatLock.Dispose();
    }
}

using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Agent.Common;
using Agent.Common.Browser;
using Agent.Common.Mp3;
using Microsoft.Web.WebView2.Core;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// The WebView2 end of the plugin bridge: carries <c>chrome.webview.postMessage</c>
/// envelopes to the shared <see cref="ZeroBridgeDispatcher"/> (ZeroCommon — the one
/// op table both hosts answer from, voice-note and live-music included) and posts its
/// responses and events back. What is left here is what only a WebView2 host can do:
/// the frame capture for vision, the folder dialog, and serving <c>https://mp3.local/</c>.
/// The WebDev panel constructs one bridge per WebView2 host.
///
/// Wire format:
///   request  → { id: number, op: string, args?: object }
///   response → { id: number, ok: boolean, result?: any, error?: string }
///   event    → { op: "event", channel: string, data: any }   (host-pushed; chat streaming)
/// </summary>
public sealed class WebDevBridge : IZeroBridgeHost
{
    private readonly CoreWebView2 _core;
    private readonly WebDevHost _host;
    private readonly ZeroBridgeDispatcher _dispatcher;
    private readonly ZeroBridgeEvents _events;
    // CoreWebView2 is STA-bound — every PostWebMessageAsJson must run on
    // the UI thread that created the WebView2. Voice capture / Whisper /
    // chat streaming all fire from background threads, so we marshal
    // every outbound message through this dispatcher. Captured at
    // construction so the bridge keeps working even if the UI thread's
    // SynchronizationContext changes later.
    private readonly Dispatcher _uiDispatcher;

    public WebDevBridge(CoreWebView2 core, WebDevHost host)
    {
        _core = core;
        _host = host;
        // The bridge is constructed from the UI thread that owns the
        // WebView2 host (WebDevPagePanel.InitAsync) — capture that
        // dispatcher so background events can hop back here.
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _dispatcher = new ZeroBridgeDispatcher(host, this, PostEvent);
        _core.WebMessageReceived += OnMessage;

        // M0029 후속 #1 — mp3.local is served by THIS handler, not the folder
        // mapping: the Chromium media stack streams <audio> with HTTP Range
        // requests, which SetVirtualHostNameToFolderMapping cannot answer —
        // scan worked but playback failed. The interceptor answers 206
        // Partial Content from a FileStream slice (real streaming + seek).
        // The folder mapping stays as a fallback for older runtimes where
        // adding the filter throws.
        try
        {
            _core.AddWebResourceRequestedFilter($"https://{Mp3VirtualHost}/*", CoreWebView2WebResourceContext.All);
            _core.WebResourceRequested += OnMp3ResourceRequested;
        }
        catch (Exception ex) { AppLogger.Log($"[WebDev:Mp3] resource filter unavailable: {ex.Message}"); }

        // Voice-note, live music, MP3 scan job, telemetry ticks — payloads shared with the Avalonia host.
        _events = new ZeroBridgeEvents(host, PostEvent);
    }

    public void Detach()
    {
        try { _core.WebMessageReceived -= OnMessage; } catch { }
        try { _core.WebResourceRequested -= OnMp3ResourceRequested; } catch { }
        _events.Dispose();
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.WebMessageAsJson; }
        catch { raw = "{}"; }

        var response = await _dispatcher.HandleRequestAsync(raw);
        PostJsonOnUi(response, "response");
    }

    // ─── IZeroBridgeHost — what only this host can do ───────────────

    // Every op, capture included, is the shared dispatcher's now.
    public Task<object?>? TryDispatchHostOp(string op, JsonElement? args) => null;

    public Task<string?> PickFolderAsync(string title, string? initialFolder)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initialFolder))
            dlg.InitialDirectory = initialFolder;
        // The dispatcher keeps the caller's context and OnMessage runs on the
        // WebView2's UI thread — safe to block on the dialog.
        return Task.FromResult(dlg.ShowDialog() == true ? dlg.FolderName : null);
    }

    // CapturePreviewAsync is reached from OnMessage — the WebView2's UI thread.
    public async Task<byte[]?> CaptureFramePngAsync()
    {
        using var ms = new MemoryStream();
        await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
        return ms.ToArray();
    }

    public void UseMp3Folder(string? folder) => EnsureMp3HostMapping(folder);

    // ─── mp3.local virtual host (M0029) ─────────────────────────────
    // Maps the CURRENT scan root so <audio src="https://mp3.local/<rel>">
    // streams from disk with native range/seek. Re-mapping the same host
    // name to a new folder is supported by WebView2 — done whenever the
    // operator changes the scan folder.
    private const string Mp3VirtualHost = "mp3.local";
    private string? _mp3MappedFolder;

    private void EnsureMp3HostMapping(string? folder)
    {
        // Timeout-bounded probe — this runs on the UI thread for every mp3.*
        // bridge call (incl. the plugin's on-load mp3.status/mp3.list). A bare
        // Directory.Exists on a disconnected network scan root would freeze the
        // whole app here (see PathAvailability). Unreachable → skip the mapping;
        // tracks just show Available=false until the drive is back.
        if (!PathAvailability.DirectoryExistsFast(folder)) return;
        if (string.Equals(_mp3MappedFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            _core.SetVirtualHostNameToFolderMapping(
                Mp3VirtualHost, folder, CoreWebView2HostResourceAccessKind.Allow);
            _mp3MappedFolder = folder;
            AppLogger.Log($"[WebDev:Mp3] mp3.local → {folder}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Mp3] vhost mapping failed: {ex.Message}");
        }
    }

    // Streams one audio file under the scan root with HTTP Range support (206)
    // — what the <audio> element actually needs for playback + seeking. Path
    // is unescaped and clamped under the root; only Mp3Scanner.SupportedExtensions
    // (mp3 / flac / wav) are served, with the extension's Content-Type.
    private void OnMp3ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        void Respond(int status, string reason, string headers, Stream? body = null)
        {
            try { e.Response = _core.Environment.CreateWebResourceResponse(body, status, reason, headers); }
            catch (Exception ex) { AppLogger.Log($"[WebDev:Mp3] response build failed: {ex.Message}"); }
        }

        try
        {
            var uri = new Uri(e.Request.Uri);
            if (!string.Equals(uri.Host, Mp3VirtualHost, StringComparison.OrdinalIgnoreCase)) return;

            var rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if (rel.Length == 0) { Respond(404, "Not Found", ""); return; }

            // Cover-art route (후속 #2): /__cover/<trackId> → ID3 APIC bytes.
            // Id-based (DB lookup), so no path handling at all on this branch.
            if (rel.StartsWith(Mp3Resource.CoverPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(rel.Substring(Mp3Resource.CoverPrefix.Length), out var coverId)
                    && _host.TryGetMp3Cover(coverId) is { } cover)
                    Respond(200, "OK",
                        $"Content-Type: {cover.Mime}\nContent-Length: {cover.Data.Length}\nCache-Control: max-age=3600",
                        new MemoryStream(cover.Data));
                else
                    Respond(404, "Not Found", "");
                return;
            }

            // Path and Range rules are shared with the Avalonia host (ZeroCommon).
            switch (Mp3Resource.Resolve(Mp3SettingsStore.Load().ScanFolder, rel, out var full))
            {
                case Mp3Resource.Outcome.Forbidden: Respond(403, "Forbidden", ""); return;
                case Mp3Resource.Outcome.NotFound:  Respond(404, "Not Found", ""); return;
            }

            long total = new FileInfo(full!).Length;
            var rangeRaw = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null;
            var ranged = Mp3Resource.TryParseRange(rangeRaw, total, out var start, out var end, out var unsatisfiable);
            if (unsatisfiable)
            { Respond(416, "Range Not Satisfiable", $"Content-Range: bytes */{total}"); return; }

            long count = end - start + 1;
            var fs = new FileStream(full!, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(start, SeekOrigin.Begin);
            var common = $"Content-Type: {Mp3Scanner.ContentTypeFor(full!)}\nAccept-Ranges: bytes";
            if (ranged)
                Respond(206, "Partial Content",
                    $"{common}\nContent-Length: {count}\nContent-Range: bytes {start}-{end}/{total}",
                    new StreamSlice(fs, count));
            else
                Respond(200, "OK", $"{common}\nContent-Length: {total}", fs);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Mp3] serve failed: {ex.GetType().Name}: {ex.Message}");
            Respond(500, "Internal Error", "");
        }
    }

    // Read-only forward view of the next N bytes of an inner stream — lets a
    // 206 response hand WebView2 exactly the requested slice without copying
    // the file into memory.
    private sealed class StreamSlice : Stream
    {
        private readonly Stream _inner;
        private long _remaining;
        public StreamSlice(Stream inner, long count) { _inner = inner; _remaining = count; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            int n = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private void PostEvent(string channel, object data)
        => PostJsonOnUi(ZeroBridgeDispatcher.BuildEvent(channel, data), channel);

    private void PostJsonOnUi(string json, string label)
    {
        // CoreWebView2 must be touched from the UI thread that hosts the
        // WebView2. Most callers already are (the request dispatcher
        // above runs from WebMessageReceived), but background work like
        // VoiceCaptureService events or Task.Run STT continuations isn't
        // — invoke on the captured dispatcher to make every path safe.
        if (_uiDispatcher.CheckAccess())
        {
            try { _core.PostWebMessageAsJson(json); }
            catch (Exception ex) { AppLogger.Log($"[WebDev] post failed ({label}): {ex.Message}"); }
            return;
        }
        _uiDispatcher.BeginInvoke((Action)(() =>
        {
            try { _core.PostWebMessageAsJson(json); }
            catch (Exception ex) { AppLogger.Log($"[WebDev] post failed ({label}): {ex.Message}"); }
        }));
    }
}

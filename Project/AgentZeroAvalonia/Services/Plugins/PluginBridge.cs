using System.Text;
using System.Text.Json;
using Agent.Common;
using Agent.Common.Browser;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AgentZeroAvalonia.Services.Plugins;

/// <summary>
/// The <see cref="NativeWebView"/> end of the plugin bridge — the Avalonia counterpart of
/// WPF's <c>WebDevBridge</c>, over the same <see cref="ZeroBridgeDispatcher"/>, so a plugin
/// gets the same answers from either host.
///
/// Transport: requests arrive through <c>WebMessageReceived</c> (posted by
/// <c>zero-webview-shim.js</c>, which stands in for <c>chrome.webview</c>); responses and
/// events go back through <c>InvokeScript("window.__zeroWebview.deliver([...])")</c>, because
/// NativeWebView has no post-message channel towards the page. One writer, in order: what
/// queues while a script runs leaves as the next batch, so a chat stream's tokens arrive as
/// a few scripts rather than one per token.
///
/// Voice-note and live music run here exactly as in WPF (the pipelines and the Windows
/// capture are ZeroCommon's); vision gets its frame from <see cref="IWebViewFrameCapture"/>.
/// Where an OS has no implementation yet, the answers are the refusals plugins already
/// handle: <c>unsupported-host</c> for audio, <c>capture-unavailable</c> for vision.
/// </summary>
internal sealed class PluginBridge : IZeroBridgeHost, IDisposable
{
    private readonly NativeWebView _web;
    private readonly ZeroBridgeDispatcher _dispatcher;
    private readonly ZeroBridgeEvents _events;
    private readonly Func<string, string?, Task<string?>> _pickFolder;
    private readonly IWebViewFrameCapture? _frameCapture = WebViewFrameCapture.Create();
    private readonly List<string> _outbox = new();
    private bool _pumping;
    private bool _detached;
    // A page that has not finished loading has no listener, and InvokeScript before the first
    // load throws ("Unable to invoke script before any page was loaded") — measured: a token
    // collector tick arrived in that window. Messages for no page are dropped; the page counts
    // as loaded once it completes navigation or sends its first message, whichever is first.
    private bool _pageLoaded;
    private int _scriptFailures;

    public PluginBridge(NativeWebView web, PluginBrowserCore core, Func<string, string?, Task<string?>> pickFolder)
    {
        _web = web;
        _pickFolder = pickFolder;
        _dispatcher = new ZeroBridgeDispatcher(core, this, PostEvent);
        _events = new ZeroBridgeEvents(core, PostEvent);
        _web.WebMessageReceived += OnWebMessage;
        _web.NavigationStarted += OnNavigationStarted;
        _web.NavigationCompleted += OnNavigationCompleted;
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e) => _pageLoaded = false;

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e) => _pageLoaded = e.IsSuccess;

    public void Dispose()
    {
        if (_detached) return;
        _detached = true;
        try { _web.WebMessageReceived -= OnWebMessage; } catch { }
        try { _web.NavigationStarted -= OnNavigationStarted; } catch { }
        try { _web.NavigationCompleted -= OnNavigationCompleted; } catch { }
        _events.Dispose();
    }

    // ── page → host ──

    private void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        // A page whose script is talking is loaded enough to be answered — plugins call the
        // bridge while their scripts run, before NavigationCompleted fires.
        _pageLoaded = true;
        var body = e.Body;
        // Dispatch on the UI thread so host calls (the folder picker) and the outbox run there.
        Dispatcher.UIThread.Post(async () =>
        {
            if (_detached) return;
            var response = await _dispatcher.HandleRequestAsync(body);
            Enqueue(response);
        });
    }

    // ── host → page ──

    private void PostEvent(string channel, object data)
    {
        var json = ZeroBridgeDispatcher.BuildEvent(channel, data);
        if (Dispatcher.UIThread.CheckAccess()) Enqueue(json);
        else Dispatcher.UIThread.Post(() => Enqueue(json));
    }

    private void Enqueue(string json)
    {
        if (_detached || !_pageLoaded) return;
        _outbox.Add(json);
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (_outbox.Count > 0 && !_detached)
            {
                var script = BuildDeliverScript(_outbox);
                _outbox.Clear();
                try { await _web.InvokeScript(script); }
                catch (Exception ex)
                {
                    if (_scriptFailures++ < 3)
                        AppLogger.Log($"[Plugins] deliver failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally { _pumping = false; }
    }

    /// <summary>
    /// The script that hands <paramref name="envelopes"/> (each one JSON object) to the shim.
    /// The dispatcher's serializer escapes every non-ASCII character and U+2028/U+2029, so the
    /// JSON drops into a script literal unchanged. A page without the shim ignores it.
    /// </summary>
    public static string BuildDeliverScript(IEnumerable<string> envelopes)
    {
        var sb = new StringBuilder("window.__zeroWebview&&window.__zeroWebview.deliver([");
        var first = true;
        foreach (var e in envelopes)
        {
            if (!first) sb.Append(',');
            sb.Append(e);
            first = false;
        }
        return sb.Append("]);").ToString();
    }

    // ── IZeroBridgeHost ──

    // Every op — voice-note and live music included — is the shared dispatcher's; the audio
    // comes from ZeroCommon's AudioCapture.Platform, which answers unsupported-host itself on
    // an OS without an implementation.
    public Task<object?>? TryDispatchHostOp(string op, JsonElement? args) => null;

    public Task<string?> PickFolderAsync(string title, string? initialFolder) => _pickFolder(title, initialFolder);

    // The plugin's frame for vision.* — through the engine (WebView2 CapturePreview on Windows).
    public Task<byte[]?> CaptureFramePngAsync()
        => _frameCapture?.CapturePngAsync(_web) ?? Task.FromResult<byte[]?>(null);

    // The plugin server reads the scan root from settings on every request.
    public void UseMp3Folder(string? folder) { }
}

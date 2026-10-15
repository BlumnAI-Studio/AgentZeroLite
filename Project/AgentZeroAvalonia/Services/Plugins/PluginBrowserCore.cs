using Agent.Common;
using Agent.Common.Browser;
using Agent.Common.Voice;

namespace AgentZeroAvalonia.Services.Plugins;

/// <summary>
/// This host's <see cref="ZeroBrowserCore"/>: everything the plugin bridge can do on any
/// OS comes from the shared base; what is added here is <c>tts.speak</c> through the
/// portable voice runtime (Supertonic / OpenAI, the same pair Settings → Voice offers)
/// and <see cref="WavPlayer"/>. Like the WPF host, speak returns once synthesis is done
/// and playback has started — it does not wait for the clip to end.
/// </summary>
internal sealed class PluginBrowserCore : ZeroBrowserCore
{
    private readonly WavPlayer _player = new();
    private CancellationTokenSource? _playback;

    public override async Task<TtsResult> SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TtsResult(false, null, 0, null, "empty text");

        var v = VoiceSettingsStore.Load();
        var tts = PortableVoiceRuntime.CreateTts(v, out var why);
        if (tts is null)
        {
            var error = v.TtsProvider == TtsProviderNames.Off
                ? "TTS provider is Off — pick one in Settings/Voice first"
                : why;
            return new TtsResult(false, v.TtsProvider, 0, null, error);
        }

        try
        {
            var bytes = await tts.SynthesizeAsync(text, PortableVoiceRuntime.VoiceFor(v), ct);
            if (bytes is null || bytes.Length == 0)
                return new TtsResult(false, tts.ProviderName, 0, tts.AudioFormat, "synthesis returned 0 bytes");

            StopSpeaking();
            var cts = new CancellationTokenSource();
            _playback = cts;
            _ = PlayAsync(bytes, cts.Token);
            AppLogger.Log($"[WebDev] TTS OK | provider={tts.ProviderName} bytes={bytes.Length} fmt={tts.AudioFormat}");
            return new TtsResult(true, tts.ProviderName, bytes.Length, tts.AudioFormat, null);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] TTS failed: {ex.GetType().Name}: {ex.Message}");
            return new TtsResult(false, tts.ProviderName, 0, tts.AudioFormat, ex.Message);
        }
        finally
        {
            if (tts is IAsyncDisposable d) await d.DisposeAsync();
        }
    }

    public override void StopSpeaking()
    {
        var cts = Interlocked.Exchange(ref _playback, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        _player.Stop();
    }

    private async Task PlayAsync(byte[] wav, CancellationToken ct)
    {
        try { await _player.PlayAsync(wav, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLogger.Log($"[WebDev] TTS playback failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) StopSpeaking();
        base.Dispose(disposing);
    }
}

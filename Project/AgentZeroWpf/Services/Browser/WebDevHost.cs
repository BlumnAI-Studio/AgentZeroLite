using Agent.Common;
using Agent.Common.Browser;
using Agent.Common.Voice;
using AgentZeroWpf.Services.Voice;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// The WPF host's <see cref="ZeroBrowserCore"/>: every
/// bridge surface — voice-note and live-music capture included, over
/// <see cref="Agent.Common.Audio.AudioCapture"/> — is the shared core's (ZeroCommon, the
/// same code the Avalonia host runs). What stays here is the WPF voice stack: TTS
/// playback through VoiceRuntimeFactory + VoicePlaybackService, and the STT factory
/// (Whisper with the WMI GPU pick, LocalGemma) the note pipeline transcribes with —
/// the same pipeline the Settings/Voice tab drives.
/// </summary>
public sealed class WebDevHost : ZeroBrowserCore
{
    private readonly VoicePlaybackService _playback = new();

    public override async Task<TtsResult> SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TtsResult(false, null, 0, null, "empty text");

        var v = VoiceSettingsStore.Load();
        var tts = VoiceRuntimeFactory.BuildTts(v);
        if (tts is null)
            return new TtsResult(false, v.TtsProvider, 0, null, "TTS provider is Off — pick one in Settings/Voice first");

        try
        {
            var voiceId = ResolveVoiceId(v);
            var bytes = await tts.SynthesizeAsync(text, voiceId, ct);
            if (bytes is null || bytes.Length == 0)
                return new TtsResult(false, tts.ProviderName, 0, tts.AudioFormat, "synthesis returned 0 bytes");

            _playback.Play(bytes, tts.AudioFormat);
            AppLogger.Log($"[WebDev] TTS OK | provider={tts.ProviderName} bytes={bytes.Length} fmt={tts.AudioFormat}");
            return new TtsResult(true, tts.ProviderName, bytes.Length, tts.AudioFormat, null);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev] TTS failed: {ex.GetType().Name}: {ex.Message}");
            return new TtsResult(false, tts.ProviderName, 0, tts.AudioFormat, ex.Message);
        }
    }

    public override void StopSpeaking() => _playback.Stop();

    protected override ISpeechToText? CreateSpeechToText(VoiceSettings v) => VoiceRuntimeFactory.BuildStt(v);

    protected override void Dispose(bool disposing)
    {
        // Capture, models and the chat session are the base's.
        base.Dispose(disposing);
        if (disposing) _playback.Dispose();
    }

    private static string ResolveVoiceId(VoiceSettings v)
    {
        if (!string.IsNullOrWhiteSpace(v.TtsVoice)) return v.TtsVoice;
        return v.TtsProvider == TtsProviderNames.OpenAITts ? "alloy" : string.Empty;
    }
}

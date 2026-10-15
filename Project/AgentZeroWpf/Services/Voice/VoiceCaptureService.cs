using Agent.Common;
using Agent.Common.Audio;
using Agent.Common.Audio.Windows;
using Agent.Common.Voice.Streams;
using NAudio.Wave;

namespace AgentZeroWpf.Services.Voice;

/// <summary>
/// Real-time microphone capture + energy-based voice activity detection (VAD), in the
/// shape the WPF callers (Settings/Voice test, AgentBot voice, Music tab, the AI CLI) were
/// written against. The device is ZeroCommon's <see cref="WindowsMicrophoneSource"/>
/// (WaveIn, 16 kHz mono 16-bit, 50 ms frames) and the VAD is ZeroCommon's
/// <see cref="VoiceActivitySegmenter"/> — both moved down so the Avalonia host's
/// plugins segment speech with the same code. This class only wires the two together.
///
/// The dual-VAD design (frame-level + utterance-level) is what lets the user pause
/// briefly mid-sentence without splitting the utterance — see the segmenter.
/// </summary>
public sealed class VoiceCaptureService : IDisposable
{
    private readonly VoiceActivitySegmenter _vad = new();
    private WindowsMicrophoneSource? _source;

    public VoiceCaptureService()
    {
        _vad.AmplitudeChanged += rms => AmplitudeChanged?.Invoke(rms);
        _vad.SpeakingStateChanged += s => SpeakingStateChanged?.Invoke(s);
        _vad.UtteranceStarted += () => UtteranceStarted?.Invoke();
        _vad.UtteranceEnded += () => UtteranceEnded?.Invoke();
        _vad.FrameAvailable += f => FrameAvailable?.Invoke(f);
    }

    /// <summary>Amplitude threshold (0~1). Default 0.08 (8%).</summary>
    public float VadThreshold { get => _vad.VadThreshold; set => _vad.VadThreshold = value; }

    /// <summary>UI debounce — short (≈300 ms) so the speaking indicator feels responsive.</summary>
    public int SilenceHangoverFrames { get => _vad.SilenceHangoverFrames; set => _vad.SilenceHangoverFrames = value; }

    /// <summary>Utterance boundary — 40 frames ≈ 2 s at 50 ms per buffer.</summary>
    public int UtteranceHangoverFrames { get => _vad.UtteranceHangoverFrames; set => _vad.UtteranceHangoverFrames = value; }

    /// <summary>Hard cap on a single utterance's length, in seconds. 0 disables it (M0015 / 후속 진행 #2).</summary>
    public double MaxUtteranceSeconds { get => _vad.MaxUtteranceSeconds; set => _vad.MaxUtteranceSeconds = value; }

    public event Action<float>? AmplitudeChanged;
    public event Action<bool>? SpeakingStateChanged;
    public event Action? UtteranceStarted;
    public event Action? UtteranceEnded;

    /// <summary>
    /// Per-frame PCM + RMS. The Akka.Streams pipeline (P1+) forwards this directly to
    /// <c>VoiceStreamActor</c>. Suppressed while <see cref="Muted"/>. Must not block.
    /// </summary>
    public event Action<MicFrame>? FrameAvailable;

    /// <summary>When true, utterance audio accumulates for <see cref="ConsumePcmBuffer"/>.</summary>
    public bool BufferPcm { get => _vad.BufferPcm; set => _vad.BufferPcm = value; }

    /// <summary>Suspend VAD + buffering while TTS plays back; the meter keeps moving.</summary>
    public bool Muted { get => _vad.Muted; set => _vad.Muted = value; }

    /// <summary>Pre-trigger audio kept (default 1 s).</summary>
    public double PreRollSeconds { get => _vad.PreRollSeconds; set => _vad.PreRollSeconds = value; }

    public bool IsCapturing => _source is { IsCapturing: true };

    public byte[] ConsumePcmBuffer() => _vad.ConsumePcmBuffer();

    /// <summary>Non-destructive snapshot (Voice Test partial transcripts, M0024 Phase 2.5).</summary>
    public byte[] PeekPcmBuffer() => _vad.PeekPcmBuffer();

    /// <summary>Seed the segment with the pre-roll ring — call on <see cref="UtteranceStarted"/>.</summary>
    public void SeedBufferWithPreRoll() => _vad.SeedBufferWithPreRoll();

    public static void WritePcmToWav(byte[] pcm, string path)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16_000, 16, 1));
        writer.Write(pcm, 0, pcm.Length);
    }

    public static IReadOnlyList<WaveInDevice> ListDevices()
        => new WindowsAudioCapture().ListMicrophones()
            .Select(d => new WaveInDevice(int.Parse(d.Id, System.Globalization.CultureInfo.InvariantCulture), d.Name))
            .ToList();

    public void Start(int deviceNumber = 0)
    {
        if (IsCapturing) return;
        _source?.Dispose();
        _source = new WindowsMicrophoneSource(deviceNumber);
        _source.FrameAvailable += _vad.Process;
        _vad.Reset();
        _source.Start();
        AppLogger.Log($"[Voice] Capture start | device={deviceNumber} sr=16000 bits=16 ch=1 preRoll={PreRollSeconds}s uttHangover={UtteranceHangoverFrames} frames");
    }

    public void Stop()
    {
        var s = _source;
        if (s is null) return;
        _source = null;
        s.FrameAvailable -= _vad.Process;
        s.Dispose();
        _vad.EndOfStream();
        AppLogger.Log("[Voice] Capture stop");
    }

    public void Dispose() => Stop();
}

public sealed record WaveInDevice(int DeviceNumber, string Name)
{
    public override string ToString() => $"[{DeviceNumber}] {Name}";
}

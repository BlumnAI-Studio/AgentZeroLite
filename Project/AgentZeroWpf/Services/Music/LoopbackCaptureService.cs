using Agent.Common.Audio;
using Agent.Common.Audio.Windows;

namespace AgentZeroWpf.Services.Music;

/// <summary>
/// One render endpoint that <see cref="LoopbackCaptureService"/> can target.
/// <see cref="Id"/> is the WASAPI MMDevice ID — opaque GUID-like string,
/// persist it; do NOT persist <see cref="Name"/>.
/// </summary>
public sealed record LoopbackDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// System-output (WASAPI loopback) capture, exposed in the same shape as
/// <see cref="Voice.VoiceCaptureService"/> so the Music and Voice tabs can swap between
/// mic and "what's playing on the speakers" without code-path forks. The device and the
/// conversion to 16 kHz mono PCM16 are ZeroCommon's <see cref="WindowsLoopbackSource"/>,
/// the buffering is <see cref="PcmCaptureBuffer"/> — both moved down so the Avalonia host's
/// plugins capture the same way. This class only wires them together.
/// </summary>
public sealed class LoopbackCaptureService : IDisposable
{
    private readonly PcmCaptureBuffer _buffer = new();
    private WindowsLoopbackSource? _source;

    public LoopbackCaptureService()
    {
        _buffer.AmplitudeChanged += rms => AmplitudeChanged?.Invoke(rms);
        _buffer.PcmFrameAvailable += chunk => PcmFrameAvailable?.Invoke(chunk);
    }

    public bool BufferPcm { get => _buffer.BufferPcm; set => _buffer.BufferPcm = value; }
    public bool Muted { get => _buffer.Muted; set => _buffer.Muted = value; }
    public bool IsCapturing => _source is { IsCapturing: true };

    public event Action<float>? AmplitudeChanged;

    /// <summary>
    /// Raw 16 kHz mono PCM16 chunk emitted alongside <see cref="AmplitudeChanged"/> — the
    /// Music tab's live spectrum + sliding-window inference. Fires from the WASAPI capture
    /// thread; the handler must not block.
    /// </summary>
    public event Action<byte[]>? PcmFrameAvailable;

    public static IReadOnlyList<LoopbackDevice> ListDevices()
        => new WindowsAudioCapture().ListLoopbackDevices()
            .Select(d => new LoopbackDevice(d.Id, d.Name, d.IsDefault))
            .ToList();

    public byte[] ConsumePcmBuffer() => _buffer.ConsumePcmBuffer();

    /// <summary>Non-destructive snapshot of the rolling buffer (live previews).</summary>
    public byte[] PeekPcmBuffer() => _buffer.PeekPcmBuffer();

    public void Start(string? deviceId = null)
    {
        if (IsCapturing) return;
        _source?.Dispose();
        _source = new WindowsLoopbackSource(deviceId);
        _source.FrameAvailable += _buffer.Process;
        _source.Start();
    }

    public void Stop()
    {
        var s = _source;
        if (s is null) return;
        _source = null;
        s.FrameAvailable -= _buffer.Process;
        s.Dispose();
    }

    public void Dispose() => Stop();
}

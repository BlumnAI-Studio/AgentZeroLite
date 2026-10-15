namespace Agent.Common.Audio;

/// <summary>One capture endpoint. <see cref="Id"/> is what <see cref="IAudioCapturePlatform"/> opens — persist it, not <see cref="Name"/>.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// A running audio input, already converted to the one shape every consumer here expects:
/// <b>16 kHz, mono, 16-bit little-endian PCM</b> (what Whisper, the AST classifier, the
/// spectrum and the diarizer take). Frames arrive on the capture thread; a handler must
/// not block it.
/// </summary>
public interface IAudioFrameSource : IDisposable
{
    bool IsCapturing { get; }

    /// <summary>One chunk of 16 kHz mono PCM16. The array is the handler's to keep.</summary>
    event Action<byte[]>? FrameAvailable;

    /// <summary>Capture ended on its own (device removed, driver error) — null when it was a clean stop.</summary>
    event Action<Exception?>? Stopped;

    void Start();
    void Stop();
}

/// <summary>
/// The OS seam for audio input — the part of voice-note / live-music that cannot be shared.
/// Everything above it (VAD, buffering, STT, diarization, the AST classifier, the spectrum)
/// lives in ZeroCommon and runs unchanged on any implementation.
///
/// <para>Implementations: <see cref="Windows.WindowsAudioCapture"/> (NAudio — WaveIn + WASAPI
/// loopback). Elsewhere <see cref="UnsupportedAudioCapture"/> answers until a platform one
/// is written; on macOS that means AVAudioEngine for the microphone and ScreenCaptureKit for
/// system audio, both behind a permission prompt — separate work, done on a Mac.</para>
/// </summary>
public interface IAudioCapturePlatform
{
    /// <summary>For logs and errors: "windows-naudio", "unsupported", …</summary>
    string Name { get; }

    bool SupportsMicrophone { get; }

    /// <summary>Capturing what the speakers play (loopback / system audio).</summary>
    bool SupportsLoopback { get; }

    IReadOnlyList<AudioDeviceInfo> ListMicrophones();
    IReadOnlyList<AudioDeviceInfo> ListLoopbackDevices();

    /// <summary>A microphone source, not started. Null/empty id = the default device.</summary>
    IAudioFrameSource OpenMicrophone(string? deviceId);

    /// <summary>A loopback source on a render endpoint, not started. Null/empty id = the default output.</summary>
    IAudioFrameSource OpenLoopback(string? deviceId);
}

/// <summary>The answer on an OS without an implementation yet.</summary>
public sealed class UnsupportedAudioCapture : IAudioCapturePlatform
{
    public string Name => "unsupported";
    public bool SupportsMicrophone => false;
    public bool SupportsLoopback => false;
    public IReadOnlyList<AudioDeviceInfo> ListMicrophones() => Array.Empty<AudioDeviceInfo>();
    public IReadOnlyList<AudioDeviceInfo> ListLoopbackDevices() => Array.Empty<AudioDeviceInfo>();
    public IAudioFrameSource OpenMicrophone(string? deviceId) => throw No();
    public IAudioFrameSource OpenLoopback(string? deviceId) => throw No();

    private static PlatformNotSupportedException No() =>
        new($"Audio capture is not implemented on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} yet.");
}

public static class AudioCapture
{
    private static IAudioCapturePlatform? _override;
    private static readonly Lazy<IAudioCapturePlatform> _platform = new(() =>
        OperatingSystem.IsWindows() ? new Windows.WindowsAudioCapture() : new UnsupportedAudioCapture());

    /// <summary>The implementation for the OS this process runs on.</summary>
    public static IAudioCapturePlatform Platform => _override ?? _platform.Value;

    /// <summary>Tests swap in a fake; disposing restores the real one.</summary>
    public static IDisposable Override(IAudioCapturePlatform platform)
    {
        var previous = _override;
        _override = platform;
        return new Restore(() => _override = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

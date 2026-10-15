using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Agent.Common.Audio.Windows;

/// <summary>
/// <see cref="IAudioCapturePlatform"/> on Windows, over NAudio: the microphone through
/// WaveIn (asked for 16 kHz mono 16-bit directly — the driver resamples), the speakers
/// through WASAPI loopback (mixer format, converted here). Moved down from the WPF host's
/// <c>VoiceCaptureService</c> / <c>LoopbackCaptureService</c>, which now wrap these.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAudioCapture : IAudioCapturePlatform
{
    public string Name => "windows-naudio";
    public bool SupportsMicrophone => true;
    public bool SupportsLoopback => true;

    /// <summary>WaveIn devices; the id is the device number. Number 0 is what an empty id opens.</summary>
    public IReadOnlyList<AudioDeviceInfo> ListMicrophones()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                list.Add(new AudioDeviceInfo(i.ToString(System.Globalization.CultureInfo.InvariantCulture), caps.ProductName, i == 0));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Audio] microphone list failed: {ex.GetType().Name}: {ex.Message}");
        }
        return list;
    }

    /// <summary>Active render endpoints; the id is the WASAPI MMDevice id.</summary>
    public IReadOnlyList<AudioDeviceInfo> ListLoopbackDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = "";
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                defaultId = def.ID;
            }
            catch { /* no render endpoint at all — laptop with audio service down */ }

            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultId));
                d.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Audio] loopback list failed: {ex.GetType().Name}: {ex.Message}");
        }
        return list;
    }

    public IAudioFrameSource OpenMicrophone(string? deviceId)
        => new WindowsMicrophoneSource(int.TryParse(deviceId, out var n) ? n : 0);

    public IAudioFrameSource OpenLoopback(string? deviceId) => new WindowsLoopbackSource(deviceId);
}

/// <summary>WaveIn at 16 kHz mono 16-bit, 50 ms buffers ×3 — the shape the VAD's frame counts assume.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMicrophoneSource : IAudioFrameSource
{
    private readonly int _deviceNumber;
    private WaveInEvent? _waveIn;
    private volatile bool _isCapturing;

    public WindowsMicrophoneSource(int deviceNumber) => _deviceNumber = deviceNumber;

    public int DeviceNumber => _deviceNumber;
    public bool IsCapturing => _isCapturing;
    public event Action<byte[]>? FrameAvailable;
    public event Action<Exception?>? Stopped;

    public void Start()
    {
        if (_waveIn is not null) return;
        _waveIn = new WaveInEvent
        {
            DeviceNumber = _deviceNumber,
            WaveFormat = new WaveFormat(16_000, bits: 16, channels: 1),
            BufferMilliseconds = 50,
            NumberOfBuffers = 3,
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += (_, e) =>
        {
            _isCapturing = false;
            if (e.Exception is not null)
                AppLogger.Log($"[Voice] Recording stopped with error: {e.Exception.GetType().Name}: {e.Exception.Message}");
            else
                AppLogger.Log("[Voice] Recording stopped (clean)");
            try { Stopped?.Invoke(e.Exception); } catch { }
        };
        _isCapturing = true;
        _waveIn.StartRecording();
    }

    public void Stop()
    {
        var w = _waveIn;
        if (w is null) return;
        _waveIn = null;
        try { w.StopRecording(); } catch { /* already stopped */ }
        w.Dispose();
        _isCapturing = false;
    }

    public void Dispose() => Stop();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
        FrameAvailable?.Invoke(chunk);
    }
}

/// <summary>
/// WASAPI loopback on a render endpoint, converted to 16 kHz mono PCM16:
/// <c>WasapiLoopbackCapture</c> (mixer format, typically 48 kHz stereo float) →
/// <c>BufferedWaveProvider</c> → samples → stereo-to-mono → WDL resampler → int16. One pull
/// per <c>DataAvailable</c> keeps latency at one WASAPI buffer (~10 ms); the chain is built
/// on <see cref="Start"/> because the format is only known once the device is picked.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLoopbackSource : IAudioFrameSource
{
    private const int TargetSampleRate = 16_000;

    private readonly string? _deviceId;
    private WasapiLoopbackCapture? _capture;
    private MMDevice? _device;
    private BufferedWaveProvider? _rawBuffer;
    private IWaveProvider? _pcm16Provider;
    private readonly byte[] _pullScratch = new byte[TargetSampleRate * 2]; // 1 s of PCM16 mono
    private volatile bool _isCapturing;

    public WindowsLoopbackSource(string? deviceId) => _deviceId = deviceId;

    public bool IsCapturing => _isCapturing;
    public string? DeviceName => _device?.FriendlyName;
    public event Action<byte[]>? FrameAvailable;
    public event Action<Exception?>? Stopped;

    public void Start()
    {
        if (_capture is not null) return;

        using (var enumerator = new MMDeviceEnumerator())
        {
            _device = string.IsNullOrEmpty(_deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)
                : enumerator.GetDevice(_deviceId);
        }

        _capture = new WasapiLoopbackCapture(_device);
        var srcFormat = _capture.WaveFormat;
        _rawBuffer = new BufferedWaveProvider(srcFormat)
        {
            // 32 MB ≈ 80 s of 48 kHz stereo float. Loopback never blocks the render
            // thread; the headroom covers sluggish consumers during ONNX warm-up.
            BufferLength = 32 * 1024 * 1024,
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };

        ISampleProvider sampleProvider = _rawBuffer.ToSampleProvider();
        if (srcFormat.Channels > 1)
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (srcFormat.SampleRate != TargetSampleRate)
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);
        _pcm16Provider = new SampleToWaveProvider16(sampleProvider);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, e) =>
        {
            _isCapturing = false;
            if (e.Exception is not null)
                AppLogger.Log($"[Music-Loopback] Recording stopped with error: {e.Exception.GetType().Name}: {e.Exception.Message}");
            else
                AppLogger.Log("[Music-Loopback] Recording stopped (clean)");
            try { Stopped?.Invoke(e.Exception); } catch { }
        };

        _isCapturing = true;
        _capture.StartRecording();
        AppLogger.Log($"[Music-Loopback] Capture start | device='{_device.FriendlyName}' srcFmt={srcFormat.SampleRate}Hz/{srcFormat.Channels}ch/{srcFormat.BitsPerSample}bit → 16000/mono/16");
    }

    public void Stop()
    {
        var c = _capture;
        if (c is null) return;
        _capture = null;
        try { c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _device = null;
        _rawBuffer = null;
        _pcm16Provider = null;
        _isCapturing = false;
    }

    public void Dispose() => Stop();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var raw = _rawBuffer;
        var pcm = _pcm16Provider;
        if (raw is null || pcm is null || e.BytesRecorded <= 0) return;

        raw.AddSamples(e.Buffer, 0, e.BytesRecorded);
        // Drain everything the converter can give in this batch, so consumers stay in
        // lock-step with real-time playback.
        while (true)
        {
            int read = pcm.Read(_pullScratch, 0, _pullScratch.Length);
            if (read <= 0) break;
            var chunk = new byte[read];
            Buffer.BlockCopy(_pullScratch, 0, chunk, 0, read);
            FrameAvailable?.Invoke(chunk);
        }
    }
}

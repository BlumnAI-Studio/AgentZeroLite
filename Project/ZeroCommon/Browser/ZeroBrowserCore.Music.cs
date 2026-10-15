using Agent.Common.Audio;
using Agent.Common.Music;

namespace Agent.Common.Browser;

/// <summary>
/// One inference cycle's payload. <see cref="Labels"/> is sorted high-to-low by score;
/// <see cref="Spectrum"/> is the bar-band snapshot taken at the same moment so the plugin
/// can correlate "what's playing now" with "what the model heard".
/// </summary>
public sealed record MusicTickInfo(
    IReadOnlyList<MusicLabel> Labels,
    float[] Spectrum,
    int Frames,
    int Bins,
    int InferMs);

/// <summary>
/// Live music classification surface (M0025 — Agent Band): loopback (or mic) capture →
/// rolling 10 s window → AST AudioSet every 1.5 s, plus a ~30 Hz spectrum stream. Moved down
/// from the WPF <c>WebDevHost</c>; the audio comes from <see cref="AudioCapture.Platform"/>,
/// so a host without an implementation answers <c>unsupported-host</c>. One live session per
/// core; <see cref="StartMusicAsync"/> is idempotent.
/// </summary>
public partial class ZeroBrowserCore
{
    private const int MusicSpectrumBars = 32;
    private const int MusicLiveCadenceMs = 1500;
    private static readonly int MusicRollingTargetBytes =
        10 * SpectrumBars.SampleRate * 2; // 10 s of 16 kHz mono PCM16

    private readonly SemaphoreSlim _musicLock = new(1, 1);
    private IAudioFrameSource? _musicSource;
    private PcmCaptureBuffer? _musicCapture;
    private OnnxAstClassifier? _musicClassifier;
    // Set when music settings change (Save/download). The inference loop drops and
    // rebuilds the classifier at a safe point so a new model path takes effect without a
    // restart (M0025 follow-up #12). Volatile: written from the settings callback thread.
    private volatile bool _musicClassifierStale;
    private CancellationTokenSource? _musicCts;
    private Task? _musicLoopTask;
    private readonly SpectrumBars _musicSpectrum = new();
    private readonly List<byte> _musicRollingPcm = new();
    private int _musicInferenceInFlight; // 0/1 single-flight gate
    private int _musicLiveTopK = 5;
    private string _musicSourceName = MusicInputSourceNames.SystemLoopback;
    private long _lastMusicAmpTick;
    private bool _musicSettingsHooked;

    public event Action<MusicTickInfo>? MusicTick;
    public event Action<float>? MusicAmplitude; // ~10 Hz throttled
    /// <summary>
    /// Fast spectrum stream (~30 Hz), independent of the 1.5 s AST tick, so the plugin's
    /// bar visualiser feels real-time instead of polling.
    /// </summary>
    public event Action<float[]>? MusicSpectrum;
    private long _lastSpectrumEmit;
    private const int SpectrumEmitThrottleMs = 33; // ~30 Hz

    public bool IsMusicLive => _musicSource is { IsCapturing: true };

    /// <summary>
    /// Begin live capture + the AST inference loop. Returns {ok, capturing, source, topK,
    /// cadenceMs}; a missing model returns {ok:false, error:"model-missing", modelPath} so
    /// the plugin can show the install hint.
    /// </summary>
    public async Task<object> StartMusicAsync(string? source, string? deviceId, int? topK, int? cadenceMs)
    {
        await _musicLock.WaitAsync();
        try
        {
            if (!_musicSettingsHooked)
            {
                // Invalidate the cached classifier when music settings change (M0025 #12).
                MusicSettingsStore.Changed += OnMusicSettingsChanged;
                _musicSettingsHooked = true;
            }

            if (IsMusicLive)
                return new { ok = true, capturing = true, source = _musicSourceName, topK = _musicLiveTopK, cadenceMs = MusicLiveCadenceMs };

            // Default to SystemLoopback: the mission is "watch what's playing" — mic is opt-in.
            var src = string.IsNullOrWhiteSpace(source) ? MusicInputSourceNames.SystemLoopback : source!;
            if (src != MusicInputSourceNames.Microphone && src != MusicInputSourceNames.SystemLoopback)
                src = MusicInputSourceNames.SystemLoopback;

            var platform = AudioCapture.Platform;
            var supported = src == MusicInputSourceNames.SystemLoopback ? platform.SupportsLoopback : platform.SupportsMicrophone;
            if (!supported)
            {
                AppLogger.Log($"[WebDev:Music] start refused — {src} capture not implemented on this OS ({platform.Name})");
                return new { ok = false, error = UnsupportedHost };
            }

            var k = topK is int kk && kk > 0 ? Math.Clamp(kk, 1, 20) : 5;

            var settings = MusicSettingsStore.Load();
            settings.TopK = k;
            settings.InputSource = src;
            var modelPath = MusicSettingsStore.ResolveModelPath(settings);
            if (!File.Exists(modelPath))
            {
                AppLogger.Log($"[WebDev:Music] start aborted — model missing at {modelPath}");
                return new { ok = false, error = "model-missing", modelPath };
            }

            _musicSourceName = src;
            _musicLiveTopK = k;
            lock (_musicRollingPcm) _musicRollingPcm.Clear();

            var capture = new PcmCaptureBuffer { BufferPcm = false };
            capture.AmplitudeChanged += OnMusicAmplitude;
            capture.PcmFrameAvailable += OnMusicPcmFrame;
            IAudioFrameSource? s = null;
            try
            {
                s = src == MusicInputSourceNames.SystemLoopback
                    ? platform.OpenLoopback(deviceId)
                    : platform.OpenMicrophone(deviceId);
                s.FrameAvailable += capture.Process;
                await StartOffCallerAsync(s);
            }
            catch (Exception ex)
            {
                try { s?.Dispose(); } catch { }
                AppLogger.Log($"[WebDev:Music] capture start failed: {ex.GetType().Name}: {ex.Message}");
                return new { ok = false, error = ex.Message };
            }
            _musicSource = s;
            _musicCapture = capture;

            _musicCts = new CancellationTokenSource();
            var token = _musicCts.Token;
            _musicLoopTask = Task.Run(() => MusicInferenceLoopAsync(settings, token));

            AppLogger.Log($"[WebDev:Music] live start | source={src} topK={k} model={Path.GetFileName(modelPath)} platform={platform.Name}");
            return new { ok = true, capturing = true, source = src, topK = k, cadenceMs = MusicLiveCadenceMs };
        }
        finally { _musicLock.Release(); }
    }

    public async Task<object> StopMusicAsync()
    {
        await _musicLock.WaitAsync();
        try
        {
            TearDownMusicLocked();
            return new { ok = true };
        }
        finally { _musicLock.Release(); }
    }

    public object GetMusicStatus() => new
    {
        capturing = IsMusicLive,
        source = _musicSourceName,
        topK = _musicLiveTopK,
        cadenceMs = MusicLiveCadenceMs,
    };

    private void OnMusicPcmFrame(byte[] pcm)
    {
        if (pcm is null || pcm.Length == 0) return;

        lock (_musicRollingPcm)
        {
            _musicRollingPcm.AddRange(pcm);
            int over = _musicRollingPcm.Count - MusicRollingTargetBytes;
            if (over > 0) _musicRollingPcm.RemoveRange(0, over);
        }
        _musicSpectrum.Push(pcm);

        // One Hann-windowed FFT per emit (cheap); more often than the visual frame rate
        // isn't worth the bridge traffic, hence the 33 ms throttle.
        if (MusicSpectrum is not null)
        {
            var now = Environment.TickCount64;
            if (now - _lastSpectrumEmit >= SpectrumEmitThrottleMs)
            {
                _lastSpectrumEmit = now;
                try { MusicSpectrum.Invoke(_musicSpectrum.ComputeBars(MusicSpectrumBars)); }
                catch (Exception ex) { AppLogger.Log($"[WebDev:Music] spectrum emit failed: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
    }

    private void OnMusicAmplitude(float rms)
    {
        // ~10 Hz throttle — matches the voice-note amplitude pattern.
        var now = Environment.TickCount64;
        if (now - _lastMusicAmpTick < 90) return;
        _lastMusicAmpTick = now;
        MusicAmplitude?.Invoke(rms);
    }

    private void OnMusicSettingsChanged() => _musicClassifierStale = true;

    private async Task MusicInferenceLoopAsync(MusicSettings settings, CancellationToken ct)
    {
        try
        {
            _musicClassifier ??= new OnnxAstClassifier(settings);
            var ready = await _musicClassifier.EnsureReadyAsync(null, ct).ConfigureAwait(false);
            if (!ready)
            {
                AppLogger.Log("[WebDev:Music] AST model failed to load");
                return;
            }

            // Give the rolling buffer ~2 s to fill so the first inference isn't on silence.
            await Task.Delay(2000, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                if (Interlocked.CompareExchange(ref _musicInferenceInFlight, 1, 0) == 0)
                {
                    try
                    {
                        // Settings changed since the classifier was built — rebuild it here
                        // (gate held, no ClassifyAsync in flight). Disposing the old session
                        // frees the ~347 MB ORT arena that once leaked across restarts.
                        if (_musicClassifierStale)
                        {
                            _musicClassifierStale = false;
                            var old = _musicClassifier;
                            _musicClassifier = null;
                            try { if (old is not null) await old.DisposeAsync().ConfigureAwait(false); } catch { }
                            settings = MusicSettingsStore.Load();
                            var fresh = new OnnxAstClassifier(settings);
                            if (await fresh.EnsureReadyAsync(null, ct).ConfigureAwait(false))
                            {
                                _musicClassifier = fresh;
                                AppLogger.Log("[WebDev:Music] classifier invalidated → reloaded with new settings");
                            }
                            else
                            {
                                try { await fresh.DisposeAsync().ConfigureAwait(false); } catch { }
                                AppLogger.Log("[WebDev:Music] classifier reload failed after settings change — will retry on next change");
                            }
                        }

                        byte[] snapshot;
                        lock (_musicRollingPcm) { snapshot = _musicRollingPcm.ToArray(); }

                        if (_musicClassifier is not null && snapshot.Length >= SpectrumBars.SampleRate * 2)
                        {
                            var result = await _musicClassifier.ClassifyAsync(snapshot, _musicLiveTopK, ct).ConfigureAwait(false);
                            var bars = _musicSpectrum.ComputeBars(MusicSpectrumBars);
                            MusicTick?.Invoke(new MusicTickInfo(
                                Labels: result.TopLabels,
                                Spectrum: bars,
                                Frames: result.SpectrogramFrames,
                                Bins: result.SpectrogramBins,
                                InferMs: (int)result.InferenceTime.TotalMilliseconds));
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"[WebDev:Music] inference iteration failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _musicInferenceInFlight, 0);
                    }
                }
                await Task.Delay(MusicLiveCadenceMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Music] inference loop crashed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TearDownMusicLocked()
    {
        try { _musicCts?.Cancel(); } catch { }
        try { _musicLoopTask?.Wait(500); } catch { }

        var s = _musicSource; _musicSource = null;
        var cap = _musicCapture; _musicCapture = null;
        if (s is not null)
        {
            if (cap is not null) s.FrameAvailable -= cap.Process;
            try { s.Stop(); } catch { }
            try { s.Dispose(); } catch { }
        }
        if (cap is not null)
        {
            cap.AmplitudeChanged -= OnMusicAmplitude;
            cap.PcmFrameAvailable -= OnMusicPcmFrame;
        }

        try { _musicCts?.Dispose(); } catch { }
        _musicCts = null;
        _musicLoopTask = null;
        // _musicClassifier stays across Start/Stop so a second Start doesn't reload ~347 MB.
        lock (_musicRollingPcm) _musicRollingPcm.Clear();
        AppLogger.Log("[WebDev:Music] live stopped");
    }

    private void DisposeMusic()
    {
        // Drop the static settings subscription first so it can't pin this core alive.
        if (_musicSettingsHooked) MusicSettingsStore.Changed -= OnMusicSettingsChanged;
        try { TearDownMusicLocked(); } catch { }
        // The deterministic place to free the classifier's native ONNX session.
        try { var mc = _musicClassifier; _musicClassifier = null; mc?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _musicLock.Dispose();
    }
}

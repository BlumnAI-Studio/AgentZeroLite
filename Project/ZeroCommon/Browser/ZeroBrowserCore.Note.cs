using Agent.Common.Audio;
using Agent.Common.Voice;
using Agent.Common.Voice.Diarization;

namespace Agent.Common.Browser;

/// <summary>
/// One transcript line emitted by the voice-note pipeline (M0024 Phase 3).
/// <see cref="Text"/> is the recognised speech; speaker fields are optional
/// (null when diarization is Off). <see cref="IsPartial"/> distinguishes
/// the 10 s rolling preview from the final utterance — partials reuse the
/// same event so plugins that don't care about the distinction render them
/// as ordinary lines.
/// </summary>
public sealed record NoteTranscriptInfo(
    string Text,
    int? SpeakerId,
    string? SpeakerLabel,
    bool IsPartial);

/// <summary>
/// Capture source for the voice-note pipeline (M0024 Phase 3.5).
/// Mirrors the Voice tab's <c>VoiceInputSourceNames</c> so plugins can
/// pass the same constant the user picked in Settings/Voice.
/// </summary>
public static class NoteSourceNames
{
    public const string Microphone = "Microphone";
    public const string SystemLoopback = "SystemLoopback";
}

/// <summary>
/// Options bag for <c>note.start</c> — extended in Phase 3.5 from the
/// original sensitivity-only call. JS callers pass any subset; null
/// fields fall back to safe defaults (mic source + Settings/Voice
/// sensitivity + 30 s loopback chunk).
/// </summary>
public sealed record NoteStartOptions(
    int? Sensitivity = null,
    string? Source = null,
    string? LoopbackDeviceId = null,
    int? LoopbackChunkSec = null);

/// <summary>
/// Voice-note plugin surface (M0007 / M0024): VAD-gated microphone or continuous
/// loopback capture → STT → optional diarization → <see cref="NoteTranscript"/>.
/// Moved down from the WPF <c>WebDevHost</c>; the audio comes from
/// <see cref="AudioCapture.Platform"/> (the OS seam), so this file runs unchanged on any
/// host that has an implementation, and answers <c>unsupported-host</c> where none exists.
/// STT comes from <see cref="CreateSpeechToText"/> — the portable runtime by default,
/// the WPF host's own factory there.
/// </summary>
public partial class ZeroBrowserCore
{
    /// <summary>The <c>error</c> a plugin gets where this host cannot capture audio.</summary>
    public const string UnsupportedHost = "unsupported-host";

    // ─── Voice-note (VAD-gated STT) state ───────────────────────────────
    // Start → a microphone source feeds the VAD with PCM buffering; each
    // UtteranceEnded auto-transcribes and raises NoteTranscript. Pause/Resume just
    // toggles Muted (capture stays alive so the plugin's level meter keeps reading).
    private readonly SemaphoreSlim _noteLock = new(1, 1);
    private IAudioFrameSource? _noteMicSource;
    private VoiceActivitySegmenter? _noteVad;
    // M0024 Phase 3.5 — second capture path (loopback) for system playback (Zoom
    // recording, YouTube, music). Only one of the two runs per StartNoteCaptureAsync.
    private IAudioFrameSource? _noteLoopbackSource;
    private PcmCaptureBuffer? _noteLoopback;
    private string _noteSource = NoteSourceNames.Microphone;
    private int _noteLoopbackChunkSec = 30;
    private Timer? _noteChunkTimer; // fires every _noteLoopbackChunkSec for the loopback path
    private ISpeechToText? _noteStt;
    private string _noteLanguage = "auto";
    private CancellationTokenSource? _noteCts;

    // M0024 Phase 3 — diarizer for speaker labels; lazily created on the first
    // utterance, null when DiarizationSettings is Off. Its own ONNX session (the model
    // file on disk is shared with Settings/Voice/Test).
    private ISpeakerDiarizer? _noteDiarizer;

    // M0024 Phase 3 — partial transcript timer: every 10 s during active capture, peek
    // the buffer and emit a partial NoteTranscript (IsPartial=true) so the user sees
    // recording is alive before the silence-segmented utterance closes.
    private Timer? _notePartialTimer;
    private int _notePartialBusy;
    private int _notePartialLastBytes;
    private const int NotePartialIntervalMs = 10_000;
    private const int NotePartialMinBytes = 16_000 * 2 * 2; // 2 s of 16 kHz mono PCM16

    // P0 (2026-07-15) — single serialization gate for ALL note-pipeline native
    // inference (partial STT, chunk STT, diarize). whisper.cpp's Vulkan factory and the
    // single Sherpa diarizer instance are NOT safe for concurrent inference: overlapping
    // calls corrupt native heap and FailFast the host a few minutes into a loopback
    // session. Chunk STT waits for an in-flight partial; partials try-acquire and skip.
    private readonly SemaphoreSlim _noteInferGate = new(1, 1);
    // Chunk-tick re-entrancy guard. Without it the 30 s periodic timer stacked
    // ProcessFinalChunkAsync tasks whenever Whisper medium ran slower than realtime.
    private int _noteChunkBusy;

    public event Action<NoteTranscriptInfo>? NoteTranscript;  // M0024 Phase 3 — rich payload
    public event Action?         NoteUtteranceStarted;
    public event Action?         NoteUtteranceEnded;
    public event Action<string>? NoteError;          // (message)
    public event Action<float>?  NoteAmplitude;      // (rms 0..1) — throttled ~10 Hz
    public event Action<bool>?   NoteSpeaking;       // frame-level VAD decision (faster than utterance)

    public float CurrentVadThreshold => _noteVad?.VadThreshold ?? 0f;

    /// <summary>
    /// The STT the note pipeline transcribes with. Default: the portable runtime
    /// (Whisper.net / OpenAI). The WPF host overrides it with its own factory (GPU pick,
    /// LocalGemma). Null = the provider is Off or not available here.
    /// </summary>
    protected virtual ISpeechToText? CreateSpeechToText(VoiceSettings v)
        => PortableVoiceRuntime.CreateStt(v, out _);

    /// <summary>Settings/Voice as the note pipeline reads it — a seam so tests need not touch the user's file.</summary>
    protected virtual VoiceSettings LoadVoiceSettings() => VoiceSettingsStore.Load();

    /// <summary>Settings/Diarization as the note pipeline reads it.</summary>
    protected virtual DiarizationSettings LoadDiarizationSettings() => DiarizationSettingsStore.Load();

    /// <summary>
    /// Begin capture. Two paths share this entry point (M0024 Phase 3.5):
    ///   • <b>Microphone</b> — VAD-gated; ~2 s of trailing silence closes an utterance →
    ///     auto-transcribe → NoteTranscript.
    ///   • <b>SystemLoopback</b> — continuous capture of the render endpoint (default or
    ///     picked). No VAD; a chunk timer (30 s default) drains the buffer as one utterance.
    /// Both share the 10 s partial preview and the diarizer. Idempotent — a second call
    /// while running returns the live values. Returns the effective sensitivity /
    /// threshold / source so JS can sync its UI.
    /// </summary>
    public async Task<object> StartNoteCaptureAsync(NoteStartOptions? opts = null)
    {
        opts ??= new NoteStartOptions();
        await _noteLock.WaitAsync();
        try
        {
            var v = LoadVoiceSettings();

            var source = string.IsNullOrEmpty(opts.Source) ? NoteSourceNames.Microphone : opts.Source!;
            if (source != NoteSourceNames.Microphone && source != NoteSourceNames.SystemLoopback)
                source = NoteSourceNames.Microphone;

            // Effective sensitivity (mic only — loopback has no VAD): the caller's slider,
            // else Settings/Voice's stored VadThreshold so the plugin inherits the value
            // the user already tuned for their mic.
            int effectiveSens = opts.Sensitivity.HasValue
                ? Math.Clamp(opts.Sensitivity.Value, 0, 100)
                : Math.Clamp(100 - v.VadThreshold, 0, 100);
            // M0015 후속 진행 #1: lecture-style audio is the Loose-profile use case, so the
            // saved "Auto" token resolves to Loose here (isAgentMode: true).
            float threshold = VoiceVadSettings.SensitivityToThreshold(effectiveSens, v, isAgentMode: true);

            if (_noteMicSource is not null || _noteLoopbackSource is not null)
                return new { ok = true, capturing = true, sensitivity = effectiveSens, threshold, source = _noteSource };

            var platform = AudioCapture.Platform;
            var supported = source == NoteSourceNames.SystemLoopback ? platform.SupportsLoopback : platform.SupportsMicrophone;
            if (!supported)
            {
                AppLogger.Log($"[WebDev:Note] start refused — {source} capture not implemented on this OS ({platform.Name})");
                return new { ok = false, error = UnsupportedHost, capturing = false };
            }

            _noteStt = CreateSpeechToText(v);
            if (_noteStt is null)
            {
                AppLogger.Log("[WebDev:Note] start aborted — STT provider is Off");
                NoteError?.Invoke("STT provider is Off — pick one in Settings/Voice first");
                return new { ok = false, error = "stt-off" };
            }
            try
            {
                AppLogger.Log($"[WebDev:Note] STT preload | provider={v.SttProvider} model={v.SttWhisperModel} gpu={v.SttUseGpu}");
                var ready = await _noteStt.EnsureReadyAsync();
                if (!ready)
                {
                    NoteError?.Invoke("STT model not ready — open Settings → Voice and complete model setup");
                    _noteStt = null;
                    return new { ok = false, error = "stt-not-ready" };
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[WebDev:Note] STT preload failed: {ex.GetType().Name}: {ex.Message}");
                NoteError?.Invoke("STT init failed: " + ex.Message);
                _noteStt = null;
                return new { ok = false, error = ex.Message };
            }

            _noteSource = source;
            _noteCts = new CancellationTokenSource();
            _noteLanguage = string.IsNullOrWhiteSpace(v.SttLanguage) ? "auto" : v.SttLanguage;

            if (source == NoteSourceNames.SystemLoopback)
            {
                // ── Loopback path — no VAD; N-second chunks via a timer, each routed
                // through the same STT + diarize + emit path as an utterance.
                _noteLoopbackChunkSec = (opts.LoopbackChunkSec is int s && s > 0) ? Math.Clamp(s, 5, 120) : 30;
                var buffer = new PcmCaptureBuffer { BufferPcm = true };
                buffer.AmplitudeChanged += OnNoteAmplitude;
                IAudioFrameSource? lb = null;
                try
                {
                    lb = platform.OpenLoopback(opts.LoopbackDeviceId);
                    lb.FrameAvailable += buffer.Process;
                    lb.Start();
                }
                catch (Exception ex)
                {
                    try { lb?.Dispose(); } catch { }
                    AppLogger.Log($"[WebDev:Note] loopback capture failed: {ex.GetType().Name}: {ex.Message}");
                    NoteError?.Invoke("Loopback capture failed: " + ex.Message);
                    return new { ok = false, error = ex.Message };
                }
                _noteLoopback = buffer;
                _noteLoopbackSource = lb;

                AppLogger.Log($"[WebDev:Note] capture started | source=SystemLoopback chunkSec={_noteLoopbackChunkSec} lang={_noteLanguage} loopbackDevice='{opts.LoopbackDeviceId ?? ""}'");
                StartNotePartialTimer();
                StartNoteChunkTimer();
                return new { ok = true, capturing = true, sensitivity = effectiveSens, threshold, source };
            }

            // ── Microphone path. M0015 / 후속 진행 #2 — PreRoll / Hangover / cap come
            // from the active sensitivity profile so Loose lecture-tuning applies here too.
            var vadCfg = VoiceVadSettings.BuildVadConfig(v, isAgentMode: true);
            var vad = new VoiceActivitySegmenter
            {
                BufferPcm = true,
                Muted = v.MicMuted,
                VadThreshold = threshold,
                PreRollSeconds = vadCfg.PreRollSeconds,
                UtteranceHangoverFrames = vadCfg.UtteranceHangoverFrames,
                MaxUtteranceSeconds = vadCfg.MaxUtteranceSeconds,
            };
            vad.UtteranceStarted += OnNoteUtteranceStarted;
            vad.UtteranceEnded   += OnNoteUtteranceEnded;
            vad.AmplitudeChanged += OnNoteAmplitude;
            vad.SpeakingStateChanged += OnNoteSpeaking;
            _noteVad = vad;

            IAudioFrameSource? mic = null;
            try
            {
                mic = platform.OpenMicrophone(v.InputDeviceId);
                mic.FrameAvailable += vad.Process;
                mic.Start();
            }
            catch (Exception ex)
            {
                try { mic?.Dispose(); } catch { }
                _noteVad = null;
                AppLogger.Log($"[WebDev:Note] mic capture failed: {ex.GetType().Name}: {ex.Message}");
                NoteError?.Invoke("Mic capture failed: " + ex.Message);
                return new { ok = false, error = ex.Message };
            }
            _noteMicSource = mic;

            // The user's STT language goes with every utterance, as in Settings/Voice:
            // Whisper's "auto" detector is unreliable on short utterances.
            AppLogger.Log($"[WebDev:Note] capture started | source=Microphone sens={effectiveSens} threshold={threshold:F4} lang={_noteLanguage} muted={vad.Muted} device={v.InputDeviceId} platform={platform.Name}");

            StartNotePartialTimer();
            return new { ok = true, capturing = true, sensitivity = effectiveSens, threshold, source };
        }
        finally { _noteLock.Release(); }
    }

    public async Task StopNoteCaptureAsync()
    {
        await _noteLock.WaitAsync();
        try { TearDownNoteCaptureLocked(); }
        finally { _noteLock.Release(); }
    }

    public void PauseNoteCapture()
    {
        var c = _noteVad; if (c is not null) c.Muted = true;
    }

    public void ResumeNoteCapture()
    {
        var c = _noteVad; if (c is not null) c.Muted = false;
    }

    public void SetNoteSensitivity(int percent)
    {
        var c = _noteVad; if (c is null) return;
        // M0015 후속 진행 #1: Auto in voice-note context resolves to Loose, so a moved
        // slider doesn't silently revert to the Strict curve.
        var v = LoadVoiceSettings();
        c.VadThreshold = VoiceVadSettings.SensitivityToThreshold(percent, v, isAgentMode: true);
    }

    public bool IsNoteCapturing =>
        _noteMicSource is { IsCapturing: true }
        || _noteLoopbackSource is { IsCapturing: true };

    private void OnNoteUtteranceStarted()
    {
        // Seed the buffer with the pre-roll ring (~1 s) so the first consonant isn't
        // clipped — missing it was why short utterances produced empty STT results.
        _noteVad?.SeedBufferWithPreRoll();
        AppLogger.Log("[WebDev:Note] utterance started");
        NoteUtteranceStarted?.Invoke();
    }

    private void OnNoteSpeaking(bool isSpeaking) => NoteSpeaking?.Invoke(isSpeaking);

    private void OnNoteUtteranceEnded()
    {
        NoteUtteranceEnded?.Invoke();
        var vad = _noteVad;
        if (vad is null) return;
        var pcm = vad.ConsumePcmBuffer();
        // 16 kHz mono 16-bit = 32 KB/s, so < 8000 bytes ≈ < 250 ms — Whisper returns
        // junk on those and the timeline would fill with noise lines.
        if (pcm.Length < 8000)
        {
            AppLogger.Log($"[WebDev:Note] utterance dropped — too short ({pcm.Length} bytes)");
            return;
        }
        AppLogger.Log($"[WebDev:Note] utterance → STT | bytes={pcm.Length} lang={_noteLanguage}");
        _ = Task.Run(() => ProcessFinalChunkAsync(pcm, "utterance"));
    }

    /// <summary>
    /// The diarizer speaker labels come from — Sherpa (pyannote segmentation + 3D-Speaker
    /// embedding) per Settings/Diarization. Null when the provider is Off; tests turn it off.
    /// </summary>
    protected virtual ISpeakerDiarizer? CreateDiarizer(DiarizationSettings s)
        => s.Provider == DiarizationProviderNames.Off ? null : new SherpaSpeakerDiarizer(s);

    /// <summary>
    /// Get-or-create the cached note-side diarizer. Null when the provider is Off or the
    /// model files aren't present yet.
    /// </summary>
    private async Task<ISpeakerDiarizer?> GetReadyNoteDiarizerAsync(CancellationToken ct)
    {
        var s = LoadDiarizationSettings();
        if (s.Provider == DiarizationProviderNames.Off) return null;

        _noteDiarizer ??= CreateDiarizer(s);
        if (_noteDiarizer is null) return null;
        var ok = await _noteDiarizer.EnsureReadyAsync(null, ct).ConfigureAwait(false);
        return ok ? _noteDiarizer : null;
    }

    private static string MajoritySpeaker(IReadOnlyList<SpeakerSegment> segments, out int speakerId)
    {
        var totals = new Dictionary<int, double>();
        foreach (var s in segments)
        {
            totals.TryGetValue(s.SpeakerId, out var sum);
            totals[s.SpeakerId] = sum + s.DurationSec;
        }
        int bestId = -1;
        double bestDur = -1;
        foreach (var kv in totals)
            if (kv.Value > bestDur) { bestDur = kv.Value; bestId = kv.Key; }
        speakerId = bestId;
        return new SpeakerSegment(0, 0, bestId).SpeakerLabel;
    }

    // ── M0024 Phase 3 — note partial timer ───────────────────────────────

    private void StartNotePartialTimer()
    {
        StopNotePartialTimer();
        _notePartialLastBytes = 0;
        Interlocked.Exchange(ref _notePartialBusy, 0);
        // A threadpool timer: STT is CPU-bound and the events are marshalled by the bridge.
        _notePartialTimer = new Timer(OnNotePartialTick, null, NotePartialIntervalMs, NotePartialIntervalMs);
        AppLogger.Log($"[WebDev:Note-Partial] timer started, interval={NotePartialIntervalMs / 1000}s");
    }

    private void StopNotePartialTimer()
    {
        try { _notePartialTimer?.Dispose(); } catch { }
        _notePartialTimer = null;
    }

    private void OnNotePartialTick(object? _)
    {
        if (Interlocked.CompareExchange(ref _notePartialBusy, 1, 0) != 0) return;
        try
        {
            var stt = _noteStt;
            var ctsToken = _noteCts?.Token ?? CancellationToken.None;
            var lang = _noteLanguage;
            if (stt is null || ctsToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _notePartialBusy, 0);
                return;
            }

            // M0024 Phase 3.5 — peek whichever capture source is active.
            byte[] pcm;
            if (_noteLoopbackSource is { IsCapturing: true } && _noteLoopback is { } lb)
                pcm = lb.PeekPcmBuffer();
            else if (_noteVad is { } vad)
                pcm = vad.PeekPcmBuffer();
            else
            {
                Interlocked.Exchange(ref _notePartialBusy, 0);
                return;
            }

            if (pcm.Length < NotePartialMinBytes || pcm.Length == _notePartialLastBytes)
            {
                Interlocked.Exchange(ref _notePartialBusy, 0);
                return;
            }
            _notePartialLastBytes = pcm.Length;

            _ = Task.Run(async () =>
            {
                // Try-acquire without blocking: partials are best-effort previews; queuing
                // behind a chunk STT would build a native-inference backlog.
                if (!await _noteInferGate.WaitAsync(0).ConfigureAwait(false))
                {
                    Interlocked.Exchange(ref _notePartialBusy, 0);
                    return;
                }
                try
                {
                    var text = await stt.TranscribeAsync(pcm, lang, ctsToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(text)) return;
                    var trimmed = text.Trim();
                    if (WhisperHallucinationFilter.IsLikelyHallucination(trimmed)) return;
                    AppLogger.Log($"[WebDev:Note-Partial] ok | bytes={pcm.Length} chars={trimmed.Length} source={_noteSource}");
                    NoteTranscript?.Invoke(new NoteTranscriptInfo(trimmed, SpeakerId: null, SpeakerLabel: null, IsPartial: true));
                }
                catch (OperationCanceledException) { /* expected on STOP */ }
                catch (Exception ex)
                {
                    AppLogger.Log($"[WebDev:Note-Partial] tick failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    _noteInferGate.Release();
                    Interlocked.Exchange(ref _notePartialBusy, 0);
                }
            });
        }
        catch
        {
            Interlocked.Exchange(ref _notePartialBusy, 0);
        }
    }

    // ── M0024 Phase 3.5 — loopback chunk timer ───────────────────────────

    private void StartNoteChunkTimer()
    {
        StopNoteChunkTimer();
        Interlocked.Exchange(ref _noteChunkBusy, 0);
        var ms = _noteLoopbackChunkSec * 1000;
        _noteChunkTimer = new Timer(OnNoteChunkTick, null, ms, ms);
        AppLogger.Log($"[WebDev:Note-Chunk] timer started, interval={_noteLoopbackChunkSec}s");
    }

    private void StopNoteChunkTimer()
    {
        try { _noteChunkTimer?.Dispose(); } catch { }
        _noteChunkTimer = null;
    }

    /// <summary>
    /// The loopback path's "synthetic utterance end": drains the buffer every
    /// <see cref="_noteLoopbackChunkSec"/> seconds into the same STT + diarize + emit
    /// pipeline; NoteUtteranceEnded fires so the plugin UI mirrors an utterance cycle.
    /// </summary>
    private void OnNoteChunkTick(object? _)
    {
        var lb = _noteLoopback;
        var stt = _noteStt;
        if (lb is null || stt is null) return;
        if ((_noteCts?.Token ?? CancellationToken.None).IsCancellationRequested) return;

        // Re-entrancy guard: never start a second concurrent inference while the previous
        // chunk is still transcribing. Drain anyway to keep the buffer bounded — losing one
        // chunk's transcript beats a native-concurrency crash or unbounded growth.
        if (Interlocked.CompareExchange(ref _noteChunkBusy, 1, 0) != 0)
        {
            var dropped = lb.ConsumePcmBuffer();
            _notePartialLastBytes = 0;
            AppLogger.Log($"[WebDev:Note-Chunk] busy — previous chunk still processing; dropped {dropped.Length} bytes ({dropped.Length / 32_000.0:F1}s)");
            return;
        }

        var pcm = lb.ConsumePcmBuffer();
        if (pcm.Length < 8000)
        {
            AppLogger.Log($"[WebDev:Note-Chunk] empty chunk skipped (bytes={pcm.Length})");
            Interlocked.Exchange(ref _noteChunkBusy, 0);
            return;
        }
        // The buffer was just drained; the next partial compares against an empty one.
        _notePartialLastBytes = 0;

        AppLogger.Log($"[WebDev:Note-Chunk] tick → STT | bytes={pcm.Length} ({pcm.Length / 32_000.0:F1}s)");
        try { NoteUtteranceEnded?.Invoke(); } catch { }
        _ = Task.Run(async () =>
        {
            try { await ProcessFinalChunkAsync(pcm, "loopback-chunk"); }
            finally { Interlocked.Exchange(ref _noteChunkBusy, 0); }
        });
    }

    /// <summary>Shared STT + diarize + emit used by the mic utterance and the loopback chunk.</summary>
    private async Task ProcessFinalChunkAsync(byte[] pcm, string label)
    {
        var stt = _noteStt;
        var ctsToken = _noteCts?.Token ?? CancellationToken.None;
        var lang = _noteLanguage;
        if (stt is null) return;

        // Serialize native inference — see _noteInferGate.
        try { await _noteInferGate.WaitAsync(ctsToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            var text = await stt.TranscribeAsync(pcm, lang, ctsToken);
            if (ctsToken.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                AppLogger.Log($"[WebDev:Note-{label}] STT returned empty | bytes={pcm.Length} lang={lang}");
                return;
            }
            var trimmed = text.Trim();
            if (WhisperHallucinationFilter.IsLikelyHallucination(trimmed))
            {
                AppLogger.Log($"[WebDev:Note-{label}] hallucination dropped | chars={trimmed.Length}");
                return;
            }

            int? speakerId = null;
            string? speakerLabel = null;
            try
            {
                var diar = await GetReadyNoteDiarizerAsync(ctsToken).ConfigureAwait(false);
                if (diar is not null)
                {
                    var dSettings = LoadDiarizationSettings();
                    var dResult = await diar.DiarizeAsync(pcm, dSettings.ExpectedSpeakerCount, ctsToken).ConfigureAwait(false);
                    if (dResult.Segments.Count > 0)
                    {
                        var lbl = MajoritySpeaker(dResult.Segments, out var id);
                        speakerId = id; speakerLabel = lbl;
                        AppLogger.Log($"[WebDev:Note-Diar:{label}] segments={dResult.Segments.Count} speakers={dResult.SpeakerCount} pick={lbl} inferMs={dResult.InferenceTime.TotalMilliseconds:F0}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception dx)
            {
                AppLogger.Log($"[WebDev:Note-Diar:{label}] failed (continuing without speaker): {dx.GetType().Name}: {dx.Message}");
            }

            AppLogger.Log($"[WebDev:Note-{label}] STT ok | chars={trimmed.Length} speaker={(speakerLabel ?? "—")}");
            NoteTranscript?.Invoke(new NoteTranscriptInfo(trimmed, speakerId, speakerLabel, IsPartial: false));
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Note-{label}] STT transcribe failed: {ex.GetType().Name}: {ex.Message}");
            NoteError?.Invoke("Transcribe failed: " + ex.Message);
        }
        finally
        {
            _noteInferGate.Release();
        }
    }

    // Throttled amplitude relay — sources fire ~20 Hz (mic) to ~100 Hz (loopback), more
    // than the JS meter needs. Coalesced to ~10 Hz to keep bridge traffic light.
    private long _lastAmpTick;
    private void OnNoteAmplitude(float rms)
    {
        var now = Environment.TickCount64;
        if (now - _lastAmpTick < 90) return;
        _lastAmpTick = now;
        NoteAmplitude?.Invoke(rms);
    }

    private void TearDownNoteCaptureLocked()
    {
        StopNotePartialTimer();
        StopNoteChunkTimer();
        var mic = _noteMicSource; _noteMicSource = null;
        var vad = _noteVad; _noteVad = null;
        if (mic is not null)
        {
            if (vad is not null) mic.FrameAvailable -= vad.Process;
            try { mic.Stop(); } catch { }
            try { mic.Dispose(); } catch { }
        }
        if (vad is not null)
        {
            vad.UtteranceStarted -= OnNoteUtteranceStarted;
            vad.UtteranceEnded   -= OnNoteUtteranceEnded;
            vad.AmplitudeChanged -= OnNoteAmplitude;
            vad.SpeakingStateChanged -= OnNoteSpeaking;
            vad.EndOfStream();
        }
        // M0024 Phase 3.5 — loopback teardown mirrors mic.
        var lbSource = _noteLoopbackSource; _noteLoopbackSource = null;
        var lb = _noteLoopback; _noteLoopback = null;
        if (lbSource is not null)
        {
            if (lb is not null) lbSource.FrameAvailable -= lb.Process;
            try { lbSource.Stop(); } catch { }
            try { lbSource.Dispose(); } catch { }
        }
        if (lb is not null) lb.AmplitudeChanged -= OnNoteAmplitude;
        try { _noteCts?.Cancel(); } catch { }
        _noteCts?.Dispose();
        _noteCts = null;
        _noteStt = null;
        // The diarizer is kept across sessions so a second Start doesn't reload ~50 MB of
        // models; DisposeNote frees it.
        AppLogger.Log($"[WebDev:Note] capture stopped (was source={_noteSource})");
    }

    private void DisposeNote()
    {
        try { TearDownNoteCaptureLocked(); } catch { }
        // P1 (2026-07-15) — release the cached diarizer's native ONNX sessions; TearDown
        // already cancelled _noteCts, so no inference is in flight against it.
        var diar = _noteDiarizer; _noteDiarizer = null;
        try { diar?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _noteLock.Dispose();
    }
}

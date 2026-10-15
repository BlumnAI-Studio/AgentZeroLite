using Agent.Common.Voice.Streams;

namespace Agent.Common.Audio;

/// <summary>
/// Energy-based voice activity detection over 16 kHz mono PCM16 frames, moved down from the
/// WPF host's <c>VoiceCaptureService</c> (ported there from the AgentWin origin) so every
/// host segments speech the same way. Feed it with <see cref="Process"/> from any
/// <see cref="IAudioFrameSource"/>; it owns no device.
///
/// Two event streams:
/// <list type="bullet">
/// <item><see cref="AmplitudeChanged"/> — RMS (0..1) per frame, for a level meter;</item>
/// <item><see cref="SpeakingStateChanged"/> — debounced bool (≈300 ms), for a mic indicator.</item>
/// </list>
/// The dual design (frame-level + utterance-level) lets a speaker pause mid-sentence
/// ("I'm a dev… eloper") without splitting the utterance: the utterance machine waits for
/// <see cref="UtteranceHangoverFrames"/> of continuous silence (~2 s at 50 ms frames) before
/// declaring the segment complete.
///
/// <para>Frame counts assume the microphone's 50 ms frames; a source with other frame
/// sizes shifts the hangover times proportionally.</para>
/// </summary>
public sealed class VoiceActivitySegmenter
{
    private bool _isSpeaking;
    private int _silenceFrames;
    private bool _inUtterance;
    private int _utteranceSilenceFrames;

    private readonly List<byte> _pcmBuffer = new();
    private readonly Queue<byte[]> _preRollRing = new();
    private long _preRollBytes;

    /// <summary>Amplitude threshold (0~1). Default 0.08 (8%).</summary>
    public float VadThreshold { get; set; } = 0.08f;

    /// <summary>UI debounce — short (≈300 ms) so the speaking indicator feels responsive.</summary>
    public int SilenceHangoverFrames { get; set; } = 6;

    /// <summary>Utterance boundary — 40 frames ≈ 2 s at 50 ms per buffer.</summary>
    public int UtteranceHangoverFrames { get; set; } = 40;

    /// <summary>
    /// Hard cap on a single utterance's length, in seconds. 0 (default) disables it.
    /// M0015 / 후속 진행 #2 — at near-floor threshold + ambient noise the hangover never
    /// trips and utterances balloon to tens of seconds; the cap force-emits so the STT
    /// queue keeps moving.
    /// </summary>
    public double MaxUtteranceSeconds { get; set; }

    /// <summary>When true, utterance audio accumulates for <see cref="ConsumePcmBuffer"/>.</summary>
    public bool BufferPcm { get; set; }

    /// <summary>
    /// Freeze the VAD state machine and buffering (e.g. while TTS plays back, so the
    /// speaker output isn't fed into the next transcription). The level meter still moves.
    /// </summary>
    public bool Muted { get; set; }

    /// <summary>Pre-trigger audio kept (default 1 s) — prevents clipping the first consonant.</summary>
    public double PreRollSeconds { get; set; } = 1.0;

    public event Action<float>? AmplitudeChanged;
    public event Action<bool>? SpeakingStateChanged;
    public event Action? UtteranceStarted;
    public event Action? UtteranceEnded;

    /// <summary>Per-frame PCM + RMS for streaming consumers; suppressed while <see cref="Muted"/>.</summary>
    public event Action<MicFrame>? FrameAvailable;

    public bool IsSpeaking => _isSpeaking;

    public byte[] ConsumePcmBuffer()
    {
        lock (_pcmBuffer)
        {
            var copy = _pcmBuffer.ToArray();
            _pcmBuffer.Clear();
            return copy;
        }
    }

    /// <summary>Non-destructive snapshot of the current buffer (partial-transcript previews).</summary>
    public byte[] PeekPcmBuffer()
    {
        lock (_pcmBuffer)
        {
            return _pcmBuffer.Count == 0 ? Array.Empty<byte>() : _pcmBuffer.ToArray();
        }
    }

    /// <summary>
    /// Reset the segment buffer and seed it with the pre-roll ring (the last
    /// <see cref="PreRollSeconds"/> of audio). Call on <see cref="UtteranceStarted"/> so the
    /// segment begins with the audio preceding the trigger. <b>Clears first</b>: leftover
    /// between-utterance audio once inflated segments by 5–15 s and confused STT.
    /// </summary>
    public void SeedBufferWithPreRoll()
    {
        if (!BufferPcm) return;
        lock (_pcmBuffer)
        {
            _pcmBuffer.Clear();
            lock (_preRollRing)
            {
                foreach (var chunk in _preRollRing)
                    _pcmBuffer.AddRange(chunk);
            }
        }
    }

    /// <summary>
    /// Forget the speaking/utterance state at the start of a capture session. Does not raise
    /// events and keeps the buffers — exactly what a restart of the WPF capture did.
    /// </summary>
    public void Reset()
    {
        _isSpeaking = false;
        _silenceFrames = 0;
        _inUtterance = false;
        _utteranceSilenceFrames = 0;
    }

    /// <summary>Capture stopped: drop a raised speaking flag so the indicator does not stick.</summary>
    public void EndOfStream()
    {
        if (!_isSpeaking) return;
        _isSpeaking = false;
        SpeakingStateChanged?.Invoke(false);
    }

    /// <summary>RMS (0..1) of a 16-bit little-endian PCM buffer.</summary>
    public static float Rms(byte[] pcm, int count)
    {
        var samples = count / 2;
        if (samples == 0) return 0f;
        double sumSquares = 0;
        for (var i = 0; i + 1 < count; i += 2)
        {
            short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var norm = sample / 32768.0;
            sumSquares += norm * norm;
        }
        return (float)Math.Sqrt(sumSquares / samples);
    }

    /// <summary>One frame of 16 kHz mono PCM16, in capture order.</summary>
    public void Process(byte[] chunk)
    {
        if (chunk.Length / 2 == 0) return;

        // Pre-roll ring updates every frame regardless of utterance state — a sliding
        // window of the last PreRollSeconds. Skipped while muted so TTS playback
        // doesn't poison the next utterance's pre-roll.
        if (!Muted)
        {
            lock (_preRollRing)
            {
                _preRollRing.Enqueue(chunk);
                _preRollBytes += chunk.Length;
                var maxBytes = (long)(PreRollSeconds * 16_000 * 2);
                while (_preRollBytes > maxBytes && _preRollRing.Count > 0)
                {
                    var old = _preRollRing.Dequeue();
                    _preRollBytes -= old.Length;
                }
            }
        }
        // (The buffer used to accumulate every frame here unconditionally, so
        //  ConsumePcmBuffer returned 9–17 s of audio for 2 s of speech. It now fills
        //  only inside the VAD transitions below.)

        var rms = Rms(chunk, chunk.Length);
        AmplitudeChanged?.Invoke(rms);

        if (FrameAvailable is not null && !Muted)
        {
            try { FrameAvailable.Invoke(new MicFrame(chunk, rms)); }
            catch (Exception ex) { AppLogger.Log($"[Voice] FrameAvailable handler threw: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Muted: keep the meter alive but freeze the state machine so releasing the mute
        // doesn't fire UtteranceStarted on stale state.
        if (Muted)
        {
            if (_isSpeaking)
            {
                _isSpeaking = false;
                SpeakingStateChanged?.Invoke(false);
            }
            _silenceFrames = 0;
            _utteranceSilenceFrames = 0;
            _inUtterance = false;
            return;
        }

        var above = rms >= VadThreshold;

        if (above)
        {
            _silenceFrames = 0;
            if (!_isSpeaking)
            {
                _isSpeaking = true;
                SpeakingStateChanged?.Invoke(true);
            }
        }
        else if (_isSpeaking)
        {
            _silenceFrames++;
            if (_silenceFrames >= SilenceHangoverFrames)
            {
                _isSpeaking = false;
                SpeakingStateChanged?.Invoke(false);
            }
        }

        if (above)
        {
            _utteranceSilenceFrames = 0;
            if (!_inUtterance)
            {
                // Utterance starts. SeedBufferWithPreRoll (called by the handler) seeds
                // the buffer with the ring, which already holds this trigger chunk — so it
                // is not added here, or the seam would carry it twice.
                _inUtterance = true;
                try { UtteranceStarted?.Invoke(); }
                catch (Exception ex) { AppLogger.Log($"[Voice] UtteranceStarted handler threw: {ex.GetType().Name}: {ex.Message}"); }
            }
            else if (BufferPcm)
            {
                lock (_pcmBuffer) { _pcmBuffer.AddRange(chunk); }
            }
            ForceEmitIfDurationCapReached();
        }
        else if (_inUtterance)
        {
            _utteranceSilenceFrames++;
            // The trailing silence belongs to the utterance — STT sees up to and
            // including this frame.
            if (BufferPcm)
            {
                lock (_pcmBuffer) { _pcmBuffer.AddRange(chunk); }
            }
            if (_utteranceSilenceFrames >= UtteranceHangoverFrames)
            {
                _inUtterance = false;
                try { UtteranceEnded?.Invoke(); }
                catch (Exception ex) { AppLogger.Log($"[Voice] UtteranceEnded handler threw: {ex.GetType().Name}: {ex.Message}"); }
            }
            else
            {
                ForceEmitIfDurationCapReached();
            }
        }
        // else: outside an utterance, below threshold — nothing accumulates.
    }

    private void ForceEmitIfDurationCapReached()
    {
        if (!_inUtterance || !BufferPcm) return;
        var capSeconds = MaxUtteranceSeconds;
        if (capSeconds <= 0) return;
        var capBytes = (long)(capSeconds * 16_000 * 2);
        long currentBytes;
        lock (_pcmBuffer) { currentBytes = _pcmBuffer.Count; }
        if (currentBytes < capBytes) return;
        _inUtterance = false;
        _utteranceSilenceFrames = 0;
        AppLogger.Log($"[Voice] utterance capped at {capSeconds:0.#}s ({currentBytes} bytes) — force-emitted");
        try { UtteranceEnded?.Invoke(); }
        catch (Exception ex) { AppLogger.Log($"[Voice] UtteranceEnded handler threw: {ex.GetType().Name}: {ex.Message}"); }
    }
}

/// <summary>
/// Continuous capture with no VAD (system audio): every frame raises its RMS and, when
/// <see cref="BufferPcm"/> is on, accumulates for <see cref="ConsumePcmBuffer"/>. The
/// loopback half of what WPF's <c>LoopbackCaptureService</c> did around WASAPI.
/// </summary>
public sealed class PcmCaptureBuffer
{
    private readonly List<byte> _pcm = new();

    public bool BufferPcm { get; set; }
    public bool Muted { get; set; }

    public event Action<float>? AmplitudeChanged;

    /// <summary>The frame itself, for live consumers (spectrum, rolling inference).</summary>
    public event Action<byte[]>? PcmFrameAvailable;

    public void Process(byte[] chunk)
    {
        if (Muted || chunk.Length == 0) return;
        if (chunk.Length >= 2)
        {
            try { AmplitudeChanged?.Invoke(VoiceActivitySegmenter.Rms(chunk, chunk.Length)); }
            catch (Exception ex) { AppLogger.Log($"[Music-Loopback] Amplitude handler threw: {ex.Message}"); }
        }
        if (BufferPcm)
        {
            lock (_pcm) _pcm.AddRange(chunk);
        }
        if (PcmFrameAvailable is not null)
        {
            try { PcmFrameAvailable.Invoke(chunk); }
            catch (Exception ex) { AppLogger.Log($"[Music-Loopback] PcmFrame handler threw: {ex.Message}"); }
        }
    }

    public byte[] ConsumePcmBuffer()
    {
        lock (_pcm)
        {
            var copy = _pcm.ToArray();
            _pcm.Clear();
            return copy;
        }
    }

    public byte[] PeekPcmBuffer()
    {
        lock (_pcm) return _pcm.Count == 0 ? Array.Empty<byte>() : _pcm.ToArray();
    }

    public void Clear()
    {
        lock (_pcm) _pcm.Clear();
    }
}

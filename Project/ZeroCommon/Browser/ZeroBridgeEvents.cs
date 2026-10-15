using Agent.Common.Telemetry;

namespace Agent.Common.Browser;

/// <summary>
/// The host-pushed events every host sends the same way — voice-note
/// (<c>note.*</c>), live music (<c>music.*</c>), the MP3 scan job
/// (<c>mp3.scan.progress</c> / <c>mp3.tracks</c> / <c>mp3.scan.done</c>) and the
/// three telemetry collectors' ticks. Payload shapes are part of the plugin
/// contract (<c>zero-bridge.js</c>), which is why they live here once instead of
/// in each host's bridge.
///
/// Subscribes on construction, unsubscribes on <see cref="Dispose"/>; one per
/// plugin web view, like the WPF bridge it came from.
/// </summary>
public sealed class ZeroBridgeEvents : IDisposable
{
    private readonly ZeroBrowserCore _core;
    private readonly Action<string, object> _post;

    public ZeroBridgeEvents(ZeroBrowserCore core, Action<string, object> post)
    {
        _core = core;
        _post = post;

        _core.NoteTranscript        += OnNoteTranscript;
        _core.NoteUtteranceStarted  += OnNoteUtteranceStarted;
        _core.NoteUtteranceEnded    += OnNoteUtteranceEnded;
        _core.NoteError             += OnNoteError;
        _core.NoteAmplitude         += OnNoteAmplitude;
        _core.NoteSpeaking          += OnNoteSpeaking;
        // M0025 — Agent Band live music ticks.
        _core.MusicTick             += OnMusicTick;
        _core.MusicAmplitude        += OnMusicAmplitude;
        _core.MusicSpectrum         += OnMusicSpectrum;
        // M0029 — Agent Band MP3 scan job stream.
        _core.Mp3ScanProgress += OnMp3ScanProgress;
        _core.Mp3TrackBatch   += OnMp3TrackBatch;
        _core.Mp3ScanDone     += OnMp3ScanDone;

        TokenUsageCollector.Instance.TickCompleted += OnTokenTick;
        TokenRemainingCollector.Instance.TickCompleted += OnTokenRemainingTick;
        SessionHeartbeatCollector.Instance.TickCompleted += OnSessionHeartbeatTick;
    }

    public void Dispose()
    {
        try { _core.NoteTranscript       -= OnNoteTranscript;      } catch { }
        try { _core.NoteUtteranceStarted -= OnNoteUtteranceStarted;} catch { }
        try { _core.NoteUtteranceEnded   -= OnNoteUtteranceEnded;  } catch { }
        try { _core.NoteError            -= OnNoteError;           } catch { }
        try { _core.NoteAmplitude        -= OnNoteAmplitude;       } catch { }
        try { _core.NoteSpeaking         -= OnNoteSpeaking;        } catch { }
        try { _core.MusicTick            -= OnMusicTick;           } catch { }
        try { _core.MusicAmplitude       -= OnMusicAmplitude;      } catch { }
        try { _core.MusicSpectrum        -= OnMusicSpectrum;       } catch { }
        try { _core.Mp3ScanProgress -= OnMp3ScanProgress; } catch { }
        try { _core.Mp3TrackBatch   -= OnMp3TrackBatch;   } catch { }
        try { _core.Mp3ScanDone     -= OnMp3ScanDone;     } catch { }
        try { TokenUsageCollector.Instance.TickCompleted -= OnTokenTick; } catch { }
        try { TokenRemainingCollector.Instance.TickCompleted -= OnTokenRemainingTick; } catch { }
        try { SessionHeartbeatCollector.Instance.TickCompleted -= OnSessionHeartbeatTick; } catch { }
    }

    /// <summary>
    /// M0024 Phase 3 — speaker fields and a partial flag. Backward compatible: older
    /// plugins ignore them; newer ones show <c>speakerLabel</c> and dim <c>isPartial</c>.
    /// </summary>
    private void OnNoteTranscript(NoteTranscriptInfo info) =>
        _post("note.transcript", new
        {
            text         = info.Text,
            speakerId    = info.SpeakerId,
            speakerLabel = info.SpeakerLabel,
            isPartial    = info.IsPartial,
        });
    private void OnNoteUtteranceStarted()             => _post("note.utterance-start", new { });
    private void OnNoteUtteranceEnded()               => _post("note.utterance-end", new { });
    private void OnNoteError(string message)          => _post("note.error", new { message });
    private void OnNoteSpeaking(bool isSpeaking)      => _post("note.speaking", new { speaking = isSpeaking });
    // The threshold rides every amplitude tick so the plugin can draw the
    // "voice must be this loud" line on its meter.
    private void OnNoteAmplitude(float rms)           => _post("note.amplitude", new { rms, threshold = _core.CurrentVadThreshold });

    private void OnMusicTick(MusicTickInfo t) =>
        _post("music.tick", new
        {
            labels = t.Labels.Select(l => new { name = l.Name, score = l.Score, index = l.Index }).ToArray(),
            spectrum = t.Spectrum,
            frames = t.Frames,
            bins = t.Bins,
            inferMs = t.InferMs,
        });
    private void OnMusicAmplitude(float rms) => _post("music.amplitude", new { rms });
    // ~30 Hz, decoupled from the 1.5 s inference tick so the visualiser moves every frame.
    private void OnMusicSpectrum(float[] bars) => _post("music.spectrum", new { spectrum = bars });

    private void OnTokenTick(TokenUsageCollector.TickSummary s)
        => _post("tokens.tick", new
        {
            filesScanned = s.FilesScanned,
            rowsInserted = s.RowsInserted,
            claudeRows   = s.ClaudeRows,
            codexRows    = s.CodexRows,
            finishedAt   = s.FinishedAt,
            error        = s.Error,
        });

    private void OnTokenRemainingTick(TokenRemainingCollector.TickSummary s)
        => _post("tokens.remaining.tick", new
        {
            filesScanned        = s.FilesScanned,
            rowsInserted        = s.RowsInserted,
            rowsSkippedSamePct  = s.RowsSkippedSamePercent,
            finishedAt          = s.FinishedAtUtc,
            error               = s.Error,
        });

    private void OnSessionHeartbeatTick(SessionHeartbeatCollector.TickSummary s)
        => _post("tokens.remaining.activeSessions.tick", new
        {
            filesScanned = s.FilesScanned,
            rowsUpserted = s.RowsUpserted,
            rowsPruned   = s.RowsPruned,
            finishedAt   = s.FinishedAtUtc,
            error        = s.Error,
        });

    // The scan runs as a background job on the host; these events are what
    // makes the playlist update INCREMENTALLY while the scan is still
    // running (each upserted track is playable the moment it arrives).
    private void OnMp3ScanProgress(Mp3ScanProgressInfo p) => _post("mp3.scan.progress", p);
    // M0030 후속#1 — 트랙 upsert는 배치(~2s/100건)로만 UI에 도달한다.
    private void OnMp3TrackBatch(IReadOnlyList<Mp3TrackDto> tracks) => _post("mp3.tracks", new { tracks });
    private void OnMp3ScanDone(Mp3ScanDoneInfo d)         => _post("mp3.scan.done", d);
}

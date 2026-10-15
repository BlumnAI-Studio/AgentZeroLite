using System.IO;
using System.Text.Json;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agent.Common.Telemetry;

/// <summary>
/// Reads per-account snapshot files written by the AgentZero statusLine
/// wrapper and persists state-changes to <see cref="AppDbContext"/>.
///
/// Snapshot format (one JSON file per account, written by the wrapper on
/// every Claude Code statusline tick):
///   {
///     "account":  "claude-pel3",
///     "writtenAt":"2026-05-04T17:48:12.123Z",
///     "model":    "Opus 4.7 (1M context)",
///     "fiveHour": { "usedPercentage": 19, "resetsAt": 1714829292 },
///     "sevenDay": { "usedPercentage": 9,  "resetsAt": 1715347692 }
///   }
///
/// Default tick: 30 s. On each tick:
///   1. enumerate %LOCALAPPDATA%\AgentZeroLite\cc-hud-snapshots\*.json
///   2. for each file, load + parse + look up latest existing row for
///      (AccountKey, Model)
///   3. INSERT only if (5h%, 7d%, resetsAt) differs (dedupe — operator's
///      "마지막 값이 중요" requirement: latest snapshot in DB matches
///      reality, but identical ticks don't bloat the table)
/// </summary>
public sealed class TokenRemainingCollector
{
    public static TokenRemainingCollector Instance { get; } = new();

    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;
    private bool _ticking;
    private int _compacted;

    public static TimeSpan DefaultInterval { get; } = TimeSpan.FromSeconds(30);
    public TimeSpan PollInterval { get; private set; } = DefaultInterval;

    public DateTime? LastTickUtc { get; private set; }
    public int LastFilesScanned { get; private set; }
    public int LastRowsInserted { get; private set; }
    public int LastRowsSkippedSamePercent { get; private set; }
    public string? LastError { get; private set; }

    public event Action<TickSummary>? TickCompleted;

    public sealed record TickSummary(
        int FilesScanned,
        int RowsInserted,
        int RowsSkippedSamePercent,
        DateTime FinishedAtUtc,
        string? Error);

    public static string SnapshotsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite",
        "cc-hud-snapshots");

    public void Start(TimeSpan? interval = null)
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            PollInterval = interval ?? DefaultInterval;
            _cts = new CancellationTokenSource();
            // First fire after 5 s — same stagger pattern as
            // TokenUsageCollector so startup isn't dominated by both
            // collectors firing simultaneously.
            _timer = new System.Threading.Timer(OnTick, null, TimeSpan.FromSeconds(5), PollInterval);
            AppLogger.Log($"[TokenRemainingCollector] started (interval={PollInterval.TotalSeconds:N0}s, dir={SnapshotsDir})");
        }
    }

    public void Stop()
    {
        Task running;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _cts?.Cancel();
            running = _running;
        }
        try { running.Wait(TimeSpan.FromSeconds(5)); } catch { /* best-effort */ }
        AppLogger.Log("[TokenRemainingCollector] stopped");
    }

    public Task<TickSummary> TickNowAsync(CancellationToken ct = default)
        => Task.Run(() => RunTick(ct), ct);

    private void OnTick(object? _)
    {
        lock (_gate)
        {
            if (_ticking) return;
            _ticking = true;
            _running = Task.Run(() =>
            {
                try { RunTick(_cts?.Token ?? CancellationToken.None); }
                finally { lock (_gate) _ticking = false; }
            });
        }
    }

    private TickSummary RunTick(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        int files = 0, inserted = 0, skipped = 0;
        string? error = null;

        try
        {
            if (!Directory.Exists(SnapshotsDir))
            {
                // Nothing installed yet — no-op tick.
                var emptySummary = new TickSummary(0, 0, 0, DateTime.UtcNow, null);
                LastTickUtc = emptySummary.FinishedAtUtc;
                LastFilesScanned = 0; LastRowsInserted = 0; LastRowsSkippedSamePercent = 0;
                LastError = null;
                try { TickCompleted?.Invoke(emptySummary); } catch { }
                return emptySummary;
            }

            using var db = new AppDbContext();
            db.Database.EnsureCreated();

            // Once per process: drop the copies the old dedup rule left behind.
            if (Interlocked.Exchange(ref _compacted, 1) == 0) CompactDuplicates(db);

            // v3 layout: snapshots live at SnapshotsDir/{account}/{sessionId}.json
            // (one file per session). v2 wrote SnapshotsDir/{account}.json
            // (flat) — wrapper v3 deletes those on first tick, but if the
            // user's wrapper hasn't upgraded yet we still want to see them.
            // Recurse so both layouts work.
            var snapshots = new List<SnapshotPayload>();
            foreach (var file in Directory.EnumerateFiles(SnapshotsDir, "*.json", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;
                files++;
                if (TryReadSnapshot(file) is { } snap) snapshots.Add(snap);
            }

            var inserts = PlanInserts(snapshots, (account, model) => db.TokenRemainingObservations
                .Where(o => o.AccountKey == account && o.Model == model)
                .OrderByDescending(o => o.ObservedAtUtc)
                .Select(o => new LatestState(o.FiveHourPercent, o.FiveHourResetsAtUtc,
                    o.SevenDayPercent, o.SevenDayResetsAtUtc, o.ObservedAtUtc))
                .FirstOrDefault(), out skipped);

            foreach (var snap in inserts)
            {
                db.TokenRemainingObservations.Add(new TokenRemainingObservation
                {
                    Vendor              = "claude",
                    AccountKey          = snap.AccountKey,
                    Model               = snap.Model,
                    FiveHourPercent     = snap.FiveHourPercent,
                    FiveHourResetsAtUtc = snap.FiveHourResetsAtUtc,
                    SevenDayPercent     = snap.SevenDayPercent,
                    SevenDayResetsAtUtc = snap.SevenDayResetsAtUtc,
                    ObservedAtUtc       = snap.WrittenAtUtc,
                });
            }
            inserted = inserts.Count;

            db.SaveChanges();
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            AppLogger.LogError("[TokenRemainingCollector] tick failed", ex);
        }

        var summary = new TickSummary(files, inserted, skipped, DateTime.UtcNow, error);
        LastTickUtc = summary.FinishedAtUtc;
        LastFilesScanned = files;
        LastRowsInserted = inserted;
        LastRowsSkippedSamePercent = skipped;
        LastError = error;

        try { TickCompleted?.Invoke(summary); } catch { /* listener errors don't kill the collector */ }
        AppLogger.Log($"[TokenRemainingCollector] tick: files={files} +rows={inserted} skipped={skipped} elapsed={(DateTime.UtcNow - started).TotalSeconds:N1}s err={(error ?? "none")}");
        return summary;
    }

    private static bool NotSameDateTime(DateTime? a, DateTime? b)
    {
        if (a is null && b is null) return false;
        if (a is null || b is null) return true;
        // Compare to second precision — wrapper writes resetsAt as epoch
        // seconds, so sub-second diffs are noise.
        return Math.Abs((a.Value - b.Value).TotalSeconds) > 1.0;
    }

    /// <summary>The newest stored state of one (account, model) — what a snapshot is compared with.</summary>
    internal sealed record LatestState(
        int FiveHourPercent,
        DateTime? FiveHourResetsAtUtc,
        int SevenDayPercent,
        DateTime? SevenDayResetsAtUtc,
        DateTime ObservedAtUtc);

    /// <summary>
    /// Which snapshots become rows. A snapshot is a state change only if it is
    /// <b>newer</b> than the newest stored state and differs from it; snapshots are
    /// walked oldest first, each accepted one becoming the state the next is compared
    /// with.
    ///
    /// <para>Measured (2026-10-15): the old rule compared every file with the newest
    /// row but ignored the file's age. The wrapper keeps one file per session and
    /// finished sessions' files stay, so every stale file whose numbers differed from
    /// the live session's was inserted again on every 30 s tick — 3,717,787 rows for
    /// 1,181 distinct snapshots, one of them 25,640 times, and the widget's queries
    /// over that table froze the UI for 30 s.</para>
    /// </summary>
    internal static List<SnapshotPayload> PlanInserts(
        IEnumerable<SnapshotPayload> snapshots,
        Func<string, string, LatestState?> latestStored,
        out int skipped)
    {
        skipped = 0;
        var result = new List<SnapshotPayload>();
        var latest = new Dictionary<(string, string), LatestState?>();
        foreach (var snap in snapshots.OrderBy(s => s.WrittenAtUtc))
        {
            var key = (snap.AccountKey, snap.Model);
            if (!latest.TryGetValue(key, out var current))
                latest[key] = current = latestStored(snap.AccountKey, snap.Model);

            if (current is not null && snap.WrittenAtUtc <= current.ObservedAtUtc)
            {
                skipped++; // already recorded, or an older session's leftover
                continue;
            }

            bool changed = current is null
                || current.FiveHourPercent != snap.FiveHourPercent
                || current.SevenDayPercent != snap.SevenDayPercent
                || NotSameDateTime(current.FiveHourResetsAtUtc, snap.FiveHourResetsAtUtc)
                || NotSameDateTime(current.SevenDayResetsAtUtc, snap.SevenDayResetsAtUtc);
            if (!changed) { skipped++; continue; }

            result.Add(snap);
            latest[key] = new LatestState(snap.FiveHourPercent, snap.FiveHourResetsAtUtc,
                snap.SevenDayPercent, snap.SevenDayResetsAtUtc, snap.WrittenAtUtc);
        }
        return result;
    }

    /// <summary>
    /// Delete rows that repeat an earlier row exactly (same account, model, time and
    /// numbers), keeping the first. Cheap once the table is clean; the first run on a
    /// table the old rule inflated removes nearly all of it — measured on a copy of a
    /// real database: 3,716,606 of 3,717,787 rows, 660 MB → 64 MB after VACUUM.
    /// Deleted in batches so the write lock is released between them (one statement
    /// held it for 31 s, stalling every other writer of the shared database).
    /// </summary>
    internal static int CompactDuplicates(AppDbContext db, int batchSize = 100_000)
    {
        var table = db.Model.FindEntityType(typeof(TokenRemainingObservation))?.GetTableName()
                    ?? "TokenRemainingObservations";
        var started = DateTime.UtcNow;
        var removed = 0;
        try
        {
            // The keep-list lives in a TEMP table, which exists per connection — hold one open.
            db.Database.OpenConnection();
            db.Database.ExecuteSqlRaw("CREATE TEMP TABLE IF NOT EXISTS tr_keep (Id INTEGER PRIMARY KEY)");
            db.Database.ExecuteSqlRaw("DELETE FROM tr_keep");
            var fill = $"INSERT INTO tr_keep SELECT MIN(\"Id\") FROM \"{table}\" GROUP BY \"AccountKey\", \"Model\", " +
                       "\"ObservedAtUtc\", \"FiveHourPercent\", \"SevenDayPercent\", \"FiveHourResetsAtUtc\", \"SevenDayResetsAtUtc\"";
            db.Database.ExecuteSqlRaw(fill);

            // Walk the primary key in ranges: each statement touches only its slice, so
            // no batch rescans what the previous ones already cleared.
            var bounds = db.TokenRemainingObservations
                .GroupBy(_ => 1)
                .Select(g => new { Min = g.Min(o => o.Id), Max = g.Max(o => o.Id) })
                .FirstOrDefault();
            var step = Math.Max(1, batchSize);
            for (var lo = bounds?.Min ?? 1; bounds is not null && lo <= bounds.Max; lo += step)
            {
                var hi = lo + step - 1;
                removed += db.Database.ExecuteSqlRaw(
                    $"DELETE FROM \"{table}\" WHERE \"Id\" BETWEEN {lo} AND {hi} AND \"Id\" NOT IN (SELECT Id FROM tr_keep)");
            }

            db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS tr_keep");
            if (removed >= 100_000) db.Database.ExecuteSqlRaw("VACUUM"); // give the space back once
            if (removed > 0)
                AppLogger.Log($"[TokenRemainingCollector] removed {removed} duplicate observation(s) in {(DateTime.UtcNow - started).TotalSeconds:N1}s");
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[TokenRemainingCollector] duplicate cleanup failed after {removed} row(s)", ex);
        }
        finally
        {
            try { db.Database.CloseConnection(); } catch { }
        }
        return removed;
    }

    internal sealed record SnapshotPayload(
        string AccountKey,
        string Model,
        int FiveHourPercent,
        DateTime? FiveHourResetsAtUtc,
        int SevenDayPercent,
        DateTime? SevenDayResetsAtUtc,
        DateTime WrittenAtUtc);

    private static SnapshotPayload? TryReadSnapshot(string file)
    {
        string text;
        try
        {
            // Wrapper writes via temp+rename, but on the off-chance we open
            // mid-rename, FileShare lets us read a previous version.
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            text = reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var account = ReadStr(root, "account");
            var model   = ReadStr(root, "model");
            // Account is required (filename should match anyway), model can be missing.
            if (string.IsNullOrEmpty(account))
                account = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(account)) return null;
            if (string.IsNullOrEmpty(model)) return null;

            int fh = 0, sd = 0;
            DateTime? fhReset = null, sdReset = null;
            if (root.TryGetProperty("fiveHour", out var fhEl) && fhEl.ValueKind == JsonValueKind.Object)
            {
                fh = ReadInt(fhEl, "usedPercentage");
                fhReset = ReadEpochSecondsUtc(fhEl, "resetsAt");
            }
            if (root.TryGetProperty("sevenDay", out var sdEl) && sdEl.ValueKind == JsonValueKind.Object)
            {
                sd = ReadInt(sdEl, "usedPercentage");
                sdReset = ReadEpochSecondsUtc(sdEl, "resetsAt");
            }

            // Without its own timestamp a snapshot is as old as its file — never "now",
            // or a stale file would look new on every tick.
            DateTime writtenAt = File.GetLastWriteTimeUtc(file);
            if (root.TryGetProperty("writtenAt", out var wEl) && wEl.ValueKind == JsonValueKind.String
                && DateTime.TryParse(wEl.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                writtenAt = parsed.ToUniversalTime();

            return new SnapshotPayload(account, model, fh, fhReset, sd, sdReset, writtenAt);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadStr(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? (el.GetString() ?? "") : "";

    private static int ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) return Math.Clamp(v, 0, 100);
        return 0;
    }

    private static DateTime? ReadEpochSecondsUtc(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var seconds) || seconds <= 0) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }

    /// <summary>
    /// Wipes all observations + snapshot files. Used by Settings "reset" to
    /// recover from a bad state without manual SQL.
    /// </summary>
    public sealed record ResetSummary(int RowsDeleted, int SnapshotFilesDeleted);

    public ResetSummary ResetData()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        var rows = db.TokenRemainingObservations.Count();
        db.TokenRemainingObservations.ExecuteDelete();

        int files = 0;
        if (Directory.Exists(SnapshotsDir))
        {
            foreach (var f in Directory.EnumerateFiles(SnapshotsDir, "*.json"))
            {
                try { File.Delete(f); files++; } catch { /* keep going */ }
            }
        }

        AppLogger.Log($"[TokenRemainingCollector] reset: rows-={rows} snapshots-={files}");
        return new ResetSummary(rows, files);
    }
}

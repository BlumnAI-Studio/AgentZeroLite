using Agent.Common.Data;
using Microsoft.EntityFrameworkCore;

namespace Agent.Common.Telemetry;

/// <summary>
/// Read-only queries over <see cref="Data.Entities.TokenRemainingObservation"/>.
/// All methods are short-lived: spin up a fresh AppDbContext, run the query,
/// dispose. Mirrors the TokenUsageQueryService pattern.
/// </summary>
public static class TokenRemainingQueryService
{
    /// <summary>Latest row per (account, model) for the given account.</summary>
    public sealed record ModelLatest(
        string Model,
        int FiveHourPercent,
        DateTime? FiveHourResetsAtUtc,
        int SevenDayPercent,
        DateTime? SevenDayResetsAtUtc,
        DateTime ObservedAtUtc,
        long Observations);

    public static IReadOnlyList<ModelLatest> GetLatestForAccount(string accountKey)
    {
        if (string.IsNullOrEmpty(accountKey)) return Array.Empty<ModelLatest>();

        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        return GetLatestForAccount(db, accountKey);
    }

    /// <remarks>
    /// Aggregates in SQL, then reads one row per model through the
    /// (AccountKey, Model, ObservedAtUtc) index. It used to pull every row of the
    /// account into memory on the assumption that there is one row per state
    /// change; measured (2026-10-15) one account held 3,717,787 rows and this
    /// call took 23 s — on the plugin host's UI thread, which Windows then
    /// reported as a hang.
    /// </remarks>
    internal static IReadOnlyList<ModelLatest> GetLatestForAccount(AppDbContext db, string accountKey)
    {
        // Anonymous projection inside GroupBy — EF 10 preview does not translate
        // positional-record constructors there (same as TokenUsageQueryService).
        var perModel = db.TokenRemainingObservations
            .Where(o => o.AccountKey == accountKey)
            .GroupBy(o => o.Model)
            .Select(g => new { Model = g.Key, Count = g.LongCount(), Last = g.Max(o => o.ObservedAtUtc) })
            .ToList();

        var result = new List<ModelLatest>(perModel.Count);
        foreach (var m in perModel)
        {
            var latest = db.TokenRemainingObservations
                .Where(o => o.AccountKey == accountKey && o.Model == m.Model)
                .OrderByDescending(o => o.ObservedAtUtc)
                .ThenByDescending(o => o.Id)
                .Select(o => new
                {
                    o.FiveHourPercent,
                    o.FiveHourResetsAtUtc,
                    o.SevenDayPercent,
                    o.SevenDayResetsAtUtc,
                    o.ObservedAtUtc,
                })
                .FirstOrDefault();
            if (latest is null) continue;
            result.Add(new ModelLatest(
                m.Model,
                latest.FiveHourPercent,
                latest.FiveHourResetsAtUtc,
                latest.SevenDayPercent,
                latest.SevenDayResetsAtUtc,
                latest.ObservedAtUtc,
                m.Count));
        }

        // Most-recently-active model first
        return result.OrderByDescending(m => m.ObservedAtUtc).ToList();
    }

    /// <summary>Models ever observed under this account — for the settings filter list.</summary>
    public sealed record ObservedModel(
        string Model,
        DateTime LastSeenUtc,
        DateTime FirstSeenUtc,
        long Observations);

    public static IReadOnlyList<ObservedModel> GetObservedModels(string accountKey)
    {
        if (string.IsNullOrEmpty(accountKey)) return Array.Empty<ObservedModel>();

        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        return GetObservedModels(db, accountKey);
    }

    /// <remarks>Grouped in SQL — the account can hold millions of rows.</remarks>
    internal static IReadOnlyList<ObservedModel> GetObservedModels(AppDbContext db, string accountKey)
    {
        var rows = db.TokenRemainingObservations
            .Where(o => o.AccountKey == accountKey)
            .GroupBy(o => o.Model)
            .Select(g => new
            {
                Model = g.Key,
                Last = g.Max(o => o.ObservedAtUtc),
                First = g.Min(o => o.ObservedAtUtc),
                Count = g.LongCount(),
            })
            .ToList();

        return rows
            .Select(r => new ObservedModel(r.Model, r.Last, r.First, r.Count))
            .OrderByDescending(m => m.LastSeenUtc)
            .ToList();
    }

    /// <summary>Per-account summary — used by Settings "Active account" picker.</summary>
    public sealed record AccountProfile(
        string AccountKey,
        long Observations,
        int ModelCount,
        DateTime? LastSeenUtc);

    public static IReadOnlyList<AccountProfile> GetAccountProfiles()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        return GetAccountProfiles(db);
    }

    /// <remarks>Grouped in SQL — see <see cref="GetLatestForAccount(AppDbContext, string)"/> for why (5.7 s at 3.7 M rows).</remarks>
    internal static IReadOnlyList<AccountProfile> GetAccountProfiles(AppDbContext db)
    {
        // Two passes over the covering index: (account, model) pairs, then per account.
        var pairs = db.TokenRemainingObservations
            .GroupBy(o => new { o.AccountKey, o.Model })
            .Select(g => new { g.Key.AccountKey, Count = g.LongCount(), Last = g.Max(o => o.ObservedAtUtc) })
            .ToList();

        return pairs
            .GroupBy(p => p.AccountKey, StringComparer.Ordinal)
            .Select(g => new AccountProfile(
                g.Key,
                g.Sum(p => p.Count),
                g.Count(),
                g.Max(p => p.Last)))
            .OrderByDescending(p => p.LastSeenUtc)
            .ToList();
    }

    /// <summary>Time-series for one model under one account — for future chart support.</summary>
    public sealed record SeriesPoint(
        DateTime ObservedAtUtc,
        int FiveHourPercent,
        int SevenDayPercent);

    public static IReadOnlyList<SeriesPoint> GetSeries(string accountKey, string model, int hours)
    {
        if (string.IsNullOrEmpty(accountKey) || string.IsNullOrEmpty(model)) return Array.Empty<SeriesPoint>();
        var since = DateTime.UtcNow.AddHours(-Math.Max(1, hours));

        using var db = new AppDbContext();
        db.Database.EnsureCreated();

        return db.TokenRemainingObservations
            .Where(o => o.AccountKey == accountKey && o.Model == model && o.ObservedAtUtc >= since)
            .OrderBy(o => o.ObservedAtUtc)
            .Select(o => new { o.ObservedAtUtc, o.FiveHourPercent, o.SevenDayPercent })
            .ToList()
            .Select(o => new SeriesPoint(o.ObservedAtUtc, o.FiveHourPercent, o.SevenDayPercent))
            .ToList();
    }

    /// <summary>Collector status — for the widget footer + Settings.</summary>
    public sealed record CollectorState(
        bool Running,
        DateTime? LastTickUtc,
        int LastFilesScanned,
        int LastRowsInserted,
        int LastRowsSkippedSamePercent,
        long TotalRows,
        string? LastError);

    public static CollectorState GetCollectorState()
    {
        long total;
        try
        {
            using var db = new AppDbContext();
            db.Database.EnsureCreated();
            total = db.TokenRemainingObservations.LongCount();
        }
        catch
        {
            total = 0;
        }
        var c = TokenRemainingCollector.Instance;
        return new CollectorState(
            Running: c.LastTickUtc is not null,
            LastTickUtc: c.LastTickUtc,
            LastFilesScanned: c.LastFilesScanned,
            LastRowsInserted: c.LastRowsInserted,
            LastRowsSkippedSamePercent: c.LastRowsSkippedSamePercent,
            TotalRows: total,
            LastError: c.LastError);
    }
}

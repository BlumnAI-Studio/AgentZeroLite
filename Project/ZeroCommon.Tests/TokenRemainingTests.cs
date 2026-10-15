using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Agent.Common.Telemetry;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Snap = Agent.Common.Telemetry.TokenRemainingCollector.SnapshotPayload;
using State = Agent.Common.Telemetry.TokenRemainingCollector.LatestState;

namespace ZeroCommon.Tests;

/// <summary>
/// Measured 2026-10-15: 3,717,787 token-remaining rows for 1,181 distinct snapshots
/// (the old dedup re-inserted every finished session's leftover file on each tick),
/// and the widget's queries pulled them all into memory — 23 s on the UI thread.
/// </summary>
[Trait("Category", "TokenRemaining")]
public sealed class TokenRemainingTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbFile); } catch { }
    }

    private AppDbContext Db()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbFile}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static Snap S(int fiveHour, int minutes, string model = "Opus", string account = "claude")
        => new(account, model, fiveHour, null, 10, null, T0.AddMinutes(minutes));

    // ── the dedup rule ──

    [Fact]
    public void A_finished_sessions_leftover_file_is_not_inserted_again()
    {
        // Stored: the live session at 30 % (minute 10). On disk: that file, plus an old
        // session's file from minute 2 at 12 % — which the old rule re-inserted every tick.
        var stored = new State(30, null, 10, null, T0.AddMinutes(10));
        var plan = TokenRemainingCollector.PlanInserts(
            new[] { S(12, 2), S(30, 10) }, (_, _) => stored, out var skipped);

        Assert.Empty(plan);
        Assert.Equal(2, skipped);
    }

    [Fact]
    public void A_newer_snapshot_with_a_new_value_is_inserted_once()
    {
        var stored = new State(30, null, 10, null, T0.AddMinutes(10));
        var plan = TokenRemainingCollector.PlanInserts(new[] { S(35, 11) }, (_, _) => stored, out _);
        Assert.Single(plan);

        // Next tick: that row is now the newest stored state; the same file is skipped.
        var after = new State(35, null, 10, null, T0.AddMinutes(11));
        Assert.Empty(TokenRemainingCollector.PlanInserts(new[] { S(35, 11) }, (_, _) => after, out _));
    }

    [Fact]
    public void Several_new_snapshots_in_one_tick_are_compared_in_time_order()
    {
        // Two live sessions; files arrive in directory order, not time order.
        var plan = TokenRemainingCollector.PlanInserts(
            new[] { S(40, 3), S(20, 1), S(20, 2) }, (_, _) => null, out var skipped);

        Assert.Equal(new[] { 1, 3 }, plan.Select(p => (int)(p.WrittenAtUtc - T0).TotalMinutes));
        Assert.Equal(1, skipped); // minute 2 repeats minute 1
    }

    [Fact]
    public void Models_and_accounts_are_tracked_separately()
    {
        var plan = TokenRemainingCollector.PlanInserts(
            new[] { S(10, 1, "Opus"), S(10, 1, "Sonnet"), S(10, 1, "Opus", "claude-work") }, (_, _) => null, out _);
        Assert.Equal(3, plan.Count);
    }

    // ── cleanup + queries on a real SQLite file ──

    private static TokenRemainingObservation Row(string model, int fiveHour, int minutes, string account = "claude")
        => new()
        {
            AccountKey = account, Model = model, FiveHourPercent = fiveHour, SevenDayPercent = 10,
            ObservedAtUtc = T0.AddMinutes(minutes),
        };

    [Fact]
    public void Cleanup_keeps_one_row_per_distinct_snapshot()
    {
        using (var db = Db())
        {
            for (var i = 0; i < 50; i++) db.TokenRemainingObservations.Add(Row("Opus", 12, 2));   // the leftover, 50×
            db.TokenRemainingObservations.Add(Row("Opus", 30, 10));
            db.TokenRemainingObservations.Add(Row("Opus", 12, 11));                              // same value, later time: kept
            db.SaveChanges();
        }
        using (var db = Db())
        {
            Assert.Equal(49, TokenRemainingCollector.CompactDuplicates(db));
            Assert.Equal(3, db.TokenRemainingObservations.Count());
            Assert.Equal(0, TokenRemainingCollector.CompactDuplicates(db));
        }
    }

    [Fact]
    public void Queries_aggregate_in_sql_with_the_same_answers()
    {
        using (var db = Db())
        {
            db.TokenRemainingObservations.AddRange(
                Row("Opus", 10, 1), Row("Opus", 20, 5), Row("Opus", 15, 3),
                Row("Sonnet", 5, 7),
                Row("Opus", 50, 9, account: "claude-work"));
            db.SaveChanges();
        }
        using var q = Db();

        var latest = TokenRemainingQueryService.GetLatestForAccount(q, "claude");
        Assert.Equal(new[] { "Sonnet", "Opus" }, latest.Select(m => m.Model));        // most recent first
        var opus = latest.Single(m => m.Model == "Opus");
        Assert.Equal(20, opus.FiveHourPercent);                                        // the minute-5 row
        Assert.Equal(3, opus.Observations);

        var accounts = TokenRemainingQueryService.GetAccountProfiles(q);
        Assert.Equal(new[] { "claude-work", "claude" }, accounts.Select(a => a.AccountKey));
        var main = accounts.Single(a => a.AccountKey == "claude");
        Assert.Equal(4, main.Observations);
        Assert.Equal(2, main.ModelCount);
        Assert.Equal(T0.AddMinutes(7), main.LastSeenUtc);

        var models = TokenRemainingQueryService.GetObservedModels(q, "claude");
        var opusSeen = models.Single(m => m.Model == "Opus");
        Assert.Equal(T0.AddMinutes(1), opusSeen.FirstSeenUtc);
        Assert.Equal(T0.AddMinutes(5), opusSeen.LastSeenUtc);
        Assert.Equal(3, opusSeen.Observations);
    }
}

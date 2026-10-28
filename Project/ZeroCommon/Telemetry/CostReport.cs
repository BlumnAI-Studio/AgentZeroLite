using Agent.Common.Data;

namespace Agent.Common.Telemetry;

/// <summary>Token totals of one group (a model, a project folder) — summed in the database.</summary>
public sealed record UsageTotals(string Model, string Cwd, long Turns, long InputTokens, long OutputTokens,
    long CacheCreateTokens, long CacheReadTokens);

/// <summary>One row of a cost breakdown.</summary>
public sealed record CostLine(string Key, decimal Usd, long Turns, long Tokens);

/// <summary>
/// "What have the agents cost" — the month-to-date spend against the budget cap, broken down by
/// model and by project folder, plus the all-time total, priced with the user's overrides
/// (<see cref="BudgetSettings.EffectiveTable"/>) before the defaults.
///
/// Built from totals grouped in the database rather than from every record: cost is linear in
/// the token counts, so pricing a model's sums equals summing its records' prices, and the
/// collectors keep one row per turn — tens of thousands after a few months.
/// The WPF host's <c>cost</c> verb priced with the defaults only and showed no budget; this is
/// what the Avalonia host's Settings → Budget, status bar and <c>-cli cost</c> share.
/// </summary>
public sealed record CostReport(
    DateTime SinceUtc,
    BudgetStatus Month,
    long MonthTurns,
    IReadOnlyList<CostLine> MonthByModel,
    IReadOnlyList<CostLine> MonthByProject,
    decimal AllTimeUsd,
    long AllTimeTurns,
    IReadOnlyList<string> UnpricedModels)
{
    /// <summary>
    /// Price grouped totals. <paramref name="month"/> are this month's (model, cwd) groups,
    /// <paramref name="allTime"/> every model's totals (cwd ignored). <paramref name="workspaceRoots"/>
    /// are the app's workspace folders: a cwd inside one counts toward it (see <see cref="ProjectName"/>).
    /// </summary>
    public static CostReport Build(IEnumerable<UsageTotals> month, IEnumerable<UsageTotals> allTime,
        BudgetSettings settings, DateTime nowUtc, IReadOnlyList<string>? workspaceRoots = null)
    {
        var table = settings.EffectiveTable();
        var unpriced = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        decimal Price(UsageTotals t)
        {
            var p = TokenCostCalculator.Lookup(t.Model, table);
            if (p is null)
            {
                if (!string.IsNullOrWhiteSpace(t.Model) && t.InputTokens + t.OutputTokens > 0) unpriced.Add(t.Model);
                return 0m;
            }
            return (t.InputTokens * p.InputPerMTok + t.OutputTokens * p.OutputPerMTok
                    + t.CacheCreateTokens * p.CacheWritePerMTok + t.CacheReadTokens * p.CacheReadPerMTok) / 1_000_000m;
        }

        static long Tokens(UsageTotals t) => t.InputTokens + t.OutputTokens + t.CacheCreateTokens + t.CacheReadTokens;

        var priced = month.Select(t => (T: t, Usd: Price(t))).ToList();
        var byModel = priced
            .GroupBy(x => string.IsNullOrWhiteSpace(x.T.Model) ? "(unknown)" : x.T.Model)
            .Select(g => new CostLine(g.Key, g.Sum(x => x.Usd), g.Sum(x => x.T.Turns), g.Sum(x => Tokens(x.T))))
            .OrderByDescending(l => l.Usd).ThenByDescending(l => l.Tokens).ToList();
        var byProject = priced
            .GroupBy(x => ProjectName(x.T.Cwd, workspaceRoots), StringComparer.OrdinalIgnoreCase)
            .Select(g => new CostLine(g.Key, g.Sum(x => x.Usd), g.Sum(x => x.T.Turns), g.Sum(x => Tokens(x.T))))
            .OrderByDescending(l => l.Usd).ThenByDescending(l => l.Tokens).ToList();

        var all = allTime.ToList();
        var allUsd = all.Sum(Price);

        return new CostReport(
            BudgetSettings.StartOfMonthUtc(nowUtc),
            settings.Evaluate(byModel.Sum(l => l.Usd)),
            byModel.Sum(l => l.Turns),
            byModel,
            byProject,
            allUsd,
            all.Sum(t => t.Turns),
            unpriced.ToList());
    }

    /// <summary>
    /// The project a cwd belongs to: the innermost workspace folder that contains it, named by its
    /// own folder name; else the cwd's own folder name; "(no folder)" when there is none.
    /// An agent changes directory as it works (measured: one month split into "Assets", "Scripts",
    /// "bin" … beside the repositories they belong to), so the folder the user opened is the
    /// project, not wherever the agent happened to stand.
    /// </summary>
    public static string ProjectName(string? cwd, IReadOnlyList<string>? workspaceRoots = null)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "(no folder)";
        var path = Normalize(cwd);
        string? best = null;
        if (workspaceRoots is not null)
            foreach (var root in workspaceRoots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var r = Normalize(root);
                var inside = path.Equals(r, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase);
                if (inside && (best is null || r.Length > best.Length)) best = r;
            }
        return LastSegment(best ?? path);
    }

    private static string Normalize(string p) => p.Trim().Replace('\\', '/').TrimEnd('/');

    private static string LastSegment(string p)
    {
        var cut = p.LastIndexOf('/');
        return cut >= 0 && cut < p.Length - 1 ? p[(cut + 1)..] : p;
    }

    /// <summary>Query the shared database and build the report. Throws if the database cannot be opened.</summary>
    public static CostReport Load(BudgetSettings settings, DateTime nowUtc)
    {
        var since = BudgetSettings.StartOfMonthUtc(nowUtc);
        using var db = new AppDbContext();
        var month = db.TokenUsageRecords.Where(r => r.RecordedAt >= since)
            .GroupBy(r => new { r.Model, r.Cwd })
            .Select(g => new
            {
                g.Key.Model, g.Key.Cwd, Turns = g.LongCount(),
                In = g.Sum(r => r.InputTokens), Out = g.Sum(r => r.OutputTokens),
                CacheW = g.Sum(r => r.CacheCreateTokens), CacheR = g.Sum(r => r.CacheReadTokens),
            })
            .AsEnumerable()
            .Select(x => new UsageTotals(x.Model, x.Cwd, x.Turns, x.In, x.Out, x.CacheW, x.CacheR))
            .ToList();
        var all = db.TokenUsageRecords
            .GroupBy(r => r.Model)
            .Select(g => new
            {
                Model = g.Key, Turns = g.LongCount(),
                In = g.Sum(r => r.InputTokens), Out = g.Sum(r => r.OutputTokens),
                CacheW = g.Sum(r => r.CacheCreateTokens), CacheR = g.Sum(r => r.CacheReadTokens),
            })
            .AsEnumerable()
            .Select(x => new UsageTotals(x.Model, "", x.Turns, x.In, x.Out, x.CacheW, x.CacheR))
            .ToList();
        var roots = db.CliGroups.Select(g => g.DirectoryPath).ToList();
        return Build(month, all, settings, nowUtc, roots);
    }

    /// <summary>One line for a status bar: "$12.34 this month" or "$12.34 / $50 (25%)".</summary>
    public string StatusText()
        => Month.HasCap
            ? $"${Month.SpentUsd:F2} / ${Month.CapUsd:F0} ({Month.FractionUsed * 100:F0}%)"
            : $"${Month.SpentUsd:F2} this month";
}

using Agent.Common.Telemetry;
using Xunit;

namespace ZeroCommon.Tests;

[Trait("Category", "Budget")]
public sealed class CostReportTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc);

    private static UsageTotals U(string model, string cwd, long turns, long input, long output = 0, long cw = 0, long cr = 0)
        => new(model, cwd, turns, input, output, cw, cr);

    [Fact]
    public void Prices_grouped_totals_like_the_per_record_calculator()
    {
        // 1M sonnet input = $3, 1M sonnet output = $15 → $18.
        var month = new[] { U("claude-sonnet-4", @"C:\code\AgentZeroLite", 10, 1_000_000, 1_000_000) };
        var r = CostReport.Build(month, month, new BudgetSettings(), Now);

        Assert.Equal(18m, r.Month.SpentUsd);
        Assert.Equal(18m, r.AllTimeUsd);
        Assert.Equal(10, r.MonthTurns);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), r.SinceUtc);
        Assert.Equal("AgentZeroLite", Assert.Single(r.MonthByProject).Key);
    }

    [Fact]
    public void Overrides_win_and_unknown_models_are_listed_not_guessed()
    {
        var settings = new BudgetSettings
        {
            Overrides = { new BudgetSettings.PriceOverride { Key = "sonnet", InputPerMTok = 1m } },
        };
        var month = new[]
        {
            U("claude-sonnet-4", "/a/x", 1, 2_000_000),
            U("claude-nova-9", "/a/x", 1, 5_000_000),
            U("", "/a/x", 1, 0),
        };
        var r = CostReport.Build(month, month, settings, Now);

        Assert.Equal(2m, r.Month.SpentUsd);
        Assert.Equal(["claude-nova-9"], r.UnpricedModels);
    }

    [Fact]
    public void Breaks_down_by_model_and_by_project_most_expensive_first()
    {
        var month = new[]
        {
            U("claude-haiku", "/w/alpha", 3, 1_000_000),     // $0.80
            U("claude-opus", "/w/alpha", 1, 1_000_000),      // $15
            U("claude-opus", "/w/beta/", 2, 2_000_000),      // $30
        };
        var r = CostReport.Build(month, month, new BudgetSettings(), Now);

        Assert.Equal(["claude-opus", "claude-haiku"], r.MonthByModel.Select(l => l.Key));
        Assert.Equal(45m, r.MonthByModel[0].Usd);
        Assert.Equal(3, r.MonthByModel[0].Turns);
        Assert.Equal(["beta", "alpha"], r.MonthByProject.Select(l => l.Key));
        Assert.Equal(15.8m, r.MonthByProject[1].Usd);
    }

    [Fact]
    public void Status_text_shows_the_cap_when_there_is_one()
    {
        var month = new[] { U("claude-opus", "/w", 1, 1_000_000) };   // $15
        Assert.Equal("$15.00 this month", CostReport.Build(month, month, new BudgetSettings(), Now).StatusText());

        var capped = CostReport.Build(month, month, new BudgetSettings { MonthlyCapUsd = 60m }, Now);
        Assert.Equal("$15.00 / $60 (25%)", capped.StatusText());
        Assert.False(capped.Month.NearingCap());
    }

    [Theory]
    [InlineData(null, "(no folder)")]
    [InlineData("", "(no folder)")]
    [InlineData(@"C:\code\psmon\AgentZeroLite\", "AgentZeroLite")]
    [InlineData("/Users/me/repo", "repo")]
    [InlineData("repo", "repo")]
    public void Project_name_is_the_folder_name(string? cwd, string expected)
        => Assert.Equal(expected, CostReport.ProjectName(cwd));

    [Theory]
    [InlineData(@"C:\code\game\Assets\Scripts", "game")]                 // a sub-folder counts toward its workspace
    [InlineData(@"c:/CODE/game", "game")]                                // case and slashes do not matter
    [InlineData(@"C:\code\game\.claude\worktrees\w1\src", "w1")]         // the innermost workspace wins
    [InlineData(@"C:\code\gamer\bin", "bin")]                            // a prefix that is not a folder boundary is not inside
    public void A_cwd_inside_a_workspace_counts_toward_that_workspace(string cwd, string expected)
    {
        string[] roots = [@"C:\code\game", @"C:\code\game\.claude\worktrees\w1\", ""];
        Assert.Equal(expected, CostReport.ProjectName(cwd, roots));
    }
}

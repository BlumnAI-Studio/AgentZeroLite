using Agent.Common.Telemetry;
using AgentZeroAvalonia.ViewModels;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>Settings → Budget and the status bar chip, over a fake report (no database).</summary>
public class BudgetViewModelTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc);

    private static Func<BudgetSettings, CostReport> Fake(params UsageTotals[] month)
        => s => CostReport.Build(month, month, s, Now);

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"budget-vm-{Guid.NewGuid():N}.json");

    [Fact]
    public void Save_writes_cap_and_prices_and_reprices_with_them()
    {
        var path = TempPath();
        try
        {
            var vm = new BudgetViewModel(path, Fake(new UsageTotals("claude-nova-9", "/w/a", 4, 2_000_000, 0, 0, 0)));
            vm.Refresh();
            Assert.True(vm.HasUnpriced);
            Assert.Contains("$0.00", vm.SpendText);

            vm.CapText = "10";
            vm.AddUnpricedCommand.Execute(null);
            var row = Assert.Single(vm.Prices);
            Assert.Equal("claude-nova-9", row.Key);
            row.Input = 1m;
            vm.SaveCommand.Execute(null);

            var stored = BudgetSettingsStore.Load(path);
            Assert.Equal(10m, stored.MonthlyCapUsd);
            Assert.Equal("claude-nova-9", Assert.Single(stored.Overrides).Key);
            Assert.False(vm.HasUnpriced);
            Assert.Equal("💲 $2.00 / $10 (20%)", vm.ChipText);
            Assert.Equal("a", Assert.Single(vm.ByProject).Key);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_cap_that_does_not_parse_is_no_cap_and_blank_rows_are_dropped()
    {
        var vm = new BudgetViewModel(TempPath(), Fake());
        vm.CapText = "lots";
        vm.AddPriceCommand.Execute(null);
        var s = vm.Build();
        Assert.Equal(0m, s.MonthlyCapUsd);
        Assert.Empty(s.Overrides);

        vm.Prices[0].RemoveCommand.Execute(null);
        Assert.Empty(vm.Prices);
    }

    [Fact]
    public void Over_the_cap_says_so()
    {
        var path = TempPath();
        try
        {
            BudgetSettingsStore.Save(new BudgetSettings { MonthlyCapUsd = 1m }, path);
            var vm = new BudgetViewModel(path, Fake(new UsageTotals("claude-opus-5-5", "/w", 1, 1_000_000, 0, 0, 0)));   // $4
            vm.Refresh();
            Assert.Contains("OVER BUDGET", vm.SpendText);
        }
        finally { File.Delete(path); }
    }
}

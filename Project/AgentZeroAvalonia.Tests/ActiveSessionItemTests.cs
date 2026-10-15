using Agent.Common.Agents;
using AgentZeroAvalonia.ViewModels;
using Avalonia.Media;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>The ACTIVE SESSIONS row shows the WPF SESSIONS panel's dot colour and chip.</summary>
public class ActiveSessionItemTests
{
    private static ActiveSessionItem Row(out List<TerminalTabViewModel> went)
    {
        var log = new List<TerminalTabViewModel>();
        went = log;
        return new ActiveSessionItem(new TerminalTabViewModel("Claude", 1, null), "AgentZeroLite", log.Add);
    }

    private static uint Rgb(IBrush b) => ((ISolidColorBrush)b).Color.ToUInt32() & 0xFFFFFF;

    [Theory]
    [InlineData(AgentActivity.Blocked, true, "blocked", 0xE06C75u, true)]
    [InlineData(AgentActivity.Working, true, "working", 0xE5C07Bu, false)]
    [InlineData(AgentActivity.Done, false, "done", 0x61AFEFu, true)]
    [InlineData(AgentActivity.Done, true, "done", 0x98C379u, false)]
    [InlineData(AgentActivity.Idle, true, "idle", 0x7F848Eu, false)]
    public void States_map_to_the_wpf_colours(AgentActivity activity, bool seen, string label, uint rgb, bool attention)
    {
        var row = Row(out _);
        row.Apply(new TerminalAgentState(0, 0, "Claude", activity, seen, "r"), isActive: false);
        Assert.Equal(label, row.StateLabel);
        Assert.Equal(rgb, Rgb(row.StateBrush));
        Assert.Equal(attention, row.NeedsAttention);
        Assert.Contains("AgentZeroLite / Claude", row.Tip);
    }

    [Fact]
    public void Undetected_rows_show_only_a_running_dot_and_click_goes_to_the_tab()
    {
        var row = Row(out var went);
        row.Apply(null, isActive: true);
        Assert.False(row.HasStateLabel);
        Assert.True(row.IsActive);
        row.GoCommand.Execute(null);
        Assert.Same(row.Tab, Assert.Single(went));
    }
}

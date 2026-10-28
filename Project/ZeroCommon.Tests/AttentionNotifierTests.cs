using Agent.Common.Agents;
using Xunit;

namespace ZeroCommon.Tests;

[Trait("Category", "Attention")]
public sealed class AttentionNotifierTests
{
    private static readonly DateTime T0 = new(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc);
    private static readonly AttentionSettings Defaults = new();

    private static TerminalAgentState S(AgentActivity a, bool seen = false, int g = 0, int t = 0, string title = "Claude")
        => new(g, t, title, a, seen, Rule: null);

    [Fact]
    public void First_pass_only_records_what_is_already_waiting()
    {
        var n = new AttentionNotifier();
        Assert.Empty(n.Observe([S(AgentActivity.Blocked)], Defaults, appActive: false, T0));
        Assert.Empty(n.Observe([S(AgentActivity.Blocked)], Defaults, appActive: false, T0.AddSeconds(2)));
    }

    [Fact]
    public void Working_to_blocked_alerts_once()
    {
        var n = new AttentionNotifier();
        n.Observe([S(AgentActivity.Working)], Defaults, false, T0);
        var alerts = n.Observe([S(AgentActivity.Blocked)], Defaults, false, T0.AddSeconds(2));
        var a = Assert.Single(alerts);
        Assert.Equal(AttentionKind.Blocked, a.Kind);
        Assert.Equal("Claude", a.Title);
        Assert.Empty(n.Observe([S(AgentActivity.Blocked)], Defaults, false, T0.AddSeconds(4)));
    }

    [Fact]
    public void Unseen_done_alerts_but_seen_done_does_not()
    {
        var n = new AttentionNotifier();
        n.Observe([S(AgentActivity.Working, g: 0), S(AgentActivity.Working, g: 1)], Defaults, false, T0);
        var alerts = n.Observe([S(AgentActivity.Done, seen: false, g: 0), S(AgentActivity.Done, seen: true, g: 1)],
            Defaults, false, T0.AddSeconds(2));
        var a = Assert.Single(alerts);
        Assert.Equal((0, AttentionKind.Done), (a.Group, a.Kind));
    }

    [Fact]
    public void Flapping_within_the_cooldown_alerts_once()
    {
        var n = new AttentionNotifier();
        n.Observe([S(AgentActivity.Working)], Defaults, false, T0);
        Assert.Single(n.Observe([S(AgentActivity.Blocked)], Defaults, false, T0.AddSeconds(1)));
        n.Observe([S(AgentActivity.Working)], Defaults, false, T0.AddSeconds(3));
        Assert.Empty(n.Observe([S(AgentActivity.Blocked)], Defaults, false, T0.AddSeconds(5)));
        n.Observe([S(AgentActivity.Working)], Defaults, false, T0.AddSeconds(30));
        Assert.Single(n.Observe([S(AgentActivity.Blocked)], Defaults, false, T0.AddSeconds(32)));
    }

    [Fact]
    public void Active_app_is_quiet_and_the_transition_is_not_announced_later()
    {
        var n = new AttentionNotifier();
        n.Observe([S(AgentActivity.Working)], Defaults, true, T0);
        Assert.Empty(n.Observe([S(AgentActivity.Blocked)], Defaults, appActive: true, T0.AddSeconds(2)));
        Assert.Empty(n.Observe([S(AgentActivity.Blocked)], Defaults, appActive: false, T0.AddSeconds(4)));
    }

    [Fact]
    public void Active_app_alerts_when_the_user_asked_for_it()
    {
        var n = new AttentionNotifier();
        var always = new AttentionSettings { OnlyWhenInactive = false };
        n.Observe([S(AgentActivity.Working)], always, true, T0);
        Assert.Single(n.Observe([S(AgentActivity.Blocked)], always, appActive: true, T0.AddSeconds(2)));
    }

    [Theory]
    [InlineData(false, true, true, AgentActivity.Blocked)]   // master switch off
    [InlineData(true, false, true, AgentActivity.Blocked)]   // blocked off
    [InlineData(true, true, false, AgentActivity.Done)]      // done off
    public void Switched_off_kinds_stay_quiet(bool enabled, bool onBlocked, bool onDone, AgentActivity to)
    {
        var settings = new AttentionSettings { Enabled = enabled, OnBlocked = onBlocked, OnDone = onDone };
        var n = new AttentionNotifier();
        n.Observe([S(AgentActivity.Working)], settings, false, T0);
        Assert.Empty(n.Observe([S(to)], settings, false, T0.AddSeconds(2)));
    }

    [Fact]
    public void A_new_terminal_at_a_closed_ones_index_starts_clean()
    {
        var n = new AttentionNotifier();
        n.Observe([S(AgentActivity.Blocked, title: "A")], Defaults, false, T0);
        n.Observe([], Defaults, false, T0.AddSeconds(2));
        // Appears already blocked after the first pass: that is a transition, so it alerts.
        Assert.Single(n.Observe([S(AgentActivity.Blocked, title: "A")], Defaults, false, T0.AddSeconds(40)));
    }

    [Fact]
    public void Store_round_trips_and_clamps_the_popup_time()
    {
        var path = Path.Combine(Path.GetTempPath(), $"attention-{Guid.NewGuid():N}.json");
        try
        {
            AttentionSettingsStore.Save(new AttentionSettings { OnDone = false, PopupSeconds = 999 }, path);
            var back = AttentionSettingsStore.Load(path);
            Assert.False(back.OnDone);
            Assert.True(back.OnBlocked);
            Assert.Equal(AttentionSettings.MaxPopupSeconds, back.ResolvePopupSeconds());
            File.WriteAllText(path, "{ not json");
            Assert.True(AttentionSettingsStore.Load(path).Enabled);
        }
        finally { File.Delete(path); }
    }
}

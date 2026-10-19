using System.Text.Json;
using Agent.Common.Agents;
using Agent.Common.Module;
using Agent.Common.Services;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>
/// The tracker the WPF SESSIONS panel and the Avalonia ACTIVE SESSIONS list both run
/// (moved down from WPF's AgentStateMonitor). Tabs use a non-agent title so the generic
/// manifest applies — the Claude manifest can be overridden by other tests' files.
/// </summary>
[Collection(AgentManifestCatalogCollection.Name)]
public sealed class AgentActivityTrackerTests
{
    private sealed class Screen(string id) : ITerminalSession
    {
        public string Text = "";
        public string SessionId => id;
        public string InternalId => id;
        public bool IsRunning => true;
        public void Write(ReadOnlySpan<char> text) { }
        public void WriteAndSubmit(string text) { }
        public void WriteAndEnter(string text) { }
        public Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default) => Task.CompletedTask;
        public void SendControl(TerminalControl control) { }
        public event Action<TerminalOutputFrame>? OutputReceived { add { } remove { } }
        public int OutputLength => Text.Length;
        public string ReadOutput(int start, int length) => Text.Substring(start, length);
        public string GetConsoleText() => Text;
        public void NoteInputAttempt(string source) { }
        public TerminalHealthState HealthState => default;
        public event Action<TerminalHealthState>? HealthChanged { add { } remove { } }
    }

    private sealed class Tab(string title, Screen? session, bool started = true) : IConsoleTabInfo
    {
        public string Title => title;
        public int CliDefinitionId => 1;
        public ITerminalSession? Session => session;
        public bool IsTerminalStarted => started;
    }

    private sealed class Group(string name, params IConsoleTabInfo[] tabs) : ICliGroupInfo
    {
        public string DirectoryPath => "C:\\" + name;
        public string DisplayName => name;
        public IReadOnlyList<IConsoleTabInfo> TabsView => tabs;
        public int ActiveTabIndex => 0;
        public string? DockLayoutJson => null;
    }

    [Fact]
    public void Detects_working_and_blocked_across_workspaces_and_counts_attention()
    {
        var a = new Screen("a") { Text = "build output\nesc to interrupt" };
        var b = new Screen("b") { Text = "Overwrite file? (y/n)" };
        var groups = new ICliGroupInfo[]
        {
            new Group("ws1", new Tab("bash", a)),
            new Group("ws2", new Tab("not-started", null, started: false), new Tab("zsh", b)),
        };
        var tracker = new AgentActivityTracker { LiveStatus = null };   // screen rules only — no real ~/.claude here

        Assert.True(tracker.Update(groups, active: (0, 0)));
        Assert.Equal(AgentActivity.Working, tracker.Lookup(0, 0)!.Activity);
        Assert.Equal(AgentActivity.Blocked, tracker.Lookup(1, 1)!.Activity);
        Assert.Null(tracker.Lookup(1, 0));                       // never started: not tracked
        Assert.Equal(1, tracker.AttentionCount());
        Assert.Equal(AgentActivity.Blocked, tracker.Snapshot()[0].Activity); // most urgent first

        Assert.False(tracker.Update(groups, active: (0, 0)));   // nothing changed → no repaint

        b.Text = "done.\n$ ";
        Assert.True(tracker.Update(groups, active: (0, 0)));
        Assert.Equal(0, tracker.AttentionCount());
    }

    [Fact]
    public void A_closed_terminal_drops_out()
    {
        var a = new Screen("a") { Text = "esc to interrupt" };
        var tracker = new AgentActivityTracker { LiveStatus = null };   // screen rules only — no real ~/.claude here
        tracker.Update(new ICliGroupInfo[] { new Group("ws", new Tab("bash", a)) }, null);
        Assert.NotNull(tracker.Lookup(0, 0));

        Assert.True(tracker.Update(new ICliGroupInfo[] { new Group("ws") }, null));
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void Cli_json_matches_the_wpf_agent_state_contract()
    {
        var tracker = new AgentActivityTracker { LiveStatus = null };   // screen rules only — no real ~/.claude here
        tracker.Update(new ICliGroupInfo[] { new Group("ws", new Tab("배시 \"q\"", new Screen("x") { Text = "continue? (y/n)" })) }, null);

        using var doc = JsonDocument.Parse(tracker.ToCliJson());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(1, root.GetProperty("attention").GetInt32());
        var t = root.GetProperty("tabs")[0];
        Assert.Equal(0, t.GetProperty("group").GetInt32());
        Assert.Equal("배시 \"q\"", t.GetProperty("title").GetString());
        Assert.Equal("blocked", t.GetProperty("state").GetString());
        Assert.True(t.GetProperty("seen").GetBoolean());
        Assert.Equal("blocked_prompt", t.GetProperty("rule").GetString());
    }
}

[Collection(AgentManifestCatalogCollection.Name)]
public sealed class AgentActivityTrackerGraceTests
{
    private sealed class Screen : Agent.Common.Services.ITerminalSession, Agent.Common.Services.ITerminalTitleSource
    {
        public string Text = "";
        public string? Title;
        public string? WindowTitle => Title;
        public string SessionId => "s";
        public string InternalId => "s";
        public bool IsRunning => true;
        public void Write(ReadOnlySpan<char> text) { }
        public void WriteAndSubmit(string text) { }
        public void WriteAndEnter(string text) { }
        public Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct = default) => Task.CompletedTask;
        public void SendControl(Agent.Common.Services.TerminalControl control) { }
        public event Action<Agent.Common.Services.TerminalOutputFrame>? OutputReceived { add { } remove { } }
        public int OutputLength => Text.Length;
        public string ReadOutput(int start, int length) => Text.Substring(start, length);
        public string GetConsoleText() => Text;
        public void NoteInputAttempt(string source) { }
        public Agent.Common.Services.TerminalHealthState HealthState => default;
        public event Action<Agent.Common.Services.TerminalHealthState>? HealthChanged { add { } remove { } }
    }

    private sealed class Tab(Screen s) : Agent.Common.Module.IConsoleTabInfo
    {
        public string Title => "Claude";
        public int CliDefinitionId => 1;
        public Agent.Common.Services.ITerminalSession? Session => s;
        public bool IsTerminalStarted => true;
    }

    private sealed class Group(Screen s) : Agent.Common.Module.ICliGroupInfo
    {
        public string DirectoryPath => @"C:\w";
        public string DisplayName => "w";
        public IReadOnlyList<Agent.Common.Module.IConsoleTabInfo> TabsView => new[] { new Tab(s) };
        public int ActiveTabIndex => 0;
        public string? DockLayoutJson => null;
    }

    private const string Working = "✶ Pondering… (esc to interrupt)\n> \n";
    private const string Idle = "✻ Cooked for 3s\n> \n";

    [Fact]
    public void One_quiet_pass_keeps_working_the_second_lets_go()
    {
        var s = new Screen { Text = Working };
        var groups = new Agent.Common.Module.ICliGroupInfo[] { new Group(s) };
        var tracker = new AgentActivityTracker { LiveStatus = null };   // screen rules only — no real ~/.claude here
        tracker.Update(groups, null);
        Assert.Equal(AgentActivity.Working, tracker.Lookup(0, 0)!.Activity);

        s.Text = Idle;                 // a redraw caught mid-frame, or the real finish
        tracker.Update(groups, null);
        Assert.Equal(AgentActivity.Working, tracker.Lookup(0, 0)!.Activity);
        tracker.Update(groups, null);
        Assert.Equal(AgentActivity.Idle, tracker.Lookup(0, 0)!.Activity);
    }

    [Fact]
    public void A_flicker_back_to_working_resets_the_grace()
    {
        var s = new Screen { Text = Working };
        var groups = new Agent.Common.Module.ICliGroupInfo[] { new Group(s) };
        var tracker = new AgentActivityTracker { LiveStatus = null };   // screen rules only — no real ~/.claude here
        tracker.Update(groups, null);
        foreach (var text in new[] { Idle, Working, Idle })
        {
            s.Text = text;
            tracker.Update(groups, null);
            Assert.Equal(AgentActivity.Working, tracker.Lookup(0, 0)!.Activity);
        }
    }

    [Fact]
    public void Blocked_is_shown_at_once()
    {
        var s = new Screen { Text = Working };
        var groups = new Agent.Common.Module.ICliGroupInfo[] { new Group(s) };
        var tracker = new AgentActivityTracker { LiveStatus = null };   // screen rules only — no real ~/.claude here
        tracker.Update(groups, null);
        s.Text = "Do you want to proceed?\n❯ 1. Yes\nEsc to cancel\n";
        tracker.Update(groups, null);
        Assert.Equal(AgentActivity.Blocked, tracker.Lookup(0, 0)!.Activity);
    }

    [Fact]
    public void The_terminal_title_reaches_the_detector()
    {
        var s = new Screen { Text = Idle, Title = "⠂ Refactor the bridge" };
        var tracker = new AgentActivityTracker { LiveStatus = null };   // screen rules only — no real ~/.claude here
        tracker.Update(new Agent.Common.Module.ICliGroupInfo[] { new Group(s) }, null);
        Assert.Equal(AgentActivity.Working, tracker.Lookup(0, 0)!.Activity);
        Assert.Equal("osc_title_spinner", tracker.Lookup(0, 0)!.Rule);
    }

    [Theory]
    [InlineData("◐ Claude Code", AgentActivity.Working)]                         // 2.1.283 while busy
    [InlineData("◓ Ping round-trip time explanation", AgentActivity.Working)]
    [InlineData("✳ Ping round-trip time explanation", AgentActivity.Idle)]       // the resting mark
    public void The_current_title_spinner_reads_as_working(string title, AgentActivity expected)
    {
        var s = new Screen { Text = Idle, Title = title };
        var tracker = new AgentActivityTracker { LiveStatus = null };
        tracker.Update(new Agent.Common.Module.ICliGroupInfo[] { new Group(s) }, null);
        Assert.Equal(expected, tracker.Lookup(0, 0)!.Activity);
    }
}

/// <summary>
/// AgentManifestJsonTests points AgentManifestCatalog.OverrideDir at a scratch folder and writes a
/// claude.json there; anything that resolves a manifest by tab title (the tracker) must not run
/// beside it, or a "Claude" tab picks up the test's override.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AgentManifestCatalogCollection
{
    public const string Name = "AgentManifestCatalog";
}

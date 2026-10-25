using System.IO;
using Agent.Common.Agents;
using Agent.Common.Module;
using Agent.Common.Platform;
using Agent.Common.Services;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>
/// Claude Code's own status files (~/.claude/sessions/&lt;pid&gt;.json) as the agent-state source:
/// parsing, finding the file for a tab through its process tree, and the tracker's use of it.
/// </summary>
[Collection(AgentManifestCatalogCollection.Name)]
public sealed class ClaudeLiveStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aztest-claude-sessions-" + Guid.NewGuid().ToString("n"));

    public ClaudeLiveStatusTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>The shape measured from Claude Code 2.1.283.</summary>
    private void WriteStatus(int pid, string status, string cwd = @"C:\work", string? waitingFor = null, long at = 1)
        => File.WriteAllText(Path.Combine(_dir, pid + ".json"),
            "{\"pid\":" + pid + ",\"sessionId\":\"s-" + pid + "\",\"cwd\":" + System.Text.Json.JsonSerializer.Serialize(cwd)
            + ",\"kind\":\"interactive\",\"status\":\"" + status + "\",\"updatedAt\":" + at + ",\"statusUpdatedAt\":" + at
            + (waitingFor is null ? "" : ",\"waitingFor\":\"" + waitingFor + "\"") + "}");

    // app (1) → shell 100 → claude 200;  app (1) → shell 300 → claude 400;  stranger 900 → claude 901
    private static ProcessTable Tree() => new(new[]
    {
        (1, 0), (100, 1), (150, 100), (200, 150), (300, 1), (400, 300), (900, 0), (901, 900),
    });

    private ClaudeLiveStatusSource Source(Func<ProcessTable>? table = null) =>
        new(new[] { _dir }, table ?? Tree, nowMs: () => 0, selfPid: 1) { TableTtlMs = 0 };

    [Fact]
    public void Parse_ReadsTheMeasuredShape()
    {
        var f = ClaudeStatusFile.Parse("{\"pid\":12908,\"sessionId\":\"e7\",\"cwd\":\"C:\\\\Users\\\\u\\\\infra\",\"status\":\"waiting\",\"statusUpdatedAt\":1792403988614,\"waitingFor\":\"input needed\"}")!;
        Assert.Equal(12908, f.Pid);
        Assert.Equal(@"C:\Users\u\infra", f.Cwd);
        Assert.Equal(AgentActivity.Blocked, f.Activity);
        Assert.Equal("input needed", f.WaitingFor);
        Assert.Null(ClaudeStatusFile.Parse("{\"pid\":1,\"status\":\"bu"));      // caught mid-write
        Assert.Null(new ClaudeStatusFile(1, null, null, "compacting?", null, 0).Activity);   // unknown → not ours to guess
    }

    [Fact]
    public void Finds_the_claude_below_the_tabs_shell_and_maps_its_status()
    {
        WriteStatus(200, "busy");
        WriteStatus(400, "waiting", waitingFor: "input needed");
        var src = Source();
        src.BeginPass();

        var a = src.StatusOf("a", 100, @"C:\work", false)!;
        Assert.Equal((AgentActivity.Working, "claude-status:busy"), (a.Activity, a.Rule));
        // It names the conversation, so the stage can find its sub-agents.
        Assert.Equal(new AgentSessionRef("claude", Path.GetDirectoryName(_dir)!, "s-200", @"C:\work"), a.Session);
        Assert.Equal(AgentActivity.Blocked, src.StatusOf("b", 300, @"C:\work", false)!.Activity);
        Assert.Null(src.StatusOf("c", 900 + 50, @"C:\work", false));   // a shell with no claude below it
    }

    [Fact]
    public void Follows_the_file_as_it_changes_and_lets_go_when_claude_exits()
    {
        WriteStatus(200, "busy");
        var alive = true;
        var src = Source(() => alive ? Tree() : new ProcessTable(new[] { (1, 0), (100, 1) }));
        src.BeginPass();
        Assert.Equal(AgentActivity.Working, src.StatusOf("a", 100, @"C:\work", false)!.Activity);

        WriteStatus(200, "idle", at: 2);
        src.BeginPass();
        Assert.Equal(AgentActivity.Idle, src.StatusOf("a", 100, @"C:\work", false)!.Activity);

        alive = false;   // claude quit; its file may linger
        src.BeginPass();
        Assert.Null(src.StatusOf("a", 100, @"C:\work", false));
    }

    [Fact]
    public void Without_a_pid_a_folder_match_counts_only_when_it_is_unambiguous()
    {
        // A native path: the workspace keeps the folder as typed (trailing separator), Claude writes it bare.
        var work = Path.Combine(Path.GetTempPath(), "aztest-work");
        WriteStatus(200, "busy", cwd: work);
        WriteStatus(901, "busy", cwd: work);   // a claude in some other app's terminal — not below us
        var src = Source();
        src.BeginPass();

        Assert.Equal(AgentActivity.Working, src.StatusOf("a", null, work + Path.DirectorySeparatorChar, onlyAgentTabInFolder: true)!.Activity);
        var notUnique = Source();
        notUnique.BeginPass();
        Assert.Null(notUnique.StatusOf("b", null, work, onlyAgentTabInFolder: false));

        WriteStatus(400, "idle", cwd: work);   // two of ours in one folder: which is which is unknowable
        var two = Source();
        two.BeginPass();
        Assert.Null(two.StatusOf("c", null, work, onlyAgentTabInFolder: true));
    }

    // ── the tracker ──────────────────────────────────────────────────────────

    private sealed class FakeSource : IAgentLiveStatusSource
    {
        public AgentLiveStatus? Next;
        public void BeginPass() { }
        public AgentLiveStatus? StatusOf(string key, int? processId, string workingDirectory, bool onlyAgentTabInFolder) => Next;
        public void Forget(string key) { }
    }

    private sealed class Screen(string text) : ITerminalSession, ITerminalProcessSource
    {
        public string Text = text;
        public int? ProcessId => 100;
        public string SessionId => "s";
        public string InternalId => "s";
        public bool IsRunning => true;
        public void Write(ReadOnlySpan<char> t) { }
        public void WriteAndSubmit(string t) { }
        public void WriteAndEnter(string t) { }
        public Task WriteAsync(ReadOnlyMemory<char> t, CancellationToken ct = default) => Task.CompletedTask;
        public void SendControl(TerminalControl control) { }
        public event Action<TerminalOutputFrame>? OutputReceived { add { } remove { } }
        public int OutputLength => Text.Length;
        public string ReadOutput(int start, int length) => Text.Substring(start, length);
        public string GetConsoleText() => Text;
        public void NoteInputAttempt(string source) { }
        public TerminalHealthState HealthState => default;
        public event Action<TerminalHealthState>? HealthChanged { add { } remove { } }
    }

    private sealed class Tab(ITerminalSession s) : IConsoleTabInfo
    {
        public string Title => "Claude";
        public int CliDefinitionId => 1;
        public ITerminalSession? Session => s;
        public bool IsTerminalStarted => true;
    }

    private sealed class Group(IConsoleTabInfo tab) : ICliGroupInfo
    {
        public string DirectoryPath => @"C:\work";
        public string DisplayName => "work";
        public IReadOnlyList<IConsoleTabInfo> TabsView => new[] { tab };
        public int ActiveTabIndex => 0;
        public string? DockLayoutJson => null;
    }

    [Fact]
    public void Tracker_trusts_the_status_over_the_screen_and_turns_a_finished_turn_into_done()
    {
        var src = new FakeSource();
        // A screen with only the prompt caret — the screen rules would say idle.
        var groups = new ICliGroupInfo[] { new Group(new Tab(new Screen("> "))) };
        var tracker = new AgentActivityTracker { LiveStatus = src };

        src.Next = new AgentLiveStatus(AgentActivity.Idle, "claude-status:idle");
        tracker.Update(groups, active: null);
        Assert.Equal(AgentActivity.Idle, tracker.Lookup(0, 0)!.Activity);   // nothing ran yet: idle, not done

        src.Next = new AgentLiveStatus(AgentActivity.Working, "claude-status:busy");
        tracker.Update(groups, active: null);
        Assert.Equal(AgentActivity.Working, tracker.Lookup(0, 0)!.Activity);
        Assert.Equal("claude-status:busy", tracker.Lookup(0, 0)!.Rule);

        src.Next = new AgentLiveStatus(AgentActivity.Idle, "claude-status:idle");
        tracker.Update(groups, active: null);
        var done = tracker.Lookup(0, 0)!;
        Assert.Equal(AgentActivity.Done, done.Activity);
        Assert.True(done.NeedsAttention);                                    // finished and not looked at

        tracker.Update(groups, active: (0, 0));                              // focusing it marks it seen
        Assert.Equal(AgentActivity.Done, tracker.Lookup(0, 0)!.Activity);
        Assert.False(tracker.Lookup(0, 0)!.NeedsAttention);

        src.Next = new AgentLiveStatus(AgentActivity.Blocked, "claude-status:waiting (input needed)");
        tracker.Update(groups, active: null);
        Assert.Equal(AgentActivity.Blocked, tracker.Lookup(0, 0)!.Activity);
    }

    [Fact]
    public void Tracker_falls_back_to_the_screen_when_the_agent_says_nothing()
    {
        var src = new FakeSource { Next = null };
        var groups = new ICliGroupInfo[] { new Group(new Tab(new Screen("✶ Gallivanting… (3s · esc to interrupt)"))) };
        var tracker = new AgentActivityTracker { LiveStatus = src };
        tracker.Update(groups, active: null);
        Assert.Equal(AgentActivity.Working, tracker.Lookup(0, 0)!.Activity);
        Assert.NotEqual("claude-status:busy", tracker.Lookup(0, 0)!.Rule);
    }
}

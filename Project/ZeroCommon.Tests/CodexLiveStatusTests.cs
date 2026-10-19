using System.Globalization;
using System.IO;
using Agent.Common.Agents;
using Agent.Common.Module;
using Agent.Common.Platform;
using Agent.Common.Services;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>
/// Codex's rollout as the agent-state source: which thread is a tab's (the codex TUI below its
/// shell and the thread created right after it started), and the turn events read from it.
/// </summary>
[Collection(AgentManifestCatalogCollection.Name)]
public sealed class CodexLiveStatusTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "aztest-codex-" + Guid.NewGuid().ToString("n"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "aztest-codex-work");
    private readonly DateTime _t0 = DateTime.UtcNow.AddMinutes(-10);

    public CodexLiveStatusTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch { }
    }

    /// <summary>A rollout the way Codex 0.162 writes it (local-date folder, session_meta first).</summary>
    private string Rollout(string id, DateTime createdUtc, string cwd, string source = "\"cli\"", string threadSource = "user", params string[] events)
    {
        var local = createdUtc.ToLocalTime();
        var dir = Path.Combine(_home, "sessions", local.ToString("yyyy", CultureInfo.InvariantCulture),
            local.ToString("MM", CultureInfo.InvariantCulture), local.ToString("dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rollout-{local:yyyy-MM-ddTHH-mm-ss}-{id}.jsonl");
        var meta = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"timestamp\":\"" + createdUtc.ToString("o")
                   + "\",\"cwd\":" + System.Text.Json.JsonSerializer.Serialize(cwd) + ",\"source\":" + source
                   + ",\"thread_source\":\"" + threadSource + "\",\"base_instructions\":{\"text\":\"…\"}}}";
        File.WriteAllLines(path, new[] { meta }.Concat(events.Select(Event)));
        return path;
    }

    private static string Event(string type) => "{\"type\":\"event_msg\",\"payload\":{\"type\":\"" + type + "\"}}";

    // app 1 → shell 100 → node 110 → codex 120 (the TUI) → codex 130 (a helper it starts)
    //       → shell 200 → node 210 → codex 220;   daemon 900 "codex" not below the app
    private static ProcessTable Tree() => new(new (int, int, string)[]
    {
        (1, 0, "AgentZeroLite.exe"),
        (100, 1, "powershell.exe"), (110, 100, "node.exe"), (120, 110, "codex.exe"), (130, 120, "codex.exe"),
        (200, 1, "powershell.exe"), (210, 200, "node.exe"), (220, 210, "codex.exe"),
        (900, 0, "codex.exe"),
    });

    private CodexLiveStatusSource Source(Dictionary<int, DateTime> starts) =>
        new(_home, Tree, nowMs: () => 0, selfPid: 1, startTimeOf: pid => starts.TryGetValue(pid, out var t) ? t : null) { RefreshMs = 0 };

    [Fact]
    public void A_turn_reads_as_working_until_its_task_completes()
    {
        var path = Rollout("01a153b3-a313", _t0.AddSeconds(1), _work, events: new[] { "task_started", "item_completed", "token_count" });
        var src = Source(new() { [120] = _t0 });

        var busy = src.StatusOf("tab", 100, _work, false)!;
        Assert.Equal(AgentActivity.Working, busy.Activity);
        Assert.Equal("codex-rollout:task_started", busy.Rule);

        File.AppendAllLines(path, new[] { Event("agent_message"), Event("task_complete"), Event("token_count") });
        Assert.Equal(AgentActivity.Idle, src.StatusOf("tab", 100, _work, false)!.Activity);
    }

    [Fact]
    public void Two_codex_tabs_in_one_folder_each_get_the_thread_created_after_their_own_start()
    {
        Rollout("thread-a", _t0.AddSeconds(1), _work, events: new[] { "task_started" });
        Rollout("thread-b", _t0.AddSeconds(61), _work, events: new[] { "task_started", "task_complete" });
        var src = Source(new() { [120] = _t0, [130] = _t0.AddSeconds(2), [220] = _t0.AddSeconds(60) });

        Assert.Equal(AgentActivity.Working, src.StatusOf("first", 100, _work, false)!.Activity);    // thread-a
        Assert.Equal(AgentActivity.Idle, src.StatusOf("second", 200, _work, false)!.Activity);      // thread-b
    }

    [Fact]
    public void Codexs_own_sub_agents_and_other_folders_are_not_the_tabs_thread()
    {
        Rollout("guardian", _t0.AddSeconds(2), _work, source: "{\"subagent\":{\"other\":\"guardian\"}}",
            threadSource: "guardian_review", events: new[] { "task_started" });
        Rollout("elsewhere", _t0.AddSeconds(3), Path.Combine(_work, "other"), events: new[] { "task_started" });
        var src = Source(new() { [120] = _t0 });

        Assert.Null(src.StatusOf("tab", 100, _work, false));
    }

    [Fact]
    public void A_thread_before_any_turn_reads_idle_and_a_tab_without_codex_says_nothing()
    {
        Rollout("fresh", _t0.AddSeconds(1), _work);
        var src = Source(new() { [120] = _t0 });
        Assert.Equal(new AgentLiveStatus(AgentActivity.Idle, "codex-rollout:no turn yet"), src.StatusOf("tab", 100, _work, false));
        Assert.Null(src.StatusOf("cmd", 999, _work, false));
        Assert.Null(src.StatusOf("nopid", null, _work, true));
    }

    [Fact]
    public void An_aborted_turn_is_over()
    {
        var path = Rollout("x", _t0, _work, events: new[] { "task_started", "turn_aborted" });
        Assert.Equal((AgentActivity.Idle, "turn_aborted"), CodexLiveStatusSource.ReadState(path));
    }

    // ── the tracker: an approval on screen wins over a running turn ──────────

    private sealed class Fixed(AgentLiveStatus status) : IAgentLiveStatusSource
    {
        public void BeginPass() { }
        public AgentLiveStatus? StatusOf(string key, int? processId, string workingDirectory, bool onlyAgentTabInFolder) => status;
        public void Forget(string key) { }
    }

    private sealed class Screen(string text) : ITerminalSession
    {
        public string SessionId => "s";
        public string InternalId => "s";
        public bool IsRunning => true;
        public void Write(ReadOnlySpan<char> t) { }
        public void WriteAndSubmit(string t) { }
        public void WriteAndEnter(string t) { }
        public Task WriteAsync(ReadOnlyMemory<char> t, CancellationToken ct = default) => Task.CompletedTask;
        public void SendControl(TerminalControl control) { }
        public event Action<TerminalOutputFrame>? OutputReceived { add { } remove { } }
        public int OutputLength => text.Length;
        public string ReadOutput(int start, int length) => text.Substring(start, length);
        public string GetConsoleText() => text;
        public void NoteInputAttempt(string source) { }
        public TerminalHealthState HealthState => default;
        public event Action<TerminalHealthState>? HealthChanged { add { } remove { } }
    }

    private sealed class Tab(ITerminalSession s) : IConsoleTabInfo
    {
        public string Title => "Codex-2";
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
    public void An_approval_on_screen_wins_over_a_running_turn()
    {
        var groups = new ICliGroupInfo[] { new Group(new Tab(new Screen("Allow command?\n  ping -n 15 127.0.0.1\n  [y/n]"))) };
        var tracker = new AgentActivityTracker { LiveStatus = new Fixed(new AgentLiveStatus(AgentActivity.Working, "codex-rollout:task_started")) };
        tracker.Update(groups, active: null);
        Assert.Equal(AgentActivity.Blocked, tracker.Lookup(0, 0)!.Activity);
        Assert.Equal("blocked_approve", tracker.Lookup(0, 0)!.Rule);
    }
}

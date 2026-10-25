using System.IO;
using System.Text.Json;
using Agent.Common.Agents;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>
/// Claude Code's sub-agent transcripts (projects/&lt;slug&gt;/&lt;session&gt;/subagents/agent-*.{meta.json,jsonl})
/// as the Agent Stage reads them: running vs finished, nesting, and incremental reads.
/// </summary>
public sealed class ClaudeSubagentReaderTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string _config = Path.Combine(Path.GetTempPath(), "aztest-claude-sub-" + Guid.NewGuid().ToString("n"));
    private readonly AgentSessionRef _session;
    private readonly string _dir;
    private DateTimeOffset _now = T0.AddSeconds(30);

    public ClaudeSubagentReaderTests()
    {
        _session = new AgentSessionRef("claude", _config, "sess-1", @"C:\code\my.repo");
        _dir = ClaudeSubagentReader.FolderOf(_session);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_config, true); } catch { }
    }

    private ClaudeSubagentReader Reader() => new(() => _now);

    private void Meta(string id, string type, string description, string toolUseId, int depth = 1)
        => File.WriteAllText(Path.Combine(_dir, $"agent-{id}.meta.json"), JsonSerializer.Serialize(new
        {
            agentType = type, description, toolUseId, spawnDepth = depth, requestShape = "background",
        }));

    private void Append(string id, params string[] lines) => AppendRaw(id, string.Concat(lines.Select(l => l + "\n")));

    /// <summary>Appends and stamps the file with the test clock, which the reader compares mtimes against.</summary>
    private void AppendRaw(string id, string text)
    {
        var path = Path.Combine(_dir, $"agent-{id}.jsonl");
        File.AppendAllText(path, text);
        File.SetLastWriteTimeUtc(path, _now.UtcDateTime);
    }

    private static string At(int s) => T0.AddSeconds(s).ToString("O");

    private static string User(int s) =>
        $$$"""{"type":"user","timestamp":"{{{At(s)}}}","message":{"role":"user","content":"go"}}""";

    private static string ToolUse(int s, string name, string input, string id = "toolu_x") =>
        $$$"""{"type":"assistant","timestamp":"{{{At(s)}}}","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"{{{id}}}","name":"{{{name}}}","input":{{{input}}}}]}}""";

    private static string EndTurn(int s) =>
        $$$"""{"type":"assistant","timestamp":"{{{At(s)}}}","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"report"}]}}""";

    [Fact]
    public void The_folder_is_the_slugged_cwd_under_the_session()
        => Assert.Equal(Path.Combine(_config, "projects", "C--code-my-repo", "sess-1", "subagents"), _dir);

    [Fact]
    public void A_running_subagent_reports_its_last_tool_and_count()
    {
        Meta("a1", "Explore", "Survey voice settings", "toolu_main1");
        Append("a1", User(0), ToolUse(2, "Grep", """{"pattern":"VoiceSettings"}"""),
            ToolUse(4, "Read", """{"file_path":"C:\\code\\my.repo\\src\\Voice.cs"}"""));

        var v = Assert.Single(Reader().Read(_session));
        Assert.Equal("a1", v.Id);
        Assert.Equal("Explore", v.Type);
        Assert.Equal("Survey voice settings", v.Description);
        Assert.Equal("working", v.State);
        Assert.Equal("Read · Voice.cs", v.Tool);
        Assert.Equal(2, v.Tools);
        Assert.Null(v.ParentId);
        Assert.True(v.Background);
        Assert.Equal(T0.ToUnixTimeMilliseconds(), v.StartedAt);
        Assert.Null(v.EndedAt);
    }

    [Fact]
    public void An_end_turn_is_done_and_drops_out_after_the_keep_window()
    {
        Meta("a1", "Plan", "Design it", "toolu_main1");
        Append("a1", User(0), EndTurn(10));
        var reader = Reader();

        var v = Assert.Single(reader.Read(_session));
        Assert.Equal("done", v.State);
        Assert.Equal(T0.AddSeconds(10).ToUnixTimeMilliseconds(), v.EndedAt);

        _now = T0.AddSeconds(10).AddMilliseconds(reader.KeepFinishedMs + 1);
        Assert.Empty(reader.Read(_session));
    }

    [Fact]
    public void A_handback_call_is_done_even_when_a_tool_result_follows()
    {
        Meta("a1", "Explore", "x", "toolu_main1");
        Append("a1", User(0), ToolUse(5, "SubagentHandback", """{"message":"report"}"""), User(6));

        Assert.Equal("done", Assert.Single(Reader().Read(_session)).State);
    }

    [Fact]
    public void A_transcript_that_went_quiet_without_an_end_is_stopped()
    {
        Meta("a1", "Explore", "x", "toolu_main1");
        Append("a1", User(0), ToolUse(1, "Bash", """{"command":"dotnet test"}"""));
        var reader = Reader();
        _now = T0.AddSeconds(1).AddMilliseconds(reader.QuietStopMs + 1000);

        var v = Assert.Single(reader.Read(_session));
        Assert.Equal("stopped", v.State);
        Assert.Equal(T0.AddSeconds(1).ToUnixTimeMilliseconds() + reader.QuietStopMs, v.EndedAt);   // when it was called stopped
    }

    [Fact]
    public void A_nested_subagent_names_the_one_that_started_it()
    {
        Meta("p1", "general-purpose", "Lead", "toolu_main1");
        Append("p1", User(0), ToolUse(1, "Agent", """{"description":"Child work","subagent_type":"Explore"}""", id: "toolu_child"));
        Meta("c1", "Explore", "Child work", "toolu_child", depth: 2);
        Append("c1", User(2));

        var list = Reader().Read(_session);
        Assert.Equal(["p1", "c1"], list.Select(v => v.Id));
        Assert.Null(list[0].ParentId);
        Assert.Equal("p1", list[1].ParentId);
        Assert.Equal(2, list[1].Depth);
        Assert.Equal("Agent · Child work", list[0].Tool);
    }

    [Fact]
    public void Appended_lines_are_folded_and_a_torn_last_line_waits_for_its_end()
    {
        Meta("a1", "Explore", "x", "toolu_main1");
        Append("a1", User(0));
        var reader = Reader();
        Assert.Equal(0, Assert.Single(reader.Read(_session)).Tools);

        // Half a record: not counted until its newline arrives.
        var line = ToolUse(3, "Glob", """{"pattern":"**/*.cs"}""");
        AppendRaw("a1", line[..20]);
        Assert.Equal(0, Assert.Single(reader.Read(_session)).Tools);

        AppendRaw("a1", line[20..] + "\n" + EndTurn(9) + "\n");
        var v = Assert.Single(reader.Read(_session));
        Assert.Equal(1, v.Tools);
        Assert.Equal("Glob · **/*.cs", v.Tool);
        Assert.Equal("done", v.State);
    }

    [Fact]
    public void A_session_without_subagents_is_empty()
        => Assert.Empty(Reader().Read(_session with { SessionId = "no-such-session" }));

    [Fact]
    public void The_status_file_names_the_session_with_its_profile()
    {
        var f = ClaudeStatusFile.Parse("""{"pid":7,"sessionId":"s-7","cwd":"C:\\w","status":"busy","statusUpdatedAt":1}""")!
            with { ConfigDir = _config };
        Assert.Equal(new AgentSessionRef("claude", _config, "s-7", @"C:\w"), f.Session);
        Assert.Null((f with { SessionId = null }).Session);
    }
}

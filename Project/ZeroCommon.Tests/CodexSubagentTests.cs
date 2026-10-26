using System.Globalization;
using System.IO;
using Agent.Common.Agents;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>
/// Codex's multi-agent sub-agents as Agent Stage's crew — rollouts shaped like a measured 0.164 run:
/// SubAgentActivity started/completed in the parent, a rollout per sub-agent that opens with a copy of
/// the parent's history.
/// </summary>
public sealed class CodexSubagentTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "aztest-codex-sub-" + Guid.NewGuid().ToString("n"));
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 45, 0, TimeSpan.Zero);
    private const string Root = "01a17798-root", Seoul = "01a17799-seoul", Nested = "01a1779a-nested";

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch { }
    }

    private void Rollout(string id, params string[] lines)
    {
        var day = Now.LocalDateTime;
        var dir = Path.Combine(_home, "sessions", day.ToString("yyyy", CultureInfo.InvariantCulture),
            day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, $"rollout-2026-10-26T18-44-07-{id}.jsonl"), lines);
    }

    private static string Q(string v) => System.Text.Json.JsonSerializer.Serialize(v);

    private static string Meta(string id, string? parent = null, string? nick = null, int depth = 1) => parent is null
        ? "{\"timestamp\":\"2026-10-26T09:44:07Z\",\"type\":\"session_meta\",\"payload\":{\"id\":" + Q(id)
          + ",\"source\":\"vscode\",\"thread_source\":\"user\"}}"
        : "{\"timestamp\":\"2026-10-26T09:44:47Z\",\"type\":\"session_meta\",\"payload\":{\"id\":" + Q(id)
          + ",\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":" + Q(parent) + ",\"depth\":" + depth
          + ",\"agent_path\":\"/root/x\",\"agent_nickname\":" + Q(nick!) + ",\"agent_role\":null}}},"
          + "\"thread_source\":\"subagent\",\"agent_nickname\":" + Q(nick!) + "}}";

    private static string Activity(string kind, string child, string path, long startedMs, long? completedMs = null) =>
        "{\"timestamp\":\"2026-10-26T09:44:48Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\","
        + "\"item\":{\"type\":\"SubAgentActivity\",\"kind\":" + Q(kind) + ",\"agent_thread_id\":" + Q(child)
        + ",\"agent_path\":" + Q(path) + "},\"started_at_ms\":" + startedMs
        + (completedMs is { } c ? ",\"completed_at_ms\":" + c : "") + "}}";

    private static string Settings(string thread) =>
        "{\"timestamp\":\"2026-10-26T09:44:47Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\",\"thread_id\":"
        + Q(thread) + "}}";

    private static string Call(string name, string args) =>
        "{\"timestamp\":\"2026-10-26T09:44:55Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":"
        + Q(name) + ",\"arguments\":" + Q(args) + "}}";

    private IReadOnlyList<SubagentView> Read() =>
        new CodexSubagentReader(() => Now).Read(new AgentSessionRef("codex", _home, Root, @"C:\w"));

    private static long Ms(int secondsBeforeNow) => Now.AddSeconds(-secondsBeforeNow).ToUnixTimeMilliseconds();

    [Fact]
    public void A_spawned_agent_works_until_the_parent_records_it_completed()
    {
        Rollout(Root, Meta(Root), Activity("started", Seoul, "/root/seoul_weather", Ms(12)));
        Rollout(Seoul, Meta(Seoul, Root, "Anscombe"),
            Meta(Root),                                       // the copy of the parent's history…
            Call("spawn_agent", "{\"task_name\":\"seoul_weather\"}"),
            Settings(Seoul),                                  // …ends here
            Call("web_search", "{\"query\":\"Seoul weather today\"}"));

        var k = Assert.Single(Read());
        Assert.Equal("working", k.State);
        Assert.Equal("seoul_weather — Anscombe", k.Description);
        Assert.Equal("general-purpose", k.Type);
        Assert.Equal("web_search · Seoul weather today", k.Tool);
        Assert.Equal(1, k.Tools);   // the copied spawn_agent is the parent's, not its own
        Assert.Null(k.ParentId);

        Rollout(Root, Meta(Root), Activity("started", Seoul, "/root/seoul_weather", Ms(12)),
            Activity("completed", Seoul, "/root/seoul_weather", Ms(12), Ms(2)));
        var done = Assert.Single(new CodexSubagentReader(() => Now).Read(new AgentSessionRef("codex", _home, Root, @"C:\w")));
        Assert.Equal("done", done.State);
        Assert.Equal(Ms(2), done.EndedAt);
    }

    [Fact]
    public void A_nested_agent_hangs_off_the_agent_that_spawned_it()
    {
        Rollout(Root, Meta(Root), Activity("started", Seoul, "/root/seoul_weather", Ms(20)));
        Rollout(Seoul, Meta(Seoul, Root, "Anscombe"), Settings(Seoul), Activity("started", Nested, "/root/seoul_weather/detail", Ms(10)));
        Rollout(Nested, Meta(Nested, Seoul, "Noether", depth: 2), Settings(Nested));

        var nested = Read().Single(k => k.Id == Nested);
        Assert.Equal(Seoul, nested.ParentId);
        Assert.Equal(2, nested.Depth);
        Assert.Equal("detail — Noether", nested.Description);
    }

    [Fact]
    public void Finished_long_ago_is_left_out_and_an_unwritten_rollout_still_shows()
    {
        Rollout(Root, Meta(Root),
            Activity("started", "old", "/root/old", Ms(900)), Activity("completed", "old", "/root/old", Ms(900), Ms(800)),
            Activity("started", "fresh", "/root/fresh", Ms(1)));
        var k = Assert.Single(Read());
        Assert.Equal("fresh", k.Id);
        Assert.Equal("fresh", k.Description);
        Assert.Equal("working", k.State);
    }
}

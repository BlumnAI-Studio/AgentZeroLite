using System.IO;
using Agent.Common.Agents;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>
/// Hermes' delegate_task sub-agents as Agent Stage's crew — rows shaped like a measured run of two
/// parallel sub-agents (source 'subagent', parent_session_id = the tab's conversation).
/// </summary>
public sealed class HermesSubagentTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "aztest-hermes-sub-" + Guid.NewGuid().ToString("n"));
    private const string Main = "20261026_181946_38baa9";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_793_006_600);

    public HermesSubagentTests()
    {
        Directory.CreateDirectory(_home);
        Exec("""
            CREATE TABLE sessions (id TEXT PRIMARY KEY, source TEXT NOT NULL, parent_session_id TEXT, started_at REAL NOT NULL,
                ended_at REAL, end_reason TEXT, tool_call_count INTEGER DEFAULT 0);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, role TEXT NOT NULL,
                content TEXT, tool_calls TEXT, timestamp REAL NOT NULL);
            """);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, true); } catch { }
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(_home, "state.db")};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void Session(string id, string? parent, double started, double? ended = null, int tools = 0) =>
        Exec("INSERT INTO sessions VALUES ($i, $src, $p, $s, $e, $r, $t)",
            ("$i", id), ("$src", parent is null ? "cli" : "subagent"), ("$p", parent), ("$s", started), ("$e", ended),
            ("$r", ended is null ? null : "agent_close"), ("$t", tools));

    private void Message(string session, string role, double at, string? content = null, string? toolCalls = null) =>
        Exec("INSERT INTO messages (session_id, role, content, tool_calls, timestamp) VALUES ($s, $r, $c, $t, $at)",
            ("$s", session), ("$r", role), ("$c", content), ("$t", toolCalls), ("$at", at));

    private IReadOnlyList<SubagentView> Read() =>
        new HermesSubagentReader(() => Now).Read(new AgentSessionRef("hermes", _home, Main, @"C:\w"));

    private static double Ago(int seconds) => Now.ToUnixTimeSeconds() - seconds;

    [Fact]
    public void Two_parallel_delegates_show_as_a_crew_one_working_one_done()
    {
        Session(Main, null, Ago(200));
        Session("sub-a", Main, Ago(100), tools: 3);
        Message("sub-a", "user", Ago(100), "서울의 오늘 날씨를 조사해 한국어로 간결하게 보고하세요.\nmore context");
        Message("sub-a", "assistant", Ago(5), toolCalls:
            """[{"id":"c1","type":"function","function":{"name":"web_search","arguments":"{\"query\": \"Seoul weather today\"}"}}]""");
        Session("sub-b", Main, Ago(100), ended: Ago(10), tools: 8);
        Message("sub-b", "user", Ago(100), "도쿄의 오늘 날씨를 조사해 한국어로 간결하게 보고하세요.");

        var crew = Read();
        Assert.Equal(2, crew.Count);
        var a = crew.Single(k => k.Id == "sub-a");
        Assert.Equal("working", a.State);
        Assert.Null(a.ParentId);   // the tab's conversation delegated it
        Assert.Equal(1, a.Depth);
        Assert.Equal("서울의 오늘 날씨를 조사해 한국어로 간결하게 보고하세요.", a.Description);
        Assert.Equal("web_search · Seoul weather today", a.Tool);
        Assert.Equal(3, a.Tools);
        var b = crew.Single(k => k.Id == "sub-b");
        Assert.Equal("done", b.State);
        Assert.Equal((long)(Ago(10) * 1000), b.EndedAt);
    }

    [Fact]
    public void A_nested_delegate_names_the_sub_agent_above_it()
    {
        Session(Main, null, Ago(200));
        Session("orch", Main, Ago(60));
        Session("leaf", "orch", Ago(30));
        Message("leaf", "user", Ago(30), "leaf task");
        var leaf = Read().Single(k => k.Id == "leaf");
        Assert.Equal("orch", leaf.ParentId);
        Assert.Equal(2, leaf.Depth);
    }

    [Fact]
    public void Old_finished_work_and_other_conversations_are_left_out()
    {
        Session(Main, null, Ago(2000));
        Session("old", Main, Ago(1000), ended: Ago(900));
        Session("theirs", "another-conversation", Ago(20));
        Assert.Empty(Read());
    }
}

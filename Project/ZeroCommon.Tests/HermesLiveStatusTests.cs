using System.IO;
using Agent.Common.Agents;
using Agent.Common.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>
/// Hermes Agent's turn, read from its lease file and its message database — the shapes measured on
/// a live Hermes tab (user → assistant tool_calls → tool → … → assistant stop; Ctrl+C → assistant
/// "Operation interrupted." with no finish reason).
/// </summary>
public sealed class HermesLiveStatusTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "aztest-hermes-" + Guid.NewGuid().ToString("n"));
    private const string Sid = "20261026_161910_543b0e";

    // shell 100 → hermes.exe 110 → venv python 120 → runtime python 130 (the lease's pid)
    private static ProcessTable Tree() => new(new (int, int, string)[]
    {
        (100, 1, "powershell.exe"), (110, 100, "hermes.exe"), (120, 110, "python.exe"), (130, 120, "python.exe"),
        (200, 1, "powershell.exe"),
    });

    public HermesLiveStatusTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, "runtime"));
        File.WriteAllText(Path.Combine(_home, "runtime", "active_sessions.json"),
            $$"""{"entries": [{"lease_id": "x", "pid": 130, "session_id": "{{Sid}}", "surface": "cli"}]}""");
        using var db = Open();
        Exec(db, "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, role TEXT NOT NULL, " +
                 "content TEXT, tool_calls TEXT, tool_name TEXT, timestamp REAL NOT NULL, finish_reason TEXT, active INTEGER NOT NULL DEFAULT 1)");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, true); } catch { }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={Path.Combine(_home, "state.db")};Pooling=False");
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Add(string role, string? finish = null, string? toolCalls = null, string session = Sid)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO messages (session_id, role, tool_calls, timestamp, finish_reason) VALUES ($s, $r, $t, 0, $f)";
        cmd.Parameters.AddWithValue("$s", session);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$t", (object?)toolCalls ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", (object?)finish ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private AgentLiveStatus? Status(int shell = 100)
    {
        var source = new HermesLiveStatusSource(new[] { _home }, Tree, () => 0);
        source.BeginPass();
        return source.StatusOf("k", shell, @"C:\w", false);
    }

    [Fact]
    public void A_turn_reads_working_until_the_assistant_stops()
    {
        Add("user");
        Assert.Equal(AgentActivity.Working, Status()!.Activity);
        Add("assistant", "tool_calls", "[{\"id\":\"c1\"}]");
        Assert.Equal("hermes-state:tool_calls", Status()!.Rule);
        Add("tool");
        Assert.Equal(AgentActivity.Working, Status()!.Activity);
        Add("assistant", "stop");
        Assert.Equal(AgentActivity.Idle, Status()!.Activity);
    }

    [Fact]
    public void An_interrupted_turn_is_over()
    {
        Add("user");
        Add("assistant", "tool_calls", "[{}]");
        Add("assistant");   // "Operation interrupted."
        Assert.Equal(AgentActivity.Idle, Status()!.Activity);
    }

    [Fact]
    public void The_lease_names_the_conversation_and_the_agent()
    {
        var s = Status()!;
        Assert.Equal("hermes-state:no turn yet", s.Rule);
        Assert.Equal(AgentActivity.Idle, s.Activity);
        Assert.Equal("hermes", s.Session!.Agent);
        Assert.Equal(Sid, s.Session.SessionId);
    }

    [Fact]
    public void Another_conversation_or_another_tab_is_not_this_one()
    {
        Add("user", session: "someone-else");
        Assert.Equal("hermes-state:no turn yet", Status()!.Rule);
        Assert.Null(Status(shell: 200));   // no hermes below that shell
    }

    [Fact]
    public void Profiles_are_homes_of_their_own_but_deleted_ones_are_not()
    {
        Directory.CreateDirectory(Path.Combine(_home, "profiles", "qwen"));
        Directory.CreateDirectory(Path.Combine(_home, "profiles", ".deleted"));
        var homes = HermesLiveStatusSource.HomesUnder(_home).ToList();
        Assert.Contains(_home, homes);
        Assert.Contains(Path.Combine(_home, "profiles", "qwen"), homes);
        Assert.DoesNotContain(homes, h => h.EndsWith(".deleted"));
    }

    [Fact]
    public void The_approval_box_is_blocked_on_screen()
    {
        var screen = new[]
        {
            "╭────────────────────────────────────────────────╮",
            "│ ⚠️  Dangerous Command                          │",
            "│ python -c print(42)                            │",
            "│ ❯ 1. Allow once                                │",
            "│   2. Allow for this session                    │",
            "│   4. Deny                                      │",
            "╰────────────────────────────────────────────────╯",
            "  💻 python -c print(42)  (  1403 · ↓ 112 tok)",
            "  ↑/↓ to select, Enter to confirm  (45s)",
            " ⚕ gpt-5.6-sol │ 21.2K/272K │ 8% │ ⏱ 20s",
            "────────────────────────────────────────",
            "⚠ ❯ ",
            "────────────────────────────────────────",
        };
        Assert.Equal("hermes", AgentManifestCatalog.ResolveId("hermes"));
        var r = AgentStateDetector.Detect(AgentManifestCatalog.ForAgent("hermes"), new ScreenSnapshot(screen, null));
        Assert.Equal(AgentActivity.Blocked, r.State);
    }
}

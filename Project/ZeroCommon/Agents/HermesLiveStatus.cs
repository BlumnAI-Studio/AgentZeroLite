using System.Text.Json;
using Agent.Common.Platform;
using Microsoft.Data.Sqlite;

namespace Agent.Common.Agents;

/// <summary>One lease in a Hermes home's <c>runtime/active_sessions.json</c>.</summary>
/// <param name="Home">The Hermes home it was read from (the main one or a profile).</param>
public sealed record HermesLease(int Pid, string SessionId, string Home);

/// <summary>
/// Hermes Agent's own record of a turn, read without a hook. Two files in its home
/// (<c>$HERMES_HOME</c>, else <c>%LOCALAPPDATA%\hermes</c> on Windows and <c>~/.hermes</c> elsewhere;
/// each profile under <c>profiles/&lt;name&gt;</c> is a home of its own):
/// <list type="bullet">
/// <item><c>runtime/active_sessions.json</c> — one lease per running agent, naming its pid and the
/// conversation it holds. The pid is the Python interpreter below <c>hermes.exe</c> below the tab's
/// shell, so the tab's lease is the one whose pid is among the shell's descendants.</item>
/// <item><c>state.db</c> (SQLite, WAL) — every message of every conversation, written as the turn
/// runs. Measured with a tool-using turn: user → assistant <c>tool_calls</c> → tool → … → assistant
/// <c>stop</c>, each row within a second of the screen; Ctrl+C ends a turn with an assistant row
/// "Operation interrupted." and no finish reason.</item>
/// </list>
/// So the last message says it: a user or tool row, or an assistant row that calls tools, is a turn
/// in progress; any other assistant row is a finished one. Approvals ("⚠️ Dangerous Command") are not
/// in the database — the turn sits on an assistant <c>tool_calls</c> row — so the screen rules report
/// those (<see cref="AgentManifestCatalog.Hermes"/>; a Blocked screen wins over a reported Working).
/// </summary>
public sealed class HermesLiveStatusSource : IAgentLiveStatusSource
{
    private readonly IReadOnlyList<string> _homes;
    private readonly Func<ProcessTable> _snapshot;
    private readonly Func<long> _now;
    private Dictionary<int, HermesLease> _leases = new();
    private ProcessTable? _table;
    private long _tableAt = long.MinValue;

    /// <summary>How old the process table may be. ps costs a process launch on macOS/Linux.</summary>
    public long TableTtlMs { get; init; } = 3_000;

    public HermesLiveStatusSource(IEnumerable<string>? homes = null, Func<ProcessTable>? snapshot = null, Func<long>? nowMs = null)
    {
        _homes = (homes ?? DefaultHomes()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _snapshot = snapshot ?? ProcessTable.Snapshot;
        _now = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>The Hermes home and every profile in it.</summary>
    public static IEnumerable<string> DefaultHomes()
    {
        var home = Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } h
            ? h.Trim().Trim('"')
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes");
        return HomesUnder(home);
    }

    /// <summary>A home and its <c>profiles/&lt;name&gt;</c> (not <c>.deleted</c> and other dot folders).</summary>
    internal static IEnumerable<string> HomesUnder(string home)
    {
        var homes = new List<string> { home };
        try
        {
            var profiles = Path.Combine(home, "profiles");
            if (Directory.Exists(profiles))
                homes.AddRange(Directory.EnumerateDirectories(profiles).Where(p => !Path.GetFileName(p).StartsWith('.')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return homes;
    }

    public void BeginPass()
    {
        var leases = new Dictionary<int, HermesLease>();
        foreach (var home in _homes)
        {
            var path = Path.Combine(home, "runtime", "active_sessions.json");
            if (!File.Exists(path)) continue;
            string json;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                json = reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var lease in ParseLeases(json, home)) leases[lease.Pid] = lease;
        }
        _leases = leases;
    }

    public AgentLiveStatus? StatusOf(string key, int? processId, string workingDirectory, bool onlyAgentTabInFolder)
    {
        // A lease carries no folder, so without the shell's pid there is nothing to match on.
        if (_leases.Count == 0 || processId is not { } root) return null;
        var table = Table();
        HermesLease? lease = null;
        foreach (var pid in table.Descendants(root))
            if (_leases.TryGetValue(pid, out var l)) { lease = l; break; }
        if (lease is null) return null;

        var session = new AgentSessionRef("hermes", lease.Home, lease.SessionId, workingDirectory);
        var last = ReadLast(Path.Combine(lease.Home, "state.db"), lease.SessionId);
        return last is { } m
            ? new AgentLiveStatus(m.Activity, "hermes-state:" + m.What, session)
            : new AgentLiveStatus(AgentActivity.Idle, "hermes-state:no turn yet", session);
    }

    public void Forget(string key) { }

    internal static IEnumerable<HermesLease> ParseLeases(string json, string home)
    {
        var found = new List<HermesLease>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return found;
            foreach (var e in entries.EnumerateArray())
            {
                if (e.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var p)
                    && e.TryGetProperty("session_id", out var sid) && sid.GetString() is { Length: > 0 } id)
                    found.Add(new HermesLease(p, id, home));
            }
        }
        catch (JsonException) { }   // caught mid-write: the next pass reads it whole
        return found;
    }

    /// <summary>The conversation's last message, mapped: null when it has none yet or the database cannot be read.</summary>
    internal static (AgentActivity Activity, string What)? ReadLast(string dbPath, string sessionId)
    {
        if (!File.Exists(dbPath)) return null;
        try
        {
            // Read-only and unpooled: a pooled handle would keep Hermes' database open between passes.
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1,
            }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT role, finish_reason, tool_calls IS NOT NULL AND tool_calls <> '' FROM messages
                WHERE session_id = $s AND active = 1 AND role IN ('user', 'assistant', 'tool')
                ORDER BY id DESC LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$s", sessionId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return Map(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), !r.IsDBNull(2) && r.GetBoolean(2));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>user / tool / an assistant calling tools → a turn in progress; any other assistant row ends it.</summary>
    internal static (AgentActivity, string) Map(string role, string? finishReason, bool callsTools) => role switch
    {
        "user" => (AgentActivity.Working, "user"),
        "tool" => (AgentActivity.Working, "tool"),
        _ when callsTools || finishReason == "tool_calls" => (AgentActivity.Working, "tool_calls"),
        _ => (AgentActivity.Idle, "assistant " + (finishReason ?? "ended")),
    };

    private ProcessTable Table()
    {
        if (_table is null || _now() - _tableAt >= TableTtlMs)
        {
            _table = _snapshot();
            _tableAt = _now();
        }
        return _table;
    }
}

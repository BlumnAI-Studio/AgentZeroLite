using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Agent.Common.Agents;

/// <summary>
/// The sub-agents a Hermes conversation is running (its <c>delegate_task</c> tool), as Agent Stage's crew
/// — the same <see cref="SubagentView"/> shape as Claude's (<see cref="ClaudeSubagentReader"/>).
///
/// <para>Hermes keeps no separate transcript for them: each is a conversation of its own in the same
/// <c>state.db</c>, with <c>source = 'subagent'</c> and <c>parent_session_id</c> naming the conversation
/// that delegated it (a nested one names the sub-agent above it). Measured with two parallel sub-agents:
/// both rows appeared the second they were dispatched, their first user message is the task they were
/// given, their messages (tool calls included) are written as they run, and a finished one gets
/// <c>ended_at</c> + <c>end_reason = 'agent_close'</c> while the parent's turn goes on.</para>
/// </summary>
public sealed class HermesSubagentReader
{
    private readonly Func<DateTimeOffset> _now;

    /// <summary>How long a finished sub-agent stays in the list, so the stage can show what just ran.</summary>
    public long KeepFinishedMs { get; init; } = 120_000;

    /// <summary>A sub-agent with no end recorded and nothing written for this long was stopped or lost.</summary>
    public long QuietStopMs { get; init; } = 600_000;

    /// <summary>The most a session reports — the newest first.</summary>
    public int MaxShown { get; init; } = 24;

    public HermesSubagentReader(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.Now);

    /// <summary>Working sub-agents, and the ones that ended within <see cref="KeepFinishedMs"/>; oldest first.</summary>
    /// <param name="session">A Hermes conversation: <see cref="AgentSessionRef.ConfigDir"/> is its home.</param>
    public IReadOnlyList<SubagentView> Read(AgentSessionRef session)
    {
        var dbPath = Path.Combine(session.ConfigDir, "state.db");
        if (!File.Exists(dbPath)) return [];
        var nowMs = _now().ToUnixTimeMilliseconds();
        var since = (nowMs - QuietStopMs - KeepFinishedMs) / 1000.0;

        var views = new List<SubagentView>();
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1,
            }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            // Every descendant of the conversation (depth-capped), with its task, its last tool call and
            // when it last wrote. A sub-agent that started before the horizon can still be running, so the
            // horizon is applied to its last write, not its start.
            cmd.CommandText = """
                WITH RECURSIVE kids(id, parent, depth) AS (
                    SELECT id, parent_session_id, 1 FROM sessions WHERE parent_session_id = $s
                    UNION ALL
                    SELECT c.id, c.parent_session_id, k.depth + 1 FROM sessions c JOIN kids k ON c.parent_session_id = k.id
                    WHERE k.depth < 6)
                SELECT k.id, k.parent, k.depth, s.started_at, s.ended_at, s.tool_call_count,
                    (SELECT content FROM messages WHERE session_id = k.id AND role = 'user' ORDER BY id LIMIT 1),
                    (SELECT tool_calls FROM messages WHERE session_id = k.id AND tool_calls IS NOT NULL AND tool_calls <> ''
                        ORDER BY id DESC LIMIT 1),
                    COALESCE((SELECT MAX(timestamp) FROM messages WHERE session_id = k.id), s.started_at)
                FROM kids k JOIN sessions s ON s.id = k.id
                WHERE COALESCE(s.ended_at, (SELECT MAX(timestamp) FROM messages WHERE session_id = k.id), s.started_at) >= $since
                """;
            cmd.Parameters.AddWithValue("$s", session.SessionId);
            cmd.Parameters.AddWithValue("$since", since);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                var parent = r.IsDBNull(1) ? null : r.GetString(1);
                var startedAt = (long)(r.GetDouble(3) * 1000);
                var lastAt = (long)(r.GetDouble(8) * 1000);

                string state;
                long? ended;
                if (!r.IsDBNull(4)) { state = "done"; ended = (long)(r.GetDouble(4) * 1000); }
                else if (nowMs - lastAt > QuietStopMs) { state = "stopped"; ended = lastAt + QuietStopMs; }
                else { state = "working"; ended = null; }
                if (ended is { } end && nowMs - end > KeepFinishedMs) continue;

                views.Add(new SubagentView(id, parent == session.SessionId ? null : parent, "delegate",
                    Task(r.IsDBNull(6) ? null : r.GetString(6)), r.GetInt32(2), Background: false, state,
                    r.IsDBNull(7) ? null : LastTool(r.GetString(7)), r.IsDBNull(5) ? 0 : r.GetInt32(5), startedAt, ended));
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
        return views.OrderBy(v => v.StartedAt).TakeLast(MaxShown).ToList();
    }

    /// <summary>The task as delegated: its first line, cut short.</summary>
    internal static string Task(string? firstUserMessage)
    {
        var text = (firstUserMessage ?? "").Trim();
        var nl = text.IndexOfAny(['\r', '\n']);
        if (nl > 0) text = text[..nl].Trim();
        return text.Length > 160 ? text[..160] + "…" : text;
    }

    /// <summary>The last call of an OpenAI-style <c>tool_calls</c> array: "web_search · seoul weather".</summary>
    internal static string? LastTool(string toolCallsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolCallsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
            var call = doc.RootElement[doc.RootElement.GetArrayLength() - 1];
            if (!call.TryGetProperty("function", out var fn) || !fn.TryGetProperty("name", out var n) || n.GetString() is not { } name)
                return null;
            // Arguments arrive as a JSON string ("{\"query\": …}").
            if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 1 } args)
            {
                try
                {
                    using var input = JsonDocument.Parse(args);
                    if (ClaudeSubagentReader.Hint(input.RootElement) is { } h) return $"{name} · {h}";
                }
                catch (JsonException) { }
            }
            return name;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

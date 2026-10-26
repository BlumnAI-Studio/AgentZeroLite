using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Agent.Common.Agents;

/// <summary>
/// The sub-agents a Codex thread is running (its multi-agent <c>spawn_agent</c> tool), as Agent Stage's
/// crew — the same <see cref="SubagentView"/> shape as Claude's and Hermes'. Measured on 0.164 with two
/// parallel sub-agents:
/// <list type="bullet">
/// <item>the parent's rollout records each one as an <c>item_completed</c> event whose item is a
/// <c>SubAgentActivity</c> — <c>kind: started</c> when spawned and <c>kind: completed</c> when it finished,
/// with <c>agent_thread_id</c>, <c>agent_path</c> (<c>/root/seoul_weather</c>) and the times;</item>
/// <item>each sub-agent is a thread with a rollout of its own, whose <c>session_meta</c> carries
/// <c>source.subagent.thread_spawn</c> (<c>parent_thread_id</c>, <c>depth</c>, <c>agent_nickname</c>,
/// <c>agent_role</c>). That rollout opens with a copy of the parent's history — its own records start at
/// the <c>thread_settings_applied</c> naming its own thread — and a nested sub-agent shows up there the
/// same way it does in the parent.</item>
/// </list>
/// The task text itself is encrypted in the rollout, so a sub-agent is described by its task name and
/// nickname ("seoul_weather — Anscombe"). Files are read incrementally, like Claude's transcripts.
/// </summary>
public sealed class CodexSubagentReader
{
    private const int MaxDepth = 6;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);   // thread id → rollout
    private readonly Dictionary<string, Thread> _threads = new(StringComparer.OrdinalIgnoreCase); // rollout → folded

    /// <summary>How long a finished sub-agent stays in the list, so the stage can show what just ran.</summary>
    public long KeepFinishedMs { get; init; } = 120_000;

    /// <summary>A sub-agent with no end recorded and nothing written for this long was stopped or lost.</summary>
    public long QuietStopMs { get; init; } = 600_000;

    /// <summary>The most a session reports — the newest first.</summary>
    public int MaxShown { get; init; } = 24;

    /// <summary>How many local-date folders back a thread's rollout is looked for.</summary>
    public int DaysBack { get; init; } = 7;

    public CodexSubagentReader(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.Now);

    /// <summary>Working sub-agents, and the ones that ended within <see cref="KeepFinishedMs"/>; oldest first.</summary>
    /// <param name="session">A Codex thread: <see cref="AgentSessionRef.ConfigDir"/> is <c>$CODEX_HOME</c>.</param>
    public IReadOnlyList<SubagentView> Read(AgentSessionRef session)
    {
        var sessions = Path.Combine(session.ConfigDir, "sessions");
        if (Fold(sessions, session.SessionId) is not { } root) return [];

        var nowMs = _now().ToUnixTimeMilliseconds();
        var views = new List<SubagentView>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(root, parentId: null, depth: 1);
        return views.OrderBy(v => v.StartedAt).TakeLast(MaxShown).ToList();

        void Walk(Thread thread, string? parentId, int depth)
        {
            if (depth > MaxDepth) return;
            foreach (var (id, spawn) in thread.Spawns)
            {
                if (!seen.Add(id)) continue;
                var child = Fold(sessions, id);   // null until its rollout is written
                var lastAt = Math.Max(spawn.StartedAt, child?.LastAt ?? 0);

                string state;
                long? ended;
                if (spawn.EndedAt is { } e) { state = "done"; ended = e; }
                else if (nowMs - lastAt > QuietStopMs) { state = "stopped"; ended = lastAt + QuietStopMs; }
                else { state = "working"; ended = null; }

                if (ended is not { } end || nowMs - end <= KeepFinishedMs)
                    views.Add(new SubagentView(id, parentId,
                        // A role-less agent is general-purpose: the stage then names it by the task's first word.
                        child?.Role ?? "general-purpose",
                        Describe(spawn.AgentPath, child?.Nickname), child?.Depth ?? depth, Background: false, state,
                        child?.LastTool, child?.ToolCount ?? 0, spawn.StartedAt, ended));

                if (child is not null) Walk(child, id, depth + 1);
            }
        }
    }

    /// <summary>"/root/seoul_weather" + "Anscombe" → "seoul_weather — Anscombe".</summary>
    internal static string Describe(string? agentPath, string? nickname)
    {
        var task = (agentPath ?? "").TrimEnd('/');
        task = task[(task.LastIndexOf('/') + 1)..];
        if (task.Length == 0) task = "sub-agent";
        return string.IsNullOrEmpty(nickname) ? task : $"{task} — {nickname}";
    }

    private Thread? Fold(string sessions, string threadId)
    {
        if (!_paths.TryGetValue(threadId, out var path))
        {
            if (Locate(sessions, threadId) is not { } found) return null;
            _paths[threadId] = path = found;
        }
        if (!_threads.TryGetValue(path, out var thread)) _threads[path] = thread = new Thread(path);
        thread.CatchUp();
        return thread;
    }

    /// <summary><c>rollout-…-&lt;thread&gt;.jsonl</c> in the local-date folders of the last <see cref="DaysBack"/> days.</summary>
    private string? Locate(string sessions, string threadId)
    {
        var today = _now().LocalDateTime.Date;
        for (var i = 0; i <= DaysBack; i++)
        {
            var day = today.AddDays(-i);
            var dir = Path.Combine(sessions, day.ToString("yyyy", CultureInfo.InvariantCulture),
                day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
            try
            {
                if (!Directory.Exists(dir)) continue;
                if (Directory.EnumerateFiles(dir, $"rollout-*-{threadId}.jsonl").FirstOrDefault() is { } hit) return hit;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    private sealed record Spawn(string? AgentPath, long StartedAt)
    {
        public long? EndedAt { get; set; }
    }

    /// <summary>One rollout, folded as it grows: who it is, what it called, whom it spawned.</summary>
    private sealed class Thread(string path)
    {
        private long _offset;
        private readonly StringBuilder _partial = new();
        private bool _metaRead;
        private bool _own;

        public string? Id { get; private set; }
        public string? Role { get; private set; }
        public string? Nickname { get; private set; }
        public int? Depth { get; private set; }
        public long LastAt { get; private set; }
        public string? LastTool { get; private set; }
        public int ToolCount { get; private set; }
        public Dictionary<string, Spawn> Spawns { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void CatchUp()
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length == _offset) return;
                fs.Seek(_offset, SeekOrigin.Begin);
                var buf = new byte[fs.Length - _offset];
                var read = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
                _offset += read;
                _partial.Append(Encoding.UTF8.GetString(buf, 0, read));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            var text = _partial.ToString();
            var lastNl = text.LastIndexOf('\n');
            if (lastNl < 0) return;
            _partial.Clear().Append(text, lastNl + 1, text.Length - lastNl - 1);
            foreach (var line in text[..lastNl].Split('\n'))
                if (line.Length > 1) Fold(line);
        }

        private void Fold(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var type = Str(r, "type");
                if (!r.TryGetProperty("payload", out var p)) return;

                if (!_metaRead && type == "session_meta")
                {
                    _metaRead = true;
                    Id = Str(p, "id");
                    Nickname = Str(p, "agent_nickname");
                    if (p.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object
                        && src.TryGetProperty("subagent", out var sub) && sub.TryGetProperty("thread_spawn", out var spawn))
                    {
                        Role = Str(spawn, "agent_role");
                        Nickname ??= Str(spawn, "agent_nickname");
                        if (spawn.TryGetProperty("depth", out var d) && d.TryGetInt32(out var n)) Depth = n;
                    }
                    else _own = true;   // a user thread: everything in it is its own
                    return;
                }

                // A sub-agent's rollout starts with a copy of its parent's history; its own records begin
                // where its own thread's settings are applied.
                if (!_own)
                {
                    if (Str(p, "type") == "thread_settings_applied" && Str(p, "thread_id") is { } tid
                        && string.Equals(tid, Id, StringComparison.OrdinalIgnoreCase))
                        _own = true;
                    return;
                }

                if (Str(r, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                    LastAt = at.ToUnixTimeMilliseconds();

                var kind = Str(p, "type");
                if (type == "response_item" && kind is ("function_call" or "custom_tool_call") && Str(p, "name") is { } name)
                {
                    ToolCount++;
                    LastTool = Str(p, "arguments") is { Length: > 1 } args && HintOf(args) is { } h ? $"{name} · {h}" : name;
                }
                else if (type == "event_msg" && kind == "item_completed" && p.TryGetProperty("item", out var item)
                         && Str(item, "type") == "SubAgentActivity" && Str(item, "agent_thread_id") is { } child)
                {
                    var startedMs = Long(p, "started_at_ms") ?? LastAt;
                    switch (Str(item, "kind"))
                    {
                        case "started":
                            if (!Spawns.ContainsKey(child)) Spawns[child] = new Spawn(Str(item, "agent_path"), startedMs);
                            break;
                        case "completed":
                            if (!Spawns.TryGetValue(child, out var s)) Spawns[child] = s = new Spawn(Str(item, "agent_path"), startedMs);
                            s.EndedAt = Long(p, "completed_at_ms") ?? startedMs;
                            break;
                    }
                }
            }
            catch (JsonException) { /* a torn line — skipped, the rest still folds */ }
        }

        private static string? HintOf(string json)
        {
            try
            {
                using var input = JsonDocument.Parse(json);
                return ClaudeSubagentReader.Hint(input.RootElement);
            }
            catch (JsonException) { return null; }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;
}

using System.Text.Json;
using Agent.Common.Platform;
using Agent.Common.Workspace;

namespace Agent.Common.Agents;

/// <summary>What an agent reports about itself, mapped to the tracker's states.</summary>
/// <param name="Activity">Working, Blocked or Idle — the tracker turns Working → Idle into Done.</param>
/// <param name="Rule">Shown where a screen rule's id would be (log, <c>agent-state</c>): <c>claude-status:busy</c>.</param>
/// <param name="Session">The conversation behind it, when the agent names one (Claude's status file does).</param>
public sealed record AgentLiveStatus(AgentActivity Activity, string Rule, AgentSessionRef? Session = null);

/// <summary>
/// A source of agent-reported state, consulted by <see cref="AgentActivityTracker"/> before the
/// screen rules. Called on the tracker's thread, once per pass: <see cref="BeginPass"/>, then
/// <see cref="StatusOf"/> per started terminal.
/// </summary>
public interface IAgentLiveStatusSource
{
    void BeginPass();

    /// <param name="key">The terminal's stable id (its session's InternalId).</param>
    /// <param name="processId">The PTY's child (the shell), when the session knows it.</param>
    /// <param name="workingDirectory">The workspace folder — the fallback match when the pid is unknown.</param>
    /// <param name="onlyAgentTabInFolder">No other tab in that workspace could be the same agent, so a folder match is unambiguous.</param>
    AgentLiveStatus? StatusOf(string key, int? processId, string workingDirectory, bool onlyAgentTabInFolder);

    /// <summary>The terminal is gone.</summary>
    void Forget(string key);
}

/// <summary>One <c>~/.claude/sessions/&lt;pid&gt;.json</c>.</summary>
public sealed record ClaudeStatusFile(int Pid, string? SessionId, string? Cwd, string Status, string? WaitingFor, long StatusUpdatedAt)
{
    /// <summary>The profile folder the file was read from (the parent of <c>sessions/</c>).</summary>
    public string? ConfigDir { get; init; }

    public AgentSessionRef? Session => ConfigDir is { } d && SessionId is { Length: > 0 } id && Cwd is { Length: > 0 } cwd
        ? new AgentSessionRef("claude", d, id, cwd)
        : null;

    /// <summary>busy → Working, waiting → Blocked, idle → Idle; anything else is not ours to guess.</summary>
    public AgentActivity? Activity => Status switch
    {
        "busy" => AgentActivity.Working,
        "waiting" => AgentActivity.Blocked,
        "idle" => AgentActivity.Idle,
        _ => null,
    };

    public static ClaudeStatusFile? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number) return null;
            static string? S(JsonElement e, string n) =>
                e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var updated = r.TryGetProperty("statusUpdatedAt", out var u) && u.TryGetInt64(out var ms) ? ms : 0;
            return new ClaudeStatusFile(pid.GetInt32(), S(r, "sessionId"), S(r, "cwd"), S(r, "status") ?? "", S(r, "waitingFor"), updated);
        }
        catch (JsonException)
        {
            return null;   // caught mid-write: the next pass reads it whole
        }
    }
}

/// <summary>
/// Claude Code's own status, read without a hook. Every interactive Claude Code process keeps
/// <c>~/.claude/sessions/&lt;pid&gt;.json</c> (or under <c>$CLAUDE_CONFIG_DIR</c>) with a
/// <c>status</c> of <c>busy</c>, <c>waiting</c> (+ <c>waitingFor</c>, e.g. "input needed") or
/// <c>idle</c>, rewritten as it changes. Measured with 2.1.283 against the screen rules over a
/// 49 s turn: the file flipped busy → idle when the turn ended, the screen rules ~3 s later, and
/// a question to the user read <c>waiting</c>. The screen can also miss "working" entirely when
/// the spinner is not where the rules look; the file does not depend on the layout.
///
/// <para>Which file belongs to which tab: the tab's PTY started a shell; the claude process is
/// one of its descendants, and the file is named by that pid. Without the shell's pid (a host
/// whose session does not implement <see cref="Services.ITerminalProcessSource"/>), a file whose
/// cwd is the workspace folder is used when it is the only Claude below this app and the only
/// Claude-looking tab there.</para>
/// </summary>
public sealed class ClaudeLiveStatusSource : IAgentLiveStatusSource
{
    private readonly IReadOnlyList<string> _dirs;
    private readonly Func<ProcessTable> _snapshot;
    private readonly Func<long> _now;
    private readonly int _selfPid;

    private Dictionary<int, ClaudeStatusFile> _files = new();
    private ProcessTable? _table;
    private long _tableAt = long.MinValue;
    private readonly Dictionary<string, int> _claudeOf = new();      // terminal key → claude pid
    private readonly Dictionary<string, long> _lastLookup = new();   // terminal key → when a pid walk last ran

    /// <summary>How old the process table may be. ps costs a process launch on macOS/Linux.</summary>
    public long TableTtlMs { get; init; } = 3_000;

    public ClaudeLiveStatusSource(IEnumerable<string>? sessionDirs = null, Func<ProcessTable>? snapshot = null,
        Func<long>? nowMs = null, int? selfPid = null)
    {
        _dirs = (sessionDirs ?? DefaultDirs()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _snapshot = snapshot ?? ProcessTable.Snapshot;
        _now = nowMs ?? (() => Environment.TickCount64);
        _selfPid = selfPid ?? Environment.ProcessId;
    }

    /// <summary><c>~/.claude/sessions</c>, and <c>$CLAUDE_CONFIG_DIR/sessions</c> when this process has one.</summary>
    public static IEnumerable<string> DefaultDirs()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
        var custom = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(custom)) yield return Path.Combine(custom.Trim().Trim('"'), "sessions");
    }

    public void BeginPass()
    {
        var files = new Dictionary<int, ClaudeStatusFile>();
        foreach (var dir in _dirs)
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> paths;
            try { paths = Directory.EnumerateFiles(dir, "*.json").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var path in paths)
            {
                string json;
                try { json = File.ReadAllText(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (ClaudeStatusFile.Parse(json) is { } f
                    && (!files.TryGetValue(f.Pid, out var other) || other.StatusUpdatedAt < f.StatusUpdatedAt))
                    files[f.Pid] = f with { ConfigDir = Path.GetDirectoryName(dir) };
            }
        }
        _files = files;
    }

    public AgentLiveStatus? StatusOf(string key, int? processId, string workingDirectory, bool onlyAgentTabInFolder)
    {
        if (_files.Count == 0) return null;
        var table = Table();

        // The claude found last time, while it still runs and still has its file.
        if (_claudeOf.TryGetValue(key, out var known) && _files.TryGetValue(known, out var kept) && table.IsAlive(known))
            return Map(kept);
        _claudeOf.Remove(key);

        // A pid walk per terminal at most once per table refresh: a shell with no claude in it
        // (most tabs) must not cost a walk every pass.
        if (_lastLookup.TryGetValue(key, out var at) && _now() - at < TableTtlMs) return null;
        _lastLookup[key] = _now();

        ClaudeStatusFile? found = null;
        if (processId is { } root)
        {
            found = table.Descendants(root)
                .Select(pid => _files.TryGetValue(pid, out var f) ? f : null)
                .Where(f => f is not null)
                .OrderByDescending(f => f!.StatusUpdatedAt)
                .FirstOrDefault();
        }
        else if (onlyAgentTabInFolder)
        {
            var mine = table.Descendants(_selfPid).ToHashSet();
            var inFolder = _files.Values
                .Where(f => mine.Contains(f.Pid) && GitWorktreeScanner.SameFolder(f.Cwd, workingDirectory))
                .ToList();
            if (inFolder.Count == 1) found = inFolder[0];
        }
        if (found is null) return null;

        _claudeOf[key] = found.Pid;
        _lastLookup.Remove(key);
        return Map(found);
    }

    public void Forget(string key)
    {
        _claudeOf.Remove(key);
        _lastLookup.Remove(key);
    }

    private ProcessTable Table()
    {
        if (_table is null || _now() - _tableAt >= TableTtlMs)
        {
            _table = _snapshot();
            _tableAt = _now();
        }
        return _table;
    }

    private static AgentLiveStatus? Map(ClaudeStatusFile f) => f.Activity is { } a
        ? new AgentLiveStatus(a, "claude-status:" + f.Status + (string.IsNullOrEmpty(f.WaitingFor) ? "" : $" ({f.WaitingFor})"), f.Session)
        : null;
}

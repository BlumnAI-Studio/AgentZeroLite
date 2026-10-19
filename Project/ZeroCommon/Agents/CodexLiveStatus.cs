using System.Globalization;
using System.Text;
using System.Text.Json;
using Agent.Common.Platform;
using Agent.Common.Workspace;

namespace Agent.Common.Agents;

/// <summary>
/// Codex's own record of a turn, read without a hook. Codex keeps no per-process status file
/// (Claude Code does — <see cref="ClaudeLiveStatusSource"/>), and measured with Codex 0.162 the
/// TUI does not even run the turn itself: it is a client of a shared <c>codex app-server</c>
/// daemon, so the agent process below the tab's shell has nothing to say. What it does have is
/// the thread's rollout, <c>$CODEX_HOME/sessions/YYYY/MM/DD/rollout-…-&lt;thread&gt;.jsonl</c>,
/// appended live: <c>task_started</c> when a turn begins, <c>task_complete</c> (or
/// <c>turn_aborted</c>) when it ends. Measured over a 37 s turn, those two events bracket it.
///
/// <para>Which thread is a tab's: the codex process below the tab's shell started at T; Codex
/// creates the user thread (session_meta, <c>thread_source: user</c>) a second later (measured
/// 19:27:23 → 19:27:24), with the workspace folder as cwd. A thread belongs to the codex TUI of
/// this app that started last before the thread was created — that keeps two Codex tabs in one
/// folder apart, and follows <c>/new</c> inside one tab. Approvals are not in the rollout; the
/// screen rules still report those (<see cref="AgentActivityTracker"/> lets a Blocked screen win
/// over Working).</para>
/// </summary>
public sealed class CodexLiveStatusSource : IAgentLiveStatusSource
{
    private const int TailBytes = 64 * 1024;
    private const int HeadBytes = 64 * 1024;
    private static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(3);

    private readonly string _sessions;
    private readonly Func<ProcessTable> _snapshot;
    private readonly Func<long> _now;
    private readonly int _selfPid;
    private readonly Func<int, DateTime?> _startOf;

    private ProcessTable? _table;
    private long _tableAt = long.MinValue;
    private List<Rollout>? _index;
    private long _indexAt = long.MinValue;
    private readonly Dictionary<int, DateTime?> _starts = new();
    private readonly Dictionary<string, Rollout?> _head = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How old the process table and the rollout listing may be.</summary>
    public long RefreshMs { get; init; } = 3_000;

    /// <param name="codexHome"><c>$CODEX_HOME</c>, else <c>~/.codex</c>.</param>
    /// <param name="startTimeOf">A process's start time in UTC (tests pass their own).</param>
    public CodexLiveStatusSource(string? codexHome = null, Func<ProcessTable>? snapshot = null, Func<long>? nowMs = null,
        int? selfPid = null, Func<int, DateTime?>? startTimeOf = null)
    {
        codexHome ??= Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
            ? h.Trim().Trim('"')
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        _sessions = Path.Combine(codexHome, "sessions");
        _snapshot = snapshot ?? ProcessTable.Snapshot;
        _now = nowMs ?? (() => Environment.TickCount64);
        _selfPid = selfPid ?? Environment.ProcessId;
        _startOf = startTimeOf ?? ProcessStartUtc;
    }

    public void BeginPass() { }

    public AgentLiveStatus? StatusOf(string key, int? processId, string workingDirectory, bool onlyAgentTabInFolder)
    {
        if (processId is not { } root || !Directory.Exists(_sessions)) return null;
        var table = Table();

        // The codex TUI in this tab (codex.exe under node under the shell, or codex directly).
        var tui = table.Descendants(root).FirstOrDefault(p => table.NameOf(p) == "codex");
        if (tui == 0) return null;
        if (StartOf(tui) is not { } started) return null;

        // Every codex TUI this app runs, by start — a thread is owned by the last one to start before it.
        var tuiStarts = table.Descendants(_selfPid)
            .Where(p => table.NameOf(p) == "codex" && !IsBelowCodex(table, p))
            .Select(p => (Pid: p, Start: StartOf(p)))
            .Where(x => x.Start is not null)
            .Select(x => (x.Pid, Start: x.Start!.Value))
            .ToList();

        var mine = Index(started)
            .Where(r => GitWorktreeScanner.SameFolder(r.Cwd, workingDirectory))
            .Where(r => r.Created >= started - StartSlack)
            .Where(r => OwnerOf(r.Created, tuiStarts) == tui)
            .OrderByDescending(r => r.LastWrite)
            .FirstOrDefault();
        if (mine is null) return null;

        return ReadState(mine.Path) is { } activity
            ? new AgentLiveStatus(activity.State, "codex-rollout:" + activity.Event)
            : new AgentLiveStatus(AgentActivity.Idle, "codex-rollout:no turn yet");
    }

    public void Forget(string key) { }

    /// <summary>The TUI that started last at or before a thread was created (slack for clock rounding).</summary>
    internal static int OwnerOf(DateTime created, IReadOnlyList<(int Pid, DateTime Start)> tuis) =>
        tuis.Where(t => t.Start <= created + StartSlack)
            .OrderByDescending(t => t.Start)
            .Select(t => t.Pid)
            .FirstOrDefault();

    /// <summary>The last turn event of a rollout: started → Working; complete / aborted → Idle.</summary>
    internal static (AgentActivity State, string Event)? ReadState(string path)
    {
        string text;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var take = (int)Math.Min(TailBytes, fs.Length);
            fs.Seek(fs.Length - take, SeekOrigin.Begin);
            var buffer = new byte[take];
            var read = 0;
            while (read < take) { var n = fs.Read(buffer, read, take - read); if (n <= 0) break; read += n; }
            text = Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        // Newest first; only whole lines (the first may be cut by the window).
        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 1; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"event_msg\"")) continue;
            foreach (var (marker, state) in TurnEvents)
            {
                if (!line.Contains(marker)) continue;
                var ev = marker.Trim('"');
                return (state, ev);
            }
        }
        return null;
    }

    private static readonly (string Marker, AgentActivity State)[] TurnEvents =
    {
        ("\"task_started\"", AgentActivity.Working),
        ("\"task_complete\"", AgentActivity.Idle),
        ("\"turn_aborted\"", AgentActivity.Idle),
    };

    private sealed record Rollout(string Path, string Cwd, DateTime Created, DateTime LastWrite);

    /// <summary>User threads (not Codex's own sub-agents) created since <paramref name="since"/>, newest listing cached briefly.</summary>
    private IReadOnlyList<Rollout> Index(DateTime since)
    {
        if (_index is not null && _now() - _indexAt < RefreshMs)
            return _index.Where(r => r.Created >= since - StartSlack).ToList();

        var found = new List<Rollout>();
        // Day folders are local dates; a TUI started yesterday can have threads in today's folder.
        for (var day = since.ToLocalTime().Date.AddDays(-1); day <= DateTime.Now.Date; day = day.AddDays(1))
        {
            var dir = Path.Combine(_sessions, day.ToString("yyyy", CultureInfo.InvariantCulture),
                day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
            if (!Directory.Exists(dir)) continue;
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(dir).EnumerateFiles("rollout-*.jsonl").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
            {
                if (!_head.TryGetValue(f.FullName, out var head))
                    _head[f.FullName] = head = ReadHead(f.FullName);
                if (head is not null) found.Add(head with { LastWrite = f.LastWriteTimeUtc });
            }
        }
        _index = found;
        _indexAt = _now();
        return found.Where(r => r.Created >= since - StartSlack).ToList();
    }

    /// <summary>session_meta: cwd and creation time of a user thread; null for a sub-agent's or an unreadable one.</summary>
    private static Rollout? ReadHead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(HeadBytes, fs.Length)];
            var read = fs.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var end = text.IndexOf('\n');
            if (end < 0) return null;   // the first record is not complete yet — the next listing reads it
            using var doc = JsonDocument.Parse(text[..end]);
            var root = doc.RootElement;
            if (!root.TryGetProperty("payload", out var p)) return null;
            if (p.TryGetProperty("thread_source", out var ts) && ts.GetString() is { } source && source != "user") return null;
            if (p.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object) return null;   // {"subagent":…}
            var cwd = p.TryGetProperty("cwd", out var c) ? c.GetString() : null;
            var stamp = p.TryGetProperty("timestamp", out var t) ? t.GetString() : null;
            if (cwd is null || stamp is null
                || !DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
                return null;
            return new Rollout(path, cwd, created, DateTime.MinValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private ProcessTable Table()
    {
        if (_table is null || _now() - _tableAt >= RefreshMs)
        {
            _table = _snapshot();
            _tableAt = _now();
        }
        return _table;
    }

    private DateTime? StartOf(int pid)
    {
        if (!_starts.TryGetValue(pid, out var start)) _starts[pid] = start = _startOf(pid);
        return start;
    }

    /// <summary>codex.exe can start helpers named codex; only the topmost one in a chain is the TUI.</summary>
    private static bool IsBelowCodex(ProcessTable table, int pid) =>
        table.Ancestors(pid).Any(a => table.NameOf(a) == "codex");

    private static DateTime? ProcessStartUtc(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>Asks each source in turn; the first answer wins (Claude's status file, then Codex's rollout).</summary>
public sealed class CompositeLiveStatusSource(params IAgentLiveStatusSource[] sources) : IAgentLiveStatusSource
{
    public void BeginPass()
    {
        foreach (var s in sources) s.BeginPass();
    }

    public AgentLiveStatus? StatusOf(string key, int? processId, string workingDirectory, bool onlyAgentTabInFolder)
    {
        foreach (var s in sources)
            if (s.StatusOf(key, processId, workingDirectory, onlyAgentTabInFolder) is { } answer)
                return answer;
        return null;
    }

    public void Forget(string key)
    {
        foreach (var s in sources) s.Forget(key);
    }
}

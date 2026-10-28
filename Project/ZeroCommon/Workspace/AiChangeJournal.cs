using System.Text;
using Agent.Common.Llm.Tools;
using Agent.Common.Module;

namespace Agent.Common.Workspace;

/// <summary>One file the AI mode changed: what it was before the first write, and where it is.</summary>
public sealed record AiFileChange(string FullPath, string Root, string RelativePath, string? OriginalText,
    DateTime FirstUtc, DateTime LastUtc, int Writes)
{
    /// <summary>The file did not exist before the AI wrote it.</summary>
    public bool Created => OriginalText is null;
}

/// <summary>
/// What the bot's AI mode has written, so a person can see it and take it back. The file tools
/// (<see cref="FileToolCore"/>) are sandboxed to the workspace but write without asking — the
/// only record used to be a log line. The host calls <see cref="BeforeWrite"/> ahead of every
/// <c>write_file</c> / <c>edit_file</c>; the first call for a file keeps its original text
/// (or "did not exist"), later ones only count. The DIFF tab's "AI changes" view diffs original
/// against the file as it is now, and <see cref="Revert"/> puts the original back (deleting a
/// file the AI created). <see cref="Keep"/> forgets a change once it has been looked at.
///
/// In memory, per app run: an undo buffer for this session, not history. Binary files and files
/// over <see cref="MaxTrackedBytes"/> are not tracked (the tools only write text). Thread-safe —
/// the agent loop calls in from the pool, the view reads on the UI thread.
/// </summary>
public sealed class AiChangeJournal
{
    public const int MaxTrackedBytes = 2 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, AiFileChange> _changes = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Func<DateTime> _clock;

    public AiChangeJournal(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>Raised after the set of changes or a change's count moved (from any thread).</summary>
    public event Action? Changed;

    /// <summary>
    /// Call before the tool writes <paramref name="path"/> under <paramref name="root"/>. A path the
    /// sandbox would refuse is ignored here too (the tool reports the error itself).
    /// </summary>
    public void BeforeWrite(string? root, string path)
    {
        if (string.IsNullOrEmpty(root) || !FileToolCore.TryResolveInsideRoot(root, path, out var full, out _)) return;
        lock (_gate)
        {
            var now = _clock();
            if (_changes.TryGetValue(full, out var known))
            {
                _changes[full] = known with { LastUtc = now, Writes = known.Writes + 1 };
            }
            else
            {
                string? original = null;
                if (File.Exists(full))
                {
                    var info = new FileInfo(full);
                    if (info.Length > MaxTrackedBytes) return;
                    var bytes = File.ReadAllBytes(full);
                    if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0) return;
                    original = Encoding.UTF8.GetString(bytes);
                }
                _changes[full] = new AiFileChange(full, root, Relative(root, full), original, now, now, 1);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Every tracked change, most recent first.</summary>
    public IReadOnlyList<AiFileChange> Snapshot()
    {
        lock (_gate) return _changes.Values.OrderByDescending(c => c.LastUtc).ToList();
    }

    /// <summary>Changes inside <paramref name="root"/> (the active workspace), most recent first.</summary>
    public IReadOnlyList<AiFileChange> For(string? root)
        => string.IsNullOrEmpty(root) ? [] : Snapshot().Where(c => SameFolder(c.Root, root)).ToList();

    public int Count { get { lock (_gate) return _changes.Count; } }

    /// <summary>Original → now. A file deleted since reads as removed; one that matches its original has no hunks.</summary>
    public static GitDiffReader.DiffFile Diff(AiFileChange change)
    {
        string? now = null;
        try { if (File.Exists(change.FullPath)) now = File.ReadAllText(change.FullPath); } catch { }
        return TextDiff.Compute(change.RelativePath, change.OriginalText, now);
    }

    /// <summary>Put the original back (delete a file the AI created) and forget the change. Returns an error, or null.</summary>
    public string? Revert(string fullPath)
    {
        AiFileChange? change;
        lock (_gate) _changes.TryGetValue(fullPath, out change);
        if (change is null) return "not a tracked change";
        try
        {
            if (change.OriginalText is null)
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, change.OriginalText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        lock (_gate) _changes.Remove(fullPath);
        AppLogger.Log($"[AiChanges] reverted {change.RelativePath} ({(change.Created ? "deleted — the AI created it" : "original restored")})");
        Changed?.Invoke();
        return null;
    }

    /// <summary>Accept the change as it is: stop tracking it.</summary>
    public void Keep(string fullPath)
    {
        bool removed;
        lock (_gate) removed = _changes.Remove(fullPath);
        if (removed) Changed?.Invoke();
    }

    private static string Relative(string root, string full)
    {
        try { return Path.GetRelativePath(root, full).Replace('\\', '/'); }
        catch { return full; }
    }

    private static bool SameFolder(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

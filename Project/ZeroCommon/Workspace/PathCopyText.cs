namespace Agent.Common.Workspace;

/// <summary>
/// The text a "copy path" menu puts on the clipboard. The absolute form is the OS's own
/// (backslashes on Windows) because it is pasted into Explorer, a shell or a dialog; relative
/// forms use '/' because they are pasted into prompts, docs and git commands, where that is
/// what every tool reads on every OS.
/// </summary>
public static class PathCopyText
{
    /// <summary>The full path in the OS's own form; null for an empty input.</summary>
    public static string? Absolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    /// <summary>
    /// <paramref name="path"/> relative to <paramref name="baseDir"/> with '/' separators — may
    /// start with <c>../</c> (a worktree beside its repository). Null when there is no relative
    /// form: an empty input, the base itself, or a different drive.
    /// </summary>
    public static string? Relative(string? baseDir, string? path)
    {
        var full = Absolute(path);
        var root = Absolute(baseDir);
        if (full is null || root is null) return null;
        var rel = Path.GetRelativePath(root, full);
        if (rel == "." || Path.IsPathRooted(rel)) return null;
        return rel.Replace('\\', '/');
    }

    /// <summary>
    /// <paramref name="path"/> as <c>~/…</c> when it is under the user's home folder — the
    /// relative form of a workspace, which has no parent folder of its own. Null otherwise.
    /// </summary>
    public static string? HomeRelative(string? path, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var rel = Relative(home, path);
        return rel is null || rel.StartsWith("../", StringComparison.Ordinal) || rel == ".." ? null : "~/" + rel;
    }
}

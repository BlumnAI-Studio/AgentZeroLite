namespace Agent.Common.Workspace;

/// <summary>One working tree of a repository.</summary>
/// <param name="Path">The working tree's folder, normalized (no trailing separator).</param>
/// <param name="Branch">The checked-out branch without <c>refs/heads/</c>, or null when HEAD is detached.</param>
/// <param name="Head">The commit id when detached (short), else null.</param>
/// <param name="IsMain">The repository's main working tree — the one whose <c>.git</c> is a folder.</param>
public sealed record GitWorktreeInfo(string Path, string? Branch, string? Head, bool IsMain)
{
    /// <summary>Who made the tree, read off where it lives (see <see cref="WorktreeOrigin"/>).</summary>
    public WorktreeOrigin Origin => WorktreeOrigins.Of(Path);

    /// <summary>
    /// What a list shows: the branch, else the short commit; a Codex-managed tree (detached, in a
    /// folder named after the repository) is prefixed with <c>codex &lt;id&gt;</c> so it says where it came from.
    /// </summary>
    public string Label
    {
        get
        {
            var plain = Branch ?? (Head is { Length: > 0 } h ? "(" + h + ")" : System.IO.Path.GetFileName(Path));
            return Origin == WorktreeOrigin.Codex && Branch is null
                ? $"codex {WorktreeOrigins.CodexId(Path)} {plain}".Trim()
                : plain;
        }
    }

    /// <summary>
    /// The workspace name to open this tree as. The folder name, except for a Codex-managed tree,
    /// whose folder is the repository's own name — opened as is it read "AgentZeroLite-2", a
    /// second "main" in the list.
    /// </summary>
    public string SuggestedWorkspaceName
    {
        get
        {
            var folder = System.IO.Path.GetFileName(Path);
            return Origin == WorktreeOrigin.Codex ? $"{folder}-codex-{WorktreeOrigins.CodexId(Path)}" : folder;
        }
    }
}

/// <summary>Who made a worktree, by its standard location.</summary>
public enum WorktreeOrigin
{
    /// <summary>Anywhere else — <c>git worktree add</c> by hand, another tool.</summary>
    Git,
    /// <summary>Claude Code: <c>&lt;repo&gt;/.claude/worktrees/&lt;name&gt;</c>, on a branch.</summary>
    Claude,
    /// <summary>Codex: <c>$CODEX_HOME/worktrees/&lt;id&gt;/&lt;repo&gt;</c> (default <c>~/.codex</c>), detached HEAD.</summary>
    Codex,
}

/// <summary>Recognises the agents' standard worktree locations (measured: Claude Code, Codex CLI 0.162 <c>--worktree</c>).</summary>
public static class WorktreeOrigins
{
    public static WorktreeOrigin Of(string path)
    {
        var parts = Segments(path);
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (!string.Equals(parts[i + 1], "worktrees", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(parts[i], ".claude", StringComparison.OrdinalIgnoreCase) && i + 2 < parts.Length)
                return WorktreeOrigin.Claude;
            // <codex home>/worktrees/<id>/<repo> — exactly two levels below.
            if (i + 4 == parts.Length && (string.Equals(parts[i], ".codex", StringComparison.OrdinalIgnoreCase) || IsCodexHome(parts, i)))
                return WorktreeOrigin.Codex;
        }
        return WorktreeOrigin.Git;
    }

    /// <summary>The Codex pool's id folder (<c>c14b</c> in <c>…/worktrees/c14b/AgentZeroLite</c>), or "".</summary>
    public static string CodexId(string path)
    {
        var parts = Segments(path);
        return parts.Length >= 2 ? parts[^2] : "";
    }

    private static string[] Segments(string path) =>
        (path ?? "").Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A relocated <c>CODEX_HOME</c> (the folder holding <c>worktrees</c>) counts too.</summary>
    private static bool IsCodexHome(string[] parts, int homeIndex)
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home)) return false;
        var homeParts = Segments(home);
        return homeParts.Length == homeIndex + 1
               && homeParts.Zip(parts.Take(homeIndex + 1)).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A folder's repository as its worktrees: the main tree and every linked one.</summary>
/// <param name="MainPath">The main working tree.</param>
/// <param name="Worktrees">Main first, then the linked trees by path.</param>
public sealed record GitWorktreeSet(string MainPath, IReadOnlyList<GitWorktreeInfo> Worktrees);

/// <summary>
/// Which worktrees a workspace folder's repository has — read from git's own files, without
/// starting <c>git</c>, because the sidebar asks every few seconds for every workspace and
/// an agent in a terminal (Claude's <c>.claude/worktrees/…</c>, <c>git worktree add</c>) can
/// create one at any time.
///
/// <para>The layout it reads: the main tree's <c>.git/</c> is the common dir; each linked tree
/// has <c>&lt;common&gt;/worktrees/&lt;name&gt;/gitdir</c> (the path of that tree's <c>.git</c>
/// file) and <c>…/HEAD</c>; a linked tree's own <c>.git</c> is a file
/// <c>gitdir: &lt;common&gt;/worktrees/&lt;name&gt;</c> whose <c>commondir</c> leads back. So
/// the answer is the same whether the workspace is the main tree or one of the others.</para>
///
/// <para>Only a folder that <em>is</em> a working tree's root counts — a workspace opened on a
/// subfolder of a repository has no worktree of its own to switch to.</para>
/// </summary>
public static class GitWorktreeScanner
{
    /// <summary>The repository <paramref name="folder"/> is the root of, as worktrees; null when it is not a working tree root.</summary>
    public static GitWorktreeSet? Scan(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            var root = Normalize(folder);
            var common = CommonDirOf(root);
            if (common is null) return null;

            var mainPath = Normalize(Path.GetDirectoryName(common) ?? common);
            var list = new List<GitWorktreeInfo>();
            var (mainBranch, mainHead) = ReadHead(Path.Combine(common, "HEAD"));
            // A bare repository has no main working tree to list.
            if (string.Equals(Path.GetFileName(common), ".git", StringComparison.OrdinalIgnoreCase))
                list.Add(new GitWorktreeInfo(mainPath, mainBranch, mainHead, IsMain: true));

            var worktrees = Path.Combine(common, "worktrees");
            if (Directory.Exists(worktrees))
            {
                var linked = new List<GitWorktreeInfo>();
                foreach (var admin in Directory.EnumerateDirectories(worktrees))
                {
                    var gitdirFile = Path.Combine(admin, "gitdir");
                    if (!File.Exists(gitdirFile)) continue;
                    var dotGit = File.ReadAllText(gitdirFile).Trim();
                    if (dotGit.Length == 0) continue;
                    if (!Path.IsPathRooted(dotGit)) dotGit = Path.GetFullPath(Path.Combine(admin, dotGit));
                    var tree = Path.GetDirectoryName(dotGit);
                    // A tree deleted without `git worktree remove` leaves its admin folder behind
                    // until `git worktree prune`; there is nothing to switch to.
                    if (tree is null || !Directory.Exists(tree)) continue;
                    var (branch, head) = ReadHead(Path.Combine(admin, "HEAD"));
                    linked.Add(new GitWorktreeInfo(Normalize(tree), branch, head, IsMain: false));
                }
                list.AddRange(linked.OrderBy(w => w.Path, StringComparer.OrdinalIgnoreCase));
            }

            // The folder must be one of the trees (not a subfolder of one).
            if (!list.Any(w => SamePath(w.Path, root))) return null;
            return new GitWorktreeSet(mainPath, list);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The repository's common dir when <paramref name="root"/> is a working tree root, else null.</summary>
    private static string? CommonDirOf(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit)) return Normalize(dotGit);
        if (!File.Exists(dotGit)) return null;

        // Linked tree: ".git" is "gitdir: <common>/worktrees/<name>".
        var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim() ?? "";
        if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
        var admin = line["gitdir:".Length..].Trim();
        if (!Path.IsPathRooted(admin)) admin = Path.GetFullPath(Path.Combine(root, admin));
        if (!Directory.Exists(admin)) return null;

        var commondirFile = Path.Combine(admin, "commondir");
        if (File.Exists(commondirFile))
        {
            var rel = File.ReadAllText(commondirFile).Trim();
            if (rel.Length > 0)
                return Normalize(Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(admin, rel)));
        }
        // Without commondir (a submodule's gitdir, say) this is not a linked worktree.
        return null;
    }

    private static (string? Branch, string? Head) ReadHead(string headFile)
    {
        try
        {
            if (!File.Exists(headFile)) return (null, null);
            var text = File.ReadLines(headFile).FirstOrDefault()?.Trim() ?? "";
            if (text.StartsWith("ref:", StringComparison.Ordinal))
            {
                var r = text["ref:".Length..].Trim();
                return (r.StartsWith("refs/heads/", StringComparison.Ordinal) ? r["refs/heads/".Length..] : r, null);
            }
            return (null, text.Length >= 7 ? text[..7] : text);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>A folder as the scanner compares it: full, OS separators, no trailing separator.</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // "C:" alone is the drive's current directory, not its root.
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
    }

    /// <summary>Two folders as typed (trailing separator, '/' or '\', case on Windows) name the same place.</summary>
    public static bool SameFolder(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return SamePath(Normalize(a), Normalize(b)); }
        catch { return SamePath(a, b); }
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

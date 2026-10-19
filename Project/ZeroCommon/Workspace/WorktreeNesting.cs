namespace Agent.Common.Workspace;

/// <summary>How one workspace row is drawn in a sidebar that nests git worktrees.</summary>
/// <param name="Branch">The branch label of the workspace's own tree, or "" when its folder is not a working tree root.</param>
/// <param name="NestedUnder">The index of the row it is drawn under, or -1 for a top-level row.</param>
/// <param name="Children">The repository's other trees, listed under this row — non-empty only for a host row.</param>
public sealed record WorktreeRow(string Branch, int NestedUnder, IReadOnlyList<GitWorktreeInfo> Children)
{
    public bool IsNested => NestedUnder >= 0;
}

/// <summary>
/// The sidebar's nesting rule, without any UI: workspaces are grouped by repository; the
/// main tree's row hosts the group (or, when the main tree is not a workspace, the first
/// listed row of that repository); the other rows of the group are drawn under the host,
/// and the host lists every tree of the repository but its own — opened or not. The rows'
/// order is never changed: CLI group indexes and the stored workspaces refer to it.
/// </summary>
public static class WorktreeNesting
{
    /// <param name="workspacePaths">The workspaces' folders, in list order.</param>
    /// <param name="scan">The scan result per folder (missing or null = not a working tree root).</param>
    public static IReadOnlyList<WorktreeRow> Plan(IReadOnlyList<string> workspacePaths,
        Func<string, GitWorktreeSet?> scan)
    {
        var sets = workspacePaths.Select(scan).ToList();
        var rows = new WorktreeRow[workspacePaths.Count];

        // Repository → the row indexes in it, in list order. Keyed and compared by resolved paths
        // (RepoKey, Real): two workspaces reaching one repository through different paths — a
        // symlink, a junction — are still one group.
        var groups = new List<(string Repo, List<int> Members)>();
        for (var i = 0; i < workspacePaths.Count; i++)
        {
            var set = sets[i];
            var own = set is null ? null : SelfOf(set, workspacePaths[i]);
            rows[i] = new WorktreeRow(own?.Label ?? "", -1, Array.Empty<GitWorktreeInfo>());
            if (set is null || own is null) continue;
            var group = groups.FirstOrDefault(g => GitWorktreeScanner.SamePath(g.Repo, set.RepoKey));
            if (group.Members is null) groups.Add((set.RepoKey, new List<int> { i }));
            else group.Members.Add(i);
        }

        foreach (var (_, members) in groups)
        {
            // The main tree hosts its repository; without it, the first listed tree does.
            var host = members.FirstOrDefault(i => SelfOf(sets[i]!, workspacePaths[i])!.IsMain, members[0]);
            var hostReal = SelfOf(sets[host]!, workspacePaths[host])!.Real;
            var children = sets[host]!.Worktrees
                .Where(t => !GitWorktreeScanner.SamePath(t.Real, hostReal))
                .ToList();
            rows[host] = rows[host] with { Children = children };
            foreach (var i in members)
                if (i != host) rows[i] = rows[i] with { NestedUnder = host };
        }
        return rows;
    }

    private static GitWorktreeInfo? SelfOf(GitWorktreeSet set, string path) =>
        set.Self ?? set.Worktrees.FirstOrDefault(t => GitWorktreeScanner.SameFolder(t.Path, path));
}

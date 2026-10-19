using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Agent.Common.Workspace;

namespace ZeroCommon.Tests;

/// <summary>The sidebar's worktree detection: git's own files read without git, and the nesting rule.</summary>
[Trait("Category", "Worktree")]
public sealed class GitWorktreeScannerTests : IDisposable
{
    private readonly string _root;

    public GitWorktreeScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aztest-wt-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);   // git marks pack files read-only
            Directory.Delete(_root, true);
        }
        catch { }
    }

    /// <summary>A main tree at <c>repo/</c> plus linked trees, laid out the way git writes them.</summary>
    private string FakeRepo(params (string Name, string Tree, string Head)[] linked)
    {
        var repo = Path.Combine(_root, "repo");
        var common = Path.Combine(repo, ".git");
        Directory.CreateDirectory(common);
        File.WriteAllText(Path.Combine(common, "HEAD"), "ref: refs/heads/main\n");
        foreach (var (name, tree, head) in linked)
        {
            var admin = Path.Combine(common, "worktrees", name);
            Directory.CreateDirectory(admin);
            Directory.CreateDirectory(tree);
            // git writes forward slashes here, even on Windows.
            File.WriteAllText(Path.Combine(admin, "gitdir"), Path.Combine(tree, ".git").Replace('\\', '/') + "\n");
            File.WriteAllText(Path.Combine(admin, "HEAD"), head + "\n");
            File.WriteAllText(Path.Combine(admin, "commondir"), "../..\n");
            File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: " + admin.Replace('\\', '/') + "\n");
        }
        return repo;
    }

    [Fact]
    public void Scan_FromTheMainTree_ListsMainThenLinkedTrees()
    {
        var a = Path.Combine(_root, "repo-feat");
        var b = Path.Combine(_root, "repo", ".claude", "worktrees", "fix");
        var repo = FakeRepo(("repo-feat", a, "ref: refs/heads/feat/x"), ("fix", b, "4622b7527589d96718c5b1da91ac261aa74c0d65"));

        var set = GitWorktreeScanner.Scan(repo)!;

        Assert.Equal(GitWorktreeScanner.RealPath(repo), GitWorktreeScanner.RealPath(set.MainPath), ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(3, set.Worktrees.Count);
        Assert.True(set.Worktrees[0].IsMain);
        Assert.Equal("main", set.Worktrees[0].Label);
        var feat = set.Worktrees.Single(w => GitWorktreeScanner.SameFolder(w.Path, a));
        Assert.Equal("feat/x", feat.Branch);
        var fix = set.Worktrees.Single(w => GitWorktreeScanner.SameFolder(w.Path, b));
        Assert.Null(fix.Branch);
        Assert.Equal("(4622b75)", fix.Label);   // detached
    }

    [Fact]
    public void Scan_FromALinkedTree_FindsTheSameRepository()
    {
        var a = Path.Combine(_root, "repo-feat");
        var repo = FakeRepo(("repo-feat", a, "ref: refs/heads/feat/x"));

        var fromLinked = GitWorktreeScanner.Scan(a + Path.DirectorySeparatorChar)!;   // a trailing separator as typed

        Assert.True(GitWorktreeScanner.SameFolder(repo, fromLinked.MainPath));
        Assert.Equal(2, fromLinked.Worktrees.Count);
    }

    [Fact]
    public void Scan_ThroughALinkedPath_StillFindsTheFoldersOwnTree()
    {
        // Measured on the macOS runner: /var/folders is /private/var/folders, git records the
        // resolved path, and comparing strings made every tree read as "not a worktree root".
        var a = Path.Combine(_root, "repo-feat");
        FakeRepo(("repo-feat", a, "ref: refs/heads/feat/x"));
        var alias = Path.Combine(Path.GetTempPath(), "aztest-wt-link-" + Guid.NewGuid().ToString("n"));
        if (!TryLink(alias, _root)) return;   // no way to make a link here: nothing to prove
        try
        {
            var viaLink = Path.Combine(alias, "repo-feat");
            var set = GitWorktreeScanner.Scan(viaLink);

            Assert.NotNull(set);
            // The folder's own entry carries the path as the workspace names it.
            Assert.Contains(set!.Worktrees, w => !w.IsMain && GitWorktreeScanner.SameFolder(w.Path, viaLink));
            Assert.Equal(2, set.Worktrees.Count);

            // The main tree opened by its real path and the linked tree opened through the link are
            // one repository: the linked one nests under the main one, and is not listed twice.
            var repo = Path.Combine(_root, "repo");
            var paths = new[] { repo, viaLink };
            var rows = WorktreeNesting.Plan(paths, GitWorktreeScanner.Scan);
            Assert.False(rows[0].IsNested);
            Assert.Equal(0, rows[1].NestedUnder);
            Assert.Single(rows[0].Children);
            Assert.Equal("feat/x", rows[1].Branch);
        }
        finally
        {
            try { Directory.Delete(alias); } catch { }
        }
    }

    /// <summary>A directory symlink, or on Windows without the privilege a junction.</summary>
    private static bool TryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch { }
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    [Fact]
    public void Scan_LeavesOutATreeDeletedWithoutRemove()
    {
        var gone = Path.Combine(_root, "gone");
        var repo = FakeRepo(("gone", gone, "ref: refs/heads/old"));
        Directory.Delete(gone, true);   // the admin folder stays until `git worktree prune`

        Assert.Single(GitWorktreeScanner.Scan(repo)!.Worktrees);
    }

    [Fact]
    public void Scan_IsNullForASubfolderOrAPlainFolder()
    {
        var repo = FakeRepo();
        var sub = Path.Combine(repo, "src");
        Directory.CreateDirectory(sub);

        Assert.Null(GitWorktreeScanner.Scan(sub));
        Assert.Null(GitWorktreeScanner.Scan(_root));
        Assert.Null(GitWorktreeScanner.Scan(Path.Combine(_root, "missing")));
        Assert.Null(GitWorktreeScanner.Scan(null));
    }

    [Fact]
    public void Scan_AgreesWithRealGit()
    {
        if (!RunGit(_root, "--version")) return;   // no git on this machine: the fake-layout tests cover the reader
        var repo = Path.Combine(_root, "real");
        Directory.CreateDirectory(repo);
        Assert.True(RunGit(repo, "init -q -b main"));
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        Assert.True(RunGit(repo, "add ."));
        Assert.True(RunGit(repo, "-c user.email=t@t -c user.name=t commit -q -m init"));
        var linked = Path.Combine(_root, "real-wt");
        Assert.True(RunGit(repo, $"worktree add -q -b feature \"{linked}\""));

        var set = GitWorktreeScanner.Scan(linked)!;

        Assert.Equal(GitWorktreeScanner.RealPath(repo), GitWorktreeScanner.RealPath(set.MainPath), ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(new[] { "main", "feature" }, set.Worktrees.Select(w => w.Label));
    }

    private static bool RunGit(string cwd, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── origin: where Claude Code and Codex put their worktrees ──────────────

    [Theory]
    [InlineData(@"C:\code\repo\.claude\worktrees\ui-improvement", WorktreeOrigin.Claude)]
    [InlineData(@"C:\code\talk-bridge\.claude\worktrees\feat\talkbridge-desktop", WorktreeOrigin.Claude)]   // branch-named, nested
    [InlineData(@"C:\Users\u\.codex\worktrees\c14b\AgentZeroLite", WorktreeOrigin.Codex)]                // measured, Codex 0.162 --worktree
    [InlineData("/Users/u/.codex/worktrees/c14b/repo", WorktreeOrigin.Codex)]
    [InlineData(@"C:\Users\u\.codex\worktrees\c14b", WorktreeOrigin.Git)]                                // not the <id>/<repo> shape
    [InlineData(@"C:\code\psmon\AgentZeroLite-avalonia-v2", WorktreeOrigin.Git)]
    public void Origin_FromTheStandardLocations(string path, WorktreeOrigin expected)
        => Assert.Equal(expected, WorktreeOrigins.Of(path));

    [Fact]
    public void CodexTree_IsLabelledAndNamedApartFromTheMainTree()
    {
        var codex = new GitWorktreeInfo(@"C:\Users\u\.codex\worktrees\c14b\AgentZeroLite", null, "4622b75", false);
        Assert.Equal("codex c14b (4622b75)", codex.Label);
        Assert.Equal("AgentZeroLite-codex-c14b", codex.SuggestedWorkspaceName);   // not a second "AgentZeroLite"

        var claude = new GitWorktreeInfo(@"C:\code\repo\.claude\worktrees\ui-improvement", "feat/ui-improvement", null, false);
        Assert.Equal("feat/ui-improvement", claude.Label);
        Assert.Equal("ui-improvement", claude.SuggestedWorkspaceName);
    }

    // ── nesting ──────────────────────────────────────────────────────────────

    private static GitWorktreeSet Set(string main, params (string Path, string Branch)[] linked) =>
        new(main, new[] { new GitWorktreeInfo(main, "main", null, true) }
            .Concat(linked.Select(l => new GitWorktreeInfo(l.Path, l.Branch, null, false))).ToList());

    [Fact]
    public void Nesting_MainHostsItsWorktrees_OpenOnesAreDrawnUnderIt()
    {
        var set = Set(@"C:\r", (@"C:\r-feat", "feat"), (@"C:\r-fix", "fix"));
        var paths = new[] { @"C:\other", @"C:\r-feat", @"C:\r" };
        var rows = WorktreeNesting.Plan(paths, p => p == @"C:\other" ? null : set);

        Assert.False(rows[0].IsNested);
        Assert.Empty(rows[0].Children);
        Assert.Equal(2, rows[1].NestedUnder);                     // feat is drawn under its repository's main tree
        Assert.Equal("feat", rows[1].Branch);
        Assert.False(rows[2].IsNested);
        Assert.Equal(new[] { "feat", "fix" }, rows[2].Children.Select(c => c.Label));   // opened or only detected
    }

    [Fact]
    public void Nesting_WithoutTheMainTree_TheFirstListedTreeHosts()
    {
        var set = Set(@"C:\r", (@"C:\r-feat", "feat"), (@"C:\r-fix", "fix"));
        var rows = WorktreeNesting.Plan(new[] { @"C:\r-fix", @"C:\r-feat" }, _ => set);

        Assert.False(rows[0].IsNested);
        Assert.Equal(new[] { "main", "feat" }, rows[0].Children.Select(c => c.Label));
        Assert.Equal(0, rows[1].NestedUnder);
    }

    [Fact]
    public void Nesting_PlainFoldersAndSeparateRepositoriesStayApart()
    {
        var r1 = Set(@"C:\a");
        var r2 = Set(@"C:\b", (@"C:\b2", "x"));
        var rows = WorktreeNesting.Plan(new[] { @"C:\a", @"C:\b", @"C:\plain" },
            p => p switch { @"C:\a" => r1, @"C:\b" => r2, _ => null });

        Assert.All(rows, r => Assert.False(r.IsNested));
        Assert.Empty(rows[0].Children);
        Assert.Single(rows[1].Children);
        Assert.Equal("", rows[2].Branch);
    }
}

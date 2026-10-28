using System.Diagnostics;
using Agent.Common.Module;
using Agent.Common.Workspace;
using Xunit;
using K = Agent.Common.Module.GitDiffReader.LineKind;

namespace ZeroCommon.Tests;

[Trait("Category", "DiffReview")]
public sealed class TextDiffTests
{
    private static string Sig(IEnumerable<GitDiffReader.DiffLine> lines)
        => string.Concat(lines.Select(l => l.Kind switch { K.Add => "+", K.Delete => "-", _ => " " }));

    [Fact]
    public void Script_is_minimal_and_numbers_both_sides()
    {
        var script = TextDiff.Script(["a", "b", "c", "d"], ["a", "x", "c", "d", "e"]);
        Assert.Equal(" -+  +", Sig(script));
        var x = script.Single(l => l.Text == "x");
        Assert.Equal((null, 2), (x.OldLineNo, x.NewLineNo));
        var d = script.Single(l => l.Text == "d");
        Assert.Equal((4, 4), (d.OldLineNo, d.NewLineNo));
    }

    [Fact]
    public void Identical_texts_have_no_hunks_and_a_new_file_is_all_adds()
    {
        Assert.Empty(TextDiff.Compute("f", "a\nb\n", "a\r\nb").Hunks);
        var created = TextDiff.Compute("f", null, "one\ntwo\n");
        Assert.True(created.IsNew);
        Assert.Equal("++", Sig(Assert.Single(created.Hunks).Lines));
        var removed = TextDiff.Compute("f", "one", null);
        Assert.True(removed.IsDeleted);
    }

    [Fact]
    public void Distant_changes_make_separate_hunks_with_three_lines_of_context()
    {
        var a = Enumerable.Range(1, 30).Select(i => $"l{i}").ToArray();
        var b = a.ToArray();
        b[1] = "changed-2";
        b[25] = "changed-26";
        var file = TextDiff.Compute("f", string.Join("\n", a), string.Join("\n", b));
        Assert.Equal(2, file.Hunks.Count);
        Assert.Equal("@@ -1,5 +1,5 @@", file.Hunks[0].Header);
        Assert.Equal(23, file.Hunks[1].OldStart);
        Assert.Equal(8, file.Hunks[1].Lines.Count);   // 3 context + one delete + one add + 3 context
    }

    [Fact]
    public void Close_changes_merge_into_one_hunk()
    {
        var a = Enumerable.Range(1, 20).Select(i => $"l{i}").ToArray();
        var b = a.ToArray();
        b[5] = "x";
        b[10] = "y";
        Assert.Single(TextDiff.Compute("f", string.Join("\n", a), string.Join("\n", b)).Hunks);
    }
}

[Trait("Category", "DiffReview")]
public sealed class AiChangeJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aichg-{Guid.NewGuid():N}");

    public AiChangeJournalTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string P(string rel) => Path.Combine(_root, rel);

    [Fact]
    public void First_write_keeps_the_original_later_writes_only_count()
    {
        File.WriteAllText(P("a.txt"), "before\n");
        var j = new AiChangeJournal();
        var raised = 0;
        j.Changed += () => raised++;

        j.BeforeWrite(_root, "a.txt");
        File.WriteAllText(P("a.txt"), "after one\n");
        j.BeforeWrite(_root, "a.txt");
        File.WriteAllText(P("a.txt"), "after two\n");

        var c = Assert.Single(j.For(_root));
        Assert.Equal("before\n", c.OriginalText);
        Assert.Equal(2, c.Writes);
        Assert.Equal("a.txt", c.RelativePath);
        Assert.Equal(2, raised);
        Assert.Equal("-+", string.Concat(AiChangeJournal.Diff(c).Hunks.Single().Lines.Select(l => l.Kind == K.Add ? "+" : "-")));
    }

    [Fact]
    public void Revert_restores_an_edited_file_and_deletes_a_created_one()
    {
        File.WriteAllText(P("keep.txt"), "original");
        var j = new AiChangeJournal();
        j.BeforeWrite(_root, "keep.txt");
        File.WriteAllText(P("keep.txt"), "rewritten");
        j.BeforeWrite(_root, "sub/new.txt");
        Directory.CreateDirectory(P("sub"));
        File.WriteAllText(P("sub/new.txt"), "fresh");

        var created = j.Snapshot().Single(c => c.Created);
        Assert.Equal("sub/new.txt", created.RelativePath);

        Assert.Null(j.Revert(P("keep.txt")));
        Assert.Equal("original", File.ReadAllText(P("keep.txt")));
        Assert.Null(j.Revert(created.FullPath));
        Assert.False(File.Exists(P("sub/new.txt")));
        Assert.Equal(0, j.Count);
        Assert.NotNull(j.Revert(P("keep.txt")));   // no longer tracked
    }

    [Fact]
    public void Paths_outside_the_root_and_binary_files_are_not_tracked_and_keep_forgets()
    {
        File.WriteAllBytes(P("img.bin"), [1, 2, 0, 3]);
        var j = new AiChangeJournal();
        j.BeforeWrite(_root, "../escape.txt");
        j.BeforeWrite(_root, "img.bin");
        j.BeforeWrite(null, "a.txt");
        Assert.Equal(0, j.Count);

        j.BeforeWrite(_root, "x.txt");
        j.Keep(P("x.txt"));
        Assert.Empty(j.For(_root));
        Assert.Empty(j.For(P("elsewhere")));
    }
}

[Trait("Category", "DiffReview")]
public sealed class DiffReviewPromptTests
{
    [Fact]
    public void Groups_by_file_orders_by_line_and_quotes_the_line()
    {
        var p = DiffReviewPrompt.Build([
            new DiffReviewNote("b.cs", 9, "new", "rename this", "var x = 1;"),
            new DiffReviewNote("a.cs", 30, "old", "why removed?"),
            new DiffReviewNote("b.cs", 2, "new", "typo"),
        ]);
        Assert.Contains("### b.cs\n- line 2 (new): typo\n- line 9 (new) `var x = 1;`: rename this", p.Replace("\r\n", "\n"));
        Assert.Contains("### a.cs", p);

        var one = DiffReviewPrompt.BuildSingleLine([new DiffReviewNote("a.cs", 1, "new", "multi\nline")]);
        Assert.DoesNotContain('\n', one);
        Assert.Contains("a.cs: line 1 (new) — multi line", one);
    }
}

[Trait("Category", "DiffReview")]
public sealed class GitDiffRunnerTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"gdr-{Guid.NewGuid():N}");

    public GitDiffRunnerTests() => Directory.CreateDirectory(_repo);
    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, true);
        }
        catch { }
    }

    private bool Git(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public async Task Working_tree_changes_and_untracked_files_both_show()
    {
        if (!Git("init", "-q")) return;   // no git on this machine
        Git("config", "user.email", "t@example.com");
        Git("config", "user.name", "t");
        File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "one\ntwo\n");
        Git("add", ".");
        Git("commit", "-q", "-m", "init");
        File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "one\nTWO\n");
        File.WriteAllText(Path.Combine(_repo, "new.txt"), "hello\n");

        var r = await GitDiffRunner.ReadAsync(_repo);

        Assert.True(r.Ok, r.Error);
        Assert.Contains(r.Files, f => f.NewPath == "tracked.txt" && f.Hunks.Count == 1);
        var created = Assert.Single(r.Files, f => f.NewPath == "new.txt");
        Assert.True(created.IsNew);
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_says_so_plainly()
    {
        if (!Git("--version")) return;   // no git on this machine
        var r = await GitDiffRunner.ReadAsync(_repo);
        Assert.False(r.Ok);
        // Not git diff's own advice ("Use --no-index to compare two paths …").
        Assert.Equal("not a git repository", r.Error);
    }

    [Fact]
    public async Task New_files_past_the_cap_are_counted_not_dropped_silently()
    {
        if (!Git("init", "-q")) return;
        for (int i = 0; i < GitDiffRunner.MaxUntrackedFiles + 3; i++)
            File.WriteAllText(Path.Combine(_repo, $"n{i:D3}.txt"), $"{i}\n");

        var r = await GitDiffRunner.ReadAsync(_repo);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(GitDiffRunner.MaxUntrackedFiles, r.Files.Count);
        Assert.Equal(GitDiffRunner.MaxUntrackedFiles + 3, r.UntrackedTotal);
        Assert.Equal(3, r.UntrackedOmitted);
    }
}

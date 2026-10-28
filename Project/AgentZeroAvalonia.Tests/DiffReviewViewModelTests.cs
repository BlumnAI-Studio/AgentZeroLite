using Agent.Common.Module;
using Agent.Common.Workspace;
using AgentZeroAvalonia.ViewModels;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>The DIFF tab over a fake git reader and a real AI-change journal in a temp folder.</summary>
public class DiffReviewViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"diffvm-{Guid.NewGuid():N}");

    public DiffReviewViewModelTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Func<string, CancellationToken, Task<GitDiffResult>> Git(params GitDiffReader.DiffFile[] files)
        => (_, _) => Task.FromResult(new GitDiffResult(true, files, null));

    private DiffReviewViewModel Vm(Func<string, CancellationToken, Task<GitDiffResult>> git, AiChangeJournal? journal = null)
        => new(journal ?? new AiChangeJournal(), git, persistNotes: false) { Directory = _dir };

    [Fact]
    public async Task Lists_files_hunks_and_lines_with_a_summary()
    {
        var vm = Vm(Git(TextDiff.Compute("src/a.cs", "x\ny\n", "x\nY\nz\n")));
        await vm.LoadAsync();

        Assert.Equal("1 file · +2 −1", vm.Summary);
        Assert.Equal([DiffRowKind.File, DiffRowKind.Hunk, DiffRowKind.Line, DiffRowKind.Line, DiffRowKind.Line, DiffRowKind.Line],
            vm.Rows.Select(r => r.Kind));
        Assert.Equal("+2 −1", vm.Rows[0].Badge);
        Assert.False(vm.Rows[0].HasChangeActions);   // git rows cannot be reverted from here
    }

    [Fact]
    public async Task A_note_is_sent_to_the_terminal_as_one_line_and_then_cleared()
    {
        var vm = Vm(Git(TextDiff.Compute("a.cs", "old\n", "new\n")));
        await vm.LoadAsync();
        string? sent = null;
        vm.SendToTerminal = text => { sent = text; return null; };

        var added = vm.Rows.Single(r => r.IsAdd);
        vm.StartNoteCommand.Execute(added);
        Assert.True(added.IsEditing);
        added.Draft = "use a constant\nplease";
        vm.SaveNoteCommand.Execute(added);
        Assert.Equal(1, vm.NoteCount);
        Assert.Equal(("a.cs", 1, "new"), (added.Notes[0].Note.FilePath, added.Notes[0].Note.LineNumber, added.Notes[0].Note.Side));

        vm.SendNotesToTerminalCommand.Execute(null);
        Assert.NotNull(sent);
        Assert.DoesNotContain('\n', sent);
        Assert.Contains("a.cs: line 1 (new) `new` — use a constant please", sent);
        Assert.Equal(0, vm.NoteCount);
        Assert.Contains("Sent 1 note", vm.Status);
    }

    [Fact]
    public async Task A_deleted_line_anchors_on_the_old_side_and_an_empty_draft_adds_nothing()
    {
        var vm = Vm(Git(TextDiff.Compute("a.cs", "keep\ngone\n", "keep\n")));
        await vm.LoadAsync();
        var deleted = vm.Rows.Single(r => r.IsDelete);
        Assert.Equal((2, "old"), deleted.Anchor);

        vm.StartNoteCommand.Execute(deleted);
        deleted.Draft = "   ";
        vm.SaveNoteCommand.Execute(deleted);
        Assert.Equal(0, vm.NoteCount);
        Assert.False(deleted.IsEditing);
    }

    [Fact]
    public async Task Notes_survive_a_refresh_while_their_line_still_exists()
    {
        var vm = Vm(Git(TextDiff.Compute("a.cs", "a\n", "b\n")));
        await vm.LoadAsync();
        var row = vm.Rows.Single(r => r.IsAdd);
        vm.StartNoteCommand.Execute(row);
        row.Draft = "why";
        vm.SaveNoteCommand.Execute(row);

        await vm.LoadAsync();
        Assert.Equal(1, vm.NoteCount);
        Assert.Equal("why", vm.Rows.Single(r => r.IsAdd).Notes.Single().Text);
    }

    [Fact]
    public async Task Ai_changes_list_the_journal_and_revert_puts_the_original_back()
    {
        var file = Path.Combine(_dir, "notes.md");
        File.WriteAllText(file, "original\n");
        var journal = new AiChangeJournal();
        journal.BeforeWrite(_dir, "notes.md");
        File.WriteAllText(file, "written by the AI\n");

        var vm = Vm(Git(), journal);
        vm.Source = DiffSource.AiChanges;
        await vm.LoadAsync();

        var header = vm.Rows.First();
        Assert.True(header.HasChangeActions);
        Assert.Equal("notes.md", header.FilePath);
        Assert.Equal(1, vm.AiChangeCount);

        vm.RevertFileCommand.Execute(header);
        Assert.Equal("original\n", File.ReadAllText(file));
        Assert.StartsWith("Reverted notes.md", vm.Status);
    }

    [Fact]
    public async Task A_folder_git_cannot_read_says_so_and_points_at_the_ai_view()
    {
        var vm = Vm((_, _) => Task.FromResult(GitDiffResult.Fail("not a git repository")));
        await vm.LoadAsync();
        Assert.Empty(vm.Rows);
        Assert.Equal("Not a git repository — the AI changes view works in any folder.", vm.Summary);
    }

    [Fact]
    public async Task The_summary_says_when_new_files_were_left_out()
    {
        // Measured on music-harness: 6 changed + 362 new files read as "56 files" with no hint of the rest.
        var shown = TextDiff.Compute("new.txt", null, "x\n");
        var vm = Vm((_, _) => Task.FromResult(new GitDiffResult(true, [shown], null, UntrackedTotal: 362)));
        await vm.LoadAsync();
        Assert.Equal("1 file · +1 −0 · showing 50 of 362 new files", vm.Summary);

        var vm2 = Vm((_, _) => Task.FromResult(new GitDiffResult(true, [shown], null, UntrackedTotal: 1)));
        await vm2.LoadAsync();
        Assert.Equal("1 file · +1 −0", vm2.Summary);
    }
}

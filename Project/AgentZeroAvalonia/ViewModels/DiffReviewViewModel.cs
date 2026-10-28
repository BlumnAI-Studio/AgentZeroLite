using System.Collections.ObjectModel;
using Agent.Common;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Agent.Common.Module;
using Agent.Common.Workspace;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>What the DIFF tab compares.</summary>
public enum DiffSource
{
    /// <summary>The repository's working tree against HEAD, untracked files included.</summary>
    WorkingTree,

    /// <summary>What the bot's AI mode wrote this run, against each file's original.</summary>
    AiChanges,
}

public enum DiffRowKind { File, Hunk, Line }

/// <summary>A reviewer note under a diff line.</summary>
public sealed class DiffNoteViewModel(DiffReviewNote note, Action<DiffNoteViewModel> remove)
{
    public DiffReviewNote Note { get; } = note;
    public string Text => Note.Text;
    internal Action<DiffNoteViewModel> Remove { get; } = remove;
}

/// <summary>
/// One row of the diff list: a file header, a hunk header, or a line. Lines carry their notes
/// and an inline editor; file headers carry the AI-change actions.
/// </summary>
public sealed partial class DiffRowViewModel : ObservableObject
{
    public DiffRowKind Kind { get; init; }
    public string FilePath { get; init; } = "";
    public string Text { get; init; } = "";
    public string OldNo { get; init; } = "";
    public string NewNo { get; init; } = "";
    public GitDiffReader.LineKind LineKind { get; init; }
    public string Marker => LineKind switch { GitDiffReader.LineKind.Add => "+", GitDiffReader.LineKind.Delete => "-", _ => " " };

    public bool IsFile => Kind == DiffRowKind.File;
    public bool IsHunk => Kind == DiffRowKind.Hunk;
    public bool IsLine => Kind == DiffRowKind.Line;
    public bool IsAdd => IsLine && LineKind == GitDiffReader.LineKind.Add;
    public bool IsDelete => IsLine && LineKind == GitDiffReader.LineKind.Delete;

    /// <summary>File rows: "new", "deleted", "binary", "+3 −1".</summary>
    public string Badge { get; init; } = "";

    /// <summary>File rows of the AI-change view: the tracked file, for Revert / Keep.</summary>
    public string? ChangePath { get; init; }
    public bool HasChangeActions => ChangePath is not null;

    public ObservableCollection<DiffNoteViewModel> Notes { get; } = new();
    public bool HasNotes => Notes.Count > 0;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _draft = "";

    public DiffRowViewModel() => Notes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNotes));

    /// <summary>The side and number a note anchors to: the new side unless the line was deleted.</summary>
    public (int Line, string Side) Anchor
        => LineKind == GitDiffReader.LineKind.Delete
            ? (int.TryParse(OldNo, out var o) ? o : 0, "old")
            : (int.TryParse(NewNo, out var n) ? n : 0, "new");
}

/// <summary>
/// The code panel's DIFF tab (the WPF host's Diff Review overlay, M0042): the workspace's changes
/// as a list a person can read and annotate — click a line, write a note, and send every note to
/// the agent at once (typed into the active terminal, or asked of the bot). Its second source is
/// the AI mode's own writes (<see cref="AiChangeJournal"/>) with Revert / Keep per file, because
/// those land in the workspace without anyone approving them.
///
/// Drawn natively (no web view): a native view over a terminal's web view is the one arrangement
/// that works on macOS, and rows are plain view-models the tests can drive.
/// </summary>
public sealed partial class DiffReviewViewModel : ObservableObject
{
    private readonly AiChangeJournal _journal;
    private readonly Func<string, CancellationToken, Task<GitDiffResult>> _readGit;
    private readonly bool _persistNotes;
    private int _generation;
    private string _sessionId = Guid.NewGuid().ToString("n");

    public DiffReviewViewModel(AiChangeJournal journal,
        Func<string, CancellationToken, Task<GitDiffResult>>? readGit = null, bool persistNotes = true)
    {
        _journal = journal;
        _readGit = readGit ?? ((d, ct) => GitDiffRunner.ReadAsync(d, ct));
        _persistNotes = persistNotes;
        _journal.Changed += () => Dispatcher.UIThread.Post(OnJournalChanged);
    }

    public ObservableCollection<DiffRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string? _directory;
    [ObservableProperty] private DiffSource _source = DiffSource.WorkingTree;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _summary = "No workspace.";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _noteCount;
    [ObservableProperty] private int _aiChangeCount;

    /// <summary>Types a one-line prompt into the active terminal; returns an error, or null. Set by the host.</summary>
    public Func<string, string?>? SendToTerminal { get; set; }

    /// <summary>Starts an AI-mode turn with the prompt. Set by the host.</summary>
    public Action<string>? AskBot { get; set; }

    public bool IsWorkingTree => Source == DiffSource.WorkingTree;
    public bool IsAiChanges => Source == DiffSource.AiChanges;
    public bool HasNotes => NoteCount > 0;
    public string SendLabel => $"➤ Send {NoteCount} to terminal";
    public string AskLabel => $"🤖 Ask bot ({NoteCount})";
    public string AiTabLabel => AiChangeCount > 0 ? $"AI changes ({AiChangeCount})" : "AI changes";

    partial void OnSourceChanged(DiffSource value)
    {
        OnPropertyChanged(nameof(IsWorkingTree));
        OnPropertyChanged(nameof(IsAiChanges));
        _ = LoadAsync();
    }

    partial void OnNoteCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(SendLabel));
        OnPropertyChanged(nameof(AskLabel));
    }

    partial void OnAiChangeCountChanged(int value) => OnPropertyChanged(nameof(AiTabLabel));

    [RelayCommand] private void ShowWorkingTree() => Source = DiffSource.WorkingTree;
    [RelayCommand] private void ShowAiChanges() => Source = DiffSource.AiChanges;
    [RelayCommand] private Task Refresh() => LoadAsync();

    private void OnJournalChanged()
    {
        AiChangeCount = _journal.For(Directory).Count;
        if (Source == DiffSource.AiChanges) _ = LoadAsync();
    }

    /// <summary>The workspace followed (set by the code panel); the AI-change count is per workspace.</summary>
    partial void OnDirectoryChanged(string? value) => AiChangeCount = _journal.For(value).Count;

    /// <summary>Read the current source again. Notes on lines that still exist are kept.</summary>
    public async Task LoadAsync()
    {
        var generation = ++_generation;
        var dir = Directory;
        if (string.IsNullOrEmpty(dir))
        {
            Apply([], "No workspace.", null);
            return;
        }
        IsLoading = true;
        try
        {
            if (Source == DiffSource.AiChanges)
            {
                var changes = _journal.For(dir);
                var files = await Task.Run(() => changes.Select(c => (File: AiChangeJournal.Diff(c), c.FullPath)).ToList());
                if (generation != _generation) return;
                Apply(files, changes.Count == 0 ? "The AI mode has not written anything in this workspace yet." : null, null);
            }
            else
            {
                var result = await _readGit(dir, CancellationToken.None);
                if (generation != _generation) return;
                Apply(result.Files.Select(f => (File: f, FullPath: (string?)null)).ToList(),
                    result.Ok && result.Files.Count == 0 ? "No changes against HEAD." : null,
                    result.Ok ? null
                        : result.Error == "not a git repository" ? "Not a git repository — the AI changes view works in any folder."
                        : $"git diff failed — {result.Error}. The AI changes view works in any folder.",
                    result.UntrackedOmitted > 0
                        ? $" · showing {GitDiffRunner.MaxUntrackedFiles} of {result.UntrackedTotal} new files"
                        : "");
            }
        }
        catch (Exception ex)
        {
            if (generation == _generation) Apply([], null, ex.Message);
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
        }
    }

    private void Apply(IReadOnlyList<(GitDiffReader.DiffFile File, string? FullPath)> files, string? empty, string? error, string tail = "")
    {
        // Keep the notes whose line is still there.
        var kept = Rows.Where(r => r.HasNotes).SelectMany(r => r.Notes.Select(n => n.Note)).ToList();
        Rows.Clear();
        int adds = 0, dels = 0;
        foreach (var (file, fullPath) in files)
        {
            var path = file.NewPath is "/dev/null" or "" ? file.OldPath : file.NewPath;
            var fa = file.Hunks.Sum(h => h.Lines.Count(l => l.Kind == GitDiffReader.LineKind.Add));
            var fd = file.Hunks.Sum(h => h.Lines.Count(l => l.Kind == GitDiffReader.LineKind.Delete));
            adds += fa; dels += fd;
            var badge = string.Join(" · ", new[]
            {
                file.IsNew ? "new" : null,
                file.IsDeleted ? "deleted" : null,
                file.IsBinary ? "binary" : null,
                fa + fd > 0 ? $"+{fa} −{fd}" : (file.IsBinary ? null : "unchanged"),
            }.Where(s => s is not null));
            Rows.Add(new DiffRowViewModel { Kind = DiffRowKind.File, FilePath = path, Text = path, Badge = badge, ChangePath = fullPath });
            foreach (var hunk in file.Hunks)
            {
                Rows.Add(new DiffRowViewModel { Kind = DiffRowKind.Hunk, FilePath = path, Text = hunk.Header });
                foreach (var line in hunk.Lines)
                    Rows.Add(new DiffRowViewModel
                    {
                        Kind = DiffRowKind.Line, FilePath = path, Text = line.Text, LineKind = line.Kind,
                        OldNo = line.OldLineNo?.ToString() ?? "", NewNo = line.NewLineNo?.ToString() ?? "",
                    });
            }
        }
        foreach (var note in kept)
        {
            var row = Rows.FirstOrDefault(r => r.IsLine && r.FilePath == note.FilePath && r.Anchor == (note.LineNumber, note.Side));
            if (row is not null) row.Notes.Add(NewNote(row, note));
        }
        NoteCount = Rows.Sum(r => r.Notes.Count);
        Summary = error ?? empty ?? $"{files.Count} file{(files.Count == 1 ? "" : "s")} · +{adds} −{dels}{tail}";
    }

    // ── notes ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private void StartNote(DiffRowViewModel? row)
    {
        if (row is not { IsLine: true }) return;
        foreach (var r in Rows.Where(r => r.IsEditing && !ReferenceEquals(r, row))) r.IsEditing = false;
        row.Draft = "";
        row.IsEditing = true;
    }

    [RelayCommand]
    private void SaveNote(DiffRowViewModel? row)
    {
        if (row is null) return;
        var text = row.Draft.Trim();
        row.IsEditing = false;
        if (text.Length == 0) return;
        var (line, side) = row.Anchor;
        var note = new DiffReviewNote(row.FilePath, line, side, text, row.Text);
        row.Notes.Add(NewNote(row, note));
        row.Draft = "";
        NoteCount = Rows.Sum(r => r.Notes.Count);
        Persist(note);
    }

    [RelayCommand]
    private void CancelNote(DiffRowViewModel? row)
    {
        if (row is null) return;
        row.IsEditing = false;
        row.Draft = "";
    }

    [RelayCommand]
    private void RemoveNote(DiffNoteViewModel? note)
    {
        if (note is null) return;
        note.Remove(note);
        NoteCount = Rows.Sum(r => r.Notes.Count);
    }

    [RelayCommand]
    private void ClearNotes()
    {
        foreach (var r in Rows) r.Notes.Clear();
        NoteCount = 0;
        _sessionId = Guid.NewGuid().ToString("n");
    }

    private DiffNoteViewModel NewNote(DiffRowViewModel row, DiffReviewNote note)
        => new(note, n => row.Notes.Remove(n));

    public IReadOnlyList<DiffReviewNote> AllNotes() => Rows.SelectMany(r => r.Notes.Select(n => n.Note)).ToList();

    [RelayCommand]
    private void SendNotesToTerminal()
    {
        var notes = AllNotes();
        if (notes.Count == 0) return;
        if (SendToTerminal is null) { Status = "No terminal to send to."; return; }
        var error = SendToTerminal(DiffReviewPrompt.BuildSingleLine(notes));
        if (error is not null) { Status = error; return; }
        Shipped(notes, "the active terminal");
    }

    [RelayCommand]
    private void AskBotWithNotes()
    {
        var notes = AllNotes();
        if (notes.Count == 0) return;
        if (AskBot is null) { Status = "The bot is not available."; return; }
        AskBot(DiffReviewPrompt.Build(notes));
        Shipped(notes, "the bot (AI mode)");
    }

    private void Shipped(IReadOnlyList<DiffReviewNote> notes, string where)
    {
        Status = $"Sent {notes.Count} note{(notes.Count == 1 ? "" : "s")} to {where}.";
        AppLogger.Log($"[DiffReview] {Status}");
        MarkShipped();
        foreach (var r in Rows) r.Notes.Clear();
        NoteCount = 0;
        _sessionId = Guid.NewGuid().ToString("n");
    }

    private void Persist(DiffReviewNote note)
    {
        if (!_persistNotes) return;
        try
        {
            using var db = new AppDbContext();
            db.DiffComments.Add(new DiffComment
            {
                SessionId = _sessionId, FilePath = note.FilePath, LineNumber = note.LineNumber,
                Side = note.Side, CommentText = note.Text, CreatedAtUtc = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch (Exception ex) { AppLogger.Log($"[DiffReview] persist failed: {ex.Message}"); }
    }

    private void MarkShipped()
    {
        if (!_persistNotes) return;
        try
        {
            using var db = new AppDbContext();
            foreach (var row in db.DiffComments.Where(c => c.SessionId == _sessionId)) row.Shipped = true;
            db.SaveChanges();
        }
        catch (Exception ex) { AppLogger.Log($"[DiffReview] mark shipped failed: {ex.Message}"); }
    }

    // ── AI changes: revert / keep ────────────────────────────────────────────

    [RelayCommand]
    private void RevertFile(DiffRowViewModel? row)
    {
        if (row?.ChangePath is not { } path) return;
        var error = _journal.Revert(path);
        Status = error is null ? $"Reverted {row.FilePath}." : $"Could not revert {row.FilePath}: {error}";
    }

    [RelayCommand]
    private void KeepFile(DiffRowViewModel? row)
    {
        if (row?.ChangePath is not { } path) return;
        _journal.Keep(path);
        Status = $"Kept {row.FilePath} as the AI left it.";
    }

    [RelayCommand]
    private void RevertAll()
    {
        var changes = _journal.For(Directory);
        var failed = changes.Select(c => (c.RelativePath, Error: _journal.Revert(c.FullPath))).Where(x => x.Error is not null).ToList();
        Status = failed.Count == 0
            ? $"Reverted {changes.Count} file{(changes.Count == 1 ? "" : "s")}."
            : $"Reverted {changes.Count - failed.Count}; failed: {string.Join(", ", failed.Select(f => $"{f.RelativePath} ({f.Error})"))}";
    }
}

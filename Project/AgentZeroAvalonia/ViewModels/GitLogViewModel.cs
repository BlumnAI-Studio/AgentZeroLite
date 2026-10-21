using System.Collections.ObjectModel;
using Agent.Common;
using Agent.Common.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>One row of the history list — subject, who, when; the full message is the tooltip.</summary>
public sealed class GitCommitRowViewModel(GitCommitInfo commit)
{
    public string ShortHash { get; } = commit.ShortHash;
    public string Subject { get; } = commit.Subject;
    public string Author { get; } = commit.Author;
    public string Ago { get; } = commit.Date == default ? "" : AgoText(DateTimeOffset.Now - commit.Date);
    public string Tip { get; } = string.Join("\n", new[]
    {
        commit.Subject,
        commit.Body.Length > 0 ? "\n" + commit.Body : null,
        $"\n{commit.Author} · {(commit.Date == default ? "" : commit.Date.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))}",
        commit.Hash,
    }.Where(s => s is not null));

    private static string AgoText(TimeSpan t) => t.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => $"{(int)t.TotalMinutes}m ago",
        < 60 * 24 => $"{(int)t.TotalHours}h ago",
        < 60 * 24 * 60 => $"{(int)t.TotalDays}d ago",
        < 60 * 24 * 365 => $"{(int)(t.TotalDays / 30)}mo ago",
        _ => $"{(int)(t.TotalDays / 365)}y ago",
    };
}

/// <summary>
/// The GIT LOG tab beside CODE: the active workspace's recent commits, newest first, as a plain
/// list (GitHub Desktop's history). Read with local git (<see cref="GitLogReader"/>); a folder git
/// cannot read disables the tab and <see cref="Note"/> says why. Read-only.
/// </summary>
public sealed partial class GitLogViewModel : ObservableObject
{
    private int _generation;

    public ObservableCollection<GitCommitRowViewModel> Commits { get; } = new();

    [ObservableProperty] private string? _directory;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _note = "No workspace.";

    /// <summary>The tab's tooltip: what it shows, or why it is off.</summary>
    public string TabTip => IsAvailable ? "Recent commits of this workspace" : $"Git log unavailable — {Note}";

    public string Summary => IsAvailable ? $"{Commits.Count} commits" : Note;

    partial void OnIsAvailableChanged(bool value) => RaiseText();
    partial void OnNoteChanged(string value) => RaiseText();

    private void RaiseText()
    {
        OnPropertyChanged(nameof(TabTip));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Read <paramref name="directory"/>'s history; a newer call drops an older one's result.</summary>
    public async Task LoadAsync(string? directory)
    {
        var generation = ++_generation;
        Directory = directory;
        if (string.IsNullOrEmpty(directory))
        {
            Apply(GitLogResult.Fail("no workspace"));
            return;
        }
        IsLoading = true;
        GitLogResult result;
        try
        {
            result = await Task.Run(() => GitLogReader.ReadAsync(directory));
        }
        catch (Exception ex)
        {
            result = GitLogResult.Fail(ex.Message);
        }
        if (generation != _generation) return;
        IsLoading = false;
        Apply(result);
        AppLogger.Log(result.Ok
            ? $"[GitLog] {directory} | {result.Commits.Count} commit(s)"
            : $"[GitLog] {directory} | unavailable | {result.Error}");
    }

    private void Apply(GitLogResult result)
    {
        Commits.Clear();
        foreach (var c in result.Commits) Commits.Add(new GitCommitRowViewModel(c));
        Note = result.Ok ? "" : result.Error ?? "unavailable";
        IsAvailable = result.Ok;
        RaiseText();
    }

    [RelayCommand] private Task Refresh() => LoadAsync(Directory);
}

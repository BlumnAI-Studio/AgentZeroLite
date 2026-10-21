using System.Collections.ObjectModel;
using Agent.Common.Module;
using Agent.Common.Services;
using AgentZeroAvalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// A workspace — a folder, the terminal tabs opened in it, and how they are split
/// (M0035/M0036). It is the <see cref="ICliGroupInfo"/> that <c>CliWorkspacePersistence</c>
/// saves and the CLI catalog lists, so the same rows the WPF host writes come back here
/// unchanged, split layout included.
/// </summary>
public partial class WorkspaceViewModel : ObservableObject, ICliGroupInfo
{
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private TerminalTabViewModel? _activeTab;

    public string DirectoryPath { get; }

    /// <summary>The flat, persisted order — what the CLI's tab indexes and the layout's indexes refer to.</summary>
    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = new();

    /// <summary>How the tabs are split into panes.</summary>
    public WorkspaceLayout<TerminalTabViewModel> Layout { get; private set; }

    /// <summary>The layout's structure changed (split, move, close).</summary>
    public event Action? LayoutChanged;

    public Action<WorkspaceViewModel>? ActivateRequested { get; set; }
    public Action<WorkspaceViewModel>? RemoveRequested { get; set; }
    public Action<string>? OpenFolderRequested { get; set; }
    public Action<string>? CopyTextRequested { get; set; }

    // Context menu "copy path": a workspace has no parent of its own, so its relative form is
    // the home-relative one (~/code/x) — shown only when the folder is under home.
    public string? AbsolutePathText => Agent.Common.Workspace.PathCopyText.Absolute(DirectoryPath);
    public string? RelativePathText => Agent.Common.Workspace.PathCopyText.HomeRelative(DirectoryPath);
    public bool HasRelativePath => RelativePathText is not null;
    public string CopyAbsoluteHeader => $"Copy absolute path   {AbsolutePathText}";
    public string CopyRelativeHeader => $"Copy relative path   {RelativePathText}";

    // ── git worktrees (sidebar nesting) ─────────────────────────────────────

    /// <summary>
    /// The repository's other working trees, listed under this row — set only on the row that
    /// hosts the repository in the sidebar (the main tree, or the first listed tree of a repo
    /// whose main tree is not a workspace). Empty for every other row.
    /// </summary>
    public ObservableCollection<WorktreeEntryViewModel> Worktrees { get; } = new();

    /// <summary>This workspace is a tree of a repository another row hosts — it is drawn under that row, not at the top level.</summary>
    [ObservableProperty] private bool _isNested;

    /// <summary>The checked-out branch when the folder is a git working tree root, shown beside the name.</summary>
    [ObservableProperty] private string _branchLabel = "";

    public bool HasWorktrees => Worktrees.Count > 0;
    public bool HasBranchLabel => BranchLabel.Length > 0;
    partial void OnBranchLabelChanged(string value) => OnPropertyChanged(nameof(HasBranchLabel));
    internal void RaiseWorktreesChanged() => OnPropertyChanged(nameof(HasWorktrees));

    public WorkspaceViewModel(string directoryPath, string displayName)
    {
        DirectoryPath = directoryPath;
        _displayName = displayName;
        Layout = new WorkspaceLayout<TerminalTabViewModel>();
        Layout.Changed += OnLayoutChanged;
    }

    /// <summary>Rebuild the split tree from a stored layout; call after the tabs are in place.</summary>
    public void RestoreLayout(string? json)
    {
        Layout.Changed -= OnLayoutChanged;
        Layout = WorkspaceLayout<TerminalTabViewModel>.FromDock(DockPaneLayout.FromJson(json), Tabs);
        Layout.Changed += OnLayoutChanged;
        if (ActiveTab is not null && Layout.PaneOf(ActiveTab) is { } pane) pane.ActiveTab = ActiveTab;
    }

    private void OnLayoutChanged() => LayoutChanged?.Invoke();

    /// <summary>The pane holding the active tab (the first pane when there is none).</summary>
    public PaneNode<TerminalTabViewModel> ActivePane
        => (ActiveTab is null ? null : Layout.PaneOf(ActiveTab)) ?? Layout.FirstPane;

    IReadOnlyList<IConsoleTabInfo> ICliGroupInfo.TabsView => Tabs;
    public int ActiveTabIndex => ActiveTab is null ? 0 : Math.Max(0, Tabs.IndexOf(ActiveTab));

    /// <summary>The split layout in the WPF host's stored form; null while unsplit.</summary>
    public string? DockLayoutJson => Layout.ToJson(Tabs);

    partial void OnActiveTabChanged(TerminalTabViewModel? oldValue, TerminalTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null)
        {
            newValue.IsActive = true;
            if (Layout.PaneOf(newValue) is { } pane) pane.ActiveTab = newValue;
        }
    }

    [RelayCommand] private void Activate() => ActivateRequested?.Invoke(this);
    [RelayCommand] private void Remove() => RemoveRequested?.Invoke(this);
    [RelayCommand] private void OpenFolder() => OpenFolderRequested?.Invoke(DirectoryPath);
    [RelayCommand] private void CopyAbsolutePath() { if (AbsolutePathText is { } t) CopyTextRequested?.Invoke(t); }
    [RelayCommand] private void CopyRelativePath() { if (RelativePathText is { } t) CopyTextRequested?.Invoke(t); }
}

using System.Text.Json.Nodes;
using Agent.Common;
using Agent.Common.Platform;
using Agent.Common.Workspace;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// One git working tree listed under the workspace row that hosts its repository. Clicking
/// it switches to that folder — opening it as a workspace the first time, so the next click
/// (and the terminals started there) land in the same place.
/// </summary>
public sealed partial class WorktreeEntryViewModel : ObservableObject
{
    private readonly Action<WorktreeEntryViewModel> _go;
    private readonly Action<WorkspaceViewModel> _close;
    private readonly Action<string> _openFolder;
    private readonly Action<string> _copyText;

    public WorktreeEntryViewModel(GitWorktreeInfo tree, WorkspaceViewModel? workspace,
        Action<WorktreeEntryViewModel> go, Action<WorkspaceViewModel> close,
        Action<string> openFolder, Action<string> copyText, string hostPath)
    {
        Path = tree.Path;
        _go = go;
        _close = close;
        _openFolder = openFolder;
        _copyText = copyText;
        // A worktree's relative form is from the repository row it is listed under
        // (.claude/worktrees/x, or ../x for one beside it).
        AbsolutePathText = PathCopyText.Absolute(tree.Path);
        RelativePathText = PathCopyText.Relative(hostPath, tree.Path);
        Update(tree, workspace);
    }

    public string Path { get; }

    public string? AbsolutePathText { get; }
    public string? RelativePathText { get; }
    public bool HasRelativePath => RelativePathText is not null;
    public string CopyAbsoluteHeader => $"Copy absolute path   {AbsolutePathText}";
    public string CopyRelativeHeader => $"Copy relative path   {RelativePathText}";

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool _isMain;
    [ObservableProperty] private string _tip = "";

    /// <summary>The workspace name this tree opens as (ZeroCommon <see cref="GitWorktreeInfo.SuggestedWorkspaceName"/>).</summary>
    public string SuggestedName { get; private set; } = "";

    /// <summary>The workspace this tree is open as, or null while it is only detected.</summary>
    [ObservableProperty] private WorkspaceViewModel? _workspace;

    public bool IsOpened => Workspace is not null;
    public double LabelOpacity => IsOpened ? 1.0 : 0.6;

    partial void OnWorkspaceChanged(WorkspaceViewModel? value)
    {
        OnPropertyChanged(nameof(IsOpened));
        OnPropertyChanged(nameof(LabelOpacity));
    }

    internal void Update(GitWorktreeInfo tree, WorkspaceViewModel? workspace)
    {
        Label = tree.IsMain ? $"{tree.Label} (main tree)" : tree.Label;
        IsMain = tree.IsMain;
        SuggestedName = tree.SuggestedWorkspaceName;
        Workspace = workspace;
        var origin = tree.Origin switch
        {
            WorktreeOrigin.Claude => "Claude Code worktree\n",
            // Codex prunes its pool (the newest 15 are kept), so the folder may go away on its own.
            WorktreeOrigin.Codex => "Codex-managed worktree (detached HEAD; Codex prunes old ones)\n",
            _ => "",
        };
        Tip = workspace is null
            ? $"{origin}{tree.Path}\nClick to open this worktree as a workspace"
            : $"{origin}{tree.Path}\nOpen as workspace '{workspace.DisplayName}' · {workspace.Tabs.Count} terminal(s)";
    }

    [RelayCommand] private void Go() => _go(this);
    [RelayCommand] private void Close() { if (Workspace is { } ws) _close(ws); }
    [RelayCommand] private void OpenFolder() => _openFolder(Path);
    [RelayCommand] private void CopyAbsolutePath() { if (AbsolutePathText is { } t) _copyText(t); }
    [RelayCommand] private void CopyRelativePath() { if (RelativePathText is { } t) _copyText(t); }
}

/// <summary>
/// Git worktrees in the sidebar: a workspace whose folder is a repository's working tree lists
/// that repository's other trees under it, and a workspace that is one of those trees is drawn
/// there instead of at the top level. An agent working "in a worktree" (Claude's
/// <c>.claude/worktrees/…</c>, <c>git worktree add ../x</c>) moves to a new folder; this is how
/// that folder shows up next to the one it came from without anyone adding it by hand.
///
/// <para>Detection is <see cref="GitWorktreeScanner"/> (git's own files, no <c>git</c> process)
/// on a 5 s timer, off the UI thread and behind <see cref="PathAvailability.DirectoryExistsFast"/>
/// — a workspace on a disconnected share must not stall the sidebar. Only the display is
/// nested: <see cref="Workspaces"/> keeps its flat order, which is what the CLI's group
/// indexes and the stored rows refer to.</para>
/// </summary>
public partial class MainWindowViewModel
{
    private DispatcherTimer? _worktreeTimer;
    private bool _worktreeRescan;

    /// <summary>Start the worktree detection (App, once the window exists).</summary>
    public void StartWorktreeMonitor()
    {
        if (_worktreeTimer is not null) return;
        Workspaces.CollectionChanged += (_, _) => _ = RefreshWorktreesAsync();
        _worktreeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _worktreeTimer.Tick += (_, _) => _ = RefreshWorktreesAsync();
        _worktreeTimer.Start();
        _ = RefreshWorktreesAsync();
    }

    public void StopWorktreeMonitor()
    {
        _worktreeTimer?.Stop();
        _worktreeTimer = null;
    }

    /// <summary>
    /// Scan every workspace folder on the pool, then apply on the UI thread. A request during a
    /// scan asks for one more pass and gets the running task — so a caller that awaits (the CLI's
    /// <c>worktree open</c>) sees the state after its own change, not the pass already under way.
    /// </summary>
    public Task RefreshWorktreesAsync()
    {
        if (_worktreeScan is { IsCompleted: false } running)
        {
            _worktreeRescan = true;
            return running;
        }
        return _worktreeScan = ScanWorktreesAsync();
    }

    private Task? _worktreeScan;

    private async Task ScanWorktreesAsync()
    {
        try
        {
            do
            {
                _worktreeRescan = false;
                var paths = Workspaces.Select(w => w.DirectoryPath).Distinct(PathComparer).ToList();
                var sets = await Task.Run(() =>
                {
                    var found = new Dictionary<string, GitWorktreeSet?>(PathComparer);
                    foreach (var p in paths)
                        found[p] = PathAvailability.DirectoryExistsFast(p) ? GitWorktreeScanner.Scan(p) : null;
                    return found;
                });
                ApplyWorktrees(sets);
            } while (_worktreeRescan);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Worktrees] refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Nest and list from scan results — the rule is ZeroCommon's <see cref="WorktreeNesting"/>; this only projects it.</summary>
    internal void ApplyWorktrees(IReadOnlyDictionary<string, GitWorktreeSet?> sets)
    {
        var workspaces = Workspaces.ToList();
        var rows = WorktreeNesting.Plan(workspaces.Select(w => w.DirectoryPath).ToList(),
            p => sets.TryGetValue(p, out var s) ? s : null);
        for (var i = 0; i < workspaces.Count; i++)
        {
            var ws = workspaces[i];
            ws.BranchLabel = rows[i].Branch;
            ws.IsNested = rows[i].IsNested;
            SyncEntries(ws, rows[i].Children, sets);
        }
        RefreshActiveWorkspaceMarks();
    }

    private void SyncEntries(WorkspaceViewModel host, IReadOnlyList<GitWorktreeInfo> trees,
        IReadOnlyDictionary<string, GitWorktreeSet?> sets)
    {
        // A tree is open when a workspace's own tree resolves to the same folder (git's recorded
        // path and the workspace's typed path can differ by a link); the typed path is the fallback.
        WorkspaceViewModel? Opened(GitWorktreeInfo tree) =>
            Workspaces.FirstOrDefault(w =>
                sets.TryGetValue(w.DirectoryPath, out var s) && s?.Self is { } self
                    ? GitWorktreeScanner.SamePath(self.Real, tree.Real)
                    : GitWorktreeScanner.SameFolder(w.DirectoryPath, tree.Path));

        var same = host.Worktrees.Count == trees.Count
                   && host.Worktrees.Select(e => e.Path).SequenceEqual(trees.Select(t => t.Path), PathComparer);
        if (same)
        {
            for (var i = 0; i < trees.Count; i++) host.Worktrees[i].Update(trees[i], Opened(trees[i]));
            return;
        }

        var before = host.Worktrees.Count;
        host.Worktrees.Clear();
        foreach (var t in trees)
            host.Worktrees.Add(new WorktreeEntryViewModel(t, Opened(t), OpenWorktree, RemoveWorkspace, OpenFolder, CopyText, host.DirectoryPath));
        host.RaiseWorktreesChanged();
        if (trees.Count != before)
            AppLogger.Log($"[Worktrees] '{host.DisplayName}': {trees.Count} other worktree(s) — {string.Join(", ", trees.Select(t => t.Label))}");
    }

    /// <summary>Switch to a worktree: its workspace if it is open, else open it as one (nested under its repository's row).</summary>
    public void OpenWorktree(WorktreeEntryViewModel entry)
    {
        Page = AppPage.Terminals;
        if (entry.Workspace is { } ws && Workspaces.Contains(ws))
        {
            ActiveWorkspace = ws;
            return;
        }
        if (!PathAvailability.DirectoryExistsFast(entry.Path))
        {
            StatusText = $"Worktree folder is not reachable: {entry.Path}";
            return;
        }
        var opened = AddWorkspace(entry.Path, entry.SuggestedName);
        entry.Workspace = opened;
        AppLogger.Log($"[Worktrees] opened worktree '{entry.Label}' as workspace '{opened.DisplayName}' -> {entry.Path}");
        _ = RefreshWorktreesAsync();
    }

    /// <summary>The Avalonia-only <c>worktree list|open</c> CLI verb.</summary>
    public async Task<string> WorktreeCommandAsync(string verb, string arg)
    {
        await RefreshWorktreesAsync();
        switch (verb)
        {
            case "list" or "":
                return WorktreeListJson();
            case "open":
            {
                var entries = Workspaces.SelectMany(w => w.Worktrees).ToList();
                var entry = entries.FirstOrDefault(e => GitWorktreeScanner.SameFolder(e.Path, SafeFull(arg)))
                            ?? entries.FirstOrDefault(e => string.Equals(e.Label, arg, StringComparison.OrdinalIgnoreCase))
                            ?? entries.FirstOrDefault(e => e.Label.StartsWith(arg + " ", StringComparison.OrdinalIgnoreCase));
                if (arg.Length == 0 || entry is null)
                    return CliIpcProtocol.ErrorJson($"no detected worktree '{arg}' — see 'worktree list'");
                OpenWorktree(entry);
                await RefreshWorktreesAsync();
                var ws = ActiveWorkspace;
                var o = new JsonObject
                {
                    ["ok"] = true,
                    ["workspace"] = ws?.DisplayName,
                    ["path"] = ws?.DirectoryPath,
                    ["group_index"] = ws is null ? -1 : Workspaces.IndexOf(ws),
                    ["nested"] = ws?.IsNested ?? false,
                };
                return o.ToJsonString();
            }
            case "close":
            {
                // Closes the workspace a worktree is open as (its terminals end); the folder stays.
                var entry = Workspaces.SelectMany(w => w.Worktrees).FirstOrDefault(e => e.Workspace is not null
                    && (GitWorktreeScanner.SameFolder(e.Path, SafeFull(arg))
                        || string.Equals(e.Label, arg, StringComparison.OrdinalIgnoreCase)));
                if (arg.Length == 0 || entry?.Workspace is not { } ws)
                    return CliIpcProtocol.ErrorJson($"no open worktree workspace '{arg}' — see 'worktree list'");
                var name = ws.DisplayName;
                RemoveWorkspace(ws);
                await RefreshWorktreesAsync();
                return new JsonObject { ["ok"] = true, ["closed"] = name, ["path"] = entry.Path }.ToJsonString();
            }
            default:
                return CliIpcProtocol.ErrorJson($"unknown worktree verb '{verb}'. Use: list, open <path|branch>, close <path|branch>");
        }
    }

    private static string SafeFull(string path)
    {
        try { return path.Length == 0 ? "" : GitWorktreeScanner.Normalize(path); }
        catch { return path; }
    }

    private string WorktreeListJson()
    {
        var rows = new JsonArray();
        for (var g = 0; g < Workspaces.Count; g++)
        {
            var ws = Workspaces[g];
            var host = ws.IsNested
                ? Workspaces.FirstOrDefault(h => h.Worktrees.Any(e => ReferenceEquals(e.Workspace, ws)))
                : null;
            var trees = new JsonArray();
            foreach (var e in ws.Worktrees)
                trees.Add(new JsonObject
                {
                    ["path"] = e.Path,
                    ["label"] = e.Label,
                    ["main"] = e.IsMain,
                    ["workspace"] = e.Workspace?.DisplayName,
                });
            rows.Add(new JsonObject
            {
                ["group_index"] = g,
                ["workspace"] = ws.DisplayName,
                ["path"] = ws.DirectoryPath,
                ["branch"] = ws.BranchLabel.Length == 0 ? null : ws.BranchLabel,
                ["nested_under"] = host?.DisplayName,
                ["active"] = ReferenceEquals(ws, ActiveWorkspace),
                ["worktrees"] = trees,
            });
        }
        return new JsonObject { ["ok"] = true, ["workspaces"] = rows }.ToJsonString();
    }
}

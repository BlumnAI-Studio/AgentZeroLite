using System.Collections.ObjectModel;
using Agent.Common;
using Agent.Common.Platform;
using Agent.Common.Workspace;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// One entry of the code view's tree. A folder loads its children the first time it is
/// expanded (off the UI thread — a workspace can live on a network drive), so opening a big
/// repository costs one folder, not the whole tree.
/// </summary>
public sealed partial class FileNodeViewModel : ObservableObject
{
    private readonly CodeViewModel _owner;
    private bool _loaded;

    public FileNodeViewModel(CodeViewModel owner, WorkspaceEntry entry)
    {
        _owner = owner;
        Name = entry.Name;
        FullPath = entry.FullPath;
        IsDirectory = entry.IsDirectory;
        // A placeholder child gives the folder its expander before anything is read.
        if (IsDirectory) Children.Add(Placeholder(owner));
    }

    private FileNodeViewModel(CodeViewModel owner)
    {
        _owner = owner;
        Name = "…";
        FullPath = "";
        _loaded = true;
    }

    private static FileNodeViewModel Placeholder(CodeViewModel owner) => new(owner);

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string Icon => IsDirectory ? (IsExpanded ? "📂" : "📁") : FileIcon(Name);
    public ObservableCollection<FileNodeViewModel> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(Icon));
        if (value && IsDirectory && !_loaded) _ = LoadAsync();
    }

    internal async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var entries = await Task.Run(() => WorkspaceFiles.List(FullPath, _owner.Patterns));
        Children.Clear();
        foreach (var e in entries) Children.Add(new FileNodeViewModel(_owner, e));
    }

    private static string FileIcon(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".md" or ".markdown" or ".txt" => "📝",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".webp" or ".svg" => "🖼",
        ".json" or ".yaml" or ".yml" or ".toml" or ".xml" or ".ini" or ".config" or ".csproj" or ".slnx" or ".sln" => "⚙",
        ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".go" or ".rs" or ".java" or ".kt" or ".cpp" or ".c" or ".h"
            or ".axaml" or ".xaml" or ".html" or ".css" or ".ps1" or ".sh" => "‹›",
        _ => "📄",
    };
}

/// <summary>One row of the folder listing — read-only details, no actions.</summary>
public sealed class FolderRowViewModel(FolderEntryInfo info)
{
    public string Name { get; } = info.Name;
    public string FullPath { get; } = info.FullPath;
    public bool IsDirectory { get; } = info.IsDirectory;
    public string Icon { get; } = info.IsDirectory ? "📁" : "📄";
    public string SizeText { get; } = info.Size is { } n ? WorkspaceFiles.FormatSize(n) : "—";
    public string ModifiedText { get; } = info.Modified.ToString("yyyy-MM-dd HH:mm");
    public string CreatedText { get; } = info.Created.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// The code view (VS Code's explorer + a read-only editor): the active workspace's files in a
/// tree, the selected one shown with VS Code's TextMate grammars. Follows the active workspace;
/// it never writes. The file rules live in ZeroCommon's <see cref="WorkspaceFiles"/>.
/// </summary>
public sealed partial class CodeViewModel : ObservableObject
{
    private int _openGeneration;

    public ObservableCollection<FileNodeViewModel> Nodes { get; } = new();

    internal IReadOnlySet<string> Patterns { get; private set; } = new HashSet<string>();

    [ObservableProperty] private string? _root;
    [ObservableProperty] private string _rootName = "No workspace";
    [ObservableProperty] private FileNodeViewModel? _selectedNode;
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private string _currentTitle = "";
    [ObservableProperty] private string _note = "Select a file in the tree.";
    [ObservableProperty] private FileViewKind _kind = FileViewKind.Text;
    [ObservableProperty] private bool _hasFile;

    /// <summary>The GIT LOG tab's content — follows the same workspace as the tree.</summary>
    public GitLogViewModel GitLog { get; } = new();

    /// <summary>True while the panel shows GIT LOG instead of the files (the tab beside CODE).</summary>
    [ObservableProperty] private bool _showGitLog;

    public CodeViewModel()
    {
        // A workspace without readable history cannot keep the tab in front.
        GitLog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GitLogViewModel.IsAvailable) && !GitLog.IsAvailable) ShowGitLog = false;
        };
    }

    [RelayCommand] private void ShowFiles() => ShowGitLog = false;
    [RelayCommand] private void ShowHistory() { if (GitLog.IsAvailable) ShowGitLog = true; }

    /// <summary>A folder selected in the tree: its entries are listed where a file would show.</summary>
    [ObservableProperty] private string? _currentFolder;
    [ObservableProperty] private bool _isFolderView;
    [ObservableProperty] private string _folderSummary = "";
    public ObservableCollection<FolderRowViewModel> FolderRows { get; } = new();

    /// <summary>What the toolbar's path / open buttons act on: the open file or the listed folder.</summary>
    public string? SelectedPath => IsFolderView ? CurrentFolder : HasFile ? CurrentPath : null;
    public bool HasSelection => SelectedPath is not null;
    public string OpenButtonTip => IsFolderView ? "Open the folder in Explorer / Finder" : "Open with the default app";

    partial void OnCurrentFolderChanged(string? value) => RaiseSelection();
    partial void OnIsFolderViewChanged(bool value) => RaiseSelection();

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedPath));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(OpenButtonTip));
    }

    /// <summary>The text the editor shows (empty for images and binaries).</summary>
    public string Text { get; private set; } = "";

    /// <summary>A Markdown file's rendered body (mermaid fences as diagrams), for the preview page.</summary>
    public string Html { get; private set; } = "";

    /// <summary>Raised when <see cref="Text"/> changed — the editor's document is set from code.</summary>
    public event Action? TextChanged;

    /// <summary>
    /// Markdown opens rendered (with mermaid diagrams) and can be switched to its source; the
    /// choice holds for the next Markdown file too, as VS Code's preview toggle does not.
    /// </summary>
    [ObservableProperty] private bool _markdownPreview = true;

    /// <summary>What the preview page last reported ("2 diagrams" …) — for the CLI's status.</summary>
    [ObservableProperty] private string _previewInfo = "";

    public bool IsMarkdown => HasFile && Kind is FileViewKind.Text or FileViewKind.TooLarge && MarkdownHtml.IsMarkdown(CurrentPath);
    public bool IsPreview => IsMarkdown && MarkdownPreview;
    public bool IsImage => HasFile && Kind == FileViewKind.Image;
    public bool IsTextView => HasFile && Kind is FileViewKind.Text or FileViewKind.TooLarge && !IsPreview;
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public string PreviewToggleLabel => MarkdownPreview ? "‹› Plain" : "◧ Preview";

    partial void OnKindChanged(FileViewKind value) => RaiseViewKind();
    partial void OnHasFileChanged(bool value) { RaiseViewKind(); RaiseSelection(); }
    partial void OnCurrentPathChanged(string? value) { RaiseViewKind(); RaiseSelection(); }
    partial void OnMarkdownPreviewChanged(bool value)
    {
        RaiseViewKind();
        OnPropertyChanged(nameof(PreviewToggleLabel));
        TextChanged?.Invoke();
    }
    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    private void RaiseViewKind()
    {
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsTextView));
        OnPropertyChanged(nameof(IsMarkdown));
        OnPropertyChanged(nameof(IsPreview));
    }

    [RelayCommand] private void TogglePreview() => MarkdownPreview = !MarkdownPreview;

    /// <summary>
    /// A link clicked in the preview: a web address goes to the browser (returned for the view
    /// to launch), a workspace file opens here. Anything else is ignored.
    /// </summary>
    public async Task<Uri?> FollowLinkAsync(string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            return abs;
        if (CurrentPath is null || Root is null || href.Contains(':')) return null;
        var relative = Uri.UnescapeDataString(href.Split('#', '?')[0]);
        if (relative.Length == 0) return null;
        var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CurrentPath)!, relative.Replace('/', Path.DirectorySeparatorChar)));
        await OpenAsync(target);
        return null;
    }

    partial void OnSelectedNodeChanged(FileNodeViewModel? value)
    {
        if (value is { FullPath.Length: > 0 })
            _ = value.IsDirectory ? ShowFolderAsync(value.FullPath) : OpenAsync(value.FullPath);
    }

    /// <summary>
    /// List a folder of the workspace — name, size, modified, created — where a file would show.
    /// Read-only, read off the UI thread (a workspace can sit on a network drive), and the same
    /// filter as the tree, so a row never names something the tree hides.
    /// </summary>
    public async Task<string?> ShowFolderAsync(string path)
    {
        if (string.IsNullOrEmpty(Root)) return "no workspace is open";
        var full = Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
        if (!WorkspaceFiles.IsInside(Root, full)) return "the folder is outside the workspace";
        var generation = ++_openGeneration;
        var patterns = Patterns;
        var rows = await Task.Run(() => PathAvailability.DirectoryExistsFast(full)
            ? WorkspaceFiles.ListDetailed(full, patterns)
            : null);
        if (generation != _openGeneration) return null; // a newer click won
        if (rows is null) return "no such folder";

        ClearFile("");
        FolderRows.Clear();
        // Files only: the tree beside it already shows the sub-folders.
        var files = rows.Where(r => !r.IsDirectory).ToList();
        foreach (var r in files) FolderRows.Add(new FolderRowViewModel(r));
        FolderSummary = $"{files.Count} file(s) · {WorkspaceFiles.FormatSize(files.Sum(r => r.Size ?? 0))}";
        if (files.Count == 0) Note = "No files directly in this folder (sub-folders are in the tree).";
        var rel = PathCopyText.Relative(Root, full);
        CurrentTitle = rel is null ? $"{RootName}/" : rel + "/";
        CurrentFolder = full;
        IsFolderView = true;
        return null;
    }

    private int _rootGeneration;
    private Task _rootLoad = Task.CompletedTask;

    /// <summary>
    /// Show <paramref name="directory"/>'s tree (the active workspace). Same root → waits for the
    /// load already under way instead of starting another: measured, opening the view from the CLI
    /// started two loads of one root (the visibility change and the command), and both filled the
    /// tree — every top-level entry twice.
    /// </summary>
    public Task SetRootAsync(string? directory, string? displayName)
    {
        if (string.Equals(directory, Root, StringComparison.OrdinalIgnoreCase)) return _rootLoad;
        _rootLoad = LoadRootAsync(directory, displayName, ++_rootGeneration);
        return _rootLoad;
    }

    private async Task LoadRootAsync(string? directory, string? displayName, int generation)
    {
        Root = directory;
        RootName = displayName ?? (directory is null ? "No workspace" : Path.GetFileName(directory.TrimEnd('\\', '/')));
        Nodes.Clear();
        ClearFile("Select a file in the tree.");
        _ = GitLog.LoadAsync(directory);
        if (string.IsNullOrEmpty(directory)) return;

        // The WPF NOTE page's rule: never touch a workspace folder bare on the UI thread.
        var root = directory;
        var (ok, patterns, entries) = await Task.Run(() =>
        {
            if (!PathAvailability.DirectoryExistsFast(root)) return (false, (IReadOnlySet<string>)new HashSet<string>(), (IReadOnlyList<WorkspaceEntry>)Array.Empty<WorkspaceEntry>());
            var p = WorkspaceFiles.LoadIgnorePatterns(root);
            return (true, (IReadOnlySet<string>)p, WorkspaceFiles.List(root, p));
        });
        if (generation != _rootGeneration) return; // switched (or refreshed) again meanwhile
        if (!ok)
        {
            Note = $"'{root}' is not reachable (offline drive?).";
            return;
        }
        Patterns = patterns;
        foreach (var e in entries) Nodes.Add(new FileNodeViewModel(this, e));
        // Nothing selected yet: the workspace's own folder is listed rather than an empty pane.
        await ShowFolderAsync(root);
    }

    [RelayCommand]
    private async Task Refresh()
    {
        // A new generation: the same root is read again, and any load still running is dropped.
        await (_rootLoad = LoadRootAsync(Root, RootName, ++_rootGeneration));
    }

    /// <summary>Open a file: a path inside the workspace (absolute, or relative to it). Returns an error, or null.</summary>
    public async Task<string?> OpenAsync(string path)
    {
        if (string.IsNullOrEmpty(Root)) return "no workspace is open";
        var full = Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
        if (!WorkspaceFiles.IsInside(Root, full)) return "the file is outside the workspace";
        var generation = ++_openGeneration;
        try
        {
            var root = Root;
            var (content, html) = await Task.Run(() =>
            {
                if (!File.Exists(full)) return ((FileViewContent?)null, "");
                var read = WorkspaceFiles.ReadForView(full);
                // Rendered off the UI thread too: it inlines the document's workspace images.
                var rendered = read.Kind is FileViewKind.Text or FileViewKind.TooLarge && MarkdownHtml.IsMarkdown(full)
                    ? MarkdownHtml.Render(read.Text, Path.GetDirectoryName(full), root)
                    : "";
                return ((FileViewContent?)read, rendered);
            });
            if (generation != _openGeneration) return null; // a newer click won
            if (content is null) return "no such file";
            IsFolderView = false;
            CurrentFolder = null;
            CurrentPath = full;
            CurrentTitle = Path.GetRelativePath(Root, full).Replace('\\', '/');
            Kind = content.Kind;
            Text = content.Text;
            Html = html;
            PreviewInfo = "";
            Note = content.Note ?? "";
            HasFile = true;
            TextChanged?.Invoke();
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Code] open failed | {full} | {ex.GetType().Name}: {ex.Message}");
            ClearFile($"Could not read the file: {ex.Message}");
            return ex.Message;
        }
    }

    private void ClearFile(string note)
    {
        IsFolderView = false;
        CurrentFolder = null;
        CurrentPath = null;
        CurrentTitle = "";
        HasFile = false;
        Kind = FileViewKind.Text;
        Text = "";
        Html = "";
        Note = note;
        TextChanged?.Invoke();
    }

    /// <summary>Path of the open file relative to the workspace, for the copy button.</summary>
    public string? RelativePath => CurrentPath is null || Root is null ? null : Path.GetRelativePath(Root, CurrentPath);

    /// <summary>The CLI <c>code status</c> reply.</summary>
    public string StatusJson(bool visible, bool docked) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["ok"] = true,
            ["visible"] = visible,
            ["docked"] = docked,
            ["root"] = Root,
            ["file"] = !HasFile || CurrentTitle.Length == 0 ? null : CurrentTitle,
            ["kind"] = HasFile ? Kind.ToString().ToLowerInvariant() : null,
            ["folder"] = IsFolderView && CurrentFolder is not null ? CurrentTitle : null,
            ["view"] = IsFolderView ? "folder" : !HasFile ? null : IsPreview ? "preview" : IsImage ? "image" : IsTextView ? "text" : "note",
            ["preview"] = PreviewInfo.Length == 0 ? null : PreviewInfo,
            ["chars"] = Text.Length,
            ["topLevel"] = Nodes.Count,
            ["gitLog"] = new System.Text.Json.Nodes.JsonObject
            {
                ["available"] = GitLog.IsAvailable,
                ["commits"] = GitLog.Commits.Count,
                ["shown"] = ShowGitLog,
                ["note"] = GitLog.Note.Length == 0 ? null : GitLog.Note,
            },
        }.ToJsonString();

    /// <summary>Marshal a root change onto the UI thread (workspace switches arrive there already).</summary>
    public void FollowWorkspace(WorkspaceViewModel? ws)
        => Dispatcher.UIThread.Post(() => _ = SetRootAsync(ws?.DirectoryPath, ws?.DisplayName));
}

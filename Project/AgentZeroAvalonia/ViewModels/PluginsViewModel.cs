using System.Collections.ObjectModel;
using Agent.Common;
using Agent.Common.Browser;
using AgentZeroAvalonia.Services.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// The Plugins page — the WPF host's WebDev page converted: built-in samples and installed
/// plugins from the same two folders (<see cref="WebDevSampleCatalog"/>), install from the
/// official catalog, a GitHub folder URL or a .zip (<see cref="WebDevPluginInstaller"/>),
/// uninstall. The web views themselves belong to the view, which keeps one per plugin so
/// switching does not reload a running plugin (as in WPF).
/// </summary>
public sealed partial class PluginsViewModel : ObservableObject
{
    private const string DefaultEntryFolder = "voice-test";

    // The xterm renderer's folder sits under this host's Wasm/ too; it is not a sample.
    private static readonly string[] HiddenBuiltIns = ["xterm"];

    public ObservableCollection<WebDevSample> Samples { get; } = new();
    public ObservableCollection<OfficialPluginEntry> OfficialPlugins { get; } = new();

    [ObservableProperty] private WebDevSample? _selected;
    [ObservableProperty] private string _counts = "";
    [ObservableProperty] private string _status = "";

    [ObservableProperty] private OfficialPluginEntry? _selectedOfficial;
    [ObservableProperty] private string _officialStatus = "Loading the official list…";
    [ObservableProperty] private string _gitUrl = "";
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _installCaption = "";
    [ObservableProperty] private string _installDetail = "";
    [ObservableProperty] private double _installPercent;
    [ObservableProperty] private bool _installIndeterminate;

    private bool _officialLoaded;
    private CancellationTokenSource? _installCts;

    /// <summary>Set by the view: a .zip picker. Null result = cancelled.</summary>
    public Func<Task<string?>>? PickZip { get; set; }

    /// <summary>Set by the view: release the plugin's web view before its folder is deleted.</summary>
    public Action<string>? ReleaseView { get; set; }

    public bool CanUninstall => Selected is { IsBuiltIn: false } && !IsInstalling;

    /// <summary>The origin plugin pages run under — shown so a developer knows where localStorage lives.</summary>
    public string OriginNote => $"Plugins run at {PluginAssetServer.Shared.Origin} (https://zero.local, plugin.local and mp3.local are rewritten to it).";

    public PluginsViewModel()
    {
        try { Directory.CreateDirectory(WebDevSampleCatalog.PluginsRoot); }
        catch (Exception ex) { AppLogger.Log($"[Plugins] plugins folder not created: {ex.Message}"); }
        Refresh(preserveSelection: false, selectId: null);
    }

    /// <summary>The page became visible; the view mounts the selected plugin on the first one.</summary>
    public event Action? Shown;

    public void NotifyShown()
    {
        Shown?.Invoke();
        _ = EnsureOfficialListAsync();
    }

    partial void OnSelectedChanged(WebDevSample? value) => OnPropertyChanged(nameof(CanUninstall));
    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(CanUninstall));

    [RelayCommand]
    private void ReloadList() => Refresh(preserveSelection: true, selectId: null);

    private void Refresh(bool preserveSelection, string? selectId)
    {
        var keep = selectId ?? (preserveSelection ? Selected?.Id : null);
        var found = WebDevSampleCatalog.Discover(HiddenBuiltIns);
        Samples.Clear();
        foreach (var s in found) Samples.Add(s);

        var builtIns = found.Count(s => s.IsBuiltIn);
        Counts = $"built-in: {builtIns}  ·  plugins: {found.Count - builtIns}";

        Selected = (keep is null ? null : Samples.FirstOrDefault(s => s.Id == keep))
                   ?? Samples.FirstOrDefault(s => s.Id == DefaultEntryFolder)
                   ?? Samples.FirstOrDefault();
    }

    /// <summary>The official catalog is fetched from GitHub when the page is first shown, not at app start.</summary>
    public async Task EnsureOfficialListAsync()
    {
        if (_officialLoaded) return;
        _officialLoaded = true;
        var list = await OfficialPluginCatalog.DiscoverAsync();
        OfficialPlugins.Clear();
        foreach (var e in list) OfficialPlugins.Add(e);
        SelectedOfficial = OfficialPlugins.FirstOrDefault();
        OfficialStatus = list.Count == 0
            ? "Official list unavailable (offline?) — a GitHub folder URL or a .zip still works."
            : $"{list.Count} official plugin(s) at psmon/AgentZeroLite · Project/Plugins";
    }

    [RelayCommand]
    private Task InstallOfficial()
    {
        if (SelectedOfficial is not { } entry) return Task.CompletedTask;
        return RunInstallAsync($"Installing {entry.Name}", (progress, ct) =>
            WebDevPluginInstaller.InstallFromGitUrlAsync(entry.GitFolderUrl, allowOverwrite: true, progress, ct));
    }

    [RelayCommand]
    private Task InstallFromUrl()
    {
        var url = GitUrl.Trim();
        if (url.Length == 0)
        {
            Status = "Paste a GitHub folder URL first (https://github.com/owner/repo/tree/branch/path).";
            return Task.CompletedTask;
        }
        return RunInstallAsync("Installing from URL", (progress, ct) =>
            WebDevPluginInstaller.InstallFromGitUrlAsync(url, allowOverwrite: true, progress, ct));
    }

    [RelayCommand]
    private async Task InstallZip()
    {
        if (PickZip is null) return;
        var zip = await PickZip();
        if (string.IsNullOrWhiteSpace(zip)) return;
        // InstallFromZip is synchronous and walks every entry (agent-band: ~160 files).
        await RunInstallAsync("Installing from .zip", (progress, ct) =>
            Task.Run(() => WebDevPluginInstaller.InstallFromZip(zip, allowOverwrite: true, progress), ct));
    }

    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    private async Task RunInstallAsync(string caption, Func<IProgress<InstallProgress>, CancellationToken, Task<InstallResult>> install)
    {
        if (IsInstalling) return;
        IsInstalling = true;
        InstallCaption = caption;
        InstallDetail = "";
        InstallPercent = 0;
        InstallIndeterminate = true;
        _installCts = new CancellationTokenSource();
        // Progress<T> captures the UI context here, so reports land on the UI thread.
        var progress = new Progress<InstallProgress>(p =>
        {
            InstallCaption = p.Caption;
            InstallDetail = p.Detail;
            InstallIndeterminate = p.PercentComplete is null;
            InstallPercent = p.PercentComplete ?? 0;
        });
        try
        {
            var result = await install(progress, _installCts.Token);
            if (result.Ok)
            {
                Status = $"Installed: {result.Name} ({result.PluginId})";
                // An overwrite of a plugin that is open must not keep showing the old files.
                if (result.PluginId is { } id) ReleaseView?.Invoke(id);
                Refresh(preserveSelection: true, selectId: result.PluginId);
            }
            else
            {
                Status = "Install failed: " + result.Error;
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Install cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Install failed: " + ex.Message;
            AppLogger.Log($"[Plugins] install failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsInstalling = false;
            _installCts.Dispose();
            _installCts = null;
        }
    }

    [RelayCommand]
    private void Uninstall()
    {
        if (Selected is not { IsBuiltIn: false } s) return;
        // The web view holds files in the folder open; let it go before deleting.
        ReleaseView?.Invoke(s.Id);
        var result = WebDevPluginInstaller.Uninstall(s.Id);
        Status = result.Ok ? $"Uninstalled: {s.DisplayName}" : "Uninstall failed: " + result.Error;
        Refresh(preserveSelection: false, selectId: null);
    }
}

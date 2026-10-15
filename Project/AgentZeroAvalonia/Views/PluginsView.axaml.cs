using Agent.Common;
using Agent.Common.Browser;
using AgentZeroAvalonia.Services.Plugins;
using AgentZeroAvalonia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AgentZeroAvalonia.Views;

/// <summary>
/// Hosts the plugin web views. As in the WPF WebDev page, each plugin gets its own
/// <see cref="NativeWebView"/> the first time it is opened and keeps it — switching is a
/// visibility flip, so a playing band or a running voice note survives a look at another
/// plugin. All views share one <see cref="PluginBrowserCore"/> (one chat session, one MP3
/// scan job), one loopback server and one browser profile, so plugins see consistent state.
/// Nothing is created until the page is first shown.
/// </summary>
public partial class PluginsView : UserControl
{
    private sealed record Slot(NativeWebView View, PluginBridge Bridge)
    {
        /// <summary>Completes on the first finished navigation — what <c>plugin open</c> waits for.</summary>
        public TaskCompletionSource<bool> Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The page's view, for the <c>-cli plugin</c> verbs (one main window, one page).</summary>
    internal static PluginsView? Current { get; private set; }

    private readonly Dictionary<string, Slot> _slots = new();
    private PluginsViewModel? _vm;
    private PluginBrowserCore? _core;
    private Slot? _active;
    private bool _shown;
    private Window? _window;

    public PluginsView()
    {
        InitializeComponent();
        ReloadButton.Click += (_, _) => ReloadActive();
        DataContextChanged += (_, _) => Bind(DataContext as PluginsViewModel);
        Current = this;
    }

    private void Bind(PluginsViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Shown -= OnShown;
        }
        _vm = vm;
        if (vm is null) return;
        vm.PickZip = PickZipAsync;
        vm.ReleaseView = Release;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.Shown += OnShown;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_window is not null) return;
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.Closed += (_, _) => TearDown();
    }

    private void OnShown()
    {
        _shown = true;
        ShowSelected();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginsViewModel.Selected) && _shown) ShowSelected();
    }

    private void ShowSelected()
    {
        if (_vm?.Selected is not { } sample) return;
        if (!_slots.TryGetValue(sample.Id, out var slot))
        {
            slot = Mount(sample);
            if (slot is null) return;
        }
        foreach (var s in _slots.Values) s.View.IsVisible = ReferenceEquals(s, slot);
        _active = slot;
    }

    private Slot? Mount(WebDevSample sample)
    {
        try
        {
            if (_core is null)
            {
                _core = new PluginBrowserCore();
                PluginAssetServer.Shared.CoverLookup = _core.TryGetMp3Cover;
            }

            var view = new NativeWebView();
            view.EnvironmentRequested += OnEnvironmentRequested;
            view.NavigationStarted += (_, _) => { if (ReferenceEquals(_active?.View, view)) LoadingLabel.IsVisible = true; };
            view.NavigationCompleted += (_, e) =>
            {
                if (ReferenceEquals(_active?.View, view)) LoadingLabel.IsVisible = false;
                if (!e.IsSuccess) AppLogger.Log($"[Plugins] navigation FAILED | {sample.Id} | {e.Request}");
            };
            var bridge = new PluginBridge(view, _core, PickFolderAsync);
            var slot = new Slot(view, bridge);
            view.NavigationCompleted += (_, e) => slot.Loaded.TrySetResult(e.IsSuccess);
            _slots[sample.Id] = slot;
            ViewHost.Children.Add(view);

            var url = PluginAssetServer.Shared.UrlFor(sample.Url);
            view.Source = new Uri(url);
            AppLogger.Log($"[Plugins] mount + navigate → {sample.Url}");
            return slot;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Plugins] mount failed for '{sample.Id}': {ex.GetType().Name}: {ex.Message}");
            if (_vm is not null) _vm.Status = $"Could not open {sample.DisplayName}: {ex.Message}";
            return null;
        }
    }

    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is WindowsWebView2EnvironmentRequestedEventArgs win)
        {
            // One profile for every plugin view, apart from the terminals' — localStorage and
            // IndexedDB persist here between runs (with the remembered server port).
            win.UserDataFolder = Path.Combine(Path.GetTempPath(), "AgentZeroLite_WebDev_Avalonia");
        }
        // Plugin authors need the inspector (the WPF page has a DevTools button): with this
        // on, right-click → Inspect / F12 opens it in the plugin's own view.
        e.EnableDevTools = true;
    }

    private void ReloadActive()
    {
        try { _active?.View.Refresh(); }
        catch (Exception ex) { AppLogger.Log($"[Plugins] reload failed: {ex.Message}"); }
    }

    // ── automation (-cli plugin …) — UI thread ──

    /// <summary>Select a plugin, mount it if needed, and wait for its first page load.</summary>
    internal async Task<(NativeWebView? View, string? Error)> OpenAsync(string id, TimeSpan timeout)
    {
        if (_vm is null) return (null, "the Plugins page is not ready");
        var sample = _vm.Samples.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (sample is null) return (null, $"no plugin '{id}' (see plugin list)");
        _vm.Selected = sample;
        if (!_shown) OnShown();
        if (!_slots.TryGetValue(sample.Id, out var slot)) return (null, $"could not open '{id}' — see the app log");
        var finished = await Task.WhenAny(slot.Loaded.Task, Task.Delay(timeout));
        if (finished != slot.Loaded.Task) return (slot.View, "timed out waiting for the page to load");
        return slot.Loaded.Task.Result ? (slot.View, null) : (slot.View, "navigation failed");
    }

    internal Task<byte[]?> CaptureAsync(string id)
        => _slots.TryGetValue(id, out var slot) ? slot.Bridge.CaptureFramePngAsync() : Task.FromResult<byte[]?>(null);

    internal IEnumerable<string> OpenIds => _slots.Keys;

    /// <summary>Drop a plugin's view (before uninstall / after reinstall) so its files are let go.</summary>
    private void Release(string sampleId)
    {
        if (!_slots.Remove(sampleId, out var slot)) return;
        try { slot.Bridge.Dispose(); } catch { }
        try { ViewHost.Children.Remove(slot.View); } catch { }
        try { (slot.View as IDisposable)?.Dispose(); } catch { }
        if (ReferenceEquals(_active, slot)) _active = null;
    }

    private void TearDown()
    {
        foreach (var id in _slots.Keys.ToArray()) Release(id);
        try { _core?.Dispose(); } catch { }
        _core = null;
    }

    private async Task<string?> PickZipAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install WebDev Plugin — pick a .zip",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Plugin package") { Patterns = ["*.zip"] }, FilePickerFileTypes.All],
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickFolderAsync(string title, string? initialFolder)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(initialFolder))
            options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(initialFolder);
        var picked = await top.StorageProvider.OpenFolderPickerAsync(options);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}

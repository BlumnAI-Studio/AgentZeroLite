using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AgentZeroWpf.Module;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Harness View overlay (M0022-6). Lazily initialises a WebView2 control that
/// loads the GitHub Pages-hosted harness-view dashboard. Same z-order pattern
/// as <see cref="WebDevPagePanel"/> and <see cref="ScrapPagePanel"/>: rendered
/// as a full overlay so only the ActivityBar stays visible.
/// </summary>
public partial class HarnessViewPanel : UserControl
{
    private const string DefaultUrl =
        "https://psmon.github.io/AgentZeroLite/Home/harness-view/#dashboard";

    private bool _initialized;
    private bool _initializing;

    public HarnessViewPanel()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
    }

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || _initialized || _initializing) return;
        _initializing = true;
        try
        {
            await webview.EnsureCoreWebView2Async();
            webview.CoreWebView2.Navigate(DefaultUrl);
            _initialized = true;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[HarnessView] WebView2 init failed: {ex.Message}");
        }
        finally
        {
            _initializing = false;
        }
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (webview.CoreWebView2 is null) return;
        webview.CoreWebView2.Reload();
    }

    private void OnOpenExternalClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(DefaultUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[HarnessView] open external failed: {ex.Message}");
        }
    }
}

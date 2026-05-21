using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Detached-window host for <see cref="ScrapPagePanel"/> (M0022-5). Mirrors the
/// FloatingWebDevWindow pattern but is simpler since Scrap is pure WPF — no
/// WebView2 airspace to negotiate. The same UserControl instance is reparented
/// in and out of MainWindow, so all in-flight state (selected HWND, captured
/// text, picker) survives the round trip.
/// </summary>
public partial class FloatingScrapWindow : Window
{
    private readonly Action _dockBack;
    private bool _dockBackInvoked;

    public FloatingScrapWindow(ScrapPagePanel panel, Action dockBack)
    {
        InitializeComponent();
        _dockBack = dockBack ?? throw new ArgumentNullException(nameof(dockBack));
        contentHost.Children.Add(panel);
        Closing += OnClosing;
    }

    /// <summary>Detaches the panel from this window's content host so the caller can re-host it.</summary>
    public ScrapPagePanel ReleaseContent()
    {
        var panel = contentHost.Children.Count > 0
            ? contentHost.Children[0] as ScrapPagePanel
            : null;
        contentHost.Children.Clear();
        return panel ?? throw new InvalidOperationException("FloatingScrapWindow has no content to release.");
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void OnDockBackClick(object sender, RoutedEventArgs e)
    {
        if (_dockBackInvoked) return;
        _dockBackInvoked = true;
        _dockBack();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_dockBackInvoked) return;
        // Closing the window via the OS X / Alt-F4 also re-docks the panel
        // to avoid losing the only instance.
        _dockBackInvoked = true;
        _dockBack();
    }
}

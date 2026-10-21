using Agent.Common.Browser;
using Avalonia;
using Avalonia.Controls;

namespace AgentZeroAvalonia.Views;

/// <summary>
/// One plugin detached from the Plugins page into a window of its own — for running a
/// plugin as a side window next to the terminals. It holds the plugin's existing
/// <see cref="NativeWebView"/> (moved, not recreated), so a playing band, a running voice
/// note or a half-filled form carries on; closing the window docks it back.
///
/// Pin (always on top), opacity and the window's rect are remembered per plugin in the file
/// the WPF host's floating window uses (<see cref="PluginWindowPrefs"/>), in device-independent
/// pixels like WPF's Left/Top, so the two hosts restore the same spot.
/// </summary>
public partial class PluginWindow : Window
{
    private readonly string _pluginId;
    private PluginWindowPref _pref;
    private bool _restoring = true;

    /// <summary>Set while the page itself closes the window (dock, uninstall, shutdown), so it does not re-dock.</summary>
    public bool ClosingProgrammatically { get; set; }

    /// <summary>The user wants the plugin back on the page (strip button or the close box).</summary>
    public event Action? DockRequested;

    /// <summary>⟳ in the strip.</summary>
    public event Action? ReloadRequested;

    // The XAML loader needs a parameterless constructor.
    public PluginWindow() : this("", "Plugin") { }

    public PluginWindow(string pluginId, string displayName)
    {
        InitializeComponent();
        _pluginId = pluginId;
        _pref = pluginId.Length > 0 ? PluginWindowPrefs.Get(pluginId) : new PluginWindowPref();

        TitleText.Text = displayName;
        Title = displayName + " — " + BuildFlavor.AppTitle;

        PinButton.IsChecked = _pref.Topmost;
        Topmost = _pref.Topmost;
        OpacitySlider.Value = _pref.SafeOpacity;
        Opacity = _pref.SafeOpacity;
        OpacityText.Text = Percent(Opacity);
        if (_pref.W is double w && _pref.H is double h && w >= MinWidth && h >= MinHeight)
        {
            Width = w;
            Height = h;
        }

        PinButton.IsCheckedChanged += (_, _) =>
        {
            Topmost = PinButton.IsChecked == true;
            _pref.Topmost = Topmost;
            Persist();
        };
        OpacitySlider.ValueChanged += (_, e) =>
        {
            Opacity = Math.Clamp(e.NewValue, 0.5, 1.0);
            OpacityText.Text = Percent(Opacity);
            _pref.Opacity = Opacity;
            Persist();
        };
        ReloadButton.Click += (_, _) => ReloadRequested?.Invoke();
        DockBackButton.Click += (_, _) => DockRequested?.Invoke();

        PositionChanged += (_, _) => RememberRect();
        SizeChanged += (_, _) => RememberRect();
        Opened += (_, _) => _restoring = false;
        Closing += (_, e) =>
        {
            RememberRect();
            if (ClosingProgrammatically || e.CloseReason is WindowCloseReason.OwnerWindowClosing
                                                        or WindowCloseReason.ApplicationShutdown
                                                        or WindowCloseReason.OSShutdown)
                return;
            e.Cancel = true;
            DockRequested?.Invoke();
        };
    }

    /// <summary>
    /// Put the window where this plugin was last parked, if that spot is still on a screen;
    /// else near <paramref name="anchor"/>. Call before <c>Show</c>.
    /// </summary>
    public void PlaceNear(Window? anchor)
    {
        var scale = anchor?.DesktopScaling ?? 1.0;
        if (_pref.X is double x && _pref.Y is double y && IsOnAScreen(anchor, x, y, scale))
        {
            Position = new PixelPoint((int)Math.Round(x * scale), (int)Math.Round(y * scale));
            return;
        }
        if (anchor is null) return;

        // First time: inside the main window's right edge — the "side window" this is for —
        // and wholly on that screen (a 720-dip window opened 80 px down ran under the taskbar).
        var right = anchor.Position.X + (int)Math.Round(anchor.Bounds.Width * scale) - (int)Math.Round(Width * scale) - 24;
        var left = Math.Max(anchor.Position.X, right);
        var top = anchor.Position.Y + (int)Math.Round(80 * scale);
        if (anchor.Screens.ScreenFromWindow(anchor)?.WorkingArea is { } area)
        {
            // Height is the client area; the OS title bar and frame come on top (measured ~30 dip
            // at 200 %), and the frame size is not known before the window is shown.
            const double FrameDip = 40;
            var maxHeight = area.Height / scale - FrameDip - 16;
            if (Height > maxHeight) Height = Math.Max(MinHeight, maxHeight);
            var outer = (int)Math.Round((Height + FrameDip) * scale);
            if (top + outer > area.Bottom) top = Math.Max(area.Y, area.Bottom - outer);
        }
        Position = new PixelPoint(left, top);
    }

    private static bool IsOnAScreen(Window? anchor, double x, double y, double scale)
    {
        // The strip (top 40 dip) has to land on some screen, or the window cannot be moved back.
        var screens = anchor?.Screens.All;
        if (screens is null || screens.Count == 0) return true;
        var header = new PixelRect((int)(x * scale), (int)(y * scale), (int)(200 * scale), (int)(40 * scale));
        return screens.Any(s => s.WorkingArea.Intersects(header));
    }

    public void Hold(NativeWebView view) => Host.Child = view;

    public NativeWebView? Release()
    {
        var view = Host.Child as NativeWebView;
        Host.Child = null;
        return view;
    }

    private void RememberRect()
    {
        if (_restoring || _pluginId.Length == 0 || WindowState != WindowState.Normal) return;
        var scale = DesktopScaling;
        _pref.X = Position.X / scale;
        _pref.Y = Position.Y / scale;
        _pref.W = ClientSize.Width;
        _pref.H = ClientSize.Height;
        Persist();
    }

    private void Persist()
    {
        if (_restoring || _pluginId.Length == 0) return;
        PluginWindowPrefs.Save(_pluginId, _pref);
    }

    private static string Percent(double v) => $"{(int)Math.Round(v * 100)}%";
}

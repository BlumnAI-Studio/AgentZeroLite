using Avalonia.Controls;
using AgentZeroAvalonia.Terminal;

namespace AgentZeroAvalonia.Views;

/// <summary>
/// One terminal tab detached from the main window (the WPF host's floating document).
/// It holds the tab's existing <see cref="XtermWebViewTerminalControl"/> — the same
/// instance, moved here — so the PTY, the session the actors talk to and the renderer
/// all carry on; the tab stays in its pane's layout, which is where docking returns it.
///
/// Closing the window docks the terminal back rather than killing it (the floating bot's
/// contract); closing the TAB is still the tab's ✕ in the main window.
/// </summary>
public partial class TerminalWindow : Window
{
    /// <summary>Set while the shell itself closes the window (dock, tab closed, shutdown), so it does not re-dock.</summary>
    public bool ClosingProgrammatically { get; set; }

    /// <summary>The user asked for the terminal back in the main window (strip button or the window's close box).</summary>
    public event Action? DockRequested;

    public TerminalWindow()
    {
        InitializeComponent();
        DockBackButton.Click += (_, _) => DockRequested?.Invoke();
        Closing += (_, e) =>
        {
            // The owner closing (app exit) takes this window with it — nothing to dock to.
            if (ClosingProgrammatically || e.CloseReason is WindowCloseReason.OwnerWindowClosing
                                                        or WindowCloseReason.ApplicationShutdown
                                                        or WindowCloseReason.OSShutdown)
                return;
            e.Cancel = true;
            DockRequested?.Invoke();
        };
    }

    /// <summary>Shown in the strip and the OS title bar.</summary>
    public void SetCaption(string caption)
    {
        TitleText.Text = caption;
        Title = caption + " — " + BuildFlavor.AppTitle;
    }

    /// <summary>Put the moved renderer in the window.</summary>
    public void Hold(XtermWebViewTerminalControl control) => Host.Child = control;

    /// <summary>Take the renderer out again (before docking it back), leaving the window empty.</summary>
    public XtermWebViewTerminalControl? Release()
    {
        var control = Host.Child as XtermWebViewTerminalControl;
        Host.Child = null;
        return control;
    }
}

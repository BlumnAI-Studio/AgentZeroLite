using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AgentZeroAvalonia.ViewModels;

namespace AgentZeroAvalonia.Views;

/// <summary>
/// The command palette's window (M0042): centred near the top of the main window, keys go to
/// the search box (↑↓ move, Enter runs, Esc closes), and it closes when it loses focus.
/// The item runs after the window is gone, so a command that opens a window or moves focus is
/// not fighting the palette for it.
/// </summary>
public partial class PaletteWindow : Window
{
    private bool _closing;

    public PaletteWindow() => InitializeComponent();

    public PaletteWindow(PaletteViewModel vm) : this()
    {
        DataContext = vm;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        ResultList.DoubleTapped += (_, _) => Run();
        Deactivated += (_, _) => CloseOnce();
        Opened += (_, _) => SearchBox.Focus();
    }

    private PaletteViewModel Vm => (PaletteViewModel)DataContext!;

    /// <summary>Show over <paramref name="owner"/>: horizontally centred, a little below its title bar.</summary>
    public void ShowOver(Window owner)
    {
        var scale = owner.RenderScaling;
        var x = owner.Position.X + (int)((owner.Bounds.Width - Width) / 2 * scale);
        var y = owner.Position.Y + (int)(80 * scale);
        Position = new PixelPoint(Math.Max(owner.Position.X, x), y);
        Show(owner);
        Activate();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: CloseOnce(); e.Handled = true; break;
            case Key.Down: Vm.Move(+1); ResultList.ScrollIntoView(Vm.SelectedIndex); e.Handled = true; break;
            case Key.Up: Vm.Move(-1); ResultList.ScrollIntoView(Vm.SelectedIndex); e.Handled = true; break;
            case Key.Enter: Run(); e.Handled = true; break;
        }
    }

    private void Run()
    {
        if (Vm.SelectedIndex < 0) return;
        CloseOnce();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Vm.InvokeSelected());
    }

    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}

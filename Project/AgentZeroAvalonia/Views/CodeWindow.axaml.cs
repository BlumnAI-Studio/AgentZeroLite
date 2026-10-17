using Avalonia.Controls;

namespace AgentZeroAvalonia.Views;

/// <summary>
/// The code view in its own window. Like <see cref="AgentBotWindow"/> it holds no state — the
/// tree, the open file and its scroll come from the shared <see cref="ViewModels.CodeViewModel"/>
/// — and closing it docks the view back into the bottom panel rather than closing it.
/// </summary>
public partial class CodeWindow : Window
{
    /// <summary>Set while the shell itself closes the window, so it does not re-dock.</summary>
    public bool ClosingProgrammatically { get; set; }

    /// <summary>The user closed the window: the view goes back to the bottom panel.</summary>
    public event Action? EmbedRequested;

    public CodeWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (ClosingProgrammatically || e.CloseReason is WindowCloseReason.OwnerWindowClosing
                                                        or WindowCloseReason.ApplicationShutdown
                                                        or WindowCloseReason.OSShutdown)
                return;
            e.Cancel = true;
            EmbedRequested?.Invoke();
        };
    }
}

using System.Windows;
using AgentZeroWpf.Services.Browser;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Modal install progress UI for both Git URL and local-ZIP plugin install
/// paths. The caller hands us an async install runner that takes the
/// progress + cancellation it'll plumb into <see cref="WebDevPluginInstaller"/>;
/// the dialog drives that runner on Loaded, surfaces every
/// <see cref="InstallProgress"/> snapshot to the UI, and exposes the final
/// <see cref="InstallResult"/> through <see cref="Result"/> once it closes.
///
/// Cancellation is wired both to the X button and the explicit Cancel
/// button — the underlying <see cref="CancellationTokenSource"/> is
/// disposed once on close, in addition to the install task's own cleanup.
///
/// M0025 follow-up — agent-band ships ~73 MB of sprite assets, the previous
/// silent loading overlay made the install feel hung. Reusable across all
/// future plugin installs (token-monitor / voice-note re-installs included).
/// </summary>
public partial class PluginInstallProgressDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<IProgress<InstallProgress>, CancellationToken, Task<InstallResult>> _runner;

    /// <summary>Final install outcome — populated once the runner returns.</summary>
    public InstallResult? Result { get; private set; }

    public PluginInstallProgressDialog(
        Func<IProgress<InstallProgress>, CancellationToken, Task<InstallResult>> runner)
    {
        _runner = runner;
        InitializeComponent();
        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        // Progress callbacks dispatch back onto this thread via Progress<T>'s
        // captured SynchronizationContext, so OnProgress always runs on the
        // dialog's UI thread — no extra Dispatcher.BeginInvoke needed.
        var progress = new Progress<InstallProgress>(OnProgress);
        try
        {
            Result = await _runner(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Result = new InstallResult(false, null, null, "cancelled");
        }
        catch (Exception ex)
        {
            Result = new InstallResult(false, null, null, ex.Message);
        }
        DialogResult = Result?.Ok ?? false;
    }

    private void OnProgress(InstallProgress p)
    {
        tbCaption.Text = p.Caption;
        tbDetail.Text  = p.Detail;
        if (p.PercentComplete is int pct)
        {
            pb.IsIndeterminate = false;
            pb.Value = Math.Clamp(pct, 0, 100);
            tbPercent.Text = $"{pct}%";
        }
        else
        {
            pb.IsIndeterminate = true;
            tbPercent.Text = "";
        }

        if (p.FilesTotal > 0)
        {
            tbCounters.Text = p.BytesTotal > 0
                ? $"{p.FilesDone}/{p.FilesTotal} files  ·  {FormatMb(p.BytesDone)}/{FormatMb(p.BytesTotal)}"
                : $"{p.FilesDone}/{p.FilesTotal} files";
        }
        else
        {
            tbCounters.Text = "";
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        btnCancel.IsEnabled = false;
        tbCaption.Text = "Cancelling…";
        try { _cts.Cancel(); } catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        base.OnClosed(e);
    }

    private static string FormatMb(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var mb = bytes / (1024.0 * 1024.0);
        if (mb < 1) return $"{bytes / 1024.0:F1} KB";
        return $"{mb:F1} MB";
    }
}

using Agent.Common;
using Agent.Common.Platform;
using Agent.Common.Services;

namespace AgentZeroAvalonia.Services;

/// <summary>
/// Which PTY backend this OS gets (M0035): Windows → <see cref="ConPtyHost"/> (the ConPTY
/// copy from the WPF app), macOS / Linux → <see cref="PortaPtyHost"/>. The choice is made
/// once, here, so the terminal control and the self-test spawn through the same door.
/// </summary>
internal static class PtyHostFactory
{
    public static string BackendName =>
        OperatingSystem.IsWindows() ? "ConPTY (kernel32, WPF host copy)"
        : OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() ? "Porta.Pty (forkpty)"
        : "none";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>Spawn <paramref name="spec"/> at <paramref name="cols"/>×<paramref name="rows"/>. Throws on failure — the caller logs and shows the reason.</summary>
    public static IPtyHost Start(TerminalLaunchSpec spec, int cols, int rows)
    {
        // Unusable folder → the home folder, named explicitly. Passing nothing (or the process's
        // current directory) meant "wherever the GUI was launched from" — for a GUI started from
        // a repository checkout, that is the main tree, and a worktree's terminal opened there.
        var cwd = UsableCwd(spec.Cwd) ?? HomeFolder();
        if (OperatingSystem.IsWindows())
        {
            var host = new ConPtyHost();
            host.Start(spec.CommandLine, cwd, spec.Env, cols, rows);
            return host;
        }
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            var host = new PortaPtyHost();
            host.Start(spec with { Cwd = cwd }, cols, rows);
            return host;
        }
        throw new PlatformNotSupportedException($"No PTY backend for {Environment.OSVersion}");
    }

    private static string HomeFolder() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Budget for the working-folder check, which runs on the UI thread.</summary>
    internal const int CwdProbeMs = 2000;

    /// <summary>
    /// The folder the terminal starts in, or null for the home folder. Bounded: a workspace on a
    /// disconnected network drive (<c>Y:</c> → <c>\\host\share</c>) made a bare Directory.Exists
    /// block the UI thread for the SMB timeout — measured 21–30 s per workspace switch.
    /// </summary>
    internal static string? UsableCwd(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return null;
        if (PathAvailability.DirectoryExistsFast(cwd, CwdProbeMs)) return cwd;
        AppLogger.Log($"[Terminals] working folder '{cwd}' is missing or did not answer within {CwdProbeMs} ms (offline network drive?) — starting in the home folder");
        return null;
    }

    /// <summary>
    /// The spec a self-test uses: a shell that echoes a marker, lingers a second, and exits.
    /// The linger matters: a child that exits within milliseconds can be gone before the
    /// reader thread attaches, and on macOS the pty master discards what was never read
    /// (seen on the CI runner with a bare echo). Real terminals are long-lived shells.
    /// </summary>
    public static TerminalLaunchSpec EchoSpec(string marker)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            return new TerminalLaunchSpec(cmd, new[] { "/c", "echo " + marker + " & ping -n 2 127.0.0.1 >nul" }, Environment.CurrentDirectory,
                TerminalEnvironment.Build(), "selftest");
        }
        return new TerminalLaunchSpec("/bin/sh", new[] { "-c", "echo " + marker + "; sleep 1" }, Environment.CurrentDirectory,
            TerminalEnvironment.BuildPosix(), "selftest");
    }
}

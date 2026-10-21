using System.Diagnostics;
using System.Runtime.Versioning;

namespace Agent.Common.Platform;

/// <summary>
/// Show a folder in the OS file manager — Explorer on Windows, Finder on macOS, whatever
/// <c>xdg-open</c> picks on Linux. <see cref="FolderOpener.Create"/> hands out the one for
/// this OS, the same way <see cref="SingleInstanceGuard"/> and <c>OsControl</c> do, so the
/// GUI's "Open folder" menu never names a platform.
///
/// <para>The folder is passed as one argument to a fixed program, never through a shell and
/// never through ShellExecute on the path itself: a path that happens to name an executable
/// or a script must open in the file manager, not run.</para>
/// </summary>
public interface IFolderOpener
{
    /// <summary>Short name of the platform behind this instance ("windows", "macos", "xdg").</summary>
    string Platform { get; }

    /// <summary>Open <paramref name="path"/>; null on success, else why it did not open.</summary>
    string? Open(string path);
}

public static class FolderOpener
{
    public static IFolderOpener Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsFolderOpener();
        if (OperatingSystem.IsMacOS()) return new MacFolderOpener();
        return new XdgFolderOpener();
    }

    /// <summary>The checks every implementation shares, then the launch; null on success.</summary>
    internal static string? Run(string program, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "no folder";
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) { return $"bad path: {ex.Message}"; }
        // A bounded probe — a workspace on a disconnected network drive must not hang the UI.
        return PathAvailability.DirectoryExistsFast(full) ? Launch(program, full) : $"folder not found: {full}";
    }

    private static string? Launch(string program, string path)
    {
        try
        {
            var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(path);
            using var _ = Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            return $"{program}: {ex.Message}";
        }
    }
}

/// <summary>Windows: <c>explorer.exe "&lt;folder&gt;"</c>, a new Explorer window on that folder.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFolderOpener : IFolderOpener
{
    public string Platform => "windows";

    public string? Open(string path)
        => FolderOpener.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), path);
}

/// <summary>macOS: <c>/usr/bin/open &lt;folder&gt;</c>, which LaunchServices hands to Finder.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacFolderOpener : IFolderOpener
{
    public string Platform => "macos";

    public string? Open(string path)
        => FolderOpener.Run("/usr/bin/open", path);
}

/// <summary>Linux and the rest: <c>xdg-open &lt;folder&gt;</c>.</summary>
public sealed class XdgFolderOpener : IFolderOpener
{
    public string Platform => "xdg";

    public string? Open(string path)
        => FolderOpener.Run("xdg-open", path);
}

namespace Agent.Common.Platform;

/// <summary>
/// Where this app keeps its data, in one place (M0033). Both GUIs and every store read the
/// folder through this class so they share one database and one settings set on Windows.
///
/// <para>Phase 1 keeps <c>LocalApplicationData</c> on every OS — on macOS that is
/// <c>~/.local/share/AgentZeroLite</c>, which is unusual for a Mac app but harmless.
/// Moving to <c>~/Library/Application Support</c> is a one-line change here.</para>
///
/// <para><b>Profiles.</b> A Debug build runs beside the installed release (a worktree under
/// development next to the copy in use), so it must not write the release's database and
/// settings. <see cref="UseProfile"/> moves <see cref="DataRoot"/> — DB, settings, aliases,
/// logs — to a sibling folder (<c>AgentZeroLite-Debug</c>), while <see cref="SharedRoot"/>
/// keeps what is large and read-mostly — downloaded models, snapshots written by hooks outside
/// the app — in the one place both read. The profile is also put in the environment
/// (<see cref="ProfileVariable"/>) so a child this process starts (the wearable host) opens the
/// same stores; terminal tabs drop it (<c>TerminalEnvironment</c>).</para>
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "AgentZeroLite";

    /// <summary>Environment variable that carries the profile name to child processes.</summary>
    public const string ProfileVariable = "AGENTZEROLITE_PROFILE";

    // Flow-local: a test's override must not leak into a store whose static path happens to be
    // initialised on another test's thread while it is in force.
    private static readonly AsyncLocal<string?> _override = new();
    private static string? _profile = Normalize(Environment.GetEnvironmentVariable(ProfileVariable));

    /// <summary>The profile in use, or null for the default (the release's) data.</summary>
    public static string? Profile => _profile;

    /// <summary>
    /// Switch this process — and the children it starts — to a named profile. Call it first
    /// thing in <c>Main</c>: stores read their paths into static fields on first use.
    /// </summary>
    public static void UseProfile(string? name)
    {
        _profile = Normalize(name);
        Environment.SetEnvironmentVariable(ProfileVariable, _profile);
    }

    /// <summary>The folder every profile shares (models, external snapshots); created on first use.</summary>
    public static string SharedRoot
    {
        get
        {
            var root = _override.Value ?? Path.Combine(LocalAppData, AppFolderName);
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>The data root of the active profile (DB, settings, logs); created on first use.</summary>
    public static string DataRoot
    {
        get
        {
            var root = _override.Value
                ?? Path.Combine(LocalAppData, _profile is null ? AppFolderName : $"{AppFolderName}-{_profile}");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>A file directly under the data root.</summary>
    public static string File(string name) => Path.Combine(DataRoot, name);

    /// <summary>A sub-folder under the data root, created on first use.</summary>
    public static string Dir(string name)
    {
        var dir = Path.Combine(DataRoot, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string LogsDir => Dir("logs");

    /// <summary>Downloaded models live in the shared root — a profile must not download them again.</summary>
    public static string ModelsDir
    {
        get
        {
            var dir = Path.Combine(SharedRoot, "models");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Tests point the root at a scratch folder; disposing restores it.</summary>
    public static IDisposable OverrideRoot(string root)
    {
        var previous = _override.Value;
        _override.Value = root;
        return new Restore(() => _override.Value = previous);
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%</c> / <c>$XDG_DATA_HOME</c> / <c>~/.local/share</c>, created when missing.
    /// The default lookup returns <c>""</c> on Unix when that folder does not exist yet, so a
    /// fresh account (a new Ubuntu user, a container) got a relative <c>AgentZeroLite</c> and the
    /// database landed in whatever folder the app was started from (measured: <c>~/AgentZeroLite</c>).
    /// </summary>
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolderOption.Create);

    private static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = new string(name.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return clean.Length == 0 ? null : clean;
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

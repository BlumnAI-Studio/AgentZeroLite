using Agent.Common;
using Agent.Common.Platform;
using Microsoft.Data.Sqlite;

namespace AgentZeroAvalonia;

/// <summary>
/// What a Debug build does differently so it can run beside an installed release — a worktree
/// under development next to the copy the user is working in. Nothing the two could fight over
/// is shared: data lives in its own profile (<c>%LOCALAPPDATA%\AgentZeroLite-Debug</c>, seeded
/// once from the release's), the single-instance mutex and the CLI pipe get their own names, and
/// every window title says <c>-DEBUG</c> so the two can be told apart on the taskbar. Downloaded
/// models stay shared (<see cref="AppPaths.SharedRoot"/>).
/// </summary>
internal static class BuildFlavor
{
#if DEBUG
    public const bool IsDebug = true;
#else
    public const bool IsDebug = false;
#endif

    /// <summary>The data profile a Debug build runs in (<see cref="AppPaths.UseProfile"/>).</summary>
    public const string DebugProfile = "Debug";

    /// <summary>The product name as window titles show it.</summary>
    public const string AppTitle = IsDebug ? "AgentZero Lite -DEBUG" : "AgentZero Lite";

    /// <summary>The CLI pipe: a Debug exe's <c>-cli</c> talks to the Debug GUI, a release's to the release.</summary>
    public const string PipeName = IsDebug ? CliIpcBridge.DefaultPipeName + ".debug" : CliIpcBridge.DefaultPipeName;

    /// <summary>The single-instance id: one Debug GUI beside one release GUI, never two of either.</summary>
    public const string InstanceId = IsDebug ? SingleInstanceGuard.DefaultId + "-" + DebugProfile : SingleInstanceGuard.DefaultId;

    /// <summary>
    /// Switch a Debug build to its own profile. Runs first in <c>Main</c>, before any store has
    /// read a path. The first time the profile is created it is seeded from the release's data
    /// so a debug session starts with the same settings and workspaces; after that the two
    /// never touch each other (delete the folder to seed again).
    /// </summary>
    public static void ApplyProfile()
    {
        if (!IsDebug) return;
        var shared = AppPaths.SharedRoot;
        var target = Path.Combine(Path.GetDirectoryName(shared)!, $"{AppPaths.AppFolderName}-{DebugProfile}");
        var fresh = !Directory.Exists(target);
        AppPaths.UseProfile(DebugProfile);
        if (fresh) Seed(shared, AppPaths.DataRoot);
    }

    private static void Seed(string from, string to)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(from, "*.json"))
            {
                // The plugin port is per process — two servers cannot hold one port.
                if (Path.GetFileName(file).Equals("plugin-host.json", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
            }
            // The AES-GCM key (macOS) that sealed the copied API keys; DPAPI needs no file.
            var key = Path.Combine(from, "secret.key");
            if (File.Exists(key)) File.Copy(key, Path.Combine(to, "secret.key"), overwrite: false);

            // A consistent snapshot of the release's database even while the release has it
            // open: VACUUM INTO runs inside one read transaction (a file copy could tear a WAL).
            var db = Path.Combine(from, "agentZeroLite.db");
            if (File.Exists(db))
            {
                using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"VACUUM INTO '{Path.Combine(to, "agentZeroLite.db").Replace("'", "''")}'";
                cmd.ExecuteNonQuery();
            }
            AppLogger.Log($"[Debug] profile seeded from {from} -> {to}");
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[Debug] profile seed failed — starting with defaults", ex);
        }
    }
}

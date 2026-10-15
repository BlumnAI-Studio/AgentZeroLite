namespace Agent.Common.Platform;

/// <summary>
/// Timeout-bounded existence checks for paths that are very often on a mapped
/// NETWORK drive — the MP3 scan root, a workspace folder a terminal starts in.
///
/// A bare <see cref="Directory.Exists"/> against a <em>disconnected</em> mapped
/// drive does not fail fast — it blocks the calling thread for the full SMB /
/// redirector timeout (tens of seconds). On a UI thread that is a frozen app:
/// measured 2026-10-15, switching to a workspace on an unreachable
/// <c>\\192.168.0.3\…</c> drive froze the Avalonia host for 21–30 s inside
/// GetFileAttributesExW, before the terminal even started; the Agent Band plugin's
/// scan-folder probe did the same on load.
///
/// We run the probe on a pool thread, give up after a short budget, and briefly
/// cache the verdict so a burst of calls pays the cost at most once. A live-but-slow
/// drive still answers well within the budget; a dead one is simply treated as
/// "not available" instead of freezing the UI. Moved here from <c>Agent.Common.Mp3</c>
/// when the terminals needed it too.
/// </summary>
public static class PathAvailability
{
    private const int CacheTtlMs = 4000;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long AtMs, bool Exists)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <see cref="Directory.Exists"/> that never blocks the caller longer than
    /// <paramref name="timeoutMs"/>. A path that does not answer within the
    /// budget (typical of a disconnected network drive) is reported as missing.
    /// </summary>
    public static bool DirectoryExistsFast(string? path, int timeoutMs = 1200)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        long now = Environment.TickCount64;
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var c) && now - c.AtMs < CacheTtlMs)
                return c.Exists;
        }

        bool exists;
        try
        {
            // The probe keeps running even if we stop waiting; its result is
            // discarded. The TTL cache means a dead drive spawns at most one
            // such orphaned probe per cache window, not one per call.
            var probe = Task.Run(() =>
            {
                try { return Directory.Exists(path); }
                catch { return false; }
            });
            exists = probe.Wait(timeoutMs) && probe.Result;
        }
        catch
        {
            exists = false;
        }

        lock (Gate)
        {
            Cache[path] = (now, exists);
        }
        return exists;
    }

    /// <summary>
    /// Drop the cached verdict for a path — call right after the operator
    /// re-picks the scan folder so a freshly reconnected drive is re-probed
    /// immediately instead of waiting out the TTL.
    /// </summary>
    public static void Invalidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (Gate) { Cache.Remove(path); }
    }
}

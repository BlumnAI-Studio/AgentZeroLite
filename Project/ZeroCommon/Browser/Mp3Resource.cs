using Agent.Common.Mp3;

namespace Agent.Common.Browser;

/// <summary>
/// The rules behind <c>https://mp3.local/</c>, the origin the Agent Band plugin's
/// &lt;audio&gt; element streams from: which file a path names, and which bytes a
/// <c>Range</c> header asks for. Both hosts serve that origin their own way (WPF from
/// a WebView2 resource handler, Avalonia from the loopback plugin server); the rules
/// are here once so a track that plays in one plays in the other.
/// </summary>
public static class Mp3Resource
{
    /// <summary>Cover-art route: <c>__cover/&lt;trackId&gt;</c> → the ID3 APIC bytes.</summary>
    public const string CoverPrefix = "__cover/";

    public enum Outcome { Ok, NotFound, Forbidden }

    /// <summary>
    /// Resolve <paramref name="relativePath"/> (already URL-unescaped, no leading slash) under
    /// the scan root. Only <see cref="Mp3Scanner.SupportedExtensions"/> are served, and a path
    /// that leaves the root is refused.
    /// </summary>
    public static Outcome Resolve(string? scanRoot, string relativePath, out string? fullPath)
    {
        fullPath = null;
        var rel = relativePath.TrimStart('/');
        if (rel.Length == 0) return Outcome.NotFound;
        if (string.IsNullOrWhiteSpace(scanRoot) || !Directory.Exists(scanRoot)) return Outcome.NotFound;

        var rootFull = Path.GetFullPath(scanRoot).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, cmp)) return Outcome.Forbidden;
        if (!Mp3Scanner.SupportedExtensions.Contains(Path.GetExtension(full)) || !File.Exists(full))
            return Outcome.NotFound;

        fullPath = full;
        return Outcome.Ok;
    }

    /// <summary>
    /// Read the first range of a <c>Range: bytes=…</c> header against a file of
    /// <paramref name="total"/> bytes. Returns false when there is no usable range (serve
    /// the whole file); <paramref name="unsatisfiable"/> is set when the range lies outside
    /// the file (answer 416).
    /// </summary>
    public static bool TryParseRange(string? header, long total, out long start, out long end, out bool unsatisfiable)
    {
        start = 0;
        end = total - 1;
        unsatisfiable = false;
        if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;

        // "bytes=start-end" / "bytes=start-" / "bytes=-suffix" (first range only)
        var spec = header.Substring(6).Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var sPart = spec.Substring(0, dash).Trim();
        var ePart = spec.Substring(dash + 1).Trim();
        bool ranged = false;
        if (sPart.Length == 0 && ePart.Length > 0 && long.TryParse(ePart, out var suffix))
        {
            start = Math.Max(0, total - suffix);
            end = total - 1;
            ranged = true;
        }
        else if (sPart.Length > 0 && long.TryParse(sPart, out var s))
        {
            start = s;
            end = ePart.Length > 0 && long.TryParse(ePart, out var en) ? Math.Min(en, total - 1) : total - 1;
            ranged = true;
        }
        if (ranged && (start > end || start >= total)) unsatisfiable = true;
        return ranged;
    }
}

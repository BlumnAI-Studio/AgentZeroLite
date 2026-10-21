using System.Text;

namespace Agent.Common.Workspace;

/// <summary>How the code view shows a file.</summary>
public enum FileViewKind
{
    /// <summary>Text with highlighting (code, config, markdown source, logs…).</summary>
    Text,
    /// <summary>A picture the view draws as an image.</summary>
    Image,
    /// <summary>Not text: shown as a note, with "open with the default app".</summary>
    Binary,
    /// <summary>Text, but over <see cref="WorkspaceFiles.MaxViewBytes"/>: only its head is shown.</summary>
    TooLarge,
}

/// <summary>One entry of a folder listing.</summary>
public sealed record WorkspaceEntry(string Name, string FullPath, bool IsDirectory);

/// <summary>One row of a folder listing (<see cref="WorkspaceFiles.ListDetailed"/>); Size is null for a folder.</summary>
public sealed record FolderEntryInfo(string Name, string FullPath, bool IsDirectory, long? Size,
    DateTime Created, DateTime Modified);

/// <summary>What <see cref="WorkspaceFiles.ReadForView"/> hands the view.</summary>
public sealed record FileViewContent(FileViewKind Kind, string Text, long Length, string? Note);

/// <summary>
/// The file side of the code view (the WPF host's FileTreePanel + DocumentViewerPanel rules,
/// moved down so the view only draws): which entries a folder shows, how a file is shown, and
/// reading it without stalling or flooding the UI.
///
/// <para>Listings are one folder at a time — the tree loads a folder when it is expanded — and
/// callers run them off the UI thread: a workspace can sit on a network drive (see
/// <see cref="Platform.PathAvailability"/>).</para>
/// </summary>
public static class WorkspaceFiles
{
    /// <summary>Text files above this are shown as their first <see cref="MaxViewBytes"/> only.</summary>
    public const int MaxViewBytes = 2 * 1024 * 1024;

    /// <summary>Folders hidden whatever .gitignore says (the WPF tree's list).</summary>
    public static readonly IReadOnlyCollection<string> AlwaysHidden = new[]
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "__pycache__", ".venv", "venv", ".next", "dist",
    };

    /// <summary>Simple .gitignore patterns at the root: names and <c>*.ext</c> (what the WPF tree honours).</summary>
    public static HashSet<string> LoadIgnorePatterns(string root)
    {
        var patterns = new HashSet<string>(AlwaysHidden, StringComparer.OrdinalIgnoreCase);
        try
        {
            var gitIgnore = Path.Combine(root, ".gitignore");
            if (!File.Exists(gitIgnore)) return patterns;
            foreach (var raw in File.ReadLines(gitIgnore))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
                var p = line.TrimEnd('/').TrimStart('/');
                // Only the shapes this matcher understands; a path pattern ("a/b") would match nothing.
                if (p.Length > 0 && !p.Contains('/')) patterns.Add(p);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return patterns;
    }

    public static bool IsHidden(string name, IReadOnlySet<string> patterns)
    {
        if (string.IsNullOrEmpty(name)) return true;
        // Dot entries stay hidden, except the two worth reading in an agent's workspace.
        if (name.StartsWith('.') && name is not (".gitignore" or ".claude" or ".github")) return true;
        foreach (var pattern in patterns)
        {
            if (string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase)) return true;
            if (pattern.StartsWith('*') && pattern.Length > 1 && !pattern[1..].Contains('*')
                && name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>One folder's visible entries: folders first, then files, each by name. Inaccessible → empty.</summary>
    public static IReadOnlyList<WorkspaceEntry> List(string directory, IReadOnlySet<string> patterns)
    {
        var result = new List<WorkspaceEntry>();
        try
        {
            var dir = new DirectoryInfo(directory);
            foreach (var d in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                if (!IsHidden(d.Name, patterns)) result.Add(new WorkspaceEntry(d.Name, d.FullName, true));
            foreach (var f in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                if (!IsHidden(f.Name, patterns)) result.Add(new WorkspaceEntry(f.Name, f.FullName, false));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return result;
    }

    /// <summary>
    /// One folder's visible entries with their details, for the code view's folder listing —
    /// the same filter and order as <see cref="List"/>, so the listing and the tree agree. A
    /// folder's size is not summed (that walks the whole subtree); it is null. Inaccessible → empty.
    /// </summary>
    public static IReadOnlyList<FolderEntryInfo> ListDetailed(string directory, IReadOnlySet<string> patterns)
    {
        var result = new List<FolderEntryInfo>();
        try
        {
            var dir = new DirectoryInfo(directory);
            foreach (var d in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                if (!IsHidden(d.Name, patterns))
                    result.Add(new FolderEntryInfo(d.Name, d.FullName, true, null, d.CreationTime, d.LastWriteTime));
            foreach (var f in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                if (!IsHidden(f.Name, patterns))
                    result.Add(new FolderEntryInfo(f.Name, f.FullName, false, f.Length, f.CreationTime, f.LastWriteTime));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return result;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it (no <c>..</c> escape).</summary>
    public static bool IsInside(string root, string path)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return rel == "." || (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel));
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".zip", ".7z", ".tar", ".gz", ".rar", ".iso", ".bin", ".obj", ".lib", ".so", ".dylib",
        ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp3", ".mp4", ".avi", ".mkv", ".mov", ".wav", ".flac",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".msix", ".nupkg", ".db", ".sqlite", ".onnx", ".gguf",
    };

    /// <summary>The kind by extension alone (no file access) — the tree's first guess.</summary>
    public static FileViewKind KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (ImageExtensions.Contains(ext)) return FileViewKind.Image;
        if (BinaryExtensions.Contains(ext)) return FileViewKind.Binary;
        return FileViewKind.Text;
    }

    /// <summary>
    /// Read a file for the view. Text is decoded as UTF-8 (BOM honoured); a NUL byte in the first
    /// 8 KB makes it binary whatever its extension; past <see cref="MaxViewBytes"/> only the head is
    /// returned with a note. Images return no text — the view loads them itself.
    /// </summary>
    public static FileViewContent ReadForView(string path)
    {
        var info = new FileInfo(path);
        var length = info.Length;
        var kind = KindOf(path);
        if (kind == FileViewKind.Image) return new FileViewContent(kind, "", length, null);
        if (kind == FileViewKind.Binary)
            return new FileViewContent(kind, "", length, $"Binary file ({FormatSize(length)}) — not shown as text.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var take = (int)Math.Min(length, MaxViewBytes);
        var buffer = new byte[take];
        var read = 0;
        while (read < take)
        {
            var n = stream.Read(buffer, read, take - read);
            if (n == 0) break;
            read += n;
        }
        if (Array.IndexOf(buffer, (byte)0, 0, Math.Min(read, 8192)) >= 0)
            return new FileViewContent(FileViewKind.Binary, "", length, $"Binary content ({FormatSize(length)}) — not shown as text.");

        var text = Decode(buffer, read);
        return length > MaxViewBytes
            ? new FileViewContent(FileViewKind.TooLarge, text, length,
                $"Large file ({FormatSize(length)}) — showing the first {FormatSize(MaxViewBytes)}.")
            : new FileViewContent(FileViewKind.Text, text, length, null);
    }

    private static string Decode(byte[] bytes, int count)
    {
        // UTF-8 with or without BOM; UTF-16 when it says so.
        if (count >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, count - 2);
        if (count >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, count - 2);
        var offset = count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, count - offset);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}

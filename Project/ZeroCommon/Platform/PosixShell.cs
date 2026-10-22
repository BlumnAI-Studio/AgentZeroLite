namespace Agent.Common.Platform;

/// <summary>
/// Which login shells a POSIX host has, and which one terminal rows should use. macOS has had
/// zsh as its default since Catalina, so the first POSIX seeding hard-coded <c>/bin/zsh</c> —
/// and stock Ubuntu has no zsh, so every seeded agent row (Claude, Codex, …) failed to start
/// there. The user's own <c>$SHELL</c> wins when it is a real file; otherwise zsh, then bash,
/// then sh, whichever exists.
///
/// <para>The file check is injected so the rule is testable on any OS: a Windows test run can
/// stand in for a Mac (zsh and bash present) or a stock Ubuntu (bash only).</para>
/// </summary>
public sealed class PosixShell
{
    /// <summary>The shells offered as built-in rows, in display order, when they exist.</summary>
    public static readonly string[] Candidates = ["/bin/zsh", "/bin/bash"];

    private readonly Func<string, bool> _exists;
    private readonly string? _shellVariable;

    public PosixShell(Func<string, bool> exists, string? shellVariable)
    {
        _exists = exists;
        _shellVariable = shellVariable;
    }

    /// <summary>This machine: real files and the real <c>$SHELL</c>.</summary>
    public static PosixShell Current => new(File.Exists, Environment.GetEnvironmentVariable("SHELL"));

    /// <summary>A Mac as the original seeding assumed it (zsh and bash, <c>$SHELL</c>=zsh) — for tests.</summary>
    public static PosixShell MacLike => new(p => p is "/bin/zsh" or "/bin/bash" or "/bin/sh", "/bin/zsh");

    /// <summary>The candidate shells that exist here, in display order; <c>/bin/sh</c> when none do.</summary>
    public IReadOnlyList<string> Installed()
    {
        var found = Candidates.Where(_exists).ToList();
        if (found.Count == 0) found.Add("/bin/sh");
        return found;
    }

    /// <summary>The shell agent rows launch through: a usable <c>$SHELL</c>, else the first installed candidate.</summary>
    public string Default()
    {
        if (!string.IsNullOrWhiteSpace(_shellVariable) && Path.IsPathRooted(_shellVariable) && _exists(_shellVariable))
            return _shellVariable;
        return Installed()[0];
    }

    /// <summary>The name a row shows for a shell path (<c>/usr/bin/fish</c> → <c>fish</c>).</summary>
    public static string NameOf(string path) => Path.GetFileName(path);
}

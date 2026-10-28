using System.Diagnostics;
using System.Text;
using Agent.Common.Module;

namespace Agent.Common.Workspace;

/// <summary>What the DIFF tab shows for a repository: the parsed files, or why there are none.</summary>
/// <param name="UntrackedTotal">New files git reported; only the first <see cref="GitDiffRunner.MaxUntrackedFiles"/> are in <paramref name="Files"/>.</param>
public sealed record GitDiffResult(bool Ok, IReadOnlyList<GitDiffReader.DiffFile> Files, string? Error, int UntrackedTotal = 0)
{
    /// <summary>New files left out of <see cref="Files"/> by the cap — the view must say so, or 50 reads as "all".</summary>
    public int UntrackedOmitted => Math.Max(0, UntrackedTotal - GitDiffRunner.MaxUntrackedFiles);

    public static GitDiffResult Fail(string error) => new(false, Array.Empty<GitDiffReader.DiffFile>(), error);
}

/// <summary>
/// The working tree against HEAD — staged and unstaged together, plus the untracked files git
/// does not diff at all, which is where an agent's new files are. The WPF host's
/// <c>GitDiffService</c> moved down and run the GIT LOG tab's way (<see cref="GitLogReader"/>):
/// no shell, prompts off, stdin closed, a time limit. A repository with no commit yet falls back to
/// the plain working-tree diff.
/// </summary>
public static class GitDiffRunner
{
    /// <summary>Untracked files larger than this are listed without their text.</summary>
    public const int MaxUntrackedBytes = 512 * 1024;

    /// <summary>At most this many untracked files get a synthesized diff.</summary>
    public const int MaxUntrackedFiles = 50;

    public static async Task<GitDiffResult> ReadAsync(string directory, CancellationToken ct = default)
    {
        // Ask first, so a plain folder reads "not a git repository" rather than git diff's own
        // advice ("Use --no-index to compare two paths outside a working tree").
        var (inside, answer, why) = await RunAsync(directory, ct, "rev-parse", "--is-inside-work-tree");
        if (!inside || answer.Trim() != "true")
            return GitDiffResult.Fail(why.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) || inside
                ? "not a git repository"
                : why);

        var (ok, output, error) = await RunAsync(directory, ct, "diff", "HEAD", "--no-color", "-U3", "--no-ext-diff");
        if (!ok)
        {
            (ok, output, error) = await RunAsync(directory, ct, "diff", "--no-color", "-U3", "--no-ext-diff");
            if (!ok) return GitDiffResult.Fail(error);
        }
        var files = GitDiffReader.Parse(output).ToList();

        var (uok, untracked, _) = await RunAsync(directory, ct, "ls-files", "--others", "--exclude-standard", "-z");
        var newFiles = uok ? untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
        foreach (var rel in newFiles.Take(MaxUntrackedFiles))
            files.Add(Untracked(directory, rel));
        return new GitDiffResult(true, files, null, newFiles.Length);
    }

    private static GitDiffReader.DiffFile Untracked(string root, string rel)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > MaxUntrackedBytes)
                return new GitDiffReader.DiffFile(rel, rel, IsBinary: true, IsNew: true, IsDeleted: false, []);
            var bytes = File.ReadAllBytes(full);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0)
                return new GitDiffReader.DiffFile(rel, rel, IsBinary: true, IsNew: true, IsDeleted: false, []);
            return TextDiff.Compute(rel, null, Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            return new GitDiffReader.DiffFile(rel, rel, IsBinary: true, IsNew: true, IsDeleted: false, []);
        }
    }

    private static async Task<(bool Ok, string Output, string Error)> RunAsync(string directory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-c", "core.quotepath=off" }.Concat(args)) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { return (false, "", $"git is not available ({ex.Message})"); }
        if (proc is null) return (false, "", "git is not available");
        using (proc)
        {
            proc.StandardInput.Close();
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (false, "", "git did not answer in time");
            }
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            var first = error.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            return proc.ExitCode == 0 ? (true, output, "") : (false, output, first.Length > 0 ? first : $"git exited {proc.ExitCode}");
        }
    }
}

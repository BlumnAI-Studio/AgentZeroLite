using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Agent.Common.Workspace;

/// <summary>One commit as the code view's GIT LOG tab shows it.</summary>
public sealed record GitCommitInfo(string Hash, string Author, DateTimeOffset Date, string Subject, string Body)
{
    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;
}

/// <summary>What a read produced: the commits, newest first — or why there are none.</summary>
public sealed record GitLogResult(bool Ok, IReadOnlyList<GitCommitInfo> Commits, string? Error)
{
    public static GitLogResult Fail(string error) => new(false, Array.Empty<GitCommitInfo>(), error);
}

/// <summary>
/// The GIT LOG tab's history reader: local <c>git log</c> — no network, no account, and it knows
/// unpushed commits. When git cannot read the folder (not a repository, no git on PATH) the tab is
/// disabled with git's reason. It runs without a shell, with prompts off and stdin closed, under a
/// time limit, so a credential helper or a hung repository can never leave the UI waiting. Unit and
/// record separators in the format keep any subject or body intact.
/// </summary>
public static class GitLogReader
{
    public const int DefaultMax = 100;

    private const char Unit = '\u001f';
    private const char Record = '\u001e';

    public static async Task<GitLogResult> ReadAsync(string directory, int max = DefaultMax, CancellationToken ct = default)
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
        foreach (var a in new[] { "-c", "log.showSignature=false", "-c", "core.quotepath=off", "log", "--no-color",
                                  $"-n{max}", "--format=%H%x1f%an%x1f%aI%x1f%s%x1f%b%x1e" })
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { return GitLogResult.Fail($"git is not available ({ex.Message})"); }
        if (proc is null) return GitLogResult.Fail("git is not available");
        using (proc)
        {
            proc.StandardInput.Close();
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return GitLogResult.Fail("git did not answer in time");
            }
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (proc.ExitCode != 0)
                return GitLogResult.Fail(FirstLine(error) is { Length: > 0 } e ? e : $"git exited {proc.ExitCode}");
            return new GitLogResult(true, Parse(output), null);
        }
    }

    /// <summary>Parse the <c>%H␟%an␟%aI␟%s␟%b␞</c> stream.</summary>
    public static IReadOnlyList<GitCommitInfo> Parse(string output)
    {
        var commits = new List<GitCommitInfo>();
        foreach (var raw in output.Split(Record))
        {
            var rec = raw.Trim('\r', '\n');
            if (rec.Length == 0) continue;
            var f = rec.Split(Unit);
            if (f.Length < 5) continue;
            DateTimeOffset.TryParse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
            commits.Add(new GitCommitInfo(f[0].Trim(), f[1], date, f[3], f[4].Trim()));
        }
        return commits;
    }

    private static string FirstLine(string text)
    {
        var t = text.Trim();
        var nl = t.IndexOf('\n');
        return (nl < 0 ? t : t[..nl]).Trim();
    }
}

namespace Agent.Common.Module;

/// <summary>
/// Line diff of two texts into the same <see cref="GitDiffReader.DiffFile"/> model the git reader
/// produces, so one review view renders both: a repository's working tree (git's own diff) and
/// files git does not see — untracked new files, and the AI mode's writes in a folder that is not
/// a repository (<see cref="Agent.Common.Workspace.AiChangeJournal"/>).
///
/// Myers' O(ND) algorithm over lines. Past <see cref="MaxEditDistance"/> edits the files are
/// shown as one replacement rather than spending seconds on a minimal script nobody reads.
/// </summary>
public static class TextDiff
{
    public const int MaxEditDistance = 4000;
    public const int DefaultContext = 3;

    /// <summary>Diff <paramref name="oldText"/> → <paramref name="newText"/>. Null means the file did not exist on that side.</summary>
    public static GitDiffReader.DiffFile Compute(string path, string? oldText, string? newText, int context = DefaultContext)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        var lines = Script(a, b);
        var hunks = Hunks(lines, context);
        return new GitDiffReader.DiffFile(path, path, IsBinary: false, IsNew: oldText is null, IsDeleted: newText is null, hunks);
    }

    /// <summary>Lines without their terminators; a trailing newline does not make an empty last line.</summary>
    public static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>The full edit script, every line with its old/new line numbers (1-based).</summary>
    public static IReadOnlyList<GitDiffReader.DiffLine> Script(string[] a, string[] b)
    {
        var ops = Myers(a, b) ?? Replace(a.Length, b.Length);
        var result = new List<GitDiffReader.DiffLine>(ops.Count);
        foreach (var (kind, ai, bi) in ops)
            result.Add(kind switch
            {
                GitDiffReader.LineKind.Delete => new GitDiffReader.DiffLine(kind, ai + 1, null, a[ai]),
                GitDiffReader.LineKind.Add => new GitDiffReader.DiffLine(kind, null, bi + 1, b[bi]),
                _ => new GitDiffReader.DiffLine(kind, ai + 1, bi + 1, a[ai]),
            });
        return result;
    }

    private static List<(GitDiffReader.LineKind, int, int)> Replace(int n, int m)
    {
        var ops = new List<(GitDiffReader.LineKind, int, int)>(n + m);
        for (int i = 0; i < n; i++) ops.Add((GitDiffReader.LineKind.Delete, i, -1));
        for (int j = 0; j < m; j++) ops.Add((GitDiffReader.LineKind.Add, -1, j));
        return ops;
    }

    /// <summary>Myers' greedy shortest edit script; null when it would exceed <see cref="MaxEditDistance"/>.</summary>
    private static List<(GitDiffReader.LineKind, int, int)>? Myers(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length, max = n + m;
        if (max == 0) return [];
        int offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        int found = -1;
        for (int d = 0; d <= max && d <= MaxEditDistance; d++)
        {
            // The trace keeps one V per step: bound it (~80 MB) for very long files.
            if ((long)(d + 1) * v.Length > 20_000_000) return null;
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset])
                    ? v[k + 1 + offset]
                    : v[k - 1 + offset] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[k + offset] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
            if (found >= 0) break;
        }
        if (found < 0) return null;

        var ops = new List<(GitDiffReader.LineKind, int, int)>();
        int cx = n, cy = m;
        for (int d = found; d >= 0; d--)
        {
            var vd = trace[d];
            int k = cx - cy;
            int prevK = k == -d || (k != d && vd[k - 1 + offset] < vd[k + 1 + offset]) ? k + 1 : k - 1;
            int prevX = vd[prevK + offset];
            int prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                ops.Add((GitDiffReader.LineKind.Context, cx - 1, cy - 1));
                cx--; cy--;
            }
            if (d > 0)
            {
                if (cx == prevX) ops.Add((GitDiffReader.LineKind.Add, -1, cy - 1));
                else ops.Add((GitDiffReader.LineKind.Delete, cx - 1, -1));
            }
            cx = prevX; cy = prevY;
        }
        ops.Reverse();
        return ops;
    }

    /// <summary>Group changed lines into hunks with <paramref name="context"/> lines around each.</summary>
    public static IReadOnlyList<GitDiffReader.DiffHunk> Hunks(IReadOnlyList<GitDiffReader.DiffLine> lines, int context = DefaultContext)
    {
        var hunks = new List<GitDiffReader.DiffHunk>();
        int i = 0;
        while (i < lines.Count)
        {
            // Next change.
            while (i < lines.Count && lines[i].Kind == GitDiffReader.LineKind.Context) i++;
            if (i >= lines.Count) break;
            int start = Math.Max(0, i - context);
            int end = i;
            // Extend while the gap to the next change is within 2×context.
            while (true)
            {
                while (end < lines.Count && lines[end].Kind != GitDiffReader.LineKind.Context) end++;
                int next = end;
                while (next < lines.Count && lines[next].Kind == GitDiffReader.LineKind.Context) next++;
                if (next < lines.Count && next - end <= 2 * context) { end = next; continue; }
                end = Math.Min(lines.Count, end + context);
                break;
            }
            var slice = lines.Skip(start).Take(end - start).ToList();
            int oldStart = slice.FirstOrDefault(l => l.OldLineNo is not null)?.OldLineNo ?? 0;
            int newStart = slice.FirstOrDefault(l => l.NewLineNo is not null)?.NewLineNo ?? 0;
            int oldCount = slice.Count(l => l.Kind != GitDiffReader.LineKind.Add);
            int newCount = slice.Count(l => l.Kind != GitDiffReader.LineKind.Delete);
            hunks.Add(new GitDiffReader.DiffHunk(oldStart, oldCount, newStart, newCount,
                $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@", slice));
            i = end;
        }
        return hunks;
    }
}

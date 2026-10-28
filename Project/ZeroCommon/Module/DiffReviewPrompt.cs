using System.Text;

namespace Agent.Common.Module;

/// <summary>One reviewer note on a diff line, before it is stored or sent.</summary>
public sealed record DiffReviewNote(string FilePath, int LineNumber, string Side, string Text, string? LineText = null);

/// <summary>
/// The review comments as one follow-up prompt for an agent — the WPF Diff Review panel's
/// <c>BuildReviewPrompt</c>, moved down so both hosts word it the same, plus the line's own text
/// so an agent reading it in a terminal (not looking at the diff) knows which line is meant.
/// </summary>
public static class DiffReviewPrompt
{
    public static string Build(IReadOnlyList<DiffReviewNote> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Please address the following code-review comments on the current diff.");
        sb.AppendLine("Each item is anchored to a file and line:");
        sb.AppendLine();
        foreach (var group in notes.GroupBy(n => n.FilePath))
        {
            sb.AppendLine($"### {group.Key}");
            foreach (var n in group.OrderBy(n => n.LineNumber))
            {
                var quoted = string.IsNullOrWhiteSpace(n.LineText) ? "" : $" `{Clip(n.LineText.Trim(), 120)}`";
                sb.AppendLine($"- line {n.LineNumber} ({n.Side}){quoted}: {n.Text.Trim()}");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The same prompt on one line, for typing into a terminal where Enter would submit early.</summary>
    public static string BuildSingleLine(IReadOnlyList<DiffReviewNote> notes)
    {
        var parts = notes.GroupBy(n => n.FilePath).Select(g =>
            $"{g.Key}: " + string.Join("; ", g.OrderBy(n => n.LineNumber).Select(n =>
                $"line {n.LineNumber} ({n.Side}){(string.IsNullOrWhiteSpace(n.LineText) ? "" : $" `{Clip(n.LineText.Trim(), 80)}`")} — {n.Text.Trim().Replace('\n', ' ')}")));
        return "Please address these code-review comments on the current diff: " + string.Join(" | ", parts);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

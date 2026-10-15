using Agent.Common.Agents;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>
/// Reported 2026-10-15: a Claude or Codex session read as working for a moment, then as idle
/// while still working. These pin the screens that flipped it — built-in manifests only, the
/// same ones AgentActivityTracker and terminal-wait --until use.
/// </summary>
public sealed class AgentWorkingDetectionTests
{
    private static AgentActivity Claude(params string[] lines)
        => AgentStateDetector.Detect(AgentManifestCatalog.Claude, new ScreenSnapshot(lines)).State;

    private static AgentActivity Codex(params string[] lines)
        => AgentStateDetector.Detect(AgentManifestCatalog.Codex, new ScreenSnapshot(lines)).State;

    // Claude Code 2.x bottom: rule, input line, rule, mode line, then a multi-line statusline.
    private static readonly string[] ClaudeFooter =
    {
        "────────────────────────────────────────────",
        "> ",
        "────────────────────────────────────────────",
        "  ⏵⏵ accept edits on (shift+tab to cycle)",
        "  [Opus 5.5] ████░░░░ 42% │ AgentZeroLite git:(main*)",
        "  ◐ Edit: ZeroCommon/Agents/AgentManifestCatalog.cs",
        "  ✓ Read ×4 | ✓ Grep ×2",
    };

    [Theory]
    [InlineData("✻")]
    [InlineData("✶")]
    [InlineData("✳")]
    [InlineData("✢")]
    [InlineData("✽")]
    [InlineData("·")]
    public void Every_spinner_frame_is_working(string glyph)
        => Assert.Equal(AgentActivity.Working,
            Claude(new[] { "⏺ Reading the files…", "", $"{glyph} Gallivanting…" }.Concat(ClaudeFooter).ToArray()));

    [Fact]
    public void The_spinner_above_a_todo_list_and_a_statusline_is_still_working()
    {
        var screen = new List<string> { "✶ Pondering… (esc to interrupt · ctrl+t to hide todos)" };
        for (var i = 0; i < 8; i++) screen.Add($"  ⎿  ☐ step {i}");
        screen.Add("");
        screen.AddRange(ClaudeFooter);
        Assert.Equal(AgentActivity.Working, Claude(screen.ToArray()));
    }

    [Fact]
    public void The_new_counter_format_is_working()
        => Assert.Equal(AgentActivity.Working,
            Claude(new[] { "✢ Thinking… (12s · ↓ 1.2k tokens · esc to interrupt)" }.Concat(ClaudeFooter).ToArray()));

    [Fact]
    public void A_finished_turn_is_idle()
        => Assert.Equal(AgentActivity.Idle,
            Claude(new[] { "⏺ Done — three files updated.", "", "✻ Cooked for 1m 12s" }.Concat(ClaudeFooter).ToArray()));

    [Fact]
    public void A_spinning_terminal_title_is_working_whatever_the_screen_shows()
    {
        var snap = new ScreenSnapshot(ClaudeFooter, OscTitle: "⠐ Fix the detector");
        Assert.Equal(AgentActivity.Working, AgentStateDetector.Detect(AgentManifestCatalog.Claude, snap).State);
    }

    [Fact]
    public void The_approval_form_still_wins()
        => Assert.Equal(AgentActivity.Blocked,
            Claude("✶ Running…", " Do you want to proceed?", " ❯ 1. Yes", "   2. No", "", "Esc to cancel"));

    [Fact]
    public void Codex_status_line_is_working()
        => Assert.Equal(AgentActivity.Working, Codex(
            "• Working (14s • esc to interrupt)",
            "",
            "▌ Ask Codex to do anything",
            "  ⏎ send   ⇧⏎ newline   ⌃T transcript   ⌃C quit",
            "  98% context left"));

    [Fact]
    public void Codex_status_without_the_hint_is_working()
        => Assert.Equal(AgentActivity.Working, Codex("◦ Thinking", "", "▌ ", "  98% context left"));

    [Fact]
    public void Codex_answer_that_mentions_working_is_not_working()
        => Assert.NotEqual(AgentActivity.Working, Codex(
            "> The fix is working now and the tests are thinking-free.",
            "",
            "▌ Ask Codex to do anything",
            "  97% context left"));
}

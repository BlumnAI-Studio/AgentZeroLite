using System.IO;
using Agent.Common.Agents;
using Agent.Common.Data.Entities;
using Agent.Common.Platform;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>
/// Which agent a tab runs, for web plugins (Agent Stage's icons). Reported: a tab renamed to Korean
/// ("agent개선") and the claude-sam profile showed a shell icon — the title was the only evidence.
/// </summary>
public sealed class AgentKindDetectorTests
{
    // shell 100 → claude 110;  shell 200 → node 210 → codex 220;  shell 300 → cmd 310 (no agent)
    private static ProcessTable Tree() => new(new (int, int, string)[]
    {
        (100, 1, "powershell.exe"), (110, 100, "claude.exe"),
        (200, 1, "powershell.exe"), (210, 200, "node.exe"), (220, 210, "codex.exe"),
        (300, 1, "powershell.exe"), (310, 300, "cmd.exe"),
    });

    private static AgentKindDetector Detector() => new(Tree, nowMs: () => 0);

    private static CliDefinition Def(string args) => new() { ExePath = "powershell.exe", Arguments = args };

    [Fact]
    public void A_renamed_tab_running_claude_is_claude()
        => Assert.Equal("claude", Detector().KindOf("agent개선", reported: null, shellPid: 100, definition: Def("-NoExit -Command claude")));

    [Fact]
    public void The_process_below_the_shell_wins_over_the_title_and_the_definition()
    {
        // A PowerShell tab titled "PW5" where codex was typed by hand.
        Assert.Equal("codex", Detector().KindOf("PW5", null, 200, Def("")));
        // A tab titled "Claude" whose claude has exited: the definition still says claude.
        Assert.Equal("claude", Detector().KindOf("Claude", null, 300, Def("-NoExit -Command claude")));
    }

    [Fact]
    public void What_the_agent_reported_comes_first()
        => Assert.Equal("claude", Detector().KindOf("PW5", new AgentSessionRef("claude", "~/.claude-sam", "s", @"C:\w"), 300, null));

    [Theory]
    [InlineData("-NoExit -Command claude-sam", "claude")]      // the multi-account profile in this setup
    [InlineData("-NoExit -Command codex_work", "codex")]
    [InlineData("-NoExit -Command claude", "claude")]           // a built-in row
    [InlineData(@"-File C:\claude-notes\run.ps1", null)]        // a folder named after the tool is not a command
    [InlineData("-NoExit", null)]
    public void A_definition_names_its_agent_or_a_profile_named_after_one(string args, string? expected)
        => Assert.Equal(expected, AgentKindDetector.FromDefinition(Def(args)));

    [Theory]
    [InlineData("Claude-2", "claude")]
    [InlineData("codex", "codex")]
    [InlineData("AgentOne", "agentone")]
    [InlineData("agent개선", "shell")]
    public void The_title_is_the_last_resort(string title, string expected)
        => Assert.Equal(expected, Detector().KindOf(title, null, shellPid: null, definition: null));

    [Fact]
    public void Every_claude_profile_folder_is_read_for_status_files()
    {
        var home = Path.Combine(Path.GetTempPath(), "aztest-profiles-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            Directory.CreateDirectory(Path.Combine(home, ".claude-sam"));
            Directory.CreateDirectory(Path.Combine(home, ".claude-profiles", "qa"));
            File.WriteAllText(Path.Combine(home, ".claude.json"), "{}");   // a file, not a profile

            var dirs = ClaudeLiveStatusSource.ProfileDirs(home, configDir: null).ToList();

            Assert.Contains(Path.Combine(home, ".claude", "sessions"), dirs);
            Assert.Contains(Path.Combine(home, ".claude-sam", "sessions"), dirs);
            Assert.Contains(Path.Combine(home, ".claude-profiles", "qa", "sessions"), dirs);
            Assert.DoesNotContain(dirs, d => d.Contains(".claude.json"));
        }
        finally
        {
            try { Directory.Delete(home, true); } catch { }
        }
    }
}

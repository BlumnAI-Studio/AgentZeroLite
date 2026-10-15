using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>
/// Display-only text must not bind TwoWay. Measured (2026-10-15): the Plugins page's
/// official-plugin combo used <c>&lt;Run Text="{Binding Version, StringFormat=' v{0}'}"&gt;</c>;
/// <c>Run.Text</c> defaults to TwoWay, so the formatted text was written back into the
/// record's init property, re-formatted and written again until the process died of a
/// StackOverflowException — two seconds after the page opened, when the list arrived.
/// </summary>
public class XamlBindingGuardTests
{
    [Fact]
    public void Run_text_still_defaults_to_two_way_which_is_why_this_guard_exists()
    {
        Assert.Equal(BindingMode.TwoWay, Run.TextProperty.GetMetadata(typeof(Run)).DefaultBindingMode);
        Assert.Equal(BindingMode.OneWay, TextBlock.TextProperty.GetMetadata(typeof(TextBlock)).DefaultBindingMode);
    }

    [Fact]
    public void Every_bound_Run_says_OneWay()
    {
        var root = FindProjectDir();
        if (root is null) return; // packaged test run without sources
        var offenders = new List<string>();
        // Up to the closing quote, not the first '}' — a StringFormat's {0} has braces of its own.
        var run = new Regex(@"<Run\b[^>]*?\bText=""\{Binding(?<args>[^""]*)""", RegexOptions.Singleline);
        foreach (var file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in run.Matches(File.ReadAllText(file)))
            {
                var args = m.Groups["args"].Value;
                if (!Regex.IsMatch(args, @"Mode\s*=\s*One(Way|Time)\b"))
                    offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
            }
        }
        Assert.True(offenders.Count == 0, "Run.Text binds TwoWay by default — add Mode=OneWay:\n" + string.Join("\n", offenders));
    }

    private static string? FindProjectDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "AgentZeroAvalonia", "AgentZeroAvalonia.csproj");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
        }
        return null;
    }
}

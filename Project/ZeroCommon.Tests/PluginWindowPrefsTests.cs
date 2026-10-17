using Agent.Common.Browser;

namespace ZeroCommon.Tests;

/// <summary>
/// PluginWindowPrefs reads and writes the WPF host's floating-prefs.json, so a plugin parked as
/// a side window comes back in the same spot whichever GUI opened it — and neither host may
/// drop the other's entries when it saves its own.
/// </summary>
public sealed class PluginWindowPrefsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "az-plugin-prefs-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _original = PluginWindowPrefs.FilePath;

    public PluginWindowPrefsTests() => PluginWindowPrefs.FilePath = _file;

    public void Dispose()
    {
        PluginWindowPrefs.FilePath = _original;
        try { File.Delete(_file); } catch { }
    }

    [Fact]
    public void Reads_the_file_the_wpf_host_wrote_and_keeps_its_entries_on_save()
    {
        // Shape written by AgentZeroWpf's WebDevFloatingPrefs.
        File.WriteAllText(_file, """
            { "voice-note": { "topmost": true, "opacity": 0.8, "chrome": false, "x": 9.5, "y": 84, "w": 783, "h": 769.5 } }
            """);

        var wpf = PluginWindowPrefs.Get("voice-note");
        Assert.True(wpf.Topmost);
        Assert.Equal(0.8, wpf.Opacity);
        Assert.False(wpf.Chrome);
        Assert.Equal(9.5, wpf.X);
        Assert.Equal(769.5, wpf.H);

        PluginWindowPrefs.Save("token-monitor", new PluginWindowPref { X = 100, Y = 50, W = 480, H = 720 });

        Assert.True(PluginWindowPrefs.Get("voice-note").Topmost);       // the other host's entry survived
        Assert.Equal(480, PluginWindowPrefs.Get("token-monitor").W);
    }

    [Fact]
    public void Unknown_plugin_and_a_broken_file_give_defaults_and_opacity_is_never_too_faint()
    {
        Assert.False(PluginWindowPrefs.Get("nothing-here").Topmost);

        File.WriteAllText(_file, "{ not json");
        var fallback = PluginWindowPrefs.Get("voice-note");
        Assert.Equal(1.0, fallback.Opacity);
        Assert.Null(fallback.X);

        Assert.Equal(0.5, new PluginWindowPref { Opacity = 0.1 }.SafeOpacity);
        Assert.Equal(1.0, new PluginWindowPref { Opacity = 3 }.SafeOpacity);
    }
}

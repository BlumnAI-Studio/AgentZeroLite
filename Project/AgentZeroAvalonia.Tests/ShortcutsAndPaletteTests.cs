using Agent.Common.Services;
using AgentZeroAvalonia.Layout;
using AgentZeroAvalonia.ViewModels;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>M0042: the hotkey table reads every host id, Settings → Shortcuts edits it, the palette finds things.</summary>
public class ShortcutsAndPaletteTests
{
    [Fact]
    public void Every_supported_id_is_described_once_and_has_a_default()
    {
        var ids = HotkeyTable.Commands.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.True(HotkeyTable.Supported.SetEquals(ids));
        Assert.All(ids, id => Assert.NotNull(HotkeyTable.DefaultGesture(id)));
    }

    [Fact]
    public void The_palette_has_a_chord_and_it_is_not_ctrl_j()
    {
        var palette = Assert.Single(HotkeyTable.Build(null, isMac: false), h => h.Name == HotkeyTable.PaletteOpen);
        Assert.True(palette.Ctrl && palette.Shift && palette.Key == "P");
        Assert.DoesNotContain(HotkeyTable.Build(null, false), h => h.Ctrl && !h.Alt && !h.Shift && h.Key == "J");
    }

    [Fact]
    public void Host_ids_can_be_rebound_and_none_removes_a_default()
    {
        var settings = new ShortcutSettings
        {
            Enabled = true,
            Bindings = { [HotkeyTable.CodeToggle] = "Ctrl+Alt+K", [HotkeyTable.FocusLeft] = "none" },
        };
        var table = HotkeyTable.Build(settings, isMac: false);
        Assert.Contains(table, h => h.Name == HotkeyTable.CodeToggle && h.Key == "K");
        Assert.DoesNotContain(table, h => h.Name == HotkeyTable.FocusLeft);
    }

    [Fact]
    public void A_user_binding_takes_a_default_s_keys_and_the_page_says_which_one_lost()
    {
        ShortcutSettings stored = new();
        var vm = new ShortcutsViewModel(() => stored, s => stored = s, isMac: false);
        var code = vm.Rows.Single(r => r.Id == HotkeyTable.CodeToggle);
        var palette = vm.Rows.Single(r => r.Id == HotkeyTable.PaletteOpen);

        code.Text = "Ctrl+Shift+P";   // the palette's default
        Assert.False(code.HasProblem);
        Assert.Contains("taken by \"Open or close the code view\"", palette.Problem);

        code.Text = "Ctrl+K+L";
        Assert.Contains("not a binding", code.Problem);
        code.Text = "none";
        Assert.Equal("no keys (removed)", code.Problem);
    }

    [Fact]
    public void Save_keeps_entries_this_host_does_not_run_and_turns_the_file_on_only_for_an_override()
    {
        ShortcutSettings stored = new() { Enabled = false, Bindings = { [WindowCommandIds.PanelCollapse] = "Ctrl+Alt+C" } };
        var saved = 0;
        var vm = new ShortcutsViewModel(() => stored, s => stored = s, isMac: false);
        vm.Saved += () => saved++;

        vm.SaveCommand.Execute(null);
        Assert.False(stored.Enabled);   // nothing rebound here: WPF's switch is left alone
        Assert.Equal("Ctrl+Alt+C", stored.Bindings[WindowCommandIds.PanelCollapse]);

        vm.Rows.Single(r => r.Id == HotkeyTable.NextTab).Text = "alt+shift+n";
        vm.SaveCommand.Execute(null);
        Assert.True(stored.Enabled);
        Assert.Equal("Alt+Shift+N", stored.Bindings[HotkeyTable.NextTab]);
        Assert.Equal("Ctrl+Alt+C", stored.Bindings[WindowCommandIds.PanelCollapse]);
        Assert.Equal(2, saved);

        vm.ResetAllCommand.Execute(null);
        vm.SaveCommand.Execute(null);
        Assert.False(stored.Bindings.ContainsKey(HotkeyTable.NextTab));
    }

    [Fact]
    public void Mac_shows_cmd_for_ctrl()
    {
        var vm = new ShortcutsViewModel(() => new ShortcutSettings(), _ => { }, isMac: true);
        Assert.Equal("Cmd+Shift+P", vm.Rows.Single(r => r.Id == HotkeyTable.PaletteOpen).DefaultText);
    }

    [Fact]
    public void Palette_ranks_moves_and_runs()
    {
        var ran = "";
        var vm = new PaletteViewModel([
            new PaletteItem("Terminal", "AgentZeroLite / Claude", "working", () => ran = "claude"),
            new PaletteItem("Terminal", "infra / PW5", "idle", () => ran = "pw5"),
            new PaletteItem("Settings", "Budget", "", () => ran = "budget"),
        ]);
        Assert.Equal(3, vm.Results.Count);

        vm.Query = "budg";
        Assert.Equal("Budget", Assert.Single(vm.Results).Label);
        Assert.True(vm.InvokeSelected());
        Assert.Equal("budget", ran);

        vm.Query = "term";
        Assert.Equal(2, vm.Results.Count);
        Assert.Equal("infra / PW5", vm.Results[0].Label);   // equal prefix matches: the shorter text ranks first
        vm.Move(-1);   // wraps to the last
        Assert.Equal(1, vm.SelectedIndex);
        vm.InvokeSelected();
        Assert.Equal("claude", ran);

        vm.Query = "zzzz";
        Assert.False(vm.InvokeSelected());
        Assert.Equal("No match", vm.Footer);
    }
}

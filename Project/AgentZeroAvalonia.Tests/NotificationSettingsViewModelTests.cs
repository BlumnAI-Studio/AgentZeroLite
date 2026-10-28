using Agent.Common.Agents;
using AgentZeroAvalonia.ViewModels;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>Settings → Notifications edits attention-settings.json and tells the main window.</summary>
public class NotificationSettingsViewModelTests
{
    [Fact]
    public void Save_writes_the_page_clamps_the_popup_time_and_raises_changed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"attention-vm-{Guid.NewGuid():N}.json");
        try
        {
            var vm = new NotificationSettingsViewModel(path);
            Assert.True(vm.Enabled);   // defaults when no file
            var changed = 0;
            vm.Changed += () => changed++;

            vm.OnDone = false;
            vm.PopupSeconds = 500;
            vm.SaveCommand.Execute(null);

            var back = AttentionSettingsStore.Load(path);
            Assert.False(back.OnDone);
            Assert.True(back.OnBlocked);
            Assert.Equal(AttentionSettings.MaxPopupSeconds, back.PopupSeconds);
            Assert.Equal(AttentionSettings.MaxPopupSeconds, vm.PopupSeconds);
            Assert.Equal(1, changed);

            var reopened = new NotificationSettingsViewModel(path);
            Assert.False(reopened.OnDone);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Test_asks_the_main_window_to_fire_an_alert()
    {
        var vm = new NotificationSettingsViewModel(Path.Combine(Path.GetTempPath(), $"none-{Guid.NewGuid():N}.json"));
        var fired = false;
        vm.TestRequested += () => fired = true;
        vm.TestCommand.Execute(null);
        Assert.True(fired);
    }
}

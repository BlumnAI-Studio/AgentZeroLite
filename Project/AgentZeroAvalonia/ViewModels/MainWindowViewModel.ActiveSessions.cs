using System.Collections.ObjectModel;
using Agent.Common;
using Agent.Common.Agents;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// One row of the sidebar's ACTIVE SESSIONS group: a running terminal in any workspace, with
/// the agent state detected on its screen (herdr H1/H2, the WPF SESSIONS panel's dot + chip).
/// Clicking it goes to that workspace and tab in one step.
/// </summary>
public sealed partial class ActiveSessionItem : ObservableObject
{
    public ActiveSessionItem(TerminalTabViewModel tab, string workspace, Action<TerminalTabViewModel> go)
    {
        Tab = tab;
        Workspace = workspace;
        _go = go;
        _title = tab.Title;
    }

    private readonly Action<TerminalTabViewModel> _go;

    public TerminalTabViewModel Tab { get; }
    public string Workspace { get; }

    /// <summary>
    /// The tab's title, re-read on every <see cref="Apply"/>. It used to be a plain pass-through
    /// with no change notification: a rename left the row (kept in place, since the set of rows
    /// had not changed) showing the old name while the workspace's own tab strip had the new one.
    /// </summary>
    [ObservableProperty] private string _title = "";

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _stateLabel = "";
    [ObservableProperty] private IBrush _stateBrush = Brushes.Gray;
    [ObservableProperty] private string _tip = "running";
    [ObservableProperty] private bool _needsAttention;

    public bool HasStateLabel => StateLabel.Length > 0;
    partial void OnStateLabelChanged(string value) => OnPropertyChanged(nameof(HasStateLabel));

    [RelayCommand] private void Go() => _go(Tab);

    /// <summary>Dot colour, chip and tooltip for a detected state — the WPF host's AgentStateVisual.</summary>
    public void Apply(TerminalAgentState? s, bool isActive)
    {
        Title = Tab.Title;
        IsActive = isActive;
        NeedsAttention = s?.NeedsAttention == true;
        var (brush, label, tip) = Visual(s, isActive);
        StateBrush = brush;
        StateLabel = label;
        Tip = $"{Workspace} / {Title} — {tip}";
    }

    internal static (IBrush Brush, string Label, string Tip) Visual(TerminalAgentState? s, bool isActive)
    {
        static IBrush B(uint rgb) => new SolidColorBrush(Color.FromUInt32(0xFF000000 | rgb));
        if (s is null)
            return (isActive ? B(0x00D4FF) : B(0xA06CFF), "", isActive ? "active · running" : "running");
        return s.Activity switch
        {
            AgentActivity.Blocked => (B(0xE06C75), "blocked", "blocked — waiting for input or approval"),
            AgentActivity.Working => (B(0xE5C07B), "working", "working"),
            AgentActivity.Done when !s.Seen => (B(0x61AFEF), "done", "done — not seen yet"),
            AgentActivity.Done => (B(0x98C379), "done", "done (seen)"),
            AgentActivity.Idle => (B(0x7F848E), "idle", "idle"),
            _ => (B(0xA06CFF), "", "running"),
        };
    }
}

/// <summary>
/// ACTIVE SESSIONS: every started terminal across all workspaces, with its detected agent
/// state. Detection is ZeroCommon's <see cref="AgentActivityTracker"/> — the same rules and
/// "seen" bookkeeping the WPF host's SESSIONS panel runs — on a 1.5 s UI-thread timer, as there.
/// </summary>
public partial class MainWindowViewModel
{
    private readonly AgentActivityTracker _agentTracker = new();
    private DispatcherTimer? _agentTimer;
    private string _sessionsSignature = "";

    /// <summary>Running terminals of every workspace, in workspace then tab order.</summary>
    public ObservableCollection<ActiveSessionItem> ActiveSessions { get; } = new();

    [ObservableProperty] private int _attentionCount;
    [ObservableProperty] private string _windowTitle = "AgentZero Lite";

    public bool HasActiveSessions => ActiveSessions.Count > 0;
    public bool HasNoActiveSessions => ActiveSessions.Count == 0;
    public string ActiveSessionsHeader => AttentionCount > 0
        ? $"ACTIVE SESSIONS · {AttentionCount} need you"
        : "ACTIVE SESSIONS";

    partial void OnAttentionCountChanged(int value)
    {
        OnPropertyChanged(nameof(ActiveSessionsHeader));
        // The WPF host's title-bar rollup, so a blocked agent shows from the taskbar too.
        WindowTitle = value > 0 ? $"AgentZero Lite  ● {value} need attention" : "AgentZero Lite";
    }

    /// <summary>Start the detection timer (App, once the window exists).</summary>
    public void StartAgentStateMonitor()
    {
        if (_agentTimer is not null) return;
        _agentTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _agentTimer.Tick += (_, _) => RefreshActiveSessions();
        _agentTimer.Start();
        RefreshActiveSessions();
    }

    public void StopAgentStateMonitor()
    {
        _agentTimer?.Stop();
        _agentTimer = null;
    }

    /// <summary>The <c>agent-state</c> CLI reply — the WPF host's contract.</summary>
    public string AgentStateJson() => _agentTracker.ToCliJson();

    /// <summary>One detection pass + list sync. Cheap when nothing changed.</summary>
    public void RefreshActiveSessions()
    {
        try
        {
            var active = ActiveWorkspace is { } aw && Workspaces.IndexOf(aw) is var gi && gi >= 0 && aw.ActiveTab is not null
                ? (gi, aw.ActiveTabIndex)
                : ((int, int)?)null;
            var stateChanged = _agentTracker.Update(Groups, active);

            // Which rows exist: started terminals, in workspace then tab order.
            var running = new List<(int G, int T, WorkspaceViewModel W, TerminalTabViewModel Tab)>();
            for (int g = 0; g < Workspaces.Count; g++)
                for (int t = 0; t < Workspaces[g].Tabs.Count; t++)
                    if (Workspaces[g].Tabs[t].IsTerminalStarted)
                        running.Add((g, t, Workspaces[g], Workspaces[g].Tabs[t]));

            var signature = string.Join("|", running.Select(r =>
                $"{r.W.DisplayName}/{r.Tab.Title}/{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r.Tab)}"))
                + $"#{active}";
            if (!stateChanged && signature == _sessionsSignature) return;
            _sessionsSignature = signature;

            // Rebuild only when the set of rows changed; otherwise update in place (no flicker).
            var sameRows = ActiveSessions.Count == running.Count
                && ActiveSessions.Select(i => i.Tab).SequenceEqual(running.Select(r => r.Tab))
                && ActiveSessions.Select(i => i.Workspace).SequenceEqual(running.Select(r => r.W.DisplayName));
            if (!sameRows)
            {
                ActiveSessions.Clear();
                foreach (var r in running) ActiveSessions.Add(new ActiveSessionItem(r.Tab, r.W.DisplayName, SelectTab));
                OnPropertyChanged(nameof(HasActiveSessions));
                OnPropertyChanged(nameof(HasNoActiveSessions));
            }
            for (int i = 0; i < running.Count; i++)
            {
                var r = running[i];
                var isActive = active is { } a && a.Item1 == r.G && a.Item2 == r.T;
                ActiveSessions[i].Apply(_agentTracker.Lookup(r.G, r.T), isActive);
            }
            AttentionCount = _agentTracker.AttentionCount();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AgentState] refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>Bold chip for the states that need the user (blocked, unseen done).</summary>
public sealed class AttentionWeight : Avalonia.Data.Converters.IValueConverter
{
    public static readonly AttentionWeight Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? FontWeight.Bold : FontWeight.Normal;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

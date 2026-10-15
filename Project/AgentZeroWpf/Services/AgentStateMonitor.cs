using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Agent.Common;
using Agent.Common.Agents;
using AgentZeroWpf.Module;

namespace AgentZeroWpf.Services;

/// <summary>
/// Live per-terminal agent-state monitor (herdr-adoption H1/H2 runtime): on a timer, one
/// <see cref="AgentActivityTracker"/> pass over every started terminal — ZeroCommon owns the
/// detection and the "seen" bookkeeping, shared with the Avalonia host's ACTIVE SESSIONS
/// list. This class only drives it on the WPF dispatcher and raises <see cref="Changed"/>
/// so the UI / CLI can surface "which agent needs me".
/// </summary>
public sealed class AgentStateMonitor
{
    private readonly Func<IReadOnlyList<CliGroupInfo>> _groups;
    private readonly Func<(int g, int t)?> _active;
    private readonly DispatcherTimer _timer;
    private readonly AgentActivityTracker _tracker = new();

    /// <summary>Raised (on the UI thread) after a detection pass that changed something.</summary>
    public event Action? Changed;

    public AgentStateMonitor(
        Func<IReadOnlyList<CliGroupInfo>> groupsProvider,
        Func<(int g, int t)?> activeProvider,
        TimeSpan? interval = null)
    {
        _groups = groupsProvider;
        _active = activeProvider;
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    /// <summary>Current per-tab states, most-urgent first (blocked/unseen-done bubble up).</summary>
    public IReadOnlyList<TerminalAgentState> Snapshot() => _tracker.Snapshot();

    /// <summary>Detected state for a specific (group, tab), or null if not tracked.</summary>
    public TerminalAgentState? Lookup(int group, int tab) => _tracker.Lookup(group, tab);

    /// <summary>How many tabs need the user right now (blocked or unseen-done).</summary>
    public int AttentionCount() => _tracker.AttentionCount();

    private void Tick()
    {
        try
        {
            var active = _active();
            if (_tracker.Update(_groups(), active is { } a ? (a.g, a.t) : null))
                Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AgentState] monitor tick failed: {ex.Message}");
        }
    }
}

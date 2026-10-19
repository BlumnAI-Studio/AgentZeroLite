using Agent.Common.Module;

namespace Agent.Common.Agents;

/// <summary>Detected state of one running terminal (herdr H1/H2). <see cref="Group"/>/<see cref="Tab"/> are indexes.</summary>
public sealed record TerminalAgentState(int Group, int Tab, string Title, AgentActivity Activity, bool Seen, string? Rule)
{
    /// <summary>Blocked, or done and not looked at yet — "this agent needs me".</summary>
    public bool NeedsAttention => Activity == AgentActivity.Blocked || (Activity == AgentActivity.Done && !Seen);
}

/// <summary>
/// Per-terminal agent-state tracking, host-agnostic: classify each started terminal's
/// on-screen text with <see cref="AgentStateDetector"/> + the manifest picked by its tab
/// title, and remember whether the user has "seen" a finished agent. Moved down from the
/// WPF host's <c>AgentStateMonitor</c> (which now drives this on its DispatcherTimer) so the
/// Avalonia host's ACTIVE SESSIONS list shows the same states from the same rules.
///
/// Not thread-safe: call <see cref="Update"/> and read the results on one thread (the UI's,
/// where the tab lists live).
/// </summary>
public sealed class AgentActivityTracker
{
    private readonly Dictionary<string, TerminalAgentState> _states = new();
    private readonly Dictionary<string, bool> _hookInstalledLifecycle = new(StringComparer.OrdinalIgnoreCase);
    // Consecutive passes a Working tab has looked not-working — see HoldWorking.
    private readonly Dictionary<string, int> _quietPasses = new();
    private string _lastSignature = "";

    /// <summary>
    /// Passes a Working tab must look not-working (idle, or no rule at all) before it is shown
    /// as such. A full-screen TUI redraws by clearing and repainting, and the viewport snapshot
    /// can land between the two; one pass of grace (≈1.5 s) absorbs that without hiding a real
    /// finish for long. Blocked and Done apply at once — they are positive signals.
    /// </summary>
    public int WorkingGracePasses { get; init; } = 1;

    /// <summary>
    /// What agents say about themselves, asked before the screen rules (null: screen only).
    /// The default reads Claude Code's own status files (<see cref="ClaudeLiveStatusSource"/>):
    /// it does not depend on where the spinner is drawn, and it knows a question to the user
    /// from a finished turn. A terminal it has no answer for keeps the screen rules.
    /// </summary>
    public IAgentLiveStatusSource? LiveStatus { get; init; } =
        new CompositeLiveStatusSource(new ClaudeLiveStatusSource(), new CodexLiveStatusSource());

    /// <summary>
    /// One detection pass over every started terminal. <paramref name="active"/> is the
    /// focused (group, tab) — focusing a finished agent marks it seen. Returns true when any
    /// state or seen flag changed since the previous pass (so the UI repaints only then).
    /// </summary>
    public bool Update(IReadOnlyList<ICliGroupInfo> groups, (int Group, int Tab)? active)
    {
        // Both hosts call this on their UI thread every 1.5 s; a pass that takes long enough to
        // be felt is logged (at most once a minute) rather than left to be guessed at.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { return UpdateCore(groups, active); }
        finally
        {
            if (clock.ElapsedMilliseconds >= SlowPassMs && Environment.TickCount64 - _lastSlowLog > 60_000)
            {
                _lastSlowLog = Environment.TickCount64;
                AppLogger.Log($"[AgentState] slow detection pass: {clock.ElapsedMilliseconds} ms over {_states.Count} terminal(s) on the UI thread");
            }
        }
    }

    private const int SlowPassMs = 50;
    private long _lastSlowLog = -60_000;

    private bool UpdateCore(IReadOnlyList<ICliGroupInfo> groups, (int Group, int Tab)? active)
    {
        var live = new HashSet<string>();
        LiveStatus?.BeginPass();

        for (int gi = 0; gi < groups.Count; gi++)
        {
            var tabs = groups[gi].TabsView;
            // Without a pid, a status file is matched to a tab by folder — only safe when no
            // other tab in this workspace could be running the same agent.
            var agentTabs = tabs.Count(t => t.IsTerminalStarted && t.Session is not Services.ITerminalProcessSource { ProcessId: not null }
                                            && AgentManifestCatalog.ResolveId(t.Title) == "claude");
            for (int ti = 0; ti < tabs.Count; ti++)
            {
                var tab = tabs[ti];
                if (!tab.IsTerminalStarted || tab.Session is null) continue;

                // InternalId is on ITerminalSession, so this is backend-agnostic.
                string key = tab.Session.InternalId;
                live.Add(key);

                string text;
                try { text = ApprovalParser.StripAnsiCodes(tab.Session.GetConsoleText()); }
                catch { continue; }

                var lines = text.Replace("\r\n", "\n").Split('\n');
                // The title the program set (OSC 0/2) — Claude Code spins a braille glyph there
                // while busy; the manifests' osc_title rules read it.
                var title = (tab.Session as Services.ITerminalTitleSource)?.WindowTitle;
                _states.TryGetValue(key, out var prev);

                // H4 authority gate: for a lifecycle-authority CLI whose hook is installed,
                // the hook is the single source of truth — screen detection is suppressed and
                // the hook-reported state is kept. Every currently-shipping agent resolves to
                // screen detection (the correct default).
                AgentActivity activity;
                string? rule;
                var pid = (tab.Session as Services.ITerminalProcessSource)?.ProcessId;
                var reported = LiveStatus?.StatusOf(key, pid, groups[gi].DirectoryPath,
                    onlyAgentTabInFolder: agentTabs == 1 && AgentManifestCatalog.ResolveId(tab.Title) == "claude");
                if (reported is not null)
                {
                    // The agent's own word. A turn that was running and now is not is Done —
                    // the "finished, look at me" the screen rules could only guess at — and it
                    // stays Done (seen or not) until the next turn starts.
                    activity = reported.Activity == AgentActivity.Idle
                               && prev?.Activity is AgentActivity.Working or AgentActivity.Blocked or AgentActivity.Done
                        ? AgentActivity.Done
                        : reported.Activity;
                    rule = reported.Rule;
                    _quietPasses.Remove(key);

                    // A record that only knows "a turn is running" (Codex's rollout has no approval
                    // events) cannot see the turn stop for a question; the screen can, and a
                    // Blocked screen is a positive signal — it wins over Working.
                    if (activity == AgentActivity.Working
                        && AgentStateDetector.Detect(AgentManifestCatalog.ForAgent(tab.Title), new ScreenSnapshot(lines, title))
                            is { State: AgentActivity.Blocked, StateChanged: true } screen)
                    {
                        activity = AgentActivity.Blocked;
                        rule = screen.MatchedRuleId;
                    }
                }
                else if (AgentIntegrationCatalog.UseScreenDetection(tab.Title, HookInstalled(tab.Title)))
                {
                    var manifest = AgentManifestCatalog.ForAgent(tab.Title);
                    var res = AgentStateDetector.Detect(manifest, new ScreenSnapshot(lines, title));
                    activity = res.StateChanged ? res.State : (prev?.Activity ?? AgentActivity.Unknown);
                    rule = res.MatchedRuleId;
                    if (HoldWorking(key, prev, activity))
                    {
                        activity = AgentActivity.Working;
                        rule = prev!.Rule;
                    }
                }
                else
                {
                    activity = prev?.Activity ?? AgentActivity.Unknown;
                    rule = prev?.Rule ?? "hook-authority";
                }

                bool isActive = active is { } a && a.Group == gi && a.Tab == ti;
                bool seen;
                if (isActive) seen = true;                                   // focusing marks it seen
                else if (activity == AgentActivity.Done && prev?.Activity != AgentActivity.Done)
                    seen = false;                                             // newly done, unseen
                else seen = prev?.Seen ?? (activity != AgentActivity.Done);

                if (prev is null || prev.Activity != activity)
                    LogTransition(groups[gi].DisplayName, tab.Title, prev?.Activity, activity, rule, lines, title);

                _states[key] = new TerminalAgentState(gi, ti, tab.Title, activity, seen, rule);
            }
        }

        // Drop tabs that no longer exist.
        foreach (var stale in _states.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _states.Remove(stale);
            _quietPasses.Remove(stale);
            LiveStatus?.Forget(stale);
        }

        var sig = string.Join("|", _states.Values
            .OrderBy(s => s.Group).ThenBy(s => s.Tab)
            .Select(s => $"{s.Group}:{s.Tab}:{s.Activity}:{s.Seen}"));
        if (sig == _lastSignature) return false;
        _lastSignature = sig;
        return true;
    }

    /// <summary>
    /// One log line per state change, with the rule that decided it and the bottom of the screen
    /// it saw — so a wrong chip ("blocked" while it was not) can be traced to the rule and the
    /// text that fired it, and the manifests tuned on real screens. Only the last three non-empty
    /// lines, each cut to 100 characters: enough to recognise a prompt, little enough to keep
    /// what was on screen out of the log.
    /// </summary>
    private static void LogTransition(string workspace, string tab, AgentActivity? from, AgentActivity to,
        string? rule, IReadOnlyList<string> lines, string? title)
    {
        var tail = lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(3)
            .Select(l => { var t = l.Trim(); return t.Length > 100 ? t[..100] + "…" : t; });
        var osc = string.IsNullOrEmpty(title) ? "" : $" title='{(title.Length > 60 ? title[..60] + "…" : title)}'";
        AppLogger.Log($"[AgentState] {workspace}/{tab} {(from?.ToString() ?? "new").ToLowerInvariant()} → {to.ToString().ToLowerInvariant()}" +
                      $" (rule {rule ?? "fallback"}){osc} | {string.Join(" ⏎ ", tail)}");
    }

    /// <summary>
    /// True while a Working tab's drop to idle / unknown is still inside the grace window — the
    /// caller then keeps it Working. A working pass resets the count; Blocked and Done pass
    /// straight through.
    /// </summary>
    private bool HoldWorking(string key, TerminalAgentState? prev, AgentActivity next)
    {
        if (next is AgentActivity.Working or AgentActivity.Blocked or AgentActivity.Done
            || prev?.Activity != AgentActivity.Working)
        {
            _quietPasses.Remove(key);
            return false;
        }
        var quiet = _quietPasses.TryGetValue(key, out var n) ? n + 1 : 1;
        if (quiet > WorkingGracePasses)
        {
            _quietPasses.Remove(key);
            return false;
        }
        _quietPasses[key] = quiet;
        return true;
    }

    /// <summary>Current per-tab states, most-urgent first (blocked / unseen-done bubble up).</summary>
    public IReadOnlyList<TerminalAgentState> Snapshot()
        => _states.Values
            .OrderByDescending(Urgency)
            .ThenBy(s => s.Group).ThenBy(s => s.Tab)
            .ToList();

    /// <summary>Detected state for a specific (group, tab), or null if not tracked.</summary>
    public TerminalAgentState? Lookup(int group, int tab)
        => _states.Values.FirstOrDefault(s => s.Group == group && s.Tab == tab);

    /// <summary>How many tabs need the user right now (blocked or unseen-done).</summary>
    public int AttentionCount() => _states.Values.Count(s => s.NeedsAttention);

    /// <summary>The <c>agent-state</c> CLI reply — one JSON contract for both hosts.</summary>
    public string ToCliJson()
    {
        var snap = Snapshot();
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"ok\":true,\"attention\":").Append(AttentionCount()).Append(",\"tabs\":[");
        for (int i = 0; i < snap.Count; i++)
        {
            var s = snap[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            sb.Append($"\"group\":{s.Group},\"tab\":{s.Tab}");
            sb.Append($",\"title\":{System.Text.Json.JsonSerializer.Serialize(s.Title)}");
            sb.Append($",\"state\":\"{s.Activity.ToString().ToLowerInvariant()}\"");
            sb.Append($",\"seen\":{(s.Seen ? "true" : "false")}");
            sb.Append($",\"rule\":{System.Text.Json.JsonSerializer.Serialize(s.Rule ?? "")}");
            sb.Append('}');
        }
        return sb.Append("]}").ToString();
    }

    private static int Urgency(TerminalAgentState s) => s.Activity switch
    {
        AgentActivity.Blocked => 4,
        AgentActivity.Done when !s.Seen => 3,
        AgentActivity.Working => 2,
        AgentActivity.Idle => 1,
        _ => 0,
    };

    // H4 authority gate: lifecycle-authority CLIs whose hook is installed. Empty today — no
    // lifecycle-authority CLI ships a hook installer yet — so the gate resolves to screen
    // detection for every shipping agent. Populate from the installer's discovery when one lands.
    private bool HookInstalled(string? title)
    {
        var integ = AgentIntegrationCatalog.Lookup(title);
        // Only lifecycle-authority CLIs are gated by hook presence; for session-identity
        // agents screen detection always wins → the flag is irrelevant.
        if (integ is null || integ.Authority != IntegrationAuthority.LifecycleAuthority)
            return false;
        return _hookInstalledLifecycle.TryGetValue(integ.Agent, out var v) && v;
    }
}

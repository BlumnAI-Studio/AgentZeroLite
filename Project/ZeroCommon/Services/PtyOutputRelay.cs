using System.Text;

namespace Agent.Common.Services;

/// <summary>
/// The <c>Output</c> event of a PTY host, minus the race at its start. A host spawns the
/// child and starts its read thread inside <c>Start()</c>, and every caller subscribes
/// right after <c>Start()</c> returns — so whatever the child printed in between had no
/// handler and was dropped. Measured on the macOS runner: a shell that echoes a marker and
/// exits lost the marker every time (the PTY test failed on every push since 2026-09-22),
/// and a real tab can lose the start of its first prompt the same way.
///
/// <para>Text raised before anyone subscribes is kept (up to <see cref="EarlyCapChars"/>)
/// and handed to the first subscriber when it attaches, ahead of anything newer: raising
/// and replaying happen under one lock, so the order is the order the child wrote. Later
/// subscribers get only what comes after them.</para>
/// </summary>
public sealed class PtyOutputRelay
{
    /// <summary>Early output kept for the first subscriber; a child that floods before anyone listens is cut here.</summary>
    public const int EarlyCapChars = 64 * 1024;

    private readonly object _gate = new();
    private Action<string>? _handlers;
    private StringBuilder? _early = new();

    public void Subscribe(Action<string>? handler)
    {
        if (handler is null) return;
        lock (_gate)
        {
            _handlers += handler;
            var early = _early;
            _early = null; // only the first subscriber gets the replay
            if (early is { Length: > 0 })
            {
                try { handler(early.ToString()); } catch { /* subscriber bug, not load-bearing */ }
            }
        }
    }

    public void Unsubscribe(Action<string>? handler)
    {
        if (handler is null) return;
        lock (_gate) { _handlers -= handler; }
    }

    /// <summary>Called by the host's read thread for every decoded chunk.</summary>
    public void Raise(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            if (_handlers is null)
            {
                if (_early is not null && _early.Length < EarlyCapChars)
                    _early.Append(text, 0, Math.Min(text.Length, EarlyCapChars - _early.Length));
                return;
            }
            try { _handlers(text); } catch { /* subscriber bug, not load-bearing */ }
        }
    }
}

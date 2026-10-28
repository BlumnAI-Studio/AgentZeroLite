using System.Collections.ObjectModel;
using Agent.Common;
using Agent.Common.Services;
using AgentZeroAvalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>One command on the Shortcuts page: what it does, its default, and the user's keys (blank = the default).</summary>
public sealed partial class ShortcutRowViewModel : ObservableObject
{
    public ShortcutRowViewModel(string id, string description, string? defaultGesture, bool isMac)
    {
        Id = id;
        Description = description;
        DefaultText = defaultGesture is null ? "(none)"
            : ShortcutGesture.TryParse(defaultGesture, out var g) ? HotkeyTable.Display(g, isMac) : defaultGesture;
    }

    public string Id { get; }
    public string Description { get; }
    public string DefaultText { get; }

    /// <summary>The user's binding as typed: blank keeps the default, "none" removes it.</summary>
    [ObservableProperty] private string _text = "";

    /// <summary>Invalid / taken by another command / removed — or empty when fine.</summary>
    [ObservableProperty] private string _problem = "";

    public bool HasProblem => Problem.Length > 0;
    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    [RelayCommand] private void Reset() => Text = "";
    [RelayCommand] private void Remove() => Text = HotkeyTable.Unbound;
}

/// <summary>
/// Settings → Shortcuts (WPF's Shortcuts tab, M0042): every command a chord runs in this host,
/// its default, and a box to rebind it — "Ctrl+Alt+K", blank for the default, "none" to free the
/// keys for the programs in the terminals. Stored in <c>shortcut-settings.json</c>, shared with the
/// WPF host: entries this host does not run are kept as they were, and the file's switch is turned
/// on only once something is rebound here.
/// </summary>
public sealed partial class ShortcutsViewModel : ObservableObject
{
    private readonly Func<ShortcutSettings> _load;
    private readonly Action<ShortcutSettings> _save;
    private readonly bool _isMac;

    public ShortcutsViewModel(Func<ShortcutSettings>? load = null, Action<ShortcutSettings>? save = null, bool? isMac = null)
    {
        _load = load ?? ShortcutSettingsStore.Load;
        _save = save ?? ShortcutSettingsStore.Save;
        _isMac = isMac ?? OperatingSystem.IsMacOS();
        foreach (var (id, description) in HotkeyTable.Commands)
        {
            var row = new ShortcutRowViewModel(id, description, HotkeyTable.DefaultGesture(id), _isMac);
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShortcutRowViewModel.Text)) Validate(); };
            Rows.Add(row);
        }
        Load();
    }

    public ObservableCollection<ShortcutRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _status = "";

    public string ModifierNote => _isMac
        ? "On macOS, Ctrl in a binding is pressed as Cmd."
        : "Every binding needs a modifier (Ctrl, Alt, Shift or Win), so plain typing in a terminal is never taken.";

    /// <summary>Saved: the main window rebuilds its table and pushes it to every terminal.</summary>
    public event Action? Saved;

    public void Load()
    {
        ShortcutSettings s;
        try { s = _load(); } catch { s = new ShortcutSettings(); }
        foreach (var row in Rows)
            row.Text = s.Enabled && s.Bindings.TryGetValue(row.Id, out var text) ? text ?? "" : "";
        Validate();
    }

    /// <summary>Mark rows whose text does not parse, or whose keys another command already has.</summary>
    public void Validate()
    {
        // Judge with the table itself, so the page and the keys never disagree about who wins.
        var effective = HotkeyTable.Effective(Build(new ShortcutSettings()));
        foreach (var row in Rows)
        {
            var t = row.Text.Trim();
            if (t.Length > 0 && !IsUnbound(t) && !ShortcutGesture.TryParse(t, out _))
                row.Problem = "not a binding — e.g. Ctrl+Alt+K (a modifier is required)";
            else if (IsUnbound(t))
                row.Problem = "no keys (removed)";
            else if (!effective.ContainsKey(row.Id) && ShortcutGesture.TryParse(EffectiveText(row), out var g))
            {
                var owner = effective.FirstOrDefault(kv => kv.Value.ToString() == g.ToString()).Key;
                var who = Rows.FirstOrDefault(r => r.Id == owner)?.Description ?? owner;
                row.Problem = $"{HotkeyTable.Display(g, _isMac)} is taken by \"{who}\" — this one will not fire";
            }
            else row.Problem = "";
        }
    }

    private static bool IsUnbound(string t) => string.Equals(t, HotkeyTable.Unbound, StringComparison.OrdinalIgnoreCase);

    private static string EffectiveText(ShortcutRowViewModel r)
    {
        var t = r.Text.Trim();
        if (IsUnbound(t)) return "";
        return t.Length > 0 ? t : HotkeyTable.DefaultGesture(r.Id) ?? "";
    }

    /// <summary>The settings to store: the file as it was, with this page's ids replaced.</summary>
    public ShortcutSettings Build(ShortcutSettings current)
    {
        var bindings = new Dictionary<string, string>(current.Bindings);
        foreach (var row in Rows) bindings.Remove(row.Id);
        foreach (var row in Rows)
        {
            var t = row.Text.Trim();
            if (t.Length == 0) continue;
            if (IsUnbound(t)) bindings[row.Id] = HotkeyTable.Unbound;
            else if (ShortcutGesture.TryParse(t, out var g)) bindings[row.Id] = g.ToString();
        }
        var overrides = Rows.Any(r => r.Text.Trim().Length > 0);
        return new ShortcutSettings { Enabled = current.Enabled || overrides, Bindings = bindings };
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            ShortcutSettings current;
            try { current = _load(); } catch { current = new ShortcutSettings(); }
            var s = Build(current);
            _save(s);
            Load();
            var invalid = Rows.Count(r => r.HasProblem && !r.Problem.StartsWith("no keys", StringComparison.Ordinal));
            Status = invalid == 0 ? "Saved — the new keys work now, in every terminal." : $"Saved; {invalid} row(s) need attention.";
            AppLogger.Log($"[Settings] shortcuts saved | overrides={Rows.Count(r => r.Text.Trim().Length > 0)}");
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            Status = "Save failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var row in Rows) row.Text = "";
        Status = "All rows back to their defaults — Save to apply.";
    }
}

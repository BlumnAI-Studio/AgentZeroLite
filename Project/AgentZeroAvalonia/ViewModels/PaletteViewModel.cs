using System.Collections.ObjectModel;
using Agent.Common.Module;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>One thing the palette can do: go to a workspace or terminal, or run a command.</summary>
public sealed record PaletteItem(string Category, string Label, string Hint, Action Invoke)
{
    /// <summary>What the query is matched against: "Terminal: AgentZeroLite / Claude".</summary>
    public string SearchText => $"{Category}: {Label}";
}

/// <summary>
/// The command palette (M0042; the WPF host's Ctrl+J popup): type a few letters, get the
/// workspaces, terminals and commands that match (<see cref="FuzzyMatcher"/>), Enter runs the
/// highlighted one. Items are built fresh each time it opens, so they are never stale.
/// </summary>
public sealed partial class PaletteViewModel : ObservableObject
{
    public const int MaxResults = 30;

    private readonly IReadOnlyList<PaletteItem> _all;

    public PaletteViewModel(IReadOnlyList<PaletteItem> items)
    {
        _all = items;
        Filter();
    }

    public ObservableCollection<PaletteItem> Results { get; } = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private int _selectedIndex;

    public string Footer => Results.Count == 0 ? "No match" : "↑↓ choose · Enter run · Esc close";

    partial void OnQueryChanged(string value) => Filter();

    private void Filter()
    {
        var ranked = string.IsNullOrWhiteSpace(Query) ? _all : FuzzyMatcher.Rank(Query.Trim(), _all, i => i.SearchText);
        Results.Clear();
        foreach (var item in ranked.Take(MaxResults)) Results.Add(item);
        SelectedIndex = Results.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(Footer));
    }

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        SelectedIndex = ((SelectedIndex + delta) % Results.Count + Results.Count) % Results.Count;
    }

    /// <summary>Run the highlighted item. Returns false when there is none.</summary>
    public bool InvokeSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return false;
        Results[SelectedIndex].Invoke();
        return true;
    }
}

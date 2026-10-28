using System.Collections.ObjectModel;
using System.Globalization;
using Agent.Common;
using Agent.Common.Telemetry;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>One editable price override (USD per million tokens), matched by substring of the model name.</summary>
public sealed partial class PriceRowViewModel : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private decimal? _input;
    [ObservableProperty] private decimal? _output;
    [ObservableProperty] private decimal? _cacheWrite;
    [ObservableProperty] private decimal? _cacheRead;

    public Action<PriceRowViewModel>? RemoveRequested { get; set; }

    [RelayCommand] private void Remove() => RemoveRequested?.Invoke(this);
}

/// <summary>
/// Settings → Budget, and the status bar's spend chip: what the agents in the terminals have
/// cost this month (from the token records the collectors already write), against an optional
/// monthly cap, with per-model price overrides. The WPF host's Budget tab, plus the breakdown by
/// model and project and the list of models no price matched. Shared settings file:
/// <c>budget-settings.json</c>; the numbers are ZeroCommon's <see cref="CostReport"/>.
/// </summary>
public partial class BudgetViewModel : ObservableObject
{
    private readonly string? _path;
    private readonly Func<BudgetSettings, CostReport> _load;

    public BudgetViewModel(string? path = null, Func<BudgetSettings, CostReport>? load = null)
    {
        _path = path;
        _load = load ?? (s => CostReport.Load(s, DateTime.UtcNow));
        LoadSettings();
    }

    [ObservableProperty] private string _capText = "0";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _spendText = "";
    [ObservableProperty] private string _chipText = "";
    [ObservableProperty] private IBrush _spendBrush = Mint;
    [ObservableProperty] private string _allTimeText = "";
    [ObservableProperty] private string _unpricedText = "";

    public ObservableCollection<PriceRowViewModel> Prices { get; } = new();
    public ObservableCollection<CostLine> ByModel { get; } = new();
    public ObservableCollection<CostLine> ByProject { get; } = new();

    public bool HasUnpriced => UnpricedText.Length > 0;
    partial void OnUnpricedTextChanged(string value) => OnPropertyChanged(nameof(HasUnpriced));

    /// <summary>The last report, for the CLI and the status bar.</summary>
    public CostReport? Report { get; private set; }

    /// <summary>The cap and the price table were saved.</summary>
    public event Action? Saved;

    private static readonly IBrush Mint = new SolidColorBrush(Color.FromUInt32(0xFF00FFA3));
    private static readonly IBrush Amber = new SolidColorBrush(Color.FromUInt32(0xFFFFC24D));
    private static readonly IBrush Red = new SolidColorBrush(Color.FromUInt32(0xFFFF4D4D));

    public void LoadSettings()
    {
        var s = BudgetSettingsStore.Load(_path);
        CapText = s.MonthlyCapUsd.ToString(CultureInfo.InvariantCulture);
        Prices.Clear();
        foreach (var o in s.Overrides)
            AddRow(new PriceRowViewModel
            {
                Key = o.Key, Input = o.InputPerMTok, Output = o.OutputPerMTok,
                CacheWrite = o.CacheWritePerMTok, CacheRead = o.CacheReadPerMTok,
            });
    }

    /// <summary>The page as a settings object. A cap that does not parse is "no cap"; blank keys are dropped.</summary>
    public BudgetSettings Build()
    {
        var s = new BudgetSettings();
        if (decimal.TryParse(CapText?.Trim().TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var cap) && cap > 0)
            s.MonthlyCapUsd = cap;
        foreach (var r in Prices)
        {
            if (string.IsNullOrWhiteSpace(r.Key)) continue;
            s.Overrides.Add(new BudgetSettings.PriceOverride
            {
                Key = r.Key.Trim(),
                InputPerMTok = r.Input ?? 0, OutputPerMTok = r.Output ?? 0,
                CacheWritePerMTok = r.CacheWrite ?? 0, CacheReadPerMTok = r.CacheRead ?? 0,
            });
        }
        return s;
    }

    [RelayCommand]
    private void AddPrice() => AddRow(new PriceRowViewModel());

    /// <summary>Add a row for every model no price matched, so the user only fills in the numbers.</summary>
    [RelayCommand]
    private void AddUnpriced()
    {
        if (Report is null) return;
        foreach (var model in Report.UnpricedModels)
            if (!Prices.Any(p => string.Equals(p.Key, model, StringComparison.OrdinalIgnoreCase)))
                AddRow(new PriceRowViewModel { Key = model.ToLowerInvariant() });
    }

    private void AddRow(PriceRowViewModel row)
    {
        row.RemoveRequested = r => Prices.Remove(r);
        Prices.Add(row);
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var s = Build();
            BudgetSettingsStore.Save(s, _path);
            CapText = s.MonthlyCapUsd.ToString(CultureInfo.InvariantCulture);
            AppLogger.Log($"[Budget] saved | cap={s.MonthlyCapUsd} overrides={s.Overrides.Count}");
            Refresh(s);
            Status = "Saved.";
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            Status = "Save failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void RefreshNow() => Refresh(Build());

    /// <summary>Re-price from the database with <paramref name="settings"/> (the stored ones when null).</summary>
    public void Refresh(BudgetSettings? settings = null)
    {
        try
        {
            var r = _load(settings ?? BudgetSettingsStore.Load(_path));
            Apply(r);
        }
        catch (Exception ex)
        {
            // First run / migration pending: show nothing rather than a wrong number.
            Status = "Could not read token usage: " + ex.Message;
            AppLogger.Log($"[Budget] refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The status bar's periodic read: query on the pool, apply on the UI thread.</summary>
    public async Task RefreshInBackgroundAsync()
    {
        try
        {
            var s = BudgetSettingsStore.Load(_path);
            var r = await Task.Run(() => _load(s));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Apply(r));
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Budget] background refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Apply(CostReport r)
    {
        Report = r;
        var m = r.Month;
        SpendBrush = m.OverBudget ? Red : m.NearingCap() ? Amber : Mint;
        var tail = m.OverBudget ? "  ⚠ OVER BUDGET" : m.NearingCap() ? "  ⚠ nearing cap" : "";
        SpendText = m.HasCap
            ? $"Spent this month: ${m.SpentUsd:F2} / ${m.CapUsd:F2}  ({m.FractionUsed * 100:F0}%){tail}"
            : $"Spent this month: ${m.SpentUsd:F2}  (no cap set)";
        ChipText = "💲 " + r.StatusText();
        AllTimeText = $"{r.MonthTurns:N0} turns this month · all time ${r.AllTimeUsd:F2} over {r.AllTimeTurns:N0} turns · estimate: prices are editable defaults, not a live feed";
        UnpricedText = r.UnpricedModels.Count == 0
            ? ""
            : $"No price matched {string.Join(", ", r.UnpricedModels)} — counted as $0. Add a price row below (\"Add rows for these\").";
        Reset(ByModel, r.MonthByModel);
        Reset(ByProject, r.MonthByProject.Take(15));
    }

    private static void Reset(ObservableCollection<CostLine> target, IEnumerable<CostLine> lines)
    {
        target.Clear();
        foreach (var l in lines) target.Add(l);
    }
}

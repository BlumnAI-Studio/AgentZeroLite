using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Common;
using Agent.Common.Llm.Tools;
using Agent.Common.Mcp;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>One header or environment row. The value is shown masked: it is usually a key.</summary>
public partial class McpKeyValueRow : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";
}

/// <summary>One tool a server listed, with the agent's permission to call it.</summary>
public partial class McpToolItem : ObservableObject
{
    public string Name { get; }
    public string Description { get; }
    public string ArgsSummary { get; }
    public string? InputSchema { get; }
    [ObservableProperty] private bool _allowed;

    public McpToolItem(McpToolInfo info, bool allowed)
    {
        Name = info.Name;
        Description = string.IsNullOrWhiteSpace(info.Description) ? "" : info.Description.Trim();
        InputSchema = info.InputSchema;
        ArgsSummary = DynamicToolCatalog.DescribeArgs(info.InputSchema);
        _allowed = allowed;
    }

    public McpToolInfo ToInfo() => new() { Name = Name, Description = Description, InputSchema = InputSchema };
}

/// <summary>One server being edited.</summary>
public partial class McpServerItem : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _transport = McpTransportKind.Stdio;
    [ObservableProperty] private string? _command;
    /// <summary>One argument per line, so an argument with spaces needs no quoting.</summary>
    [ObservableProperty] private string _argsText = "";
    [ObservableProperty] private string? _url;
    [ObservableProperty] private string _toolsState = "Tools not loaded yet.";

    public ObservableCollection<McpKeyValueRow> Headers { get; } = [];
    public ObservableCollection<McpKeyValueRow> Env { get; } = [];
    public ObservableCollection<McpToolItem> Tools { get; } = [];
    public DateTime? ToolsLoadedAt { get; set; }

    public IReadOnlyList<string> Transports => McpTransportKind.All;
    public bool IsStdio => Transport == McpTransportKind.Stdio;
    public bool IsHttp => !IsStdio;
    public string ListLabel => (Enabled ? "● " : "○ ") + (string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name);
    public string AllowedSummary => Tools.Count == 0 ? "" : $"{Tools.Count(t => t.Allowed)} of {Tools.Count} allowed";

    partial void OnTransportChanged(string value)
    {
        OnPropertyChanged(nameof(IsStdio));
        OnPropertyChanged(nameof(IsHttp));
    }
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(ListLabel));

    [RelayCommand] private void AddHeader() => Headers.Add(new McpKeyValueRow());
    [RelayCommand] private void RemoveHeader(McpKeyValueRow? row) { if (row is not null) Headers.Remove(row); }
    [RelayCommand] private void AddEnv() => Env.Add(new McpKeyValueRow());
    [RelayCommand] private void RemoveEnv(McpKeyValueRow? row) { if (row is not null) Env.Remove(row); }
    [RelayCommand] private void AllowAllTools() { foreach (var t in Tools) t.Allowed = true; }
    [RelayCommand] private void DenyAllTools() { foreach (var t in Tools) t.Allowed = false; }
    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(ListLabel));

    public static McpServerItem From(McpServerConfig c)
    {
        var item = new McpServerItem
        {
            Name = c.Name,
            Enabled = c.Enabled,
            Transport = c.Transport,
            Command = c.Command,
            ArgsText = string.Join('\n', c.Args),
            Url = c.Url,
            ToolsLoadedAt = c.ToolsLoadedAt,
        };
        foreach (var (k, v) in c.Headers) item.Headers.Add(new McpKeyValueRow { Key = k, Value = v });
        foreach (var (k, v) in c.Env) item.Env.Add(new McpKeyValueRow { Key = k, Value = v });
        item.SetTools(c.Tools, c.DisabledTools);
        if (c.ToolsLoadedAt is { } at) item.ToolsState = $"{c.Tools.Count} tools, loaded {at.ToLocalTime():yyyy-MM-dd HH:mm}.";
        return item;
    }

    /// <summary>Show a fresh tool list, keeping the allow/deny choices made for names seen before.</summary>
    public void SetTools(IEnumerable<McpToolInfo> tools, IEnumerable<string> disabled)
    {
        foreach (var t in Tools) t.PropertyChanged -= OnToolChanged;
        Tools.Clear();
        var off = disabled.ToHashSet(StringComparer.Ordinal);
        foreach (var t in tools)
        {
            var item = new McpToolItem(t, !off.Contains(t.Name));
            item.PropertyChanged += OnToolChanged;
            Tools.Add(item);
        }
        OnPropertyChanged(nameof(AllowedSummary));
    }

    private void OnToolChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(McpToolItem.Allowed)) OnPropertyChanged(nameof(AllowedSummary));
    }

    public McpServerConfig ToConfig() => new()
    {
        Name = Name.Trim(),
        Enabled = Enabled,
        Transport = Transport,
        Command = string.IsNullOrWhiteSpace(Command) ? null : Command.Trim(),
        Args = ArgsText.Split('\n').Select(a => a.TrimEnd('\r')).Where(a => a.Trim().Length > 0).ToList(),
        Url = string.IsNullOrWhiteSpace(Url) ? null : Url.Trim(),
        Headers = Rows(Headers),
        Env = Rows(Env),
        Tools = Tools.Select(t => t.ToInfo()).ToList(),
        DisabledTools = Tools.Where(t => !t.Allowed).Select(t => t.Name).ToList(),
        ToolsLoadedAt = ToolsLoadedAt,
    };

    private static Dictionary<string, string> Rows(IEnumerable<McpKeyValueRow> rows)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
            if (!string.IsNullOrWhiteSpace(r.Key)) d[r.Key.Trim()] = r.Value ?? "";
        return d;
    }
}

/// <summary>
/// Settings → MCP: the servers the bot's AI mode may use, each with its tools and a per-tool
/// allow switch. Loading a server's tools is how the agent learns them — the list is stored in
/// <c>mcp-settings.json</c> and the next AI-mode session's prompt is built from it.
/// </summary>
public partial class McpSettingsViewModel : ObservableObject
{
    public ObservableCollection<McpServerItem> Servers { get; } = [];

    [ObservableProperty] private McpServerItem? _selectedServer;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _importText = "";
    [ObservableProperty] private McpToolItem? _testTool;
    [ObservableProperty] private string _testArgs = "{}";
    [ObservableProperty] private string _testResult = "";

    public bool HasSelection => SelectedServer is not null;
    public bool IsIdle => !IsBusy;

    partial void OnSelectedServerChanged(McpServerItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        TestTool = value?.Tools.FirstOrDefault();
        TestResult = "";
    }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    partial void OnTestToolChanged(McpToolItem? value) => TestArgs = SampleArgs(value?.InputSchema);

    private bool _saving;

    public McpSettingsViewModel()
    {
        Reload();
        // `-cli mcp import|refresh|allow|deny` saves from outside this page; show what it saved.
        McpSettingsStore.Saved += _ =>
        {
            if (_saving) return;
            Dispatcher.UIThread.Post(() =>
            {
                var selected = SelectedServer?.Name;
                Reload();
                SelectedServer = Servers.FirstOrDefault(s => s.Name == selected) ?? Servers.FirstOrDefault();
                Status = "Reloaded — the MCP settings were changed outside this page.";
            });
        };
    }

    private void Reload()
    {
        Servers.Clear();
        foreach (var s in McpSettingsStore.Load().Servers) Servers.Add(McpServerItem.From(s));
        SelectedServer = Servers.FirstOrDefault();
    }

    [RelayCommand]
    private void AddServer()
    {
        var name = UniqueName("server");
        var item = new McpServerItem { Name = name, Transport = McpTransportKind.Http };
        Servers.Add(item);
        SelectedServer = item;
        Status = "New server — fill in its URL (or command), then Load tools.";
    }

    [RelayCommand]
    private void RemoveServer()
    {
        if (SelectedServer is null) return;
        var name = SelectedServer.Name;
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.FirstOrDefault();
        Status = $"Removed {name} — Save to make it final.";
    }

    /// <summary>Paste the <c>mcpServers</c> block from <c>.mcp.json</c> / <c>~/.claude.json</c> / Claude Desktop.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        List<McpServerConfig> found;
        try { found = McpSettingsStore.ParseServersJson(ImportText); }
        catch (Exception ex)
        {
            Status = "Import: that is not an mcpServers JSON block — " + ex.Message;
            return;
        }
        if (found.Count == 0) { Status = "Import: no servers in that JSON."; return; }

        var added = new List<McpServerItem>();
        foreach (var cfg in found)
        {
            var existing = Servers.FirstOrDefault(s => string.Equals(s.Name, cfg.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) Servers.Remove(existing);
            var item = McpServerItem.From(cfg);
            Servers.Add(item);
            added.Add(item);
        }
        ImportText = "";
        SelectedServer = added[0];
        // Loading right away is the point of importing: the tools are what the agent needs.
        foreach (var item in added) await LoadToolsForAsync(item);
        Status = $"Imported {string.Join(", ", added.Select(a => a.Name))}. Review the tools, then Save.";
    }

    [RelayCommand]
    private async Task LoadToolsAsync()
    {
        if (SelectedServer is null) return;
        await LoadToolsForAsync(SelectedServer);
    }

    private async Task LoadToolsForAsync(McpServerItem item)
    {
        var cfg = item.ToConfig();
        if (cfg.Validate() is { } error) { item.ToolsState = error; Status = $"{item.Name}: {error}"; return; }
        IsBusy = true;
        item.ToolsState = $"Connecting ({cfg.Transport})…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var (tools, label) = await McpHub.LoadToolsAsync(cfg, cts.Token);
            item.SetTools(tools, cfg.DisabledTools);
            item.ToolsLoadedAt = DateTime.UtcNow;
            item.ToolsState = $"{label} — {tools.Count} tools. Untick a tool to keep the agent from calling it.";
            Status = $"{item.Name}: {tools.Count} tools loaded. Save to let the bot use them.";
            if (ReferenceEquals(item, SelectedServer)) TestTool = item.Tools.FirstOrDefault();
            AppLogger.Log($"[MCP] settings: {item.Name} → {tools.Count} tools ({label})");
        }
        catch (Exception ex)
        {
            item.ToolsState = "Could not load tools: " + ex.Message;
            Status = $"{item.Name}: " + ex.Message;
            AppLogger.Log($"[MCP] settings: {item.Name} load failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var settings = BuildSettings(out var error);
        if (error is not null) { Status = error; return; }
        try
        {
            _saving = true;
            try { McpSettingsStore.Save(settings); }
            finally { _saving = false; }
            await McpHub.Shared.ResetAsync();
            var allowed = McpHub.EnabledToolSpecs(settings).Count;
            Status = $"Saved — {settings.Servers.Count} server(s), {allowed} tool(s) the bot may call. " +
                     "AI mode picks them up from its next session (↻ New session in the bot).";
        }
        catch (Exception ex) { Status = "Save failed: " + ex.Message; }
    }

    /// <summary>Call the chosen tool with the edited (even unsaved) settings — what the agent's call would do.</summary>
    [RelayCommand]
    private async Task TestCallAsync()
    {
        if (SelectedServer is null || TestTool is null) { TestResult = "Load tools and pick one first."; return; }
        JsonObject args;
        try { args = JsonNode.Parse(string.IsNullOrWhiteSpace(TestArgs) ? "{}" : TestArgs) as JsonObject ?? new JsonObject(); }
        catch (JsonException ex) { TestResult = "Arguments are not a JSON object: " + ex.Message; return; }

        var cfg = SelectedServer.ToConfig();
        cfg.Enabled = true;
        cfg.DisabledTools.Remove(TestTool.Name); // a test is the person asking, not the agent
        IsBusy = true;
        TestResult = $"Calling {McpToolNames.Qualify(cfg.Name, TestTool.Name)}…";
        try
        {
            var hub = new McpHub(() => new McpSettings { Servers = [cfg] });
            await using (hub)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                var envelope = await hub.CallAsync(McpToolNames.Qualify(cfg.Name, TestTool.Name), args, cts.Token);
                TestResult = Pretty(envelope);
            }
        }
        catch (Exception ex) { TestResult = "Failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private McpSettings BuildSettings(out string? error)
    {
        error = null;
        var settings = new McpSettings();
        foreach (var item in Servers)
        {
            var cfg = item.ToConfig();
            if (cfg.Validate() is { } e) { error = $"{(string.IsNullOrWhiteSpace(cfg.Name) ? "A server" : cfg.Name)}: {e}"; return settings; }
            if (settings.Find(cfg.Name) is not null) { error = $"Two servers are named '{cfg.Name}'."; return settings; }
            settings.Servers.Add(cfg);
        }
        return settings;
    }

    private string UniqueName(string stem)
    {
        var name = stem;
        for (var i = 2; Servers.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); i++) name = stem + i;
        return name;
    }

    /// <summary>The tool's text field, unescaped, when the envelope has one — a JSON-escaped page is unreadable.</summary>
    private static string Pretty(string envelope)
    {
        try
        {
            if (JsonNode.Parse(envelope) is JsonObject o)
            {
                var ok = (bool?)o["ok"] ?? false;
                var text = (string?)o["text"] ?? (string?)o["error"] ?? envelope;
                var note = (string?)o["note"];
                return (ok ? "✓ ok\n\n" : "✗ failed\n\n") + text + (note is null ? "" : "\n\n(" + note + ")");
            }
        }
        catch (JsonException) { }
        return envelope;
    }

    /// <summary>A starting point for the test box: the required arguments with placeholder values.</summary>
    internal static string SampleArgs(string? schemaJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(schemaJson) || JsonNode.Parse(schemaJson) is not JsonObject schema) return "{}";
            var required = (schema["required"] as JsonArray)?.Select(r => r?.ToString()).ToHashSet() ?? [];
            var sample = new JsonObject();
            if (schema["properties"] is JsonObject props)
                foreach (var (name, p) in props)
                {
                    if (!required.Contains(name)) continue;
                    var type = p?["type"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : "";
                    sample[name] = type switch
                    {
                        "integer" or "number" => 1,
                        "boolean" => false,
                        "array" => new JsonArray(),
                        "object" => new JsonObject(),
                        _ => "",
                    };
                }
            return sample.ToJsonString(new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (JsonException) { return "{}"; }
    }
}

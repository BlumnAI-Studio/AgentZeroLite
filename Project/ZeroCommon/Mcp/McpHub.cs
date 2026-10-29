using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Common.Llm.Tools;

namespace Agent.Common.Mcp;

/// <summary>
/// The agent's way into the configured MCP servers. Two jobs, kept apart on purpose:
/// <list type="bullet">
/// <item><see cref="EnabledToolSpecs"/> answers "what may the agent call?" from the settings file
/// alone — the tool lists cached at the last "Load tools", minus disabled servers and tools — so
/// building a prompt never waits on a network.</item>
/// <item><see cref="CallAsync"/> connects on first use and keeps one client per server, rebuilt when
/// that server's settings change or its connection drops.</item>
/// </list>
/// The allow list is enforced here, at call time, and not only by leaving a tool out of the prompt:
/// a model can still name a tool it has heard of.
/// </summary>
public sealed class McpHub : IAsyncDisposable
{
    /// <summary>What reaches the model from one call; the rest is cut with a note saying so.</summary>
    public const int MaxResultChars = 12_000;

    public static McpHub Shared { get; } = new(McpSettingsStore.Load);

    private readonly Func<McpSettings> _settings;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, (McpClient Client, string Fingerprint)> _clients = new(StringComparer.OrdinalIgnoreCase);

    public McpHub(Func<McpSettings> settings) => _settings = settings;

    /// <summary>Every tool the agent may call right now, named <c>server.tool</c>.</summary>
    public IReadOnlyList<DynamicToolSpec> EnabledToolSpecs() => EnabledToolSpecs(_settings());

    /// <summary>The configured server named <paramref name="name"/>, as the settings file has it now.</summary>
    public McpServerConfig? FindServer(string name) => _settings().Find(name);

    public static IReadOnlyList<DynamicToolSpec> EnabledToolSpecs(McpSettings settings)
    {
        var list = new List<DynamicToolSpec>();
        foreach (var s in settings.Servers)
        {
            if (!s.Enabled || !McpToolNames.IsValidServerName(s.Name)) continue;
            foreach (var t in s.Tools)
            {
                if (!s.IsToolAllowed(t.Name) || !McpToolNames.IsUsableToolName(t.Name)) continue;
                list.Add(new DynamicToolSpec(McpToolNames.Qualify(s.Name, t.Name), t.Description, t.InputSchema));
            }
        }
        return list;
    }

    /// <summary>
    /// Run <paramref name="qualifiedName"/> (<c>memorizer.search</c>) with the model's arguments and
    /// return the JSON envelope the loop feeds back: <c>{ok, server, tool, text}</c> or <c>{ok:false, error}</c>.
    /// Never throws for a server-side failure — a broken server is something to tell the model about.
    /// </summary>
    public async Task<string> CallAsync(string qualifiedName, JsonObject args, CancellationToken ct)
    {
        if (!McpToolNames.TrySplit(qualifiedName, out var serverName, out var toolName))
            return Fail($"'{qualifiedName}' is not an MCP tool name (expected <server>.<tool>)");

        var settings = _settings();
        var server = settings.Find(serverName);
        if (server is null) return Fail($"no MCP server named '{serverName}' is configured");
        if (!server.Enabled) return Fail($"MCP server '{serverName}' is turned off in Settings → MCP");
        var tool = server.Tools.FirstOrDefault(t => t.Name == toolName);
        if (tool is null) return Fail($"'{serverName}' has no loaded tool named '{toolName}' (Settings → MCP → Load tools)");
        if (!server.IsToolAllowed(toolName)) return Fail($"tool '{qualifiedName}' is not allowed (Settings → MCP)");

        var coerced = DynamicToolCatalog.CoerceArgs(args, tool.InputSchema);
        var started = DateTime.UtcNow;
        for (var attempt = 0; ; attempt++)
        {
            McpClient client;
            try { client = await GetClientAsync(server, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                AppLogger.Log($"[MCP:{serverName}] connect failed: {ex.GetType().Name}: {ex.Message}");
                return Fail($"could not connect to MCP server '{serverName}': {ex.Message}");
            }

            try
            {
                var result = await client.CallToolAsync(toolName, coerced, ct);
                var ms = (DateTime.UtcNow - started).TotalMilliseconds;
                AppLogger.Log($"[MCP:{serverName}] {toolName} → {(result.IsError ? "error" : "ok")} {result.Text.Length} chars in {ms:0} ms");
                return Envelope(serverName, toolName, result);
            }
            catch (McpException ex)
            {
                return Fail($"{qualifiedName}: {ex.Message} (code {ex.Code})");
            }
            catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested && ex is IOException or HttpRequestException)
            {
                // The connection went stale (server restarted, SSE stream timed out). Drop it and retry once.
                AppLogger.Log($"[MCP:{serverName}] {toolName} failed on a stale connection ({ex.Message}) — reconnecting once");
                await DropAsync(serverName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                AppLogger.Log($"[MCP:{serverName}] {toolName} failed: {ex.GetType().Name}: {ex.Message}");
                return Fail($"{qualifiedName} failed: {ex.Message}");
            }
        }
    }

    internal static string Envelope(string server, string tool, McpCallResult result)
    {
        var text = result.Text;
        var truncated = text.Length > MaxResultChars;
        if (truncated) text = text[..MaxResultChars];
        var o = new JsonObject
        {
            ["ok"] = !result.IsError,
            ["server"] = server,
            ["tool"] = tool,
            ["text"] = text,
        };
        if (truncated) o["note"] = $"result cut at {MaxResultChars} of {result.Text.Length} chars — ask the tool for less (a smaller limit, a narrower query)";
        if (result.IsError) o["error"] = "the tool reported an error; the text says why";
        return o.ToJsonString(ToolJson.Options);
    }

    private static string Fail(string error) => ToolJson.Fail(error);

    private static string Fingerprint(McpServerConfig s)
        => JsonSerializer.Serialize(new { s.Transport, s.Command, s.Args, s.Env, s.Url, s.Headers });

    private async Task<McpClient> GetClientAsync(McpServerConfig server, CancellationToken ct)
    {
        var fp = Fingerprint(server);
        await _lock.WaitAsync(ct);
        try
        {
            if (_clients.TryGetValue(server.Name, out var existing))
            {
                if (existing.Fingerprint == fp && existing.Client.IsConnected) return existing.Client;
                _clients.Remove(server.Name);
                await existing.Client.DisposeAsync();
            }
            var client = await McpClient.ConnectAsync(server, ct);
            _clients[server.Name] = (client, fp);
            return client;
        }
        finally { _lock.Release(); }
    }

    private async Task DropAsync(string serverName)
    {
        await _lock.WaitAsync();
        try
        {
            if (_clients.Remove(serverName, out var e)) await e.Client.DisposeAsync();
        }
        finally { _lock.Release(); }
    }

    /// <summary>Close every connection (settings saved, app closing). The next call reconnects.</summary>
    public async Task ResetAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var (_, e) in _clients)
                try { await e.Client.DisposeAsync(); } catch { }
            _clients.Clear();
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// For the settings page: connect with <paramref name="cfg"/> as edited (saved or not), list its
    /// tools, disconnect. Throws with the server's own words when it cannot.
    /// </summary>
    public static async Task<(IReadOnlyList<McpToolInfo> Tools, string ServerLabel)> LoadToolsAsync(McpServerConfig cfg, CancellationToken ct)
    {
        await using var client = await McpClient.ConnectAsync(cfg, ct);
        var tools = await client.ListToolsAsync(ct);
        var label = string.Join(' ', new[] { client.ServerName, client.ServerVersion }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return (tools, $"{(label.Length > 0 ? label : cfg.Name)} · MCP {client.ProtocolVersion}");
    }

    public async ValueTask DisposeAsync() => await ResetAsync();
}

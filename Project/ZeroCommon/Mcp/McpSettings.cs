using Agent.Common.Platform;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agent.Common.Security;

namespace Agent.Common.Mcp;

/// <summary>How a client reaches an MCP server.</summary>
public static class McpTransportKind
{
    /// <summary>A child process speaking newline-delimited JSON-RPC on stdin/stdout.</summary>
    public const string Stdio = "stdio";
    /// <summary>The 2024-11-05 HTTP+SSE transport: GET an event stream, POST to the endpoint it names.</summary>
    public const string Sse = "sse";
    /// <summary>The 2025-03-26 Streamable HTTP transport: POST every message to one URL.</summary>
    public const string Http = "http";

    public static readonly IReadOnlyList<string> All = [Stdio, Sse, Http];

    /// <summary>Claude Code / Desktop / Cursor spell the HTTP one "http" or "streamable-http", and omit the type for stdio.</summary>
    public static string Normalize(string? type, bool hasUrl)
    {
        var t = (type ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "sse" => Sse,
            "http" or "streamable-http" or "streamablehttp" or "streamable_http" => Http,
            "stdio" => Stdio,
            _ => hasUrl ? Http : Stdio,
        };
    }
}

/// <summary>One tool a server advertised the last time its tools were loaded.</summary>
public sealed class McpToolInfo
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>The tool's <c>inputSchema</c> as raw JSON, kept verbatim for the prompt and argument coercion.</summary>
    public string? InputSchema { get; set; }
}

/// <summary>
/// One MCP server the agent may use. The tool list is a cache of what the server said at the
/// last "Load tools" — it is what the agent's prompt is built from, so a run does not have to
/// connect to every server just to learn what it could call. Allow/deny is per tool name and
/// stored as the deny list, so a tool the server adds later starts allowed.
/// </summary>
public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Transport { get; set; } = McpTransportKind.Stdio;

    // stdio
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    /// <summary>Extra environment for the child. Values are sealed at rest (they are usually tokens).</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    // sse / http
    public string? Url { get; set; }
    /// <summary>Request headers. Values are sealed at rest (they are usually API keys).</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    public List<string> DisabledTools { get; set; } = [];
    public List<McpToolInfo> Tools { get; set; } = [];
    public DateTime? ToolsLoadedAt { get; set; }

    public bool IsToolAllowed(string toolName) => !DisabledTools.Contains(toolName, StringComparer.Ordinal);

    /// <summary>Null when the server can be connected to; otherwise what is missing.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "A name is required.";
        if (!McpToolNames.IsValidServerName(Name)) return "The name may use letters, digits, '_' and '-' only (it prefixes every tool name).";
        if (Transport == McpTransportKind.Stdio)
            return string.IsNullOrWhiteSpace(Command) ? "A stdio server needs a command." : null;
        if (string.IsNullOrWhiteSpace(Url)) return "An HTTP server needs a URL.";
        return Uri.TryCreate(Url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? null
            : "The URL must be an absolute http(s) URL.";
    }

    public McpServerConfig Clone()
    {
        var json = JsonSerializer.Serialize(this, McpSettingsStore.JsonOpts);
        return JsonSerializer.Deserialize<McpServerConfig>(json, McpSettingsStore.JsonOpts)!;
    }
}

public sealed class McpSettings
{
    public List<McpServerConfig> Servers { get; set; } = [];

    public McpServerConfig? Find(string name)
        => Servers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// <c>%LOCALAPPDATA%\AgentZeroLite\mcp-settings.json</c>. Header and env values go through
/// <see cref="SecretProtection"/> like the API keys in <c>llm-settings.json</c>: they are where
/// an MCP server's token lives (memorizer's <c>X-API-Key</c>, a GitHub PAT in <c>env</c>).
/// </summary>
public static class McpSettingsStore
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Overridable for tests; the hosts use the default.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        AppPaths.DataRoot,
        "mcp-settings.json");

    /// <summary>Raised after <see cref="Save"/> so a host can drop connections built from the old settings.</summary>
    public static event Action<McpSettings>? Saved;

    public static McpSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new McpSettings();
            var settings = JsonSerializer.Deserialize<McpSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new McpSettings();
            foreach (var s in settings.Servers)
            {
                s.Headers = MapValues(s.Headers ?? [], SecretProtection.Unprotect);
                s.Env = MapValues(s.Env ?? [], SecretProtection.Unprotect);
                s.Args ??= [];
                s.DisabledTools ??= [];
                s.Tools ??= [];
                s.Transport = McpTransportKind.Normalize(s.Transport, !string.IsNullOrWhiteSpace(s.Url));
            }
            return settings;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[MCP] settings could not be read ({ex.GetType().Name}: {ex.Message}) — starting empty");
            return new McpSettings();
        }
    }

    public static void Save(McpSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // Seal a copy: the caller's object keeps plaintext.
        var sealedCopy = new McpSettings
        {
            Servers = settings.Servers.Select(s =>
            {
                var c = s.Clone();
                c.Headers = MapValues(s.Headers, SecretProtection.Protect);
                c.Env = MapValues(s.Env, SecretProtection.Protect);
                return c;
            }).ToList(),
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(sealedCopy, JsonOpts));
        try { Saved?.Invoke(settings); } catch (Exception ex) { AppLogger.Log($"[MCP] Saved handler failed: {ex.Message}"); }
    }

    private static Dictionary<string, string> MapValues(Dictionary<string, string> source, Func<string?, string> map)
        => source.ToDictionary(kv => kv.Key, kv => map(kv.Value));

    /// <summary>
    /// Reads the <c>mcpServers</c> block every MCP host shares (Claude Code's <c>.mcp.json</c> and
    /// <c>~/.claude.json</c>, Claude Desktop, Cursor): <c>{"mcpServers":{"name":{"type","url","headers",
    /// "command","args","env"}}}</c>. A bare <c>{"name":{…}}</c> map is accepted too, so either half of a
    /// copied snippet works. Returns the servers found; throws <see cref="JsonException"/> on bad JSON.
    /// </summary>
    public static List<McpServerConfig> ParseServersJson(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("expected a JSON object");
        var map = root["mcpServers"] as JsonObject ?? root;
        var result = new List<McpServerConfig>();
        foreach (var (name, node) in map)
        {
            if (node is not JsonObject o) continue;
            var url = (string?)o["url"];
            var cfg = new McpServerConfig
            {
                Name = McpToolNames.SanitizeServerName(name),
                Transport = McpTransportKind.Normalize((string?)o["type"] ?? (string?)o["transport"], !string.IsNullOrWhiteSpace(url)),
                Url = url,
                Command = (string?)o["command"],
                Enabled = !((bool?)o["disabled"] ?? false),
            };
            if (o["args"] is JsonArray args) cfg.Args = args.Select(a => a?.ToString() ?? "").ToList();
            if (o["env"] is JsonObject env) cfg.Env = env.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
            if (o["headers"] is JsonObject headers) cfg.Headers = headers.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
            result.Add(cfg);
        }
        return result;
    }
}

/// <summary>The agent addresses an MCP tool as <c>&lt;server&gt;.&lt;tool&gt;</c>; server names carry no dot, so the first dot splits.</summary>
public static class McpToolNames
{
    public static bool IsValidServerName(string name)
        => name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static string SanitizeServerName(string name)
    {
        var chars = (name ?? "").Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-').ToArray();
        var s = new string(chars).Trim('-');
        return s.Length == 0 ? "server" : s;
    }

    /// <summary>
    /// The tool part is kept to what the GBNF literal and a model's JSON can carry
    /// verbatim; MCP names are <c>[A-Za-z0-9_.-]</c> by the spec's recommendation anyway.
    /// </summary>
    public static bool IsUsableToolName(string name)
        => name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    public static string Qualify(string server, string tool) => server + "." + tool;

    public static bool TrySplit(string qualified, out string server, out string tool)
    {
        var dot = qualified.IndexOf('.');
        server = dot > 0 ? qualified[..dot] : "";
        tool = dot > 0 ? qualified[(dot + 1)..] : "";
        return dot > 0 && tool.Length > 0;
    }
}

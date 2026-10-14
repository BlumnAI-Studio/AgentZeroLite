using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Common.Mcp;

/// <summary>A JSON-RPC error the server returned.</summary>
public sealed class McpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>What one <c>tools/call</c> produced, flattened to text for a model.</summary>
public sealed record McpCallResult(bool IsError, string Text, JsonNode? StructuredContent);

/// <summary>
/// A minimal MCP client: <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c>, and polite
/// answers to the server's own <c>ping</c>. Written here rather than taken from a package for
/// the same reason the terminal's pseudo-console is: three transports and a request table are
/// small, and owning them keeps ZeroCommon free of a dependency the Store build would have to
/// certify. Thread-safe: concurrent requests are matched by id.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    /// <summary>The newest revision this client speaks; a server may answer with an older one.</summary>
    public const string LatestProtocolVersion = "2025-06-18";

    private readonly IMcpTransport _transport;
    private readonly string _name;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private long _nextId;
    private Task? _readLoop;
    private volatile bool _closed;

    public string? ServerName { get; private set; }
    public string? ServerVersion { get; private set; }
    public string? ProtocolVersion { get; private set; }
    public string? Instructions { get; private set; }
    public bool IsConnected => !_closed;

    public McpClient(IMcpTransport transport, string name)
    {
        _transport = transport;
        _name = name;
    }

    /// <summary>Start a transport for <paramref name="cfg"/>, initialize, and return a ready client.</summary>
    public static async Task<McpClient> ConnectAsync(McpServerConfig cfg, CancellationToken ct, TimeSpan? timeout = null)
    {
        var error = cfg.Validate();
        if (error is not null) throw new InvalidOperationException(error);

        var transport = McpTransportFactory.Create(cfg);
        var client = new McpClient(transport, cfg.Name);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            await client.StartAsync(cts.Token);
            AppLogger.Log($"[MCP:{cfg.Name}] connected — {transport.Describe} · server={client.ServerName} {client.ServerVersion} · protocol={client.ProtocolVersion}");
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _transport.StartAsync(ct);
        _readLoop = Task.Run(ReadLoopAsync);

        var version = typeof(McpClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0";
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = LatestProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "AgentZeroLite", ["version"] = version.Split('+')[0] },
        }, ct);

        ProtocolVersion = (string?)result?["protocolVersion"] ?? LatestProtocolVersion;
        ServerName = (string?)result?["serverInfo"]?["name"];
        ServerVersion = (string?)result?["serverInfo"]?["version"];
        Instructions = (string?)result?["instructions"];
        _transport.ProtocolVersion = ProtocolVersion;
        await NotifyAsync("notifications/initialized", null, ct);
    }

    /// <summary>Every tool the server lists, following <c>nextCursor</c> pages.</summary>
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        for (var page = 0; page < 50; page++)
        {
            var result = await RequestAsync("tools/list", cursor is null ? null : new JsonObject { ["cursor"] = cursor }, ct);
            if (result?["tools"] is JsonArray arr)
                foreach (var t in arr.OfType<JsonObject>())
                {
                    var name = (string?)t["name"];
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    tools.Add(new McpToolInfo
                    {
                        Name = name,
                        Description = (string?)t["description"],
                        InputSchema = t["inputSchema"] is { } schema ? McpWire.Serialize(schema) : null,
                    });
                }
            cursor = (string?)result?["nextCursor"];
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return tools;
    }

    public async Task<McpCallResult> CallToolAsync(string name, JsonObject? args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = args?.DeepClone() ?? new JsonObject(),
        }, ct, timeout ?? TimeSpan.FromSeconds(120));
        return ToCallResult(result);
    }

    /// <summary>Text items verbatim, everything else as a short marker a model can still reason about.</summary>
    internal static McpCallResult ToCallResult(JsonNode? result)
    {
        var isError = (bool?)result?["isError"] ?? false;
        var sb = new StringBuilder();
        if (result?["content"] is JsonArray content)
            foreach (var item in content.OfType<JsonObject>())
            {
                if (sb.Length > 0) sb.Append('\n');
                switch ((string?)item["type"])
                {
                    case "text": sb.Append((string?)item["text"]); break;
                    case "image": sb.Append($"[image {(string?)item["mimeType"]}]"); break;
                    case "audio": sb.Append($"[audio {(string?)item["mimeType"]}]"); break;
                    case "resource_link": sb.Append($"[resource {(string?)item["uri"]} {(string?)item["name"]}]".TrimEnd()); break;
                    case "resource":
                        var res = item["resource"];
                        sb.Append((string?)res?["text"] ?? $"[resource {(string?)res?["uri"]}]");
                        break;
                    default: sb.Append(item.ToJsonString()); break;
                }
            }
        var structured = result?["structuredContent"];
        if (sb.Length == 0 && structured is not null) sb.Append(structured.ToJsonString());
        return new McpCallResult(isError, sb.ToString(), structured?.DeepClone());
    }

    private async Task<JsonNode?> RequestAsync(string method, JsonObject? @params, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (_closed) throw new IOException($"MCP server '{_name}' is disconnected");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (@params is not null) msg["params"] = @params;
            await _transport.SendAsync(McpWire.Serialize(msg), ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
            try { return await tcs.Task.WaitAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"MCP server '{_name}' did not answer {method} within {(timeout ?? TimeSpan.FromSeconds(60)).TotalSeconds:0}s");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null) msg["params"] = @params;
        return _transport.SendAsync(McpWire.Serialize(msg), ct);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var raw in _transport.Incoming.ReadAllAsync())
            {
                JsonObject? msg;
                try { msg = JsonNode.Parse(raw) as JsonObject; }
                catch (JsonException)
                {
                    AppLogger.Log($"[MCP:{_name}] ignored a non-JSON line: {(raw.Length > 120 ? raw[..120] + "…" : raw)}");
                    continue;
                }
                if (msg is null) continue;

                var method = (string?)msg["method"];
                var idNode = msg["id"];
                if (method is null && idNode is not null)
                {
                    if (!TryReadId(idNode, out var id) || !_pending.TryGetValue(id, out var tcs)) continue;
                    if (msg["error"] is JsonObject err)
                        tcs.TrySetException(new McpException((int?)err["code"] ?? 0, (string?)err["message"] ?? "error"));
                    else
                        tcs.TrySetResult(msg["result"]?.DeepClone());
                }
                else if (method is not null && idNode is not null)
                {
                    // A request from the server. Only ping is part of what this client offers.
                    var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone() };
                    if (method == "ping") reply["result"] = new JsonObject();
                    else reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"method not supported: {method}" };
                    try { await _transport.SendAsync(McpWire.Serialize(reply), CancellationToken.None); } catch { }
                }
                // Notifications (progress, list_changed, log messages) need no answer.
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[MCP:{_name}] read loop stopped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _closed = true;
            foreach (var (_, tcs) in _pending) tcs.TrySetException(new IOException($"MCP server '{_name}' closed the connection"));
        }
    }

    private static bool TryReadId(JsonNode node, out long id)
    {
        id = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<long>(out id)) return true;
        return v.TryGetValue<string>(out var s) && long.TryParse(s, out id);
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        await _transport.DisposeAsync();
        if (_readLoop is not null)
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }
}

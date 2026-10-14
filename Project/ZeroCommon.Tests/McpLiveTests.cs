using System.Text.Json.Nodes;
using Agent.Common.Mcp;
using Xunit.Abstractions;

namespace ZeroCommon.Tests;

/// <summary>
/// Against a real MCP server, when one is named — otherwise each test returns at once.
/// Point <c>AGENTZERO_MCP_LIVE_FILE</c> at a file holding an <c>mcpServers</c> snippet (the shape
/// Claude Code keeps in <c>.mcp.json</c>); the first server in it is used. A file, not the
/// variable's value, so a key never lands in a shell history or a CI log.
/// <c>AGENTZERO_MCP_LIVE_CALL</c> (optional) names a tool and JSON args: <c>search {"query":"actor"}</c>.
/// </summary>
public sealed class McpLiveTests(ITestOutputHelper output)
{
    private static McpServerConfig? LiveServer()
    {
        var path = Environment.GetEnvironmentVariable("AGENTZERO_MCP_LIVE_FILE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return McpSettingsStore.ParseServersJson(File.ReadAllText(path)).FirstOrDefault();
    }

    [Fact]
    public async Task Lists_tools_and_calls_one()
    {
        var cfg = LiveServer();
        if (cfg is null) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var (tools, label) = await McpHub.LoadToolsAsync(cfg, cts.Token);
        output.WriteLine($"{cfg.Name} ({cfg.Transport}) — {label} — {tools.Count} tools");
        foreach (var t in tools) output.WriteLine($"  {t.Name}: {Agent.Common.Llm.Tools.DynamicToolCatalog.DescribeArgs(t.InputSchema)}");
        Assert.NotEmpty(tools);

        var call = Environment.GetEnvironmentVariable("AGENTZERO_MCP_LIVE_CALL");
        if (string.IsNullOrWhiteSpace(call)) return;
        var space = call.IndexOf(' ');
        var toolName = space < 0 ? call : call[..space];
        var args = space < 0 ? new JsonObject() : JsonNode.Parse(call[(space + 1)..])!.AsObject();

        // Through the hub, as the agent would: allow list, coercion, envelope.
        cfg.Tools = tools.ToList();
        var hub = new McpHub(() => new McpSettings { Servers = [cfg] });
        await using var _ = hub;
        var envelope = await hub.CallAsync(McpToolNames.Qualify(cfg.Name, toolName), args, cts.Token);
        output.WriteLine(envelope.Length > 3000 ? envelope[..3000] + "…" : envelope);
        Assert.Contains("\"ok\":true", envelope);
    }
}

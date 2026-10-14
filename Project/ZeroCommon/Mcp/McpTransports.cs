using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Agent.Common.Mcp;

/// <summary>
/// Moves JSON-RPC messages between <see cref="McpClient"/> and one server. Every transport
/// delivers what the server sends — responses, server requests, notifications — through
/// <see cref="Incoming"/>, so the client has one reader whichever way the bytes travel.
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    Task SendAsync(string json, CancellationToken ct);
    ChannelReader<string> Incoming { get; }
    /// <summary>For the log: "stdio npx …", "sse https://…".</summary>
    string Describe { get; }
    /// <summary>Set by the client once initialize settled a version; the HTTP transport echoes it as a header.</summary>
    string? ProtocolVersion { get; set; }
}

internal static class McpTransportFactory
{
    public static IMcpTransport Create(McpServerConfig cfg) => cfg.Transport switch
    {
        McpTransportKind.Stdio => new StdioMcpTransport(cfg),
        McpTransportKind.Sse => new SseMcpTransport(cfg),
        _ => new StreamableHttpMcpTransport(cfg),
    };

    public static HttpClient CreateHttpClient(McpServerConfig cfg)
    {
        // No overall timeout: an event stream stays open for the life of the connection.
        // Each request is bounded by the caller's token instead.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var (k, v) in cfg.Headers)
            if (!string.IsNullOrWhiteSpace(k)) http.DefaultRequestHeaders.TryAddWithoutValidation(k.Trim(), v);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentZeroLite-MCP/1.0");
        return http;
    }
}

/// <summary>Reads <c>text/event-stream</c> framing: <c>event:</c> / <c>data:</c> lines, a blank line ends an event.</summary>
internal static class SseReader
{
    public static async Task ReadAsync(Stream stream, Func<string?, string, Task> onEvent, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? evt = null;
        var data = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (data.Length > 0) await onEvent(evt, data.ToString());
                evt = null;
                data.Clear();
                continue;
            }
            if (line[0] == ':') continue; // comment / keep-alive
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (field == "event") evt = value;
            else if (field == "data")
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(value);
            }
        }
        if (data.Length > 0) await onEvent(evt, data.ToString());
    }
}

/// <summary>A child process; one JSON-RPC message per line on stdin and stdout, stderr is the server's log.</summary>
internal sealed class StdioMcpTransport : IMcpTransport
{
    private readonly McpServerConfig _cfg;
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private Process? _process;
    private StreamWriter? _stdin;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public StdioMcpTransport(McpServerConfig cfg) => _cfg = cfg;

    public ChannelReader<string> Incoming => _incoming.Reader;
    public string Describe => $"stdio {_cfg.Command} {string.Join(' ', _cfg.Args)}".TrimEnd();
    public string? ProtocolVersion { get; set; }

    public Task StartAsync(CancellationToken ct)
    {
        var psi = BuildStartInfo(_cfg.Command!, _cfg.Args);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        foreach (var (k, v) in _cfg.Env)
            if (!string.IsNullOrWhiteSpace(k)) psi.Environment[k.Trim()] = v;

        _process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {_cfg.Command}");
        // The child reads UTF-8 without a BOM; StandardInput would use the console code page.
        _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        var name = _cfg.Name;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync() is { } line)
                    if (line.Length > 0) await _incoming.Writer.WriteAsync(line);
            }
            catch { /* the process went away */ }
            finally { _incoming.Writer.TryComplete(); }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync() is { } line)
                    if (line.Length > 0) AppLogger.Log($"[MCP:{name}] stderr: {(line.Length > 300 ? line[..300] + "…" : line)}");
            }
            catch { }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// On Windows <c>npx</c> / <c>uvx</c> are <c>.cmd</c> shims, and a process started without a shell
    /// cannot run a batch file by bare name (the same trap the settings installer measured). Such a
    /// command is resolved on PATH and run through <c>cmd.exe /d /s /c</c>.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string command, IReadOnlyList<string> args)
    {
        if (OperatingSystem.IsWindows())
        {
            var resolved = ResolveOnPath(command);
            var ext = Path.GetExtension(resolved ?? command).ToLowerInvariant();
            if (ext is ".cmd" or ".bat" || (resolved is null && ext.Length == 0))
            {
                var line = Quote(resolved ?? command) + (args.Count > 0 ? " " + string.Join(' ', args.Select(Quote)) : "");
                return new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{line}\"" };
            }
            command = resolved ?? command;
        }
        var psi = new ProcessStartInfo(command);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static string Quote(string s)
        => s.Length > 0 && !s.Any(c => char.IsWhiteSpace(c) || c == '"') ? s : "\"" + s.Replace("\"", "\\\"") + "\"";

    internal static string? ResolveOnPath(string command)
    {
        if (Path.IsPathRooted(command)) return File.Exists(command) ? command : null;
        var exts = Path.HasExtension(command)
            ? new[] { "" }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), command + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* a malformed PATH entry */ }
            }
        return null;
    }

    public async Task SendAsync(string json, CancellationToken ct)
    {
        if (_stdin is null || _process is null || _process.HasExited) throw new IOException("the MCP server process is not running");
        await _writeLock.WaitAsync(ct);
        try { await _stdin.WriteLineAsync(json.AsMemory(), ct); }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        var p = _process;
        if (p is null) return;
        try { _stdin?.Dispose(); } catch { }
        try
        {
            if (!p.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
            }
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch { }
        p.Dispose();
        _incoming.Writer.TryComplete();
    }
}

/// <summary>
/// The 2024-11-05 HTTP+SSE transport: the client GETs an event stream, the first <c>endpoint</c>
/// event names the URL to POST messages to, and every answer comes back on the stream.
/// </summary>
internal sealed class SseMcpTransport : IMcpTransport
{
    private readonly McpServerConfig _cfg;
    private readonly HttpClient _http;
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<Uri> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _life = new();

    public SseMcpTransport(McpServerConfig cfg)
    {
        _cfg = cfg;
        _http = McpTransportFactory.CreateHttpClient(cfg);
    }

    public ChannelReader<string> Incoming => _incoming.Reader;
    public string Describe => $"sse {_cfg.Url}";
    public string? ProtocolVersion { get; set; }

    public async Task StartAsync(CancellationToken ct)
    {
        var baseUri = new Uri(_cfg.Url!);
        var req = new HttpRequestMessage(HttpMethod.Get, baseUri);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeBodyAsync(resp);
            resp.Dispose();
            throw new HttpRequestException($"GET {baseUri} → {(int)resp.StatusCode} {resp.ReasonPhrase}{body}", null, resp.StatusCode);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using (resp)
                await using (var stream = await resp.Content.ReadAsStreamAsync(_life.Token))
                    await SseReader.ReadAsync(stream, async (evt, data) =>
                    {
                        if (evt == "endpoint")
                        {
                            if (Uri.TryCreate(baseUri, data.Trim(), out var ep)) _endpoint.TrySetResult(ep);
                        }
                        else if (evt is null or "message")
                        {
                            await _incoming.Writer.WriteAsync(data);
                        }
                    }, _life.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Log($"[MCP:{_cfg.Name}] event stream ended: {ex.GetType().Name}: {ex.Message}");
            }
            catch (OperationCanceledException) { }
            finally
            {
                _endpoint.TrySetException(new IOException("the event stream closed before naming an endpoint"));
                _incoming.Writer.TryComplete();
            }
        });

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(20));
        await _endpoint.Task.WaitAsync(wait.Token);
    }

    public async Task SendAsync(string json, CancellationToken ct)
    {
        var ep = await _endpoint.Task.WaitAsync(ct);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(ep, content, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"POST {ep.AbsolutePath} → {(int)resp.StatusCode} {resp.ReasonPhrase}{await SafeBodyAsync(resp)}", null, resp.StatusCode);
    }

    internal static async Task<string> SafeBodyAsync(HttpResponseMessage resp)
    {
        try
        {
            var s = (await resp.Content.ReadAsStringAsync()).Trim();
            return s.Length == 0 ? "" : ": " + (s.Length > 300 ? s[..300] + "…" : s);
        }
        catch { return ""; }
    }

    public ValueTask DisposeAsync()
    {
        try { _life.Cancel(); } catch { }
        _http.Dispose();
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The 2025-03-26 Streamable HTTP transport: every message is a POST to one URL; the answer is
/// a JSON body or a short event stream on that same response. The session id the server hands
/// out on initialize is echoed on every later request.
/// </summary>
internal sealed class StreamableHttpMcpTransport : IMcpTransport
{
    private readonly McpServerConfig _cfg;
    private readonly HttpClient _http;
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _life = new();
    private string? _sessionId;

    public StreamableHttpMcpTransport(McpServerConfig cfg)
    {
        _cfg = cfg;
        _http = McpTransportFactory.CreateHttpClient(cfg);
    }

    public ChannelReader<string> Incoming => _incoming.Reader;
    public string Describe => $"http {_cfg.Url}";
    public string? ProtocolVersion { get; set; }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task SendAsync(string json, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_sessionId is not null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (ProtocolVersion is not null) req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);

        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _sessionId = ids.FirstOrDefault() ?? _sessionId;

        if (!resp.IsSuccessStatusCode)
        {
            var body = await SseMcpTransport.SafeBodyAsync(resp);
            resp.Dispose();
            throw new HttpRequestException($"POST {_cfg.Url} → {(int)resp.StatusCode} {resp.ReasonPhrase}{body}", null, resp.StatusCode);
        }
        if (resp.StatusCode == HttpStatusCode.Accepted || resp.Content.Headers.ContentLength == 0)
        {
            resp.Dispose();
            return;
        }

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // Read the answer off the stream in the background: the client is waiting on the
            // request id, not on this call, and the server may close the stream late.
            _ = Task.Run(async () =>
            {
                try
                {
                    using (resp)
                    await using (var stream = await resp.Content.ReadAsStreamAsync(_life.Token))
                        await SseReader.ReadAsync(stream, async (evt, data) =>
                        {
                            if (evt is null or "message") await _incoming.Writer.WriteAsync(data);
                        }, _life.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLogger.Log($"[MCP:{_cfg.Name}] response stream ended: {ex.GetType().Name}: {ex.Message}");
                }
                catch (OperationCanceledException) { }
            });
            return;
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return;
            // A batch answer is an array; hand the reader one message at a time.
            if (body.TrimStart().StartsWith('[') && JsonNode.Parse(body) is JsonArray batch)
                foreach (var item in batch) { if (item is not null) await _incoming.Writer.WriteAsync(item.ToJsonString(), ct); }
            else
                await _incoming.Writer.WriteAsync(body, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _life.Cancel(); } catch { }
        if (_sessionId is not null)
        {
            // Polite session end; a server that does not support it answers 405.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var req = new HttpRequestMessage(HttpMethod.Delete, _cfg.Url);
                req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
                using var _ = await _http.SendAsync(req, cts.Token);
            }
            catch { }
        }
        _http.Dispose();
        _incoming.Writer.TryComplete();
    }
}

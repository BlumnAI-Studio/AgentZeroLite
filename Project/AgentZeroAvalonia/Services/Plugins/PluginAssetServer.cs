using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Common;
using Agent.Common.Browser;
using Agent.Common.Mp3;
using Agent.Common.Platform;

namespace AgentZeroAvalonia.Services.Plugins;

/// <summary>
/// Serves WebDev plugins to the Plugins page's web views over loopback — the Avalonia
/// counterpart of the three origins the WPF host maps inside WebView2:
/// <list type="bullet">
/// <item><c>https://zero.local/</c> → <c>{base}zero/</c>: the exe's <c>Wasm/</c> folder (built-in
/// samples and <c>common/zero-bridge.js</c>, linked byte-identical from the WPF project).</item>
/// <item><c>https://plugin.local/</c> → <c>{base}plugin/</c>: installed plugins under
/// <c>%LOCALAPPDATA%\AgentZeroLite\Wasm\plugins</c> — the same folder, so a plugin installed
/// from either host shows up in both.</item>
/// <item><c>https://mp3.local/</c> → <c>{base}mp3/</c>: the MP3 scan root with HTTP Range
/// (<see cref="Mp3Resource"/>, the rules the WPF handler uses) and <c>__cover/&lt;id&gt;</c>.</item>
/// </list>
/// <c>NativeWebView</c> can neither map a folder nor answer a request (see
/// <see cref="LocalAssetServer"/>), so plugins are fetched from here. Plugins hard-code those
/// https origins (<c>&lt;script src="https://zero.local/common/zero-bridge.js"&gt;</c>,
/// <c>'https://mp3.local/' + path</c>), so text responses are served with the three origins
/// rewritten to this server, and every HTML page gets <c>zero-webview-shim.js</c> as its first
/// script — that is the whole compatibility layer; no plugin file is edited on disk.
///
/// <para>One server for the app. Its port is remembered in <c>plugin-host.json</c> and reused
/// on the next start, because the page's origin includes the port and a plugin's
/// <c>localStorage</c> lives under that origin — an ephemeral port would empty it every run.
/// The path token is fresh per run; it is not part of the origin.</para>
/// </summary>
internal sealed class PluginAssetServer : IDisposable
{
    public const string ZeroMount = "zero";
    public const string PluginMount = "plugin";
    public const string Mp3Mount = "mp3";
    public const string ShimPath = "common/zero-webview-shim.js";

    private static readonly Lazy<PluginAssetServer> _shared = new(() =>
        new PluginAssetServer(WebDevSampleCatalog.BuiltInRoot, WebDevSampleCatalog.PluginsRoot, PortFile));

    /// <summary>The one server every plugin view shares.</summary>
    public static PluginAssetServer Shared => _shared.Value;

    private static string PortFile => AppPaths.File("plugin-host.json");

    private static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".wasm"] = "application/wasm",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".onnx"] = "application/octet-stream",
        [".bin"] = "application/octet-stream",
    };

    // Rewritten in these only — binary bodies pass through untouched.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".js", ".mjs", ".css", ".json", ".svg" };

    private readonly string _builtInRoot;
    private readonly string _pluginsRoot;
    private readonly string _token;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>Cover art for <c>mp3/__cover/&lt;id&gt;</c>; set by the page that owns the bridge core.</summary>
    public Func<int, (byte[] Data, string Mime)?>? CoverLookup { get; set; }

    public int Port { get; }

    /// <summary><c>http://127.0.0.1:{port}/{token}/</c>.</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}/{_token}/";

    /// <summary>The origin plugins run under — what a <c>localStorage</c> entry is keyed by.</summary>
    public string Origin => $"http://127.0.0.1:{Port}";

    public PluginAssetServer(string builtInRoot, string pluginsRoot, string? portFile)
    {
        _builtInRoot = Path.GetFullPath(builtInRoot);
        _pluginsRoot = Path.GetFullPath(pluginsRoot);
        _token = RandomNumberGenerator.GetHexString(48, lowercase: true);
        _listener = Bind(portFile);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
        AppLogger.Log($"[Plugins] serving zero={_builtInRoot} plugin={_pluginsRoot} at 127.0.0.1:{Port}");
    }

    /// <summary>The URL a catalog entry (<c>https://zero.local/…</c> / <c>https://plugin.local/…</c>) loads from here.</summary>
    public string UrlFor(string canonicalUrl) => RewriteText(canonicalUrl, BaseUrl);

    // ── pure rules — the tests pin these ──

    /// <summary>Point the three plugin origins at <paramref name="baseUrl"/> (which ends in '/').</summary>
    public static string RewriteText(string text, string baseUrl)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text
            .Replace("https://" + WebDevSampleCatalog.BuiltInVirtualHost + "/", baseUrl + ZeroMount + "/", StringComparison.OrdinalIgnoreCase)
            .Replace("https://" + WebDevSampleCatalog.PluginVirtualHost + "/", baseUrl + PluginMount + "/", StringComparison.OrdinalIgnoreCase)
            .Replace("https://mp3.local/", baseUrl + Mp3Mount + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Put the shim in front of every other script: right after <c>&lt;head …&gt;</c>, else after
    /// <c>&lt;html …&gt;</c>, else at the very top. A page that already carries it is left alone.
    /// </summary>
    public static string InjectShim(string html, string shimUrl)
    {
        if (html.Contains("zero-webview-shim.js", StringComparison.OrdinalIgnoreCase)) return html;
        var tag = $"<script src=\"{shimUrl}\"></script>";
        foreach (var open in new[] { "<head", "<html" })
        {
            var at = html.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var end = html.IndexOf('>', at);
            if (end < 0) continue;
            return html[..(end + 1)] + tag + html[(end + 1)..];
        }
        return tag + html;
    }

    public enum Outcome { Ok, BadToken, Forbidden, NotFound }

    /// <summary>Split <c>/{token}/{mount}/{rest}</c>. The rest is URL-unescaped.</summary>
    public static Outcome Route(string token, string requestPath, out string mount, out string rest)
    {
        mount = "";
        rest = "";
        var q = requestPath.IndexOf('?');
        if (q >= 0) requestPath = requestPath[..q];
        var hash = requestPath.IndexOf('#');
        if (hash >= 0) requestPath = requestPath[..hash];

        var prefix = "/" + token + "/";
        if (!requestPath.StartsWith(prefix, StringComparison.Ordinal)) return Outcome.BadToken;
        var tail = requestPath[prefix.Length..];
        var slash = tail.IndexOf('/');
        if (slash <= 0) return Outcome.NotFound;
        mount = tail[..slash];
        rest = Uri.UnescapeDataString(tail[(slash + 1)..]);
        return mount is ZeroMount or PluginMount or Mp3Mount ? Outcome.Ok : Outcome.NotFound;
    }

    /// <summary>A static file under <paramref name="root"/>: no escape, no unknown extension.</summary>
    public static Outcome ResolveStatic(string root, string rel, out string? fullPath, out string? contentType)
    {
        fullPath = null;
        contentType = null;
        if (rel.Length == 0 || rel.EndsWith('/')) rel += "index.html";
        if (rel.Contains("..") || rel.Contains('\\') || rel.Contains(':') || rel.Contains('\0')) return Outcome.Forbidden;

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootFull, cmp)) return Outcome.Forbidden;
        if (!ContentTypes.TryGetValue(Path.GetExtension(candidate), out var ct)) return Outcome.Forbidden;
        if (!File.Exists(candidate)) return Outcome.NotFound;

        fullPath = candidate;
        contentType = ct;
        return Outcome.Ok;
    }

    // ── the responder ──

    private static TcpListener Bind(string? portFile)
    {
        var remembered = ReadPort(portFile);
        if (remembered > 0)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, remembered);
                l.Start();
                return l;
            }
            catch (SocketException ex)
            {
                AppLogger.Log($"[Plugins] port {remembered} unavailable ({ex.SocketErrorCode}) — plugin localStorage starts empty this run");
            }
        }
        var fresh = new TcpListener(IPAddress.Loopback, 0);
        fresh.Start();
        WritePort(portFile, ((IPEndPoint)fresh.LocalEndpoint).Port);
        return fresh;
    }

    private static int ReadPort(string? file)
    {
        try
        {
            if (file is null || !File.Exists(file)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("port", out var p) && p.TryGetInt32(out var port) && port is > 1024 and < 65536 ? port : 0;
        }
        catch { return 0; }
    }

    private static void WritePort(string? file, int port)
    {
        if (file is null) return;
        try { File.WriteAllText(file, JsonSerializer.Serialize(new { port })); }
        catch (Exception ex) { AppLogger.Log($"[Plugins] port not remembered: {ex.Message}"); }
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                AppLogger.Log($"[Plugins] accept failed: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            try
            {
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, ct);
                if (request is null) return;
                var (method, target, headers) = request.Value;
                if (method is not ("GET" or "HEAD")) { await WriteSimpleAsync(stream, 405, "method not allowed", ct); return; }
                var head = method == "HEAD";

                if (Route(_token, target, out var mount, out var rest) != Outcome.Ok)
                {
                    await WriteSimpleAsync(stream, 403, "forbidden", ct);
                    return;
                }

                if (mount == Mp3Mount)
                {
                    await ServeMp3Async(stream, rest, headers, head, ct);
                    return;
                }

                var root = mount == ZeroMount ? _builtInRoot : _pluginsRoot;
                switch (ResolveStatic(root, rest, out var path, out var contentType))
                {
                    case Outcome.Ok:
                        if (TextExtensions.Contains(Path.GetExtension(path!)))
                        {
                            var text = RewriteText(await File.ReadAllTextAsync(path!, ct), BaseUrl);
                            if (contentType!.StartsWith("text/html", StringComparison.Ordinal))
                                text = InjectShim(text, BaseUrl + ZeroMount + "/" + ShimPath);
                            var bytes = Encoding.UTF8.GetBytes(text);
                            await WriteHeadAsync(stream, 200, contentType, bytes.Length, null, ct);
                            if (!head) await stream.WriteAsync(bytes, ct);
                        }
                        else
                        {
                            await WriteFileAsync(stream, path!, contentType!, headers, head, ct);
                        }
                        break;
                    case Outcome.NotFound:
                        await WriteSimpleAsync(stream, 404, "not found", ct);
                        break;
                    default:
                        await WriteSimpleAsync(stream, 403, "forbidden", ct);
                        break;
                }
                await stream.FlushAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // The page navigated, the media element cancelled a range — normal.
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Plugins] serve failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task ServeMp3Async(NetworkStream stream, string rel, IReadOnlyDictionary<string, string> headers, bool head, CancellationToken ct)
    {
        if (rel.StartsWith(Mp3Resource.CoverPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(rel[Mp3Resource.CoverPrefix.Length..], out var id) && CoverLookup?.Invoke(id) is { } cover)
            {
                await WriteHeadAsync(stream, 200, cover.Mime, cover.Data.Length, null, ct, cacheControl: "max-age=3600");
                if (!head) await stream.WriteAsync(cover.Data, ct);
            }
            else await WriteSimpleAsync(stream, 404, "not found", ct);
            return;
        }

        switch (Mp3Resource.Resolve(Mp3SettingsStore.Load().ScanFolder, rel, out var full))
        {
            case Mp3Resource.Outcome.Ok:
                await WriteFileAsync(stream, full!, Mp3Scanner.ContentTypeFor(full!), headers, head, ct);
                break;
            case Mp3Resource.Outcome.Forbidden:
                await WriteSimpleAsync(stream, 403, "forbidden", ct);
                break;
            default:
                await WriteSimpleAsync(stream, 404, "not found", ct);
                break;
        }
    }

    // Whole file or one byte range — <audio>/<video> seek with Range, and a media element
    // that gets 200 for a range request refuses to seek.
    private static async Task WriteFileAsync(NetworkStream stream, string path, string contentType,
        IReadOnlyDictionary<string, string> headers, bool head, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var total = fs.Length;
        headers.TryGetValue("range", out var rangeHeader);
        var ranged = Mp3Resource.TryParseRange(rangeHeader, total, out var start, out var end, out var unsatisfiable);
        if (unsatisfiable)
        {
            await WriteHeadAsync(stream, 416, "text/plain", 0, $"Content-Range: bytes */{total}\r\n", ct);
            return;
        }

        var count = ranged ? end - start + 1 : total;
        var extra = "Accept-Ranges: bytes\r\n" + (ranged ? $"Content-Range: bytes {start}-{end}/{total}\r\n" : "");
        await WriteHeadAsync(stream, ranged ? 206 : 200, contentType, count, extra, ct);
        if (head || count == 0) return;

        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var left = count;
        while (left > 0)
        {
            var n = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
            if (n <= 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, n), ct);
            left -= n;
        }
    }

    /// <summary>Request line + headers (lower-cased names). Null on EOF or an oversized head.</summary>
    private static async Task<(string Method, string Target, IReadOnlyDictionary<string, string> Headers)?> ReadRequestAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var total = 0;
        var headEnd = -1;
        while (total < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct);
            if (n <= 0) break;
            total += n;
            headEnd = buf.AsSpan(0, total).IndexOf("\r\n\r\n"u8);
            if (headEnd >= 0) break;
        }
        if (total == 0) return null;
        var text = Encoding.ASCII.GetString(buf, 0, headEnd >= 0 ? headEnd : total);
        var lines = text.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        return (parts[0], parts[1], headers);
    }

    private static async Task WriteSimpleAsync(NetworkStream stream, int status, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await WriteHeadAsync(stream, status, "text/plain; charset=utf-8", bytes.Length, null, ct);
        await stream.WriteAsync(bytes, ct);
    }

    private static async Task WriteHeadAsync(NetworkStream stream, int status, string contentType, long length, string? extra,
        CancellationToken ct, string cacheControl = "no-store")
    {
        var reason = status switch
        {
            200 => "OK", 206 => "Partial Content", 403 => "Forbidden", 404 => "Not Found",
            405 => "Method Not Allowed", 416 => "Range Not Satisfiable", _ => "Error",
        };
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(length).Append("\r\n")
            .Append("Cache-Control: ").Append(cacheControl).Append("\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append(extra ?? "")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}

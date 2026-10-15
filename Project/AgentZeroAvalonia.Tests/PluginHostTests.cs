using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentZeroAvalonia.Services.Plugins;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>
/// The Plugins page's compatibility layer: a plugin written for the WPF host (absolute
/// https://zero.local / plugin.local / mp3.local URLs, chrome.webview messaging) must load
/// unchanged from the loopback server.
/// </summary>
public class PluginHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "az-plugins-" + Guid.NewGuid().ToString("N"));
    private string BuiltIn => Path.Combine(_root, "Wasm");
    private string Plugins => Path.Combine(_root, "plugins");
    private string PortFile => Path.Combine(_root, "plugin-host.json");

    public PluginHostTests()
    {
        Directory.CreateDirectory(Path.Combine(BuiltIn, "common"));
        Directory.CreateDirectory(Path.Combine(BuiltIn, "voice-test"));
        Directory.CreateDirectory(Path.Combine(Plugins, "demo", "assets"));
        File.WriteAllText(Path.Combine(BuiltIn, "common", "zero-bridge.js"), "window.zero = {};");
        File.WriteAllText(Path.Combine(BuiltIn, "common", "zero-webview-shim.js"), "window.__zeroWebview = {};");
        File.WriteAllText(Path.Combine(BuiltIn, "voice-test", "index.html"), "<html><head><title>v</title></head><body></body></html>");
        File.WriteAllText(Path.Combine(Plugins, "demo", "index.html"),
            "<!doctype html>\n<html lang=\"ko\">\n<head>\n<meta charset=\"utf-8\">\n" +
            "<script src=\"https://zero.local/common/zero-bridge.js\"></script>\n</head><body></body></html>");
        File.WriteAllText(Path.Combine(Plugins, "demo", "app.js"),
            "const src = 'https://mp3.local/' + encodeURI(p); const me = 'https://plugin.local/demo/';");
        File.WriteAllBytes(Path.Combine(Plugins, "demo", "assets", "sprite.png"), Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        File.WriteAllText(Path.Combine(Plugins, "demo", "run.exe"), "MZ");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Rewrites_the_three_plugin_origins()
    {
        const string b = "http://127.0.0.1:5000/tok/";
        var text = "<script src=\"https://zero.local/common/zero-bridge.js\"></script>" +
                   "'https://mp3.local/' + x; fetch('https://plugin.local/demo/a.json'); https://example.com/keep";
        var r = PluginAssetServer.RewriteText(text, b);

        Assert.Contains("http://127.0.0.1:5000/tok/zero/common/zero-bridge.js", r);
        Assert.Contains("'http://127.0.0.1:5000/tok/mp3/' + x", r);
        Assert.Contains("http://127.0.0.1:5000/tok/plugin/demo/a.json", r);
        Assert.Contains("https://example.com/keep", r);
        Assert.DoesNotContain(".local/", r);
    }

    [Theory]
    [InlineData("<html><head><title>x</title></head></html>", "<html><head><script src=\"S\"></script><title>")]
    [InlineData("<HTML lang=\"ko\"><HEAD data-x=\"1\">", "<HEAD data-x=\"1\"><script src=\"S\"></script>")]
    [InlineData("<html><body>no head</body></html>", "<html><script src=\"S\"></script><body>")]
    [InlineData("<p>fragment</p>", "<script src=\"S\"></script><p>")]
    public void The_shim_goes_in_front_of_every_other_script(string html, string expected)
        => Assert.Contains(expected, PluginAssetServer.InjectShim(html, "S"));

    [Fact]
    public void A_page_that_already_loads_the_shim_is_left_alone()
    {
        const string html = "<head><script src=\"x/zero-webview-shim.js\"></script></head>";
        Assert.Equal(html, PluginAssetServer.InjectShim(html, "S"));
    }

    [Fact]
    public void Routes_need_the_token_and_a_known_mount()
    {
        Assert.Equal(PluginAssetServer.Outcome.Ok, PluginAssetServer.Route("t", "/t/plugin/demo/index.html?x=1", out var mount, out var rest));
        Assert.Equal("plugin", mount);
        Assert.Equal("demo/index.html", rest);
        Assert.Equal(PluginAssetServer.Outcome.Ok, PluginAssetServer.Route("t", "/t/mp3/%EA%B0%80%EC%88%98/a.mp3", out _, out var mp3));
        Assert.Equal("가수/a.mp3", mp3);
        Assert.Equal(PluginAssetServer.Outcome.BadToken, PluginAssetServer.Route("t", "/u/plugin/demo/index.html", out _, out _));
        Assert.Equal(PluginAssetServer.Outcome.NotFound, PluginAssetServer.Route("t", "/t/other/x", out _, out _));
    }

    [Fact]
    public void Static_files_stay_under_the_root_and_on_the_whitelist()
    {
        Assert.Equal(PluginAssetServer.Outcome.Ok, PluginAssetServer.ResolveStatic(Plugins, "demo/", out var index, out _));
        Assert.EndsWith("index.html", index);
        Assert.Equal(PluginAssetServer.Outcome.Forbidden, PluginAssetServer.ResolveStatic(Plugins, "../plugin-host.json", out _, out _));
        Assert.Equal(PluginAssetServer.Outcome.Forbidden, PluginAssetServer.ResolveStatic(Plugins, "demo/run.exe", out _, out _));
        Assert.Equal(PluginAssetServer.Outcome.NotFound, PluginAssetServer.ResolveStatic(Plugins, "demo/missing.js", out _, out _));
    }

    [Fact]
    public async Task Serves_a_wpf_style_plugin_unchanged_on_disk()
    {
        using var server = new PluginAssetServer(BuiltIn, Plugins, PortFile);
        using var http = new HttpClient();

        var page = await http.GetStringAsync(server.UrlFor("https://plugin.local/demo/index.html"));
        var shim = page.IndexOf("zero/common/zero-webview-shim.js", StringComparison.Ordinal);
        var bridge = page.IndexOf(server.BaseUrl + "zero/common/zero-bridge.js", StringComparison.Ordinal);
        Assert.True(shim > 0, page);
        Assert.True(bridge > shim, "the shim must load before zero-bridge.js");
        Assert.Contains("https://zero.local/", File.ReadAllText(Path.Combine(Plugins, "demo", "index.html")));

        var js = await http.GetStringAsync(server.BaseUrl + "plugin/demo/app.js");
        Assert.Contains(server.BaseUrl + "mp3/", js);
        Assert.Contains(server.BaseUrl + "plugin/demo/", js);

        var bridgeJs = await http.GetAsync(server.BaseUrl + "zero/common/zero-bridge.js");
        Assert.Equal(HttpStatusCode.OK, bridgeJs.StatusCode);
        Assert.Equal("text/javascript", bridgeJs.Content.Headers.ContentType?.MediaType);

        var builtIn = await http.GetStringAsync(server.UrlFor("https://zero.local/voice-test/index.html"));
        Assert.Contains("zero-webview-shim.js", builtIn);
    }

    [Fact]
    public async Task Binary_assets_honour_range_requests()
    {
        using var server = new PluginAssetServer(BuiltIn, Plugins, PortFile);
        using var http = new HttpClient();

        var whole = await http.GetAsync(server.BaseUrl + "plugin/demo/assets/sprite.png");
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal(100, (await whole.Content.ReadAsByteArrayAsync()).Length);

        using var req = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl + "plugin/demo/assets/sprite.png");
        req.Headers.Range = new RangeHeaderValue(10, 19);
        var part = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(Enumerable.Range(10, 10).Select(i => (byte)i).ToArray(), await part.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes 10-19/100", part.Content.Headers.ContentRange?.ToString());
    }

    [Fact]
    public async Task Refuses_wrong_tokens_and_escapes()
    {
        using var server = new PluginAssetServer(BuiltIn, Plugins, PortFile);
        using var http = new HttpClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"{server.Origin}/nottoken/plugin/demo/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(server.BaseUrl + "plugin/demo/run.exe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(server.BaseUrl + "plugin/demo/nope.js")).StatusCode);
    }

    [Fact]
    public void The_port_is_remembered_so_localStorage_survives_a_restart()
    {
        int first;
        using (var a = new PluginAssetServer(BuiltIn, Plugins, PortFile)) first = a.Port;
        using var b = new PluginAssetServer(BuiltIn, Plugins, PortFile);
        Assert.Equal(first, b.Port);
    }

    [Fact]
    public void Deliver_script_hands_envelopes_to_the_shim()
    {
        var script = PluginBridge.BuildDeliverScript(new[] { "{\"id\":1,\"ok\":true}", "{\"op\":\"event\",\"channel\":\"c\",\"data\":{}}" });
        Assert.Equal("window.__zeroWebview&&window.__zeroWebview.deliver([{\"id\":1,\"ok\":true},{\"op\":\"event\",\"channel\":\"c\",\"data\":{}}]);", script);
    }

    [Fact]
    public void The_bridge_script_ships_byte_identical_to_the_wpf_host()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Wasm", "common", "zero-bridge.js");
        var shim = Path.Combine(AppContext.BaseDirectory, "Wasm", "common", "zero-webview-shim.js");
        Assert.True(File.Exists(shipped), shipped);
        Assert.True(File.Exists(shim), shim);

        var wpf = FindUp("AgentZeroWpf", "Wasm", "common", "zero-bridge.js");
        if (wpf is not null) Assert.Equal(File.ReadAllBytes(wpf), File.ReadAllBytes(shipped));
        Assert.Contains("__zeroWebview", File.ReadAllText(shim));
    }

    private static string? FindUp(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

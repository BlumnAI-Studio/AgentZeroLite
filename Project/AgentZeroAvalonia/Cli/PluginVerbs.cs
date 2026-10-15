using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Common;
using AgentZeroAvalonia.ViewModels;
using AgentZeroAvalonia.Views;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AgentZeroAvalonia.Cli;

/// <summary>
/// <c>-cli plugin …</c> — drive the Plugins page from a shell, so a plugin can be exercised
/// end to end without clicking: <c>call</c> runs <c>window.zero.invoke</c> <b>inside the
/// page</b>, so the answer has travelled page → shim → bridge → dispatcher and back, exactly
/// as a plugin's own call does. Server side runs on the UI thread (web views live there).
/// </summary>
internal static class PluginVerbs
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    // ── GUI side ──

    public static Task<string> HandleAsync(JsonElement root, MainWindowViewModel vm)
        => Dispatcher.UIThread.InvokeAsync(() => HandleOnUiAsync(root, vm));

    private static async Task<string> HandleOnUiAsync(JsonElement root, MainWindowViewModel vm)
    {
        var verb = Str(root, "verb");
        var id = Str(root, "id");
        switch (verb)
        {
            case "list":
            {
                var open = PluginsView.Current?.OpenIds.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
                var arr = new JsonArray(vm.Plugins.Samples.Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = s.Id,
                    ["name"] = s.DisplayName,
                    ["builtIn"] = s.IsBuiltIn,
                    ["url"] = s.Url,
                    ["open"] = open.Contains(s.Id),
                }).ToArray());
                return new JsonObject { ["ok"] = true, ["plugins"] = arr }.ToJsonString(Opts);
            }
            case "open":
            {
                var (view, error) = await OpenAsync(vm, id, Int(root, "timeout_ms", 20000));
                return view is not null && error is null
                    ? new JsonObject { ["ok"] = true, ["id"] = id }.ToJsonString(Opts)
                    : Fail(error ?? "could not open");
            }
            case "call":
            {
                var (view, error) = await OpenAsync(vm, id, 20000);
                if (view is null || error is not null) return Fail(error ?? "could not open");
                var op = Str(root, "op");
                var args = Str(root, "args");
                if (op.Length == 0) return Fail("op is required");
                if (args.Length == 0) args = "{}";
                try { JsonDocument.Parse(args).Dispose(); }
                catch (JsonException ex) { return Fail("args is not JSON: " + ex.Message); }
                return await CallInPageAsync(view, op, args, Int(root, "timeout_ms", 60000));
            }
            case "capture":
            {
                var (view, error) = await OpenAsync(vm, id, 20000);
                if (view is null || error is not null) return Fail(error ?? "could not open");
                var png = await PluginsView.Current!.CaptureAsync(ResolveId(vm, id));
                if (png is null) return Fail("capture-unavailable on this host");
                var path = Str(root, "out");
                if (path.Length == 0)
                    path = Path.Combine(Path.GetTempPath(), $"agentzero-plugin-{id}-{DateTime.Now:HHmmss}.png");
                await File.WriteAllBytesAsync(path, png);
                return new JsonObject { ["ok"] = true, ["path"] = path, ["bytes"] = png.Length }.ToJsonString(Opts);
            }
            default:
                return Fail($"unknown plugin verb '{verb}'");
        }
    }

    private static string ResolveId(MainWindowViewModel vm, string id)
        => vm.Plugins.Samples.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))?.Id ?? id;

    private static async Task<(NativeWebView? View, string? Error)> OpenAsync(MainWindowViewModel vm, string id, int timeoutMs)
    {
        if (id.Length == 0) return (null, "plugin id is required");
        vm.Page = AppPage.Plugins;  // mounts on first show, as a click would
        var page = PluginsView.Current;
        if (page is null) return (null, "the Plugins page is not created");
        return await page.OpenAsync(id, TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs)));
    }

    /// <summary>
    /// Start <c>window.zero.invoke(op, args)</c> in the page, then poll for its settled value —
    /// InvokeScript returns synchronously and cannot await a Promise itself.
    /// </summary>
    private static async Task<string> CallInPageAsync(NativeWebView view, string op, string argsJson, int timeoutMs)
    {
        var key = "c" + Guid.NewGuid().ToString("N")[..12];
        var start =
            "(function(){var b=window.__azCli||(window.__azCli={});" +
            "if(!window.zero){b['" + key + "']={ok:false,error:'window.zero is missing — the page does not load zero-bridge.js'};return 'no-zero';}" +
            "window.zero.invoke(" + JsonSerializer.Serialize(op) + "," + argsJson + ")" +
            ".then(function(r){b['" + key + "']={ok:true,result:r};}," +
            "function(e){b['" + key + "']={ok:false,error:String(e&&e.message||e)};});return 'started';})()";
        await view.InvokeScript(start);

        var poll = "(function(){var v=(window.__azCli||{})['" + key + "'];if(!v)return '';delete window.__azCli['" + key + "'];return JSON.stringify(v);})()";
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));
        while (DateTime.UtcNow < deadline)
        {
            var raw = await view.InvokeScript(poll);
            var text = Unquote(raw);
            if (!string.IsNullOrEmpty(text)) return text;
            await Task.Delay(100);
        }
        return Fail($"timed out after {timeoutMs} ms waiting for {op}");
    }

    // Engines return a script's string result JSON-encoded ("\"…\""); some return it bare.
    private static string Unquote(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null") return "";
        if (raw.StartsWith('"'))
        {
            try { return JsonSerializer.Deserialize<string>(raw) ?? ""; }
            catch (JsonException) { return raw; }
        }
        return raw;
    }

    private static string Fail(string error) => new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(Opts);

    private static string Str(JsonElement r, string name)
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement r, string name, int fallback)
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    // ── shell side ──

    public static int Run(CliClient client, string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }
        var verb = args[0].ToLowerInvariant();
        var payload = new JsonObject { ["command"] = "plugin", ["verb"] = verb };
        switch (verb)
        {
            case "list":
                break;
            case "open":
                if (args.Length < 2) { Console.Error.WriteLine("Usage: plugin open <id>"); return 1; }
                payload["id"] = args[1];
                break;
            case "call":
                if (args.Length < 3) { Console.Error.WriteLine("Usage: plugin call <id> <op> [json-args]"); return 1; }
                payload["id"] = args[1];
                payload["op"] = args[2];
                payload["args"] = args.Length > 3 ? string.Join(" ", args.Skip(3)) : "{}";
                break;
            case "capture":
                if (args.Length < 2) { Console.Error.WriteLine("Usage: plugin capture <id> [out.png]"); return 1; }
                payload["id"] = args[1];
                if (args.Length > 2) payload["out"] = Path.GetFullPath(args[2]);
                break;
            default:
                Usage();
                return 1;
        }
        if (client.TimeoutMs < 90000) client.TimeoutMs = 90000;
        var reply = client.Send(payload.ToJsonString());
        if (reply is null) return client.NoWait ? 0 : 1;
        Console.WriteLine(reply);
        return CliClient.Ok(CliClient.Parse(reply)) ? 0 : 1;
    }

    private static void Usage()
    {
        Console.WriteLine("Usage: AgentZeroLite -cli plugin <verb> [options]");
        Console.WriteLine();
        Console.WriteLine("The Plugins page (WebDev plugins) from a shell. The GUI must be running.");
        Console.WriteLine();
        Console.WriteLine("Verbs:");
        Console.WriteLine("  list                          Built-in samples and installed plugins, and which are open");
        Console.WriteLine("  open <id>                     Show the page, open the plugin, wait for it to load");
        Console.WriteLine("  call <id> <op> [json-args]    window.zero.invoke(op, args) inside the plugin's page — e.g.");
        Console.WriteLine("                                plugin call voice-test version   |   plugin call agent-band vision.status");
        Console.WriteLine("  capture <id> [out.png]        The plugin's rendered frame, as vision.* sees it");
        Console.WriteLine();
        Console.WriteLine("Output is one JSON object; exit code 0 when ok is true. Default --timeout is 90000 ms.");
    }
}

using Agent.Common.Llm.Tools;

namespace Agent.Common.Mcp;

/// <summary>
/// A browser the agent can drive through an MCP server — chrome-devtools-mcp or Playwright MCP
/// attached to a Chrome over CDP. Found from the allowed tool names alone, so the prompt and the
/// toolbelt agree on it without a connection.
///
/// <para>Why it matters: with a controllable browser configured, a small model still reached for
/// <c>os_launch chrome</c> when asked to "open Chrome" (measured, 2026-10-29). That started a
/// second Chrome on the default profile with no debugging port — it stopped at the profile
/// picker, which neither the MCP server nor the os tools could read or click. And
/// <c>web_open</c> loads the GUI's Browser page, which the person is not looking at.</para>
/// </summary>
public sealed record McpBrowser(string Server, string? NewTab, string? Navigate, string? Snapshot, string? Front)
{
    /// <summary>The first enabled server that can open or navigate a page, or null.</summary>
    public static McpBrowser? Find(IEnumerable<DynamicToolSpec> tools)
    {
        var byServer = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var t in tools)
        {
            if (!McpToolNames.TrySplit(t.Name, out var server, out var tool)) continue;
            if (!byServer.TryGetValue(server, out var set))
            {
                byServer[server] = set = new HashSet<string>(StringComparer.Ordinal);
                order.Add(server);
            }
            set.Add(tool);
        }

        foreach (var server in order)
        {
            var set = byServer[server];
            string? Pick(params string[] names) => names.FirstOrDefault(set.Contains) is { } n ? $"{server}.{n}" : null;

            // chrome-devtools-mcp names first, Playwright MCP second.
            var newTab = Pick("new_page", "browser_tabs");
            var navigate = Pick("navigate_page", "browser_navigate");
            if (newTab is null && navigate is null) continue;
            return new McpBrowser(server, newTab, navigate,
                Snapshot: Pick("take_snapshot", "browser_snapshot"),
                Front: Pick("select_page"));
        }
        return null;
    }

    /// <summary>
    /// The clause the watch's prompt carries while such a browser is configured: use it for
    /// anything the person wants to happen in a browser, and say why the other two ways fail.
    /// </summary>
    public string PromptRule()
    {
        var open = string.Join(" / ", new[] { NewTab, Navigate }.Where(n => n is not null));
        var rule = $"A Chrome you can control is connected through the {Server}.* tools. Whenever the user wants " +
                   $"something in a browser or in Chrome — open or show a site, search on it, click, read what is on " +
                   $"the page — use those tools ({open}";
        if (Snapshot is not null) rule += $", {Snapshot} to read the page";
        rule += "). Never start a browser with os_launch: that one cannot be controlled. Never use web_open to " +
                "show a page: its tab is not on the user's screen. web_search is only for a quick fact the user " +
                "did not ask to see.";
        if (Front is not null) rule += $" To bring the page forward, {Front} with bringToFront true.";
        rule += " Its window is an ordinary one for the os tools: os_list_windows(\"chrome\") shows it under the " +
                "selected page's title, so it can be moved to another monitor with os_move_window.";
        return rule;
    }

    /// <summary>
    /// The CDP endpoint a server's arguments attach it to (<c>--browser-url</c> for chrome-devtools-mcp,
    /// <c>--cdp-endpoint</c> for Playwright MCP), or null when the server launches its own browser.
    /// </summary>
    public static string? CdpEndpoint(McpServerConfig server)
    {
        var args = server.Args;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            foreach (var flag in new[] { "--browser-url", "--browserUrl", "-u", "--cdp-endpoint", "--cdpEndpoint" })
            {
                if (a == flag && i + 1 < args.Count) return args[i + 1];
                if (a.StartsWith(flag + "=", StringComparison.Ordinal)) return a[(flag.Length + 1)..];
            }
        }
        return null;
    }

    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromMilliseconds(800) };

    /// <summary>Whether a Chrome answers on <paramref name="endpoint"/> (<c>/json/version</c>).</summary>
    public static async Task<bool> CdpAnswersAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            using var response = await Probe.GetAsync(endpoint.TrimEnd('/') + "/json/version", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="program"/> (an os_launch argument: "chrome", "msedge.exe", "Google Chrome")
    /// names a web browser.
    /// </summary>
    public static bool IsBrowserProgram(string? program)
    {
        if (string.IsNullOrWhiteSpace(program)) return false;
        var name = Path.GetFileName(program.Trim().Trim('"'));
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name.Replace(" ", "").ToLowerInvariant();
        return name is "chrome" or "googlechrome" or "chromium" or "msedge" or "edge" or "microsoftedge"
            or "firefox" or "mozillafirefox" or "brave" or "opera" or "whale" or "naverwhale"
            or "iexplore" or "vivaldi" or "browser" or "크롬" or "엣지" or "웨일";
    }
}

using Agent.Common.Platform;
using System.IO;
using Agent.Common;

namespace Agent.Common.Browser;

/// <summary>
/// One sample shown in the WebDev menu sample list. Built-in samples ship
/// with the exe under <c>{exeDir}/Wasm/{folder}/</c>; user plugins live
/// under <c>%LOCALAPPDATA%/AgentZeroLite/Wasm/plugins/{id}/</c>. Both
/// resolve to virtual hosts handed to WebView2.
/// </summary>
public sealed record WebDevSample(
    string Id,
    string DisplayName,
    string Url,
    bool IsBuiltIn,
    string? Description = null);

/// <summary>
/// Discovers WebDev samples from two roots:
///   • <c>{exeDir}/Wasm/*</c> (excluding <c>common/</c>) — built-in entries
///   • <c>%LOCALAPPDATA%/AgentZeroLite/Wasm/plugins/*</c> — installed plugins
/// Emits stable <see cref="WebDevSample"/> records the WebDev panel can
/// bind to a ListBox. Pure I/O — safe to call repeatedly.
/// </summary>
public static class WebDevSampleCatalog
{
    public const string BuiltInVirtualHost = "zero.local";
    public const string PluginVirtualHost  = "plugin.local";

    public static string BuiltInRoot => Path.Combine(AppContext.BaseDirectory, "Wasm");

    public static string PluginsRoot => Path.Combine(
        AppPaths.DataRoot,
        "Wasm", "plugins");

    /// <param name="hiddenBuiltIns">
    /// Folders under <c>Wasm/</c> that are not samples in this host (the Avalonia host's
    /// <c>xterm</c> renderer lives there too). <c>common/</c> is always skipped.
    /// </param>
    public static IReadOnlyList<WebDevSample> Discover(IReadOnlyCollection<string>? hiddenBuiltIns = null)
    {
        var list = new List<WebDevSample>();
        AddBuiltIns(list, hiddenBuiltIns);
        AddPlugins(list);
        return list;
    }

    private static void AddBuiltIns(List<WebDevSample> list, IReadOnlyCollection<string>? hidden)
    {
        var root = BuiltInRoot;
        if (!Directory.Exists(root)) return;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, "common", StringComparison.OrdinalIgnoreCase)) continue;
            if (hidden is not null && hidden.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var indexHtml = Path.Combine(dir, "index.html");
            if (!File.Exists(indexHtml)) continue;
            list.Add(new WebDevSample(
                Id: name,
                DisplayName: name,
                Url: $"https://{BuiltInVirtualHost}/{name}/index.html",
                IsBuiltIn: true,
                Description: $"{name}/index.html"));
        }
    }

    private static void AddPlugins(List<WebDevSample> list)
    {
        var root = PluginsRoot;
        if (!Directory.Exists(root)) return;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(dir);
            try
            {
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    AppLogger.Log($"[WebDev] plugin '{id}' missing manifest.json — skipping");
                    continue;
                }
                var m = PluginManifest.Parse(File.ReadAllText(manifestPath));
                if (!string.Equals(m.Id, id, StringComparison.Ordinal))
                {
                    AppLogger.Log($"[WebDev] plugin folder '{id}' disagrees with manifest.id '{m.Id}' — using folder name");
                }
                list.Add(new WebDevSample(
                    Id: id,
                    DisplayName: m.Name,
                    Url: $"https://{PluginVirtualHost}/{id}/{m.Entry.TrimStart('/')}",
                    IsBuiltIn: false,
                    Description: m.Description ?? $"plugin · v{m.Version ?? "?"}"));
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[WebDev] plugin '{id}' load failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

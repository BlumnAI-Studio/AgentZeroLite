using Agent.Common.Platform;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Common.Browser;

/// <summary>
/// What a detached plugin window remembers per plugin: pinned (always on top), opacity
/// (0.5–1.0), the WPF titlebar flag, and its rect (x, y, w, h in device-independent
/// pixels). Same file and same fields as the WPF host's <c>WebDevFloatingPrefs</c> —
/// <c>%LOCALAPPDATA%\AgentZeroLite\WebDev\floating-prefs.json</c>, keyed by plugin id — so
/// a plugin parked as a side window comes back in the same spot whichever GUI opened it.
/// (A reader for that file rather than a move of the WPF class: conversion work does not
/// edit the WPF project.)
/// </summary>
public sealed class PluginWindowPref
{
    [JsonPropertyName("topmost")] public bool Topmost { get; set; }
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 1.0;
    [JsonPropertyName("chrome")] public bool Chrome { get; set; } = true;
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("w")] public double? W { get; set; }
    [JsonPropertyName("h")] public double? H { get; set; }

    /// <summary>Opacity the window may actually use: never so faint it is lost.</summary>
    [JsonIgnore] public double SafeOpacity => Math.Clamp(Opacity, 0.5, 1.0);
}

public static class PluginWindowPrefs
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Overridable for tests; the default is the file the WPF host uses.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        AppPaths.DataRoot,
        "WebDev", "floating-prefs.json");

    public static PluginWindowPref Get(string pluginId)
    {
        lock (Gate)
        {
            return LoadUnlocked().TryGetValue(pluginId, out var p) ? p : new PluginWindowPref();
        }
    }

    public static void Save(string pluginId, PluginWindowPref pref)
    {
        lock (Gate)
        {
            var all = LoadUnlocked();
            all[pluginId] = pref;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all, JsonOpts));
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Plugins:Prefs] save failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static Dictionary<string, PluginWindowPref> LoadUnlocked()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, PluginWindowPref>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Plugins:Prefs] load failed: {ex.GetType().Name}: {ex.Message}");
            return new();
        }
    }
}

using System.Text.Json;

namespace Agent.Common.Browser;

/// <summary>
/// What <see cref="ZeroBridgeDispatcher"/> cannot do itself because it needs the
/// host's UI, web view or audio devices. One per plugin web view.
/// </summary>
public interface IZeroBridgeHost
{
    /// <summary>
    /// Ops only this host implements — WPF answers <c>note.*</c> and <c>music.*</c>
    /// here (NAudio capture). Return null for an op that is not one of them; the
    /// dispatcher then runs its own table.
    /// </summary>
    Task<object?>? TryDispatchHostOp(string op, JsonElement? args);

    /// <summary>A native folder picker. Null when the user cancelled (or the host has none).</summary>
    Task<string?> PickFolderAsync(string title, string? initialFolder);

    /// <summary>
    /// The plugin's rendered frame as PNG, in device pixels — what <c>vision.analyze</c>
    /// and <c>vision.motion</c> crop. Null when this host cannot capture its web view.
    /// </summary>
    Task<byte[]?> CaptureFramePngAsync();

    /// <summary>
    /// The MP3 scan root the plugin is about to address as <c>https://mp3.local/</c>.
    /// Called before every op that may lead to playback; a host that serves that
    /// origin from settings on each request can ignore it.
    /// </summary>
    void UseMp3Folder(string? folder);
}

using Agent.Common;
using Agent.Common.Workspace;
using AgentZeroAvalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace AgentZeroAvalonia.Views;

/// <summary>
/// Draws <see cref="CodeViewModel"/>: the editor's document and grammar are set here when the
/// open file changes (AvaloniaEdit's document is not a bindable string), and images are loaded
/// here. One TextMate installation per editor, Dark+ — VS Code's default dark theme.
/// </summary>
public partial class CodeView : UserControl
{
    private static readonly RegistryOptions Registry = new(ThemeName.DarkPlus);

    private CodeViewModel? _vm;
    private TextMate.Installation? _textMate;
    private Bitmap? _bitmap;

    public CodeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as CodeViewModel);
        CopyPathButton.Click += async (_, _) =>
        {
            if (_vm?.RelativePath is { } rel && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(rel.Replace('\\', '/'));
        };
        OpenExternalButton.Click += async (_, _) =>
        {
            if (_vm?.CurrentPath is { } path && TopLevel.GetTopLevel(this) is { } top)
                await top.Launcher.LaunchFileInfoAsync(new FileInfo(path));
        };
        DetachedFromVisualTree += (_, _) =>
        {
            // The view is rebuilt when the panel floats into its own window; the model keeps the state.
            _textMate?.Dispose();
            _textMate = null;
        };
        AttachedToVisualTree += (_, _) => Show();
    }

    private void Bind(CodeViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null) _vm.TextChanged -= Show;
        _vm = vm;
        if (_vm is not null) _vm.TextChanged += Show;
        Show();
    }

    // ── Markdown preview ─────────────────────────────────────────────────────

    private NativeWebView? _preview;
    private bool _previewReady;
    private string? _pendingHtml;

    private void ShowPreview(string html)
    {
        if (_preview is null)
        {
            _preview = new NativeWebView();
            _preview.EnvironmentRequested += (_, e) =>
            {
                if (e is WindowsWebView2EnvironmentRequestedEventArgs win)
                    win.UserDataFolder = Path.Combine(Path.GetTempPath(), "AgentZeroLite_Markdown_Avalonia");
            };
            _preview.WebMessageReceived += (_, e) => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPreviewMessage(e.Body));
            PreviewHost.Children.Add(_preview);
            _preview.Source = new Uri(Services.LocalAssetServer.Markdown.UrlFor("index.html"));
        }
        _pendingHtml = html;
        if (_previewReady) FlushPreview();
    }

    private void FlushPreview()
    {
        if (_preview is null || _pendingHtml is null) return;
        var html = _pendingHtml;
        _pendingHtml = null;
        _ = _preview.InvokeScript("window.renderDoc && window.renderDoc(" + System.Text.Json.JsonSerializer.Serialize(html) + ")");
    }

    private async void OnPreviewMessage(string? body)
    {
        if (_vm is null || string.IsNullOrEmpty(body)) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    _previewReady = true;
                    FlushPreview();
                    break;
                case "rendered":
                    var diagrams = root.TryGetProperty("mermaid", out var m) ? m.GetInt32() : 0;
                    var failed = root.TryGetProperty("failed", out var f) ? f.GetInt32() : 0;
                    _vm.PreviewInfo = $"rendered; mermaid {diagrams - failed}/{diagrams}";
                    if (failed > 0) AppLogger.Log($"[Code] mermaid could not draw {failed} of {diagrams} diagram(s) | {_vm.CurrentTitle}");
                    break;
                case "link":
                    if (await _vm.FollowLinkAsync(root.GetProperty("href").GetString() ?? "") is { } web
                        && TopLevel.GetTopLevel(this) is { } top)
                        await top.Launcher.LaunchUriAsync(web);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Code] preview message failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Show()
    {
        if (_vm is null) return;
        try
        {
            if (_vm.IsPreview)
            {
                ShowPreview(_vm.Html);
                return;
            }

            if (_vm.IsImage && _vm.CurrentPath is { } image)
            {
                var old = _bitmap;
                _bitmap = new Bitmap(image);
                Picture.Source = _bitmap;
                old?.Dispose();
                Editor.Document = new TextDocument();
                return;
            }

            Picture.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;

            _textMate ??= Editor.InstallTextMate(Registry);
            Editor.Document = new TextDocument(_vm.Text);
            ApplyGrammar(_vm.CurrentPath);
            Editor.ScrollToHome();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Code] render failed | {_vm.CurrentPath} | {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ApplyGrammar(string? path)
    {
        if (_textMate is null) return;
        var ext = path is null ? "" : Path.GetExtension(path);
        var language = ext.Length == 0 ? null : Registry.GetLanguageByExtension(ext);
        // No grammar for this extension: plain text, no highlighting.
        _textMate.SetGrammar(language is null ? null : Registry.GetScopeByLanguageId(language.Id));
    }
}

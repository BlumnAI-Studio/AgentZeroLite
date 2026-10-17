using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Agent.Common.Workspace;

/// <summary>
/// Markdown → the HTML body the code view's preview page shows (GitHub-flavoured: tables, task
/// lists, autolinks, footnotes…, via Markdig). Two things are done here rather than in the page,
/// so they are testable and the page needs no file access:
/// <list type="bullet">
/// <item>a <c>```mermaid</c> fence becomes <c>&lt;pre class="mermaid"&gt;</c>, which mermaid.js
/// turns into a diagram (text stays HTML-escaped; mermaid reads it back as text);</item>
/// <item>an image with a relative path is inlined as a data URI when it resolves to an image
/// file inside the workspace — the page is served from a loopback origin that cannot read the
/// disk, and nothing outside the workspace is read.</item>
/// </list>
/// Raw HTML in the document is escaped, not rendered: a README is content, and the preview
/// should not run what it contains.
/// </summary>
public static partial class MarkdownHtml
{
    /// <summary>Images larger than this are left as a broken link rather than inlined.</summary>
    public const long MaxInlineImageBytes = 8 * 1024 * 1024;

    // YAML front matter (a harness agent / engine file opens with one) is parsed and not drawn,
    // as VS Code's preview does — without it the block rendered as a rule and a run-on paragraph.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .DisableHtml()
        .Build();

    public static bool IsMarkdown(string? path) =>
        Path.GetExtension(path ?? "").ToLowerInvariant() is ".md" or ".markdown" or ".mdx" or ".mdown";

    /// <param name="markdown">The file's text.</param>
    /// <param name="baseDirectory">The file's folder: relative image paths resolve against it.</param>
    /// <param name="workspaceRoot">Images are inlined only from inside this folder.</param>
    public static string Render(string markdown, string? baseDirectory = null, string? workspaceRoot = null)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        if (baseDirectory is not null && workspaceRoot is not null)
        {
            foreach (var link in doc.Descendants<LinkInline>())
                if (link.IsImage && link.Url is { Length: > 0 } url && InlineImage(url, baseDirectory, workspaceRoot) is { } data)
                    link.Url = data;
        }
        var html = doc.ToHtml(Pipeline);
        return MermaidFence().Replace(html, m => "<pre class=\"mermaid\">" + m.Groups[1].Value + "</pre>");
    }

    private static string? InlineImage(string url, string baseDirectory, string root)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || Uri.TryCreate(url, UriKind.Absolute, out var abs) && !abs.IsFile)
            return null; // remote or already inline: the page loads it as it is
        try
        {
            var relative = Uri.UnescapeDataString(url.Split('#', '?')[0]).Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(baseDirectory, relative));
            if (!WorkspaceFiles.IsInside(root, full) || !File.Exists(full)) return null;
            var mime = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                ".bmp" => "image/bmp",
                ".ico" => "image/x-icon",
                _ => null,
            };
            if (mime is null || new FileInfo(full).Length > MaxInlineImageBytes) return null;
            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(full))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    [GeneratedRegex("<pre><code class=\"language-mermaid\">(.*?)</code></pre>", RegexOptions.Singleline)]
    private static partial Regex MermaidFence();
}

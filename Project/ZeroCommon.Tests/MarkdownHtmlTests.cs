using Agent.Common.Workspace;

namespace ZeroCommon.Tests;

/// <summary>MarkdownHtml — what the code view's preview page is handed.</summary>
public sealed class MarkdownHtmlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "az-md-" + Guid.NewGuid().ToString("N"));

    public MarkdownHtmlTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "img"));
        File.WriteAllBytes(Path.Combine(_root, "docs", "img", "shot.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void A_mermaid_fence_becomes_a_mermaid_block_with_its_text_kept()
    {
        var html = MarkdownHtml.Render("# T\n\n```mermaid\ngraph LR\n  A-->B\n```\n\n```cs\nvar x = 1;\n```\n");
        Assert.Contains("<pre class=\"mermaid\">graph LR\n  A--", html);
        Assert.DoesNotContain("language-mermaid", html);
        // The arrow survives as text (escaped or not, mermaid reads textContent).
        Assert.Matches("A--(&gt;|>)B", html);
        Assert.Contains("<code class=\"language-cs\">", html);   // other fences stay code
    }

    [Fact]
    public void Tables_and_task_lists_render_and_raw_html_is_escaped()
    {
        var html = MarkdownHtml.Render("| a | b |\n|---|---|\n| 1 | 2 |\n\n- [x] done\n\n<script>alert(1)</script>\n");
        Assert.Contains("<table>", html);
        Assert.Contains("type=\"checkbox\"", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Local_images_inside_the_workspace_are_inlined_and_nothing_outside_is_read()
    {
        var docs = Path.Combine(_root, "docs");
        var html = MarkdownHtml.Render("![s](img/shot.png) ![w](https://example.com/a.png) ![x](../../outside.png)", docs, _root);
        Assert.Contains("src=\"data:image/png;base64,iVBORw==\"", html);
        Assert.Contains("src=\"https://example.com/a.png\"", html);   // remote: left for the page
        Assert.Contains("src=\"../../outside.png\"", html);           // outside the workspace: not read
    }

    [Fact]
    public void Yaml_front_matter_is_not_drawn()
    {
        var html = MarkdownHtml.Render("---\nname: mission-dispatch\nagents: [tamer]\n---\n\n# Mission\n");
        Assert.DoesNotContain("name: mission-dispatch", html);
        Assert.DoesNotContain("<hr", html);
        Assert.Contains("<h1 id=\"mission\">Mission</h1>", html);
    }

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("notes.MARKDOWN", true)]
    [InlineData("app.cs", false)]
    [InlineData(null, false)]
    public void Markdown_files_are_recognised(string? path, bool expected)
        => Assert.Equal(expected, MarkdownHtml.IsMarkdown(path));
}

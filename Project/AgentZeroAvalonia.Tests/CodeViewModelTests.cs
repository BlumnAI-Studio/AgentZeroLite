using Agent.Common.Workspace;
using AgentZeroAvalonia.Layout;
using AgentZeroAvalonia.ViewModels;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>The code view's model: the workspace tree, opening files, staying inside the workspace.</summary>
public sealed class CodeViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "az-code-" + Guid.NewGuid().ToString("N"));

    public CodeViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        File.WriteAllText(Path.Combine(_root, "src", "app.cs"), "class App {}");
        File.WriteAllText(Path.Combine(_root, "README.md"), "# readme");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Two_loads_of_one_root_fill_the_tree_once()
    {
        // Measured: opening the view from the CLI started two loads of the same root and the
        // tree showed every top-level entry twice.
        var vm = new CodeViewModel();
        await Task.WhenAll(vm.SetRootAsync(_root, "ws"), vm.SetRootAsync(_root, "ws"));
        Assert.Equal(new[] { "src", "README.md" }, vm.Nodes.Select(n => n.Name));   // node_modules hidden
    }

    [Fact]
    public async Task A_folder_loads_its_children_when_expanded()
    {
        var vm = new CodeViewModel();
        await vm.SetRootAsync(_root, "ws");
        var src = vm.Nodes.Single(n => n.Name == "src");
        Assert.Equal("…", Assert.Single(src.Children).Name);   // placeholder until expanded

        await src.LoadAsync();
        Assert.Equal("app.cs", Assert.Single(src.Children).Name);
    }

    [Fact]
    public async Task Opening_reads_inside_the_workspace_only()
    {
        var vm = new CodeViewModel();
        await vm.SetRootAsync(_root, "ws");

        Assert.Null(await vm.OpenAsync("src/app.cs"));
        Assert.Equal("src/app.cs", vm.CurrentTitle);
        Assert.Equal("class App {}", vm.Text);
        Assert.Equal(FileViewKind.Text, vm.Kind);
        Assert.True(vm.IsTextView);

        Assert.Equal("the file is outside the workspace", await vm.OpenAsync("../elsewhere.txt"));
        Assert.Equal("no such file", await vm.OpenAsync("missing.txt"));
        Assert.Equal("src/app.cs", vm.CurrentTitle);   // a refused open leaves the shown file alone
    }

    [Fact]
    public async Task Markdown_opens_rendered_and_switches_to_its_source()
    {
        File.WriteAllText(Path.Combine(_root, "docs.md"), "# Title\n\n```mermaid\ngraph LR\n A-->B\n```\n\n[app](src/app.cs)\n");
        var vm = new CodeViewModel();
        await vm.SetRootAsync(_root, "ws");
        await vm.OpenAsync("docs.md");

        Assert.True(vm.IsMarkdown);
        Assert.True(vm.IsPreview);          // rendered by default
        Assert.False(vm.IsTextView);
        Assert.Contains("<pre class=\"mermaid\">", vm.Html);

        vm.TogglePreviewCommand.Execute(null);
        Assert.False(vm.IsPreview);
        Assert.True(vm.IsTextView);         // the source, highlighted

        // A relative link opens the workspace file here; a web link is handed back to launch.
        Assert.Null(await vm.FollowLinkAsync("src/app.cs"));
        Assert.Equal("src/app.cs", vm.CurrentTitle);
        Assert.Equal(new Uri("https://example.com/x"), await vm.FollowLinkAsync("https://example.com/x"));
        Assert.False(vm.IsMarkdown);        // a .cs file is never previewed
    }

    [Fact]
    public void The_toggle_chord_is_bound()
    {
        var binding = Assert.Single(HotkeyTable.Build(null, isMac: false), b => b.Name == HotkeyTable.CodeToggle);
        Assert.True(binding.Ctrl && binding.Alt && binding.Key == "E");
    }
}

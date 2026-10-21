using System.Text;
using Agent.Common.Workspace;

namespace ZeroCommon.Tests;

/// <summary>WorkspaceFiles — the code view's listing, ignore rules and file reading.</summary>
public sealed class WorkspaceFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "az-ws-" + Guid.NewGuid().ToString("N"));

    public WorkspaceFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "Docs"));
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "# hi");
        File.WriteAllText(Path.Combine(_root, "app.log"), "x");
        File.WriteAllText(Path.Combine(_root, ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "# comment\n*.log\nout/\nsrc/generated\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void A_folder_lists_folders_first_and_hides_what_the_tree_should_not_show()
    {
        var patterns = WorkspaceFiles.LoadIgnorePatterns(_root);
        var names = WorkspaceFiles.List(_root, patterns).Select(e => (e.Name, e.IsDirectory)).ToList();

        Assert.Equal(new[] { ("Docs", true), ("src", true), (".gitignore", false), ("README.md", false) }, names);
        // bin (always hidden), .git / .env (dot entries), out/ and *.log (.gitignore) are gone;
        // a path pattern ("src/generated") is not something this matcher understands, so src stays.
    }

    [Fact]
    public void The_detailed_listing_matches_the_tree_and_carries_sizes_for_files_only()
    {
        var patterns = WorkspaceFiles.LoadIgnorePatterns(_root);
        var rows = WorkspaceFiles.ListDetailed(_root, patterns);

        Assert.Equal(WorkspaceFiles.List(_root, patterns).Select(e => e.Name), rows.Select(r => r.Name));
        Assert.Null(rows.Single(r => r.Name == "Docs").Size);
        Assert.Equal(4, rows.Single(r => r.Name == "README.md").Size);
        Assert.All(rows, r => Assert.NotEqual(default, r.Modified));
    }

    [Fact]
    public void Containment_rejects_escapes()
    {
        Assert.True(WorkspaceFiles.IsInside(_root, Path.Combine(_root, "src", "a.cs")));
        Assert.True(WorkspaceFiles.IsInside(_root, _root));
        Assert.False(WorkspaceFiles.IsInside(_root, Path.Combine(_root, "..", "elsewhere.txt")));
        Assert.False(WorkspaceFiles.IsInside(_root, Path.GetTempPath()));
    }

    [Fact]
    public void Text_images_binaries_and_large_files_are_told_apart()
    {
        var utf8 = Path.Combine(_root, "한글.txt");
        File.WriteAllText(utf8, "안녕 — hello", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var text = WorkspaceFiles.ReadForView(utf8);
        Assert.Equal(FileViewKind.Text, text.Kind);
        Assert.Equal("안녕 — hello", text.Text);   // BOM stripped

        Assert.Equal(FileViewKind.Image, WorkspaceFiles.ReadForView(WriteBytes("logo.png", 1, 2, 3)).Kind);
        Assert.Equal(FileViewKind.Binary, WorkspaceFiles.ReadForView(WriteBytes("tool.dll", 1, 2)).Kind);
        // No telling extension, but a NUL byte: binary.
        Assert.Equal(FileViewKind.Binary, WorkspaceFiles.ReadForView(WriteBytes("data.dat", 65, 0, 66)).Kind);

        var big = Path.Combine(_root, "big.txt");
        File.WriteAllText(big, new string('a', WorkspaceFiles.MaxViewBytes + 10));
        var head = WorkspaceFiles.ReadForView(big);
        Assert.Equal(FileViewKind.TooLarge, head.Kind);
        Assert.Equal(WorkspaceFiles.MaxViewBytes, head.Text.Length);
        Assert.NotNull(head.Note);
    }

    private string WriteBytes(string name, params byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

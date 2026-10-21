using System.IO;
using Agent.Common.Workspace;

namespace ZeroCommon.Tests.Platform;

/// <summary>The copy-path menus: absolute in the OS's form, relative with '/'.</summary>
[Trait("Category", "Platform")]
public sealed class PathCopyTextTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "azcopy", "repo");

    [Fact]
    public void Relative_uses_forward_slashes_and_may_climb_out_of_the_base()
    {
        Assert.Equal("src/a.cs", PathCopyText.Relative(Root, Path.Combine(Root, "src", "a.cs")));
        Assert.Equal("../wt", PathCopyText.Relative(Root, Path.Combine(Root, "..", "wt")));
        Assert.Null(PathCopyText.Relative(Root, Root));
        Assert.Null(PathCopyText.Relative(Root, ""));
    }

    [Fact]
    public void Absolute_is_the_full_os_path()
    {
        var file = Path.Combine(Root, "src", "a.cs");
        Assert.Equal(Path.GetFullPath(file), PathCopyText.Absolute(file));
        Assert.Null(PathCopyText.Absolute("  "));
    }

    [Fact]
    public void HomeRelative_is_tilde_form_only_under_home()
    {
        var home = Path.Combine(Path.GetTempPath(), "azcopy", "home");
        Assert.Equal("~/code/x", PathCopyText.HomeRelative(Path.Combine(home, "code", "x"), home));
        Assert.Null(PathCopyText.HomeRelative(Root, home));
        Assert.Null(PathCopyText.HomeRelative(home, home));
    }
}

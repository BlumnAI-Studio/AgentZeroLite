using Agent.Common.Browser;
using Xunit;

namespace ZeroCommon.Tests.Browser;

[Trait("Category", "ZeroBridge")]
public sealed class Mp3ResourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mp3res-" + Guid.NewGuid().ToString("N"));

    public Mp3ResourceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "가수"));
        File.WriteAllBytes(Path.Combine(_root, "가수", "노래 1.mp3"), new byte[10]);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void A_track_under_the_root_resolves()
    {
        Assert.Equal(Mp3Resource.Outcome.Ok, Mp3Resource.Resolve(_root, "가수/노래 1.mp3", out var full));
        Assert.Equal(Path.Combine(_root, "가수", "노래 1.mp3"), full);
    }

    [Theory]
    [InlineData("../outside.mp3", Mp3Resource.Outcome.Forbidden)]
    [InlineData("notes.txt", Mp3Resource.Outcome.NotFound)]        // not an audio extension
    [InlineData("missing.mp3", Mp3Resource.Outcome.NotFound)]
    [InlineData("", Mp3Resource.Outcome.NotFound)]
    public void Everything_else_is_refused(string rel, Mp3Resource.Outcome expected)
        => Assert.Equal(expected, Mp3Resource.Resolve(_root, rel, out _));

    [Fact]
    public void No_scan_root_is_not_found()
        => Assert.Equal(Mp3Resource.Outcome.NotFound, Mp3Resource.Resolve(null, "a.mp3", out _));

    [Theory]
    [InlineData("bytes=0-", 0, 99)]
    [InlineData("bytes=10-19", 10, 19)]
    [InlineData("bytes=90-500", 90, 99)]     // end clamped to the file
    [InlineData("bytes=-10", 90, 99)]        // suffix
    [InlineData("bytes=5-9, 20-30", 5, 9)]   // first range only
    public void Ranges(string header, long start, long end)
    {
        Assert.True(Mp3Resource.TryParseRange(header, 100, out var s, out var e, out var bad));
        Assert.False(bad);
        Assert.Equal(start, s);
        Assert.Equal(end, e);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-5")]
    [InlineData("bytes=abc")]
    public void No_usable_range_means_the_whole_file(string? header)
        => Assert.False(Mp3Resource.TryParseRange(header, 100, out _, out _, out _));

    [Fact]
    public void A_range_past_the_end_is_unsatisfiable()
    {
        Assert.True(Mp3Resource.TryParseRange("bytes=100-", 100, out _, out _, out var bad));
        Assert.True(bad);
    }
}

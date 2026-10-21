using Agent.Common.Workspace;

namespace ZeroCommon.Tests;

/// <summary>The code view's GIT LOG: the git log parser and the refusal of a non-repository.</summary>
public sealed class GitLogReaderTests
{
    [Fact]
    public void Git_output_keeps_subject_body_and_author_through_the_separators()
    {
        const string output =
            "abc1234567890\u001fKim\u001f2026-10-21T21:00:00+09:00\u001ffeat: add | pipes\u001fline one\nline two\n\u001e\n" +
            "def4567890123\u001fLee\u001f2026-10-20T08:30:00+09:00\u001ffix: 한글 제목\u001f\u001e\n";

        var commits = GitLogReader.Parse(output);

        Assert.Equal(2, commits.Count);
        Assert.Equal("abc1234", commits[0].ShortHash);
        Assert.Equal("Kim", commits[0].Author);
        Assert.Equal("feat: add | pipes", commits[0].Subject);
        Assert.Equal("line one\nline two", commits[0].Body);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 21, 0, 0, TimeSpan.FromHours(9)), commits[0].Date);
        Assert.Equal("fix: 한글 제목", commits[1].Subject);
        Assert.Equal("", commits[1].Body);
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_is_refused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "az-nogit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var result = await GitLogReader.ReadAsync(dir, 5);
            Assert.False(result.Ok);   // not a repository — or no git at all; either way no commits
            Assert.Empty(result.Commits);
            Assert.False(string.IsNullOrEmpty(result.Error));
        }
        finally { Directory.Delete(dir, true); }
    }
}

using Agent.Common.Browser;
using Xunit;

namespace ZeroCommon.Tests.Browser;

[Trait("Category", "ZeroBridge")]
public sealed class PluginManifestTests
{
    [Fact]
    public void Minimal_manifest_parses_with_defaults()
    {
        var m = PluginManifest.Parse("""{ "id": "voice-note", "name": "Voice Note" }""");
        Assert.Equal("voice-note", m.Id);
        Assert.Equal("index.html", m.Entry);
    }

    [Theory]
    [InlineData("""{ "name": "x" }""")]                                        // no id
    [InlineData("""{ "id": "Bad_Id", "name": "x" }""")]                       // not kebab-case
    [InlineData("""{ "id": "ok", "name": "" }""")]                            // no name
    [InlineData("""{ "id": "ok", "name": "x", "entry": "../evil.html" }""")]  // escapes the folder
    [InlineData("""{ "id": "ok", "name": "x", "entry": "/abs.html" }""")]
    [InlineData("""{ "id": "ok", "name": "x", "entry": "main.js" }""")]       // not html
    public void Invalid_manifests_are_refused(string json)
        => Assert.Throws<InvalidDataException>(() => PluginManifest.Parse(json));
}

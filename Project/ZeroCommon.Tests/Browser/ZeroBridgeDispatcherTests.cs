using System.Text.Json;
using Agent.Common.Browser;
using Xunit;

namespace ZeroCommon.Tests.Browser;

/// <summary>
/// The plugin bridge's op table lives in ZeroCommon so both hosts answer every
/// plugin from one switch. These pin the envelope (what zero-bridge.js parses)
/// and the seams a host plugs into — not the ops' own logic, which needs an LLM,
/// a DB or a model.
/// </summary>
[Trait("Category", "ZeroBridge")]
public sealed class ZeroBridgeDispatcherTests
{
    private sealed class FakeHost : IZeroBridgeHost
    {
        public string? PickedFolder;
        public byte[]? Frame;
        public readonly List<string> HostOps = new();
        public readonly List<string?> Mp3Folders = new();

        public Task<object?>? TryDispatchHostOp(string op, JsonElement? args)
        {
            if (!op.StartsWith("note.", StringComparison.Ordinal)) return null;
            HostOps.Add(op);
            return Task.FromResult<object?>(new { handledBy = "host", op });
        }

        public Task<string?> PickFolderAsync(string title, string? initialFolder) => Task.FromResult(PickedFolder);
        public Task<byte[]?> CaptureFramePngAsync() => Task.FromResult(Frame);
        public void UseMp3Folder(string? folder) => Mp3Folders.Add(folder);
    }

    private static (ZeroBridgeDispatcher Dispatcher, FakeHost Host, List<(string Channel, object Data)> Events) Make()
    {
        var host = new FakeHost();
        var events = new List<(string, object)>();
        var core = new ZeroBrowserCore();
        return (new ZeroBridgeDispatcher(core, host, (c, d) => events.Add((c, d))), host, events);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Version_answers_with_the_request_id_and_ok()
    {
        var (d, _, _) = Make();
        var r = Parse(await d.HandleRequestAsync("""{"id":7,"op":"version","args":{}}"""));

        Assert.Equal(7, r.GetProperty("id").GetInt32());
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(r.GetProperty("result").GetProperty("version").GetString()));
        // Null fields are left out, as before the move — zero-bridge.js reads msg.error only on failure.
        Assert.False(r.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Unknown_op_is_a_failure_envelope_not_an_exception()
    {
        var (d, _, _) = Make();
        var r = Parse(await d.HandleRequestAsync("""{"id":3,"op":"no.such.op"}"""));

        Assert.Equal(3, r.GetProperty("id").GetInt32());
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown op 'no.such.op'", r.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_request_wrapped_in_a_json_string_is_unwrapped_once()
    {
        // invokeCSharpAction / WKWebView hand over the object's JSON as a string.
        var (d, _, _) = Make();
        var inner = """{"id":11,"op":"version"}""";
        var r = Parse(await d.HandleRequestAsync(JsonSerializer.Serialize(inner)));

        Assert.Equal(11, r.GetProperty("id").GetInt32());
        Assert.True(r.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public async Task Garbage_gets_an_id_zero_failure(string? raw)
    {
        var (d, _, _) = Make();
        var r = Parse(await d.HandleRequestAsync(raw));
        Assert.Equal(0, r.GetProperty("id").GetInt32());
        Assert.False(r.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Host_ops_are_offered_to_the_host_first()
    {
        var (d, host, _) = Make();
        var r = Parse(await d.HandleRequestAsync("""{"id":1,"op":"note.start","args":{"sensitivity":40}}"""));

        Assert.Equal(new[] { "note.start" }, host.HostOps);
        Assert.Equal("host", r.GetProperty("result").GetProperty("handledBy").GetString());
    }

    [Fact]
    public async Task Vision_without_a_frame_says_capture_unavailable()
    {
        var (d, host, _) = Make();
        host.Frame = null;

        var a = Parse(await d.HandleRequestAsync("""{"id":1,"op":"vision.analyze","args":{"x":0,"y":0,"w":10,"h":10}}"""));
        var m = Parse(await d.HandleRequestAsync("""{"id":2,"op":"vision.motion","args":{}}"""));

        Assert.True(a.GetProperty("ok").GetBoolean());          // the envelope succeeded…
        Assert.False(a.GetProperty("result").GetProperty("ok").GetBoolean()); // …the op said no
        Assert.Equal("capture-unavailable", a.GetProperty("result").GetProperty("error").GetString());
        Assert.Equal(0.0, m.GetProperty("result").GetProperty("motion").GetDouble());
    }

    [Fact]
    public async Task Mp3_pick_folder_cancelled()
    {
        var (d, host, _) = Make();
        host.PickedFolder = null;
        var r = Parse(await d.HandleRequestAsync("""{"id":1,"op":"mp3.pickFolder"}"""));

        Assert.False(r.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal("cancelled", r.GetProperty("result").GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mp3_pick_folder_returns_the_choice()
    {
        var (d, host, _) = Make();
        host.PickedFolder = @"C:\Music";
        var r = Parse(await d.HandleRequestAsync("""{"id":1,"op":"mp3.pickFolder"}"""));

        Assert.True(r.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal(@"C:\Music", r.GetProperty("result").GetProperty("folder").GetString());
    }

    [Fact]
    public void Event_envelope_matches_zero_bridge_js()
    {
        var e = Parse(ZeroBridgeDispatcher.BuildEvent("chat.token", new { streamId = "s1", token = "hi" }));

        Assert.Equal("event", e.GetProperty("op").GetString());
        Assert.Equal("chat.token", e.GetProperty("channel").GetString());
        Assert.Equal("s1", e.GetProperty("data").GetProperty("streamId").GetString());
        Assert.Equal("hi", e.GetProperty("data").GetProperty("token").GetString());
    }

    [Fact]
    public void Response_uses_camel_case_for_result_records()
    {
        var r = Parse(ZeroBridgeDispatcher.BuildResponse(5, true, new LlmStatusInfo(true, "Local", "m", "ready"), null));
        Assert.True(r.GetProperty("result").GetProperty("available").GetBoolean());
        Assert.Equal("Local", r.GetProperty("result").GetProperty("backend").GetString());
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=UG05pxLGwzc&list=x", "UG05pxLGwzc")]
    [InlineData("https://youtu.be/UG05pxLGwzc?t=10", "UG05pxLGwzc")]
    [InlineData("UG05pxLGwzc", "UG05pxLGwzc")]
    [InlineData("https://example.com/not-a-video", null)]
    public void ParseYouTubeId_survived_the_move(string raw, string? expected)
        => Assert.Equal(expected, ZeroBrowserCore.ParseYouTubeId(raw));
}

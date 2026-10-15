using System.Text.Json;
using Agent.Common.Audio;
using Agent.Common.Browser;
using Agent.Common.Voice;
using Agent.Common.Voice.Diarization;
using Xunit;
using ZeroCommon.Tests.Audio;

namespace ZeroCommon.Tests.Browser;

/// <summary>
/// The voice-note pipeline now runs in ZeroCommon over IAudioCapturePlatform. These drive it
/// end to end — note.start through the dispatcher, frames from a fake device, transcript
/// out as a note.transcript event — with a fake STT and no diarizer, so nothing touches a
/// real device, model or the user's settings files.
/// </summary>
[Collection("AudioCapturePlatform")]
[Trait("Category", "Audio")]
public sealed class NotePipelineTests
{
    private sealed class FakeSource : IAudioFrameSource
    {
        public bool IsCapturing { get; private set; }
        public event Action<byte[]>? FrameAvailable;
        public event Action<Exception?>? Stopped;
        public void Start() => IsCapturing = true;
        public void Stop() { IsCapturing = false; Stopped?.Invoke(null); }
        public void Dispose() => IsCapturing = false;
        public void Push(byte[] frame) => FrameAvailable?.Invoke(frame);
    }

    private sealed class FakePlatform(bool mic = true, bool loopback = true) : IAudioCapturePlatform
    {
        public FakeSource? LastMic, LastLoopback;
        public string Name => "fake";
        public bool SupportsMicrophone => mic;
        public bool SupportsLoopback => loopback;
        public IReadOnlyList<AudioDeviceInfo> ListMicrophones() => [new("0", "fake mic", true)];
        public IReadOnlyList<AudioDeviceInfo> ListLoopbackDevices() => [new("out", "fake out", true)];
        public IAudioFrameSource OpenMicrophone(string? deviceId) => LastMic = new FakeSource();
        public IAudioFrameSource OpenLoopback(string? deviceId) => LastLoopback = new FakeSource();
    }

    private sealed class FakeStt : ISpeechToText
    {
        public int Calls;
        public int LastBytes;
        public string ProviderName => "fake";
        public Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<string> TranscribeAsync(byte[] pcm16kMono, string language = "auto", CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            LastBytes = pcm16kMono.Length;
            return Task.FromResult("안녕하세요 테스트입니다");
        }
    }

    private sealed class TestCore(ISpeechToText? stt) : ZeroBrowserCore
    {
        protected override ISpeechToText? CreateSpeechToText(VoiceSettings v) => stt;
        protected override VoiceSettings LoadVoiceSettings() => new()
        {
            SttProvider = SttProviderNames.WhisperLocal, SttLanguage = "ko", VadThreshold = 25,
            SensitivityProfile = "Strict", MicMuted = false,
        };
        protected override DiarizationSettings LoadDiarizationSettings() => new() { Provider = DiarizationProviderNames.Off };
    }

    private sealed class NoHost : IZeroBridgeHost
    {
        public Task<object?>? TryDispatchHostOp(string op, JsonElement? args) => null;
        public Task<string?> PickFolderAsync(string title, string? initialFolder) => Task.FromResult<string?>(null);
        public Task<byte[]?> CaptureFramePngAsync() => Task.FromResult<byte[]?>(null);
        public void UseMp3Folder(string? folder) { }
    }

    private static JsonElement Result(string response) => JsonDocument.Parse(response).RootElement.GetProperty("result").Clone();

    [Fact]
    public async Task Mic_utterance_becomes_a_transcript_event()
    {
        var platform = new FakePlatform();
        using var _ = AudioCapture.Override(platform);
        var stt = new FakeStt();
        using var core = new TestCore(stt);
        var events = new List<(string Channel, object Data)>();
        var d = new ZeroBridgeDispatcher(core, new NoHost(), (c, o) => { lock (events) events.Add((c, o)); });
        using var ev = new ZeroBridgeEvents(core, (c, o) => { lock (events) events.Add((c, o)); });

        var start = Result(await d.HandleRequestAsync("""{"id":1,"op":"note.start","args":{"sensitivity":90,"source":"Microphone"}}"""));
        Assert.True(start.GetProperty("ok").GetBoolean());
        Assert.Equal("Microphone", start.GetProperty("source").GetString());
        Assert.True(Result(await d.HandleRequestAsync("""{"id":2,"op":"note.status"}""")).GetProperty("capturing").GetBoolean());

        // 1 s of speech, then 3 s of silence — longer than any profile's hangover.
        var mic = platform.LastMic!;
        for (var i = 0; i < 20; i++) mic.Push(VoiceActivitySegmenterTests.Loud);
        for (var i = 0; i < 60; i++) mic.Push(VoiceActivitySegmenterTests.Quiet);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (events) if (events.Any(e => e.Channel == "note.transcript")) break;
            await Task.Delay(20);
        }

        string[] channels;
        lock (events) channels = events.Select(e => e.Channel).ToArray();
        Assert.Contains("note.utterance-start", channels);
        Assert.Contains("note.utterance-end", channels);
        Assert.Contains("note.speaking", channels);
        Assert.Contains("note.transcript", channels);
        Assert.Equal(1, stt.Calls);
        Assert.True(stt.LastBytes >= 20 * 1600, $"utterance carried {stt.LastBytes} bytes");

        var transcript = JsonDocument.Parse(ZeroBridgeDispatcher.BuildEvent("x", events.First(e => e.Channel == "note.transcript").Data))
            .RootElement.GetProperty("data");
        Assert.Equal("안녕하세요 테스트입니다", transcript.GetProperty("text").GetString());
        Assert.False(transcript.GetProperty("isPartial").GetBoolean());

        Assert.True(Result(await d.HandleRequestAsync("""{"id":3,"op":"note.stop"}""")).GetProperty("ok").GetBoolean());
        Assert.False(Result(await d.HandleRequestAsync("""{"id":4,"op":"note.status"}""")).GetProperty("capturing").GetBoolean());
        Assert.False(mic.IsCapturing);
    }

    [Fact]
    public async Task A_host_without_capture_says_unsupported_host()
    {
        using var _ = AudioCapture.Override(new UnsupportedAudioCapture());
        using var core = new TestCore(new FakeStt());
        var d = new ZeroBridgeDispatcher(core, new NoHost(), (_, _) => { });

        var note = Result(await d.HandleRequestAsync("""{"id":1,"op":"note.start"}"""));
        Assert.False(note.GetProperty("ok").GetBoolean());
        Assert.Equal("unsupported-host", note.GetProperty("error").GetString());

        var music = Result(await d.HandleRequestAsync("""{"id":2,"op":"music.start","args":{"source":"SystemLoopback"}}"""));
        Assert.False(music.GetProperty("ok").GetBoolean());
        Assert.Equal("unsupported-host", music.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Stt_off_is_reported_before_any_device_opens()
    {
        var platform = new FakePlatform();
        using var _ = AudioCapture.Override(platform);
        using var core = new TestCore(stt: null);
        var d = new ZeroBridgeDispatcher(core, new NoHost(), (_, _) => { });

        var r = Result(await d.HandleRequestAsync("""{"id":1,"op":"note.start"}"""));
        Assert.Equal("stt-off", r.GetProperty("error").GetString());
        Assert.Null(platform.LastMic);
    }

    [Fact]
    public async Task Loopback_path_opens_the_loopback_device()
    {
        var platform = new FakePlatform();
        using var _ = AudioCapture.Override(platform);
        using var core = new TestCore(new FakeStt());
        var d = new ZeroBridgeDispatcher(core, new NoHost(), (_, _) => { });

        var r = Result(await d.HandleRequestAsync("""{"id":1,"op":"note.start","args":{"source":"SystemLoopback","loopbackChunkSec":5}}"""));
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.NotNull(platform.LastLoopback);
        Assert.Null(platform.LastMic);
        await d.HandleRequestAsync("""{"id":2,"op":"note.stop"}""");
    }
}

[CollectionDefinition("AudioCapturePlatform", DisableParallelization = true)]
public sealed class AudioCapturePlatformCollection;

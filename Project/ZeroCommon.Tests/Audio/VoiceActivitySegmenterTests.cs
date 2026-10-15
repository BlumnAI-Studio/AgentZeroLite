using Agent.Common.Audio;
using Xunit;

namespace ZeroCommon.Tests.Audio;

/// <summary>
/// The VAD moved down from WPF's VoiceCaptureService; these pin the behaviour the voice
/// note and AgentBot voice depend on, now that both hosts run it.
/// </summary>
[Trait("Category", "Audio")]
public sealed class VoiceActivitySegmenterTests
{
    // 50 ms of 16 kHz mono PCM16 = 800 samples = 1600 bytes — the microphone's frame.
    internal static byte[] Frame(short level)
    {
        var b = new byte[1600];
        for (var i = 0; i < b.Length; i += 2) { b[i] = (byte)(level & 0xFF); b[i + 1] = (byte)((level >> 8) & 0xFF); }
        return b;
    }

    internal static readonly byte[] Loud = Frame(16384);  // RMS 0.5
    internal static readonly byte[] Quiet = Frame(0);

    private static (VoiceActivitySegmenter Vad, List<string> Log) Make(int hangover = 4)
    {
        var vad = new VoiceActivitySegmenter { VadThreshold = 0.1f, UtteranceHangoverFrames = hangover, SilenceHangoverFrames = 2, BufferPcm = true, PreRollSeconds = 0.1 };
        var log = new List<string>();
        vad.UtteranceStarted += () => { log.Add("start"); vad.SeedBufferWithPreRoll(); };
        vad.UtteranceEnded += () => log.Add("end");
        vad.SpeakingStateChanged += s => log.Add(s ? "speaking" : "quiet");
        return (vad, log);
    }

    [Fact]
    public void Rms_of_a_constant_signal()
    {
        Assert.Equal(0.5f, VoiceActivitySegmenter.Rms(Loud, Loud.Length), 3);
        Assert.Equal(0f, VoiceActivitySegmenter.Rms(Quiet, Quiet.Length));
    }

    [Fact]
    public void An_utterance_ends_after_the_hangover_and_carries_preroll_speech_and_tail()
    {
        var (vad, log) = Make(hangover: 4);
        vad.Process(Quiet);
        vad.Process(Quiet);                     // pre-roll ring (0.1 s = 2 frames)
        for (var i = 0; i < 3; i++) vad.Process(Loud);
        for (var i = 0; i < 4; i++) vad.Process(Quiet);

        Assert.Equal(new[] { "speaking", "start", "quiet", "end" }, log);
        // Seed = ring at trigger (1 quiet + trigger frame, capped to 0.1 s) + 2 more loud + 4 tail.
        var pcm = vad.ConsumePcmBuffer();
        Assert.Equal((2 + 2 + 4) * 1600, pcm.Length);
        Assert.Empty(vad.ConsumePcmBuffer());
    }

    [Fact]
    public void A_short_pause_does_not_split_the_utterance()
    {
        var (vad, log) = Make(hangover: 4);
        vad.Process(Loud);
        for (var i = 0; i < 3; i++) vad.Process(Quiet); // under the hangover
        vad.Process(Loud);
        for (var i = 0; i < 4; i++) vad.Process(Quiet);

        Assert.Equal(1, log.Count(e => e == "start"));
        Assert.Equal(1, log.Count(e => e == "end"));
    }

    [Fact]
    public void Muted_frames_move_the_meter_but_not_the_state()
    {
        var (vad, log) = Make();
        var levels = new List<float>();
        vad.AmplitudeChanged += levels.Add;
        vad.Muted = true;
        for (var i = 0; i < 5; i++) vad.Process(Loud);

        Assert.Equal(5, levels.Count);
        Assert.Empty(log);
        Assert.Empty(vad.ConsumePcmBuffer());
    }

    [Fact]
    public void The_duration_cap_force_ends_a_runaway_utterance()
    {
        var (vad, log) = Make(hangover: 1000);
        vad.MaxUtteranceSeconds = 0.25;            // 8000 bytes = 5 frames
        for (var i = 0; i < 12; i++) vad.Process(Loud);

        Assert.Contains("end", log);
    }

    [Fact]
    public void Loopback_buffer_accumulates_and_reports_level()
    {
        var buf = new PcmCaptureBuffer { BufferPcm = true };
        var levels = new List<float>();
        var frames = 0;
        buf.AmplitudeChanged += levels.Add;
        buf.PcmFrameAvailable += _ => frames++;
        buf.Process(Loud);
        buf.Process(Loud);

        Assert.Equal(2, frames);
        Assert.Equal(2, levels.Count);
        Assert.Equal(3200, buf.PeekPcmBuffer().Length);
        Assert.Equal(3200, buf.ConsumePcmBuffer().Length);
        Assert.Empty(buf.PeekPcmBuffer());
    }
}

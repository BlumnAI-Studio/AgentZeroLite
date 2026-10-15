using System.Runtime.InteropServices;
using Agent.Common.Audio;
using Agent.Common.Audio.Windows;
using Xunit;
using Xunit.Abstractions;

namespace ZeroCommon.Tests.Audio;

/// <summary>
/// The real Windows devices, opened through the same IAudioCapturePlatform the plugins use.
/// Opt-in (<c>AGENTZERO_AUDIO_LIVE=1</c>): it needs a microphone, an output device, and
/// plays a short tone through the speakers to have something to loop back.
/// </summary>
[Collection("AudioCapturePlatform")]
[Trait("Category", "AudioLive")]
public sealed class WindowsAudioLiveTests(ITestOutputHelper output)
{
    private static bool Enabled =>
        OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("AGENTZERO_AUDIO_LIVE") == "1";

    [Fact]
    public void The_platform_on_windows_is_the_naudio_one()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("windows-naudio", AudioCapture.Platform.Name);
        Assert.True(AudioCapture.Platform.SupportsMicrophone);
        Assert.True(AudioCapture.Platform.SupportsLoopback);
    }

    [Fact]
    public async Task Microphone_delivers_16k_mono_frames()
    {
        if (!Enabled) return;
        var platform = new WindowsAudioCapture();
        foreach (var d in platform.ListMicrophones()) output.WriteLine($"mic {d.Id} '{d.Name}' default={d.IsDefault}");

        using var mic = platform.OpenMicrophone(null);
        long bytes = 0; var frames = 0;
        mic.FrameAvailable += f => { Interlocked.Add(ref bytes, f.Length); Interlocked.Increment(ref frames); };
        mic.Start();
        await Task.Delay(1000);
        mic.Stop();

        output.WriteLine($"mic: {frames} frames, {bytes} bytes in ~1 s");
        // 1 s of 16 kHz mono PCM16 = 32 000 bytes; allow start-up latency.
        Assert.InRange(bytes, 16_000, 48_000);
    }

    [Fact]
    public async Task Loopback_hears_what_the_speakers_play()
    {
        if (!Enabled) return;
        var platform = new WindowsAudioCapture();
        foreach (var d in platform.ListLoopbackDevices()) output.WriteLine($"out '{d.Name}' default={d.IsDefault}");

        using var lb = platform.OpenLoopback(null);
        long bytes = 0; float peak = 0;
        lb.FrameAvailable += f =>
        {
            Interlocked.Add(ref bytes, f.Length);
            var rms = VoiceActivitySegmenter.Rms(f, f.Length);
            if (rms > peak) peak = rms;
        };
        lb.Start();
        PlayTone(seconds: 1.0);
        await Task.Delay(1500);
        lb.Stop();

        output.WriteLine($"loopback: {bytes} bytes, peak RMS {peak:F3}");
        Assert.True(bytes > 16_000, "no loopback audio arrived while a tone played");
        Assert.True(peak > 0.01f, "loopback frames were silent while a tone played");
    }

    // A 440 Hz tone through winmm, async — the loopback should hear it.
    private static void PlayTone(double seconds)
    {
        const int rate = 16_000;
        var samples = (int)(rate * seconds);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(samples * 2);
            for (var i = 0; i < samples; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000));
        }
        _wav = ms.ToArray();
        _pin = GCHandle.Alloc(_wav, GCHandleType.Pinned);
        PlaySound(_pin.AddrOfPinnedObject(), IntPtr.Zero, 0x0004 | 0x0001 | 0x0002); // MEMORY | ASYNC | NODEFAULT
    }

    private static byte[]? _wav;
    private static GCHandle _pin;

    [DllImport("winmm.dll")]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}

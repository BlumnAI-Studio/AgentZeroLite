# Audio capture pipeline — mic + WASAPI loopback

**Owner**: music-curator
**Lifecycle**: convention — binding for any change to capture paths in `Project/AgentZeroWpf/Services/Music/`
**Last updated**: 2026-06-06

## Why two paths

AST AudioSet expects a single canonical input: 16 kHz mono PCM16. But
*where* the audio comes from differs by user intent:

| User intent | Source | Service |
|---|---|---|
| "I want to analyse my own playing right now" | Microphone | `VoiceCaptureService` (existing Voice tab pipeline) |
| "I want to analyse what's playing on this PC's speakers" | Render endpoint (Spotify, YouTube, game, Zoom) | `LoopbackCaptureService` (new for Music) |

The mic path was free — `VoiceCaptureService` already produces 16 kHz
mono. The loopback path is the work this knowledge file is about.

## WASAPI loopback — the format problem

`WasapiLoopbackCapture` captures the mixer output of a render endpoint.
The format is **whatever Windows is mixing for that device**, which on
modern hardware is almost always:

- **Sample rate**: 48 000 Hz (sometimes 44 100 Hz; rarely 96 000 / 192 000)
- **Channels**: 2 (rarely 1, occasionally 6/8 for surround virtual devices)
- **Encoding**: IEEE Float 32-bit (rarely PCM 16/24)

AST requires 16 000 / 1 / PCM16. So every loopback capture needs a
**format normalization stage** between the raw frames and the rolling
PCM buffer.

## The NAudio sample-provider chain

`LoopbackCaptureService.Start` builds this chain on the WASAPI capture's
known format:

```
WasapiLoopbackCapture (e.g. 48 kHz / 2 ch / float32)
       │ DataAvailable (raw bytes)
       ▼
BufferedWaveProvider (rawFormat, 32 MB buffer, DiscardOnOverflow)
       │
       ▼
ISampleProvider  (.ToSampleProvider() — float samples)
       │
       ▼  (if channels > 1)
StereoToMonoSampleProvider (0.5 L + 0.5 R)
       │
       ▼  (if sampleRate != 16000)
WdlResamplingSampleProvider(16000)
       │
       ▼
SampleToWaveProvider16 (float → int16 little-endian)
       │
       ▼
byte[] (the 16 kHz mono PCM16 AST wants)
```

### Why this exact chain

- **`BufferedWaveProvider` first** — decouples the WASAPI capture thread
  from the sample-provider pull. Raw bytes go in immediately; the
  conversion happens on the same `DataAvailable` thread but in a `while`
  loop pulling as much PCM16 as the converter will yield. Headroom of
  32 MB ≈ 80 s of 48 kHz stereo float survives ONNX warm-up stalls.
- **`StereoToMono` before resampler** — fewer samples to resample (half
  the work for stereo input). Order matters for CPU but not correctness.
- **`WdlResamplingSampleProvider`** — high-quality cross-rate resampler,
  no aliasing artifacts at the bands AST cares about. Don't substitute
  the cheaper `MediaFoundationResampler` (Win32 dependency,
  bigger startup cost, no quality win).
- **`SampleToWaveProvider16`** — last step. AST reads PCM16, not float.

### Pulling output — the inner `while` loop

```csharp
while (true)
{
    int read = _pcm16Provider.Read(_pullScratch, 0, _pullScratch.Length);
    if (read <= 0) break;
    // ... RMS for level meter, append to _pcmBuffer, fan out to PcmFrameAvailable
}
```

The loop is critical. `Read()` returns *some* bytes per call; the
converter may have multiple resampled chunks ready from one input batch.
A single `Read` call leaves audio stuck inside the chain → next call
returns 0 even though more is buffered → you lose ~50 ms of audio per
capture frame. Always drain.

## MMDevice lifecycle — must Dispose

`MMDeviceEnumerator` enumeration returns `MMDevice` instances that hold
COM references. Forgetting `Dispose` leaks COM until process exit.

```csharp
using var enumerator = new MMDeviceEnumerator();
using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
// ...
foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
{
    list.Add(new LoopbackDevice(d.ID, d.FriendlyName, d.ID == defaultId));
    d.Dispose(); // ← required, foreach doesn't dispose enumerable items
}
```

`MMDeviceEnumerator` itself is `IDisposable` in NAudio 2.x — `using` it.
`MMDevice` instances inside an enumeration are NOT auto-disposed by
`foreach` — call `Dispose()` explicitly.

## Default-endpoint fallback

The Music tab persists the chosen render endpoint by MMDevice ID. That
ID embeds the device GUID and is invalidated by:

- Unplugging the headphones / speakers
- Switching audio interface
- Sleep/wake cycles on some Realtek drivers

If `_settings.LoopbackDeviceId` doesn't resolve, fall back to
`Role.Console` default — the tab's UI offers `(Default — current Windows
playback device)` as the first dropdown row precisely for this case.
Don't throw on "device not found"; pick default + log a warning.

## PcmFrameAvailable event pattern

The capture service has two consumers in the Music tab:

1. **Rolling PCM buffer** (for the 1.5 s inference cadence)
2. **`SpectrumBars` analyzer** (for the ~30 Hz visual repaint)

The original design accumulated into `_pcmBuffer` and let the caller
poll. That works for one consumer but fans out poorly. The cleaner shape:

```csharp
public event Action<byte[]>? PcmFrameAvailable;
```

Fires from the WASAPI capture thread immediately after the inner pull
loop produces a converted PCM16 chunk. Subscribers:

- Music tab handler appends to rolling buffer + pushes to `SpectrumBars`
- Future consumers (recording-to-file, live transcript, …) attach
  without needing the buffer-drain dance

`BufferPcm = false` is the default — let the event do the work; only
flip it on when a caller needs the buffer-drain pattern instead of an
event subscription.

## Capture thread → UI thread

WASAPI fires `DataAvailable` from its own thread. Three things must hop
the WPF dispatcher:

1. **Spectrum bar repaint** — `Dispatcher.BeginInvoke` at `Render`
   priority, throttled to ~30 Hz via a `DateTime _lastPaint` check
2. **Level meter (`AmplitudeChanged`)** — same `BeginInvoke` pattern;
   safe to fire every chunk because it just sets one `Value` property
3. **Top-K label list** — fired from the inference loop background
   task, also via `BeginInvoke`

Never call into WPF controls directly from `OnDataAvailable` — guaranteed
freeze at first capture.

## Pitfalls catalogue

| # | Pitfall | Symptom | Fix |
|---|---|---|---|
| L1 | Forget `while (read > 0)` loop on resampler output | ~50 ms of audio lost per capture frame, spectrum stutters | Drain in a `while` loop |
| L2 | Don't dispose `MMDevice` enumerated entries | COM leak (visible as "Audio Device" handles climbing in Process Explorer) | Explicit `d.Dispose()` after each use |
| L3 | Persist render endpoint by FriendlyName instead of MMDevice ID | Breaks when user has two devices with same name (laptop dock + builtin), or on driver rename | Persist `MMDevice.ID` (GUID-form), display FriendlyName |
| L4 | Assume float32 input — hardcode `float[]` conversion | Crashes on PCM16-only devices (some pro audio interfaces) | Go through the sample-provider chain which handles both |
| L5 | Re-create `WasapiLoopbackCapture` per `DataAvailable` | Format negotiation overhead 10–20 ms per frame; tab feels laggy | Create on `Start`, reuse, dispose on `Stop` |
| L6 | Capture the default device once at `Start`, never re-check | User switches default playback mid-test → capture continues on the old (now-silent) device | Re-resolve default on each `Start` (already done) |
| L7 | Set `BufferPcm = true` AND subscribe to `PcmFrameAvailable` | Double-counted memory; rolling buffer grows from both sources | Pick one: events for fan-out, buffer for single consumer |
| L8 | Forget `DiscardOnBufferOverflow = true` on the BufferedWaveProvider | If ONNX warms slowly the buffer fills, then throws `InvalidOperationException`, killing capture | Discard on overflow; the rolling buffer downstream is the source of truth |

## Reference

- NAudio docs (WASAPI Capture): <https://github.com/naudio/NAudio/blob/master/Docs/WasapiCapture.md>
- Sample provider chain reference: `Project/AgentZeroWpf/Services/Music/LoopbackCaptureService.cs`
- The mic side is `Project/AgentZeroWpf/Services/Voice/VoiceCaptureService.cs` — owned by Voice subsystem, used as-is by Music for the mic path.

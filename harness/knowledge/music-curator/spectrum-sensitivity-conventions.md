# Spectrum sensitivity conventions

**Owner**: music-curator
**Lifecycle**: convention — binding for any change to `SpectrumBars` or the Music tab spectrum canvas
**Last updated**: 2026-06-06

## Why this file exists

The first cut of the Music tab spectrum bars had every band saturate at
100% the moment any real audio hit the input. The bug shipped because
the FFT power values were on the wrong scale for the dB range I'd
defaulted to. This file pins the correct normalization and dB defaults
so the next iteration doesn't regress.

## The math (short version)

For a 2048-point FFT of 16 kHz mono PCM normalized to [-1, +1] with a
Hann window:

- **Peak FFT magnitude** for a unit-amplitude sine wave at bin `k`:
  `|X[k]| = (N/2) * coherent_gain(window)`
- Hann window `coherent_gain = mean(window) ≈ 0.5`
- So peak `|X[k]| = N/4 = 512` for `N = 2048`
- Peak power `|X[k]|² = (N/4)² = 262144`

If you take raw `|X|²` and put it through `10 * log10(...)`, a digital
full-scale sine returns **+54 dB**, not 0. With a "ceiling" of `-10 dB`,
even quiet music (`-30 dBFS` per bin) registers around `+24 dB` raw,
which is 34 dB **above** ceiling → saturate at 1.0 → bars always full.

## The fix — normalize first, then dB

```csharp
// Hann window peak power for unit sine = (N/4)²
private readonly float _powerNormFactor = 1f / ((FftSize / 4f) * (FftSize / 4f));

// Per bin:
float power = (real * real + imag * imag) * _powerNormFactor;
float db    = power > 0 ? 10f * MathF.Log10(power) : floorDb;
```

After this normalization, `0 dB = digital full-scale sine`. The dB
scale finally matches what audio engineers call dBFS, and standard
defaults work:

- **Floor**: `-60 dBFS` (typical music noise floor)
- **Ceiling**: `-3 dBFS` (just under digital clipping headroom)

## Calibration table (after the fix)

| Input signal | dBFS | Bar height |
|---|---:|---:|
| Digital silence | -∞ | 0% |
| Very quiet (background noise) | -50 dBFS | ~17% |
| Quiet music | -40 dBFS | ~35% |
| Typical music | -30 dBFS | ~53% |
| Loud music | -20 dBFS | ~70% |
| Very loud | -10 dBFS | ~88% |
| Full volume / near-clipping | -3 dBFS | 100% |

The expected band heights for *real music* with this calibration is
20–80% across the active bands, leaving headroom on both ends. If
everything sits at 100% again, the normalization regressed; if
everything sits at 0–10%, someone tightened the ceiling.

## Band layout

64 log-spaced bands from 30 Hz to 8000 Hz (Nyquist for 16 kHz). The
band edges are geometric:

```csharp
float loHz = MathF.Exp(logMin + (logMax - logMin) * b       / nBars);
float hiHz = MathF.Exp(logMin + (logMax - logMin) * (b + 1) / nBars);
```

Per band we take the **peak power** of the FFT bins inside `[loHz, hiHz)`,
not the sum. Music has sharp tonal peaks; peak shows them better than
energy sum (which smears narrow peaks into wide blobs).

Why 64 bars: enough to resolve a guitar's harmonics, few enough that
each bar is wide enough to perceive at typical settings tab widths
(canvas usually 400–600 px → 6–9 px per bar).

Why log frequency: matches musical perception. An octave is a doubling;
log mapping gives equal screen space per octave instead of cramming
everything below 1 kHz into 6 bars and giving 58 bars to the inaudible
high mids.

## Rendering contract (WPF)

The bars are WPF `Rectangle` elements parented to a `Canvas`, one per
band, with cyan→magenta color gradient by index:

```csharp
double t = (double)i / (SpectrumBarCount - 1);
byte r = (byte)(0 + t * 255);                 // 0 → 255
byte g = (byte)(229 + t * (45 - 229));        // 229 → 45
byte b = (byte)(255 + t * (149 - 255));       // 255 → 149
var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
brush.Freeze();  // ← required for cross-thread safety + perf
```

Bars grow from the bottom of the canvas — for each repaint we set:

```csharp
_musicSpectrumBars[i].Height = barH;
Canvas.SetTop(_musicSpectrumBars[i], canvasHeight - barH);
```

Don't try `LayoutTransform` ScaleY — kills FPS by triggering full layout
pass per bar per frame.

## Repaint throttle

Capture frames arrive at WASAPI's natural cadence (~10 ms per chunk).
Repainting at that rate burns CPU on the dispatcher for no visible
benefit (the human eye can't resolve > 60 Hz changes meaningfully on a
spectrum). Throttle to ~30 Hz:

```csharp
private DateTime _musicLastSpectrumPaint = DateTime.MinValue;

private void OnMusicLivePcmFrame(byte[] pcm) {
    // ... push to ring + rolling buffer always
    var now = DateTime.UtcNow;
    if ((now - _musicLastSpectrumPaint).TotalMilliseconds < 33) return;
    _musicLastSpectrumPaint = now;
    // ... dispatcher BeginInvoke to repaint
}
```

The push to the `SpectrumBars` ring stays per-frame (cheap, no UI);
only the FFT-compute + dispatcher-hop is throttled.

## The Color collision gotcha

The AgentZeroWpf project enables both `<UseWPF>true</UseWPF>` and
`<UseWindowsForms>true</UseWindowsForms>`. That brings two `Color` and
two `Rectangle` types into scope at every `using System.Drawing` or
`using System.Windows.Shapes` import. The Music tab handler pins the
WPF variants explicitly:

```csharp
using System.Windows.Media;             // Color, SolidColorBrush
using Rectangle = System.Windows.Shapes.Rectangle;  // alias
```

DON'T `using System.Windows.Shapes;` — that re-introduces ambiguity in
files that already `using System.Drawing;` for Forms code elsewhere. The
alias is surgical.

## Future: sensitivity slider

A user-facing sensitivity slider would shift `floorDb` and `ceilDb`
together (preserve dynamic range, slide the visible window up/down):

```csharp
float center = (floorDb + ceilDb) / 2f;      // -31.5 dB by default
float halfRange = (ceilDb - floorDb) / 2f;   // 28.5 dB by default
center += userOffsetDb;
return ComputeBars(nBars, ..., floorDb: center - halfRange,
                              ceilDb:  center + halfRange);
```

Add `MusicSettings.SpectrumSensitivityDb` (signed offset, default 0)
when this lands. Don't expose floor/ceil individually — too easy to
misconfigure into "bars all 0" or "bars all 100" territory again.

## Reference

- Implementation: `Project/ZeroCommon/Music/SpectrumBars.cs`
- Renderer: `Project/AgentZeroWpf/UI/Components/SettingsPanel.Music.cs`
  → `BuildSpectrumBars` / `UpdateSpectrumBars`
- Hann window properties: <https://en.wikipedia.org/wiki/Window_function#Hann_and_Hamming_windows>
- dBFS reference: <https://en.wikipedia.org/wiki/DBFS>

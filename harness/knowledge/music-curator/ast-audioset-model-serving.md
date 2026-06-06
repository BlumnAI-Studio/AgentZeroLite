# AST AudioSet — model card + serving contract

**Owner**: music-curator
**Lifecycle**: convention — binding for any change to `OnnxAstClassifier` or `AstModelDownloader`
**Last updated**: 2026-06-06

## Model summary

- **Official ID**: `MIT/ast-finetuned-audioset-10-10-0.4593`
- **Paper**: Gong et al., *AST: Audio Spectrogram Transformer* (Interspeech 2021)
- **Params**: 86.6M
- **License**: BSD-3-Clause (HuggingFace card)
- **mAP on AudioSet eval**: 0.459 (the `0.4593` suffix in the model name)
- **Pretraining**: ImageNet-21k → ImageNet-1k (ViT init) → AudioSet
- **Task**: multi-label audio classification, 527 AudioSet classes

## Input contract — exact

| Field | Value |
|---|---|
| Sample rate | 16 000 Hz (model is single-rate; resample anywhere else *before* preprocessing) |
| Channels | 1 (mono — average down stereo) |
| Window length | 10 s = 160 000 samples |
| Mel bins | 128 (`num_mel_bins=128`) |
| Frames | 1024 = `ceil((160000 - 400) / 160) + padding` |
| Frame length | 400 samples = 25 ms |
| Frame shift | 160 samples = 10 ms |
| Window function | **Povey window** (Kaldi default) — reference; this build uses Hann as approximation |
| Pre-emphasis | 0.97 (Kaldi default) — reference; **NOT applied** in this build |
| Normalization | `(x - mean) / (std * 2)` with `mean = -4.2677393`, `std = 4.5689974` |
| Tensor shape | `(batch=1, time=1024, freq=128)`, float32 |
| Tensor input name | resolved at load from `InferenceSession.InputMetadata.Keys.First()` — do **not** hardcode `"input_values"` |

## Output contract

- **Shape**: `(1, 527)` — raw logits, **NOT** probabilities
- **Activation**: sigmoid (multi-label, classes overlap), one score per class
- **Label index**: matches Google's `class_labels_indices.csv` row order
  (header `index,mid,display_name`)

## Known preprocessing drift in this build

The reference preprocessing is `torchaudio.compliance.kaldi.fbank` with
the Povey window + 0.97 pre-emphasis + `dither=0.0` + energy floor. This
build (`Project/ZeroCommon/Music/MelSpectrogram.cs`) uses **Hann window
+ HTK mel scale + no pre-emphasis** as a self-contained .NET
approximation that avoids adding `TorchSharp` or `KaldiSharp`
dependencies for one file.

### Measured impact

- **Clear single instruments** (piano, drum kit, electric guitar, violin,
  trumpet, voice) — top-K labels match the reference within ±1 position
- **Environmental sounds / sound effects** — small drift (~2-5% score
  delta on the same clip); top-K still meaningful
- **Multi-instrument fusion / sparse mixes** — biggest drift area;
  occasional label swaps that don't happen with Kaldi parity

### Refinement plan (open follow-up)

If the drift becomes user-visible:

1. **Option A** — port Kaldi `fbank` to pure C# (~150 LOC: Povey window
   + pre-emphasis filter + Slaney mel + energy floor). No new
   dependencies. ~1 day of work + a regression test fixture against
   `librosa` reference outputs.
2. **Option B** — call `torchaudio` through a Python sidecar process
   (same pattern as Supertonic). Adds Python install requirement; not
   worth it for one preprocessing step.

Option A is the agreed default. Track in a new mission if/when filed.

## ONNX export — `onnx-community` mirror

The first iteration **does not** depend on a local Python `optimum-cli`
export. The community has already published a pre-converted ONNX bundle:

- **Repo**: `onnx-community/ast-finetuned-audioset-10-10-0.4593-ONNX`
- **Path**: `onnx/model.onnx` (also `model_fp16.onnx`, `model_int8.onnx`,
  `model_q4.onnx`, `model_quantized.onnx`, `model_bnb4.onnx`, …)
- **Default we ship**: full fp32 `model.onnx` (347 MB) — best accuracy,
  no quantization artifacts on the spectrum visualization, CPU-friendly

### Variant matrix (for future toggles)

| File | Size | Use when |
|---|---|---|
| `model.onnx` (fp32) | 347 MB | Default — accuracy first |
| `model_fp16.onnx` | 173 MB | Half the disk + memory; ~no accuracy hit on most cards; ORT CPU may auto-fallback to fp32 internally |
| `model_int8.onnx` / `model_quantized.onnx` / `model_uint8.onnx` | ~91 MB | Older hardware; small mAP drop (~1-2 pp) |
| `model_q4.onnx`, `model_bnb4.onnx`, `model_q4f16.onnx` | 51-60 MB | Aggressive — only if installer size becomes critical |

### Direct URLs (stable, public CDN)

```
https://huggingface.co/onnx-community/ast-finetuned-audioset-10-10-0.4593-ONNX/resolve/main/onnx/model.onnx
https://raw.githubusercontent.com/YuanGongND/ast/master/egs/audioset/data/class_labels_indices.csv
```

The labels CSV comes from the AST paper authors' own preprocessing repo
(YuanGongND/ast) — canonical for index ordering. `class_labels_indices.csv`
schema: `index,mid,display_name` with quoted display name when commas
present.

## Cache layout

```
%LOCALAPPDATA%\AgentZeroLite\models\ast-audioset\
├── model.onnx                  ~ 347 MB, fp32
└── class_labels_indices.csv    ~ 14 KB, 527 rows + header
```

Both resolved via `MusicSettingsStore.ResolveModelPath` /
`ResolveLabelsPath`. Missing labels CSV degrades top-K output to
`class_<index>` strings rather than throwing — intentional graceful
degradation.

## Adding a new music model — checklist

When the next model lands (MERT, CLAP, …), the music-curator agent
walks the requester through:

1. **New `IMusicClassifier` implementation** in `Project/ZeroCommon/Music/`.
   Keep `RequiredSampleRate` + `RequiredDurationSeconds` as instance
   properties — different models have different windows.
2. **Preprocessing module** in the same file or beside `MelSpectrogram.cs`
   — never assume the next model wants AST's mean/std normalization.
3. **Provider name constant** in `MusicClassifierProviderNames`.
4. **Settings field** in `MusicSettings` if model-specific (e.g.
   `MertVersion = "95M" | "330M"`).
5. **Settings tab UI** — Provider ComboBox gains a new `<ComboBoxItem
   Tag="...">`. The UI is already designed to accept multiple options;
   it was just locked to AST for the first iteration.
6. **Download path** — extend `AstModelDownloader` or add a sibling
   downloader, share the `ModelDownloadDialog` contract.
7. **Knowledge entry** — new `harness/knowledge/music-curator/<model>-model-serving.md`
   alongside this one.

## References

- AST paper: <https://arxiv.org/abs/2104.01778>
- AST HuggingFace card: <https://huggingface.co/MIT/ast-finetuned-audioset-10-10-0.4593>
- ONNX mirror: <https://huggingface.co/onnx-community/ast-finetuned-audioset-10-10-0.4593-ONNX>
- AudioSet ontology: <https://research.google.com/audioset/ontology/index.html>
- YuanGongND/ast (label CSV source): <https://github.com/YuanGongND/ast>

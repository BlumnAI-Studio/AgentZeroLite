---
date: 2026-10-21T10:13:53+09:00
agent: test-runner
type: evaluation
mode: execution
trigger: "전체 유닛테스트 수행해 — 맥미니에서 빌드 및 유닛테스트 성공 여부 체크 (WPF 제외)"
---

# macOS (Mac mini) — build + headless test check

## Why this run

The operator asked outright: the machine is a Mac mini, a macOS build recently
succeeded, check that build and unit tests pass. WPF (and anything Windows-TFM)
is excluded — it is maintained on the Windows machine.

## Environment

- Darwin 24.6.0 (arm64), `global.json` pins SDK 10.0.0 / rollForward latestMajor.
- **Trap**: `dotnet` on PATH is `/usr/local/share/dotnet` (9.0.305 only) and fails
  SDK resolution. The .NET 10 SDK (10.0.402) is under `~/.dotnet`. All runs used
  `DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`.

## Scope

Excluded: `AgentZeroWpf`, `AgentTest` (WPF), `ZeroWearable` (net10.0-windows10.0.19041.0),
`ZeroWearableDevice` (ESP-IDF), `LlmProbe`.

| Project | Build (Debug) | Tests |
|---|---|---|
| ZeroCommon | ✅ 0 errors | — |
| AgentZeroAvalonia | ✅ 0 errors | — |
| AgentOne | ✅ 0 errors | — |
| ZeroCommon.Tests | ✅ 0 errors | ❌ 1185 passed / **6 failed** / 25 skipped (1216) |
| AgentZeroAvalonia.Tests | ✅ 0 errors | ✅ 104 / 104 |
| AgentOne.Tests | ✅ 0 errors | ✅ 680 / 680 (28 s) |

Warnings worth noting: NU1903 high-severity advisories on
`System.Security.Cryptography.Pkcs` / `.Xml` 10.0.11 (transitive via ZeroCommon).

## The 6 failures — all Windows-path fixtures

On Unix `\` is not a directory separator, so `Path.GetFileName` / `GetRelativePath`
treat `C:\music\a.mp3` as one file name. Each failing case feeds a Windows literal:

| Test | Fixture | Result on macOS |
|---|---|---|
| `Mp3ScannerTests.ToRelativePath_MapsUnderRootOnly` ×2 | `C:\music` / `C:\music\…\a.mp3` | `""` |
| `SshCommandBuilderTests.DetectShellKind_…` ×2 | `C:\Windows\System32\cmd.exe`, `C:\Program Files\PowerShell\7\pwsh.exe` | `Other` |
| `SshCommandBuilderTests.ComposeArguments_powershell_7_with_pem_quotes_the_path` | `C:\Program Files\PowerShell\7\pwsh.exe` | no `-NoExit -Command` prefix (shell → Other) |
| `WearableSettingsTests.Normalize_names_aliasless_roots_…` | `D:\Media\Music` | alias `d--media-music` instead of `music` |

Judgement: test-portability defects, not macOS product bugs — on macOS these code
paths receive POSIX paths (Mp3 roots under `/Users/…`), the shell is zsh (Other is
correct), and the wearable host is Windows-only. The bare-name cases of the same
theories (`cmd.exe`, `pwsh.exe`) pass.

## 평가

| 축 | 평가 |
|---|---|
| 코드 안전성 | B — product code builds clean on macOS; NU1903 crypto advisories open |
| 아키텍처 정합성 | A — ZeroCommon / Avalonia / AgentOne all build on plain net10.0 without Windows pieces |
| 테스트 가능성 | C — 6 ZeroCommon tests assume Windows path semantics and are not OS-gated, so the headless suite is not green on macOS |

## 다음 단계 제안

1. Gate the 6 cases to Windows (or give them POSIX twins) so `ZeroCommon.Tests` is
   green on macOS — the repo already uses `OperatingSystem.IsWindows()` skips in
   other test classes.
2. Bump/pin `System.Security.Cryptography.Pkcs` / `.Xml` past 10.0.11 for NU1903.
3. Put `~/.dotnet` first on PATH on this Mac (or remove the 9.0-only system install)
   so `dotnet` resolves the pinned SDK without the env override.

## Follow-up — OS-split fixtures (same session, author-verify)

Operator asked to split the cases by OS, keeping the Windows ones as they passed on Windows.
Using the already-referenced `Xunit.SkippableFact`:

- Windows-literal cases → `[SkippableFact/Theory]` + `Skip.IfNot(OperatingSystem.IsWindows())`
  (data and assertions unchanged).
- POSIX twins added, `Skip.If(OperatingSystem.IsWindows())`:
  `ToRelativePath_MapsUnderRootOnly_Posix` (5), `DetectShellKind_classifies_full_posix_paths` (4),
  `ComposeArguments_powershell_7_with_pem_quotes_the_path_posix`,
  `Normalize_names_aliasless_roots_…_posix`.
- Bare-name `DetectShellKind` rows stay a plain `[Theory]` on both OSes.

Re-run on macOS: `ZeroCommon.Tests` ✅ 1193 passed / 0 failed / 34 skipped (1227).
Not yet re-run on Windows — expected: the Windows cases run as before, the POSIX twins skip.

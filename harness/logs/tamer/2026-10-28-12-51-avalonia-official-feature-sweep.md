---
date: 2026-10-28T12:51:00+09:00
agent: tamer
type: review
mode: full-review
command: "/harness-creator 전체 점검해 (기능추가 관점, 아발로니아 = 공식 AgentZero)"
---

# 전체 점검 — 아발로니아 공식화 이후 기능추가 후보 취합

전제: 공식 "AgentZero" = `Project/AgentZeroAvalonia` (Win/macOS/Linux, MS Store).
WPF는 실험실. 정원에 `full-review` 엔진이 없어 정원지기가 전문가별 관점으로 순차 조사했다.

## 실행 요약

조사 근거:
- `README-Avalonia.md` 전환표의 ⏳ 항목
- WPF 전용 파일 대비 아발로니아 부재 기능 grep (CommandPalette, DiffReview, AutomationScheduler 등)
- CLI 동사 비교: WPF `CliHandler.cs` 대비 `AgentZeroAvalonia/Cli/*`
- 로직이 이미 ZeroCommon에 있는지 확인 (= 전환 비용이 UI뿐인지)
- 보안 표면(`CliIpcBridge` CurrentUserOnly 파이프, 루프백 에셋 서버 토큰), 루프 툴벨트(`WorkspaceToolHost`)
- 테스트 프로젝트 구성, 버전 소스(`AgentZeroWpf/version.txt`를 아발로니아가 읽음)

## 결과

### code-coach — 전환 비용이 거의 UI뿐인 WPF 기능 (ZeroCommon 로직 존재)
| 기능 | ZeroCommon에 있는 것 | 아발로니아 |
|---|---|---|
| 커맨드 팔레트 (Ctrl+J) | `Module/FuzzyMatcher` | 없음 |
| 단축키 설정 탭 | `ShortcutSettings`, `WindowCommandIds` (HotkeyTable은 이미 읽음) | UI 없음 |
| Diff 리뷰 + 라인 코멘트 → 에이전트로 전송 | `GitDiffReader`, `DiffComment` 테이블 | 없음 |
| 비용/예산 (`cost`, 예산 탭) | `TokenUsageCollector` 등 **이미 App에서 수집 중**, `BudgetSettingsStore`, `TokenCostCalculator` | 볼 화면 없음 |
| 자동화 스케줄러 (`automation`) | `AutomationSchedule`, `Automation` 테이블 | 없음 |
| 오케스트레이션 (`orchestrate`) | `Orchestration*` 테이블 | 없음 |
| 클립보드 히스토리 | `ClipboardEntry` 테이블 | 없음 |
| 에이전트 훅 / 스킬 스텁 / trust-workspace 설치 | `AgentHookSettingsMerger`, `TrustPresetWriter` (설치기는 WPF 쪽) | 없음 |

### code-coach / herdr — 양쪽 모두에 없는 새 기능
- 알림 없음: 아발로니아에 OS 알림, 트레이, 작업표시줄/Dock 주의 표시가 하나도 없다. WPF의 `TaskbarFlasher`(herdr H2)도 전환되지 않았다. 지금은 Blocked/Done을 창 제목의 숫자로만 알 수 있다.
- 터미널 내 검색(xterm search addon): 두 호스트 모두 없다.
- 테마는 Dark 고정(`RequestedThemeVariant="Dark"`)이다.
- Store 밖 빌드(zip/.deb/.pkg)에 인앱 업데이트 확인이 없다.
- 코드 뷰 편집과 PDF 보기는 README에 "Not yet"으로 명시돼 있다.

### security-guard
- 양호: CLI 파이프 `PipeOptions.CurrentUserOnly`, 루프백 에셋 서버 경로 토큰(32/24바이트).
- 공백: `knowledge/security-guard/security-surface.md`가 WPF 표면(WM_COPYDATA/MMF)만 다룬다. 아발로니아 표면(named pipe / Unix socket, LocalAssetServer·PluginAssetServer, MCP stdio 명령 실행, GitHub/zip 플러그인 설치)이 지도에 없다.
- 기능 후보: 자동 승인(`AutoApprove`)과 봇→터미널 전달에 감사 로그가 없다. Wearable OsToolActor는 jsonl 감사 로그를 남기므로 같은 형식이 가능하다.

### agent-loop-auditor
- AI 모드 `write_file`/`edit_file`은 워크스페이스 루트로 샌드박스되어 있다. 하지만 승인, diff 미리보기, 되돌리기가 없다(로그 한 줄뿐). Diff 리뷰 전환과 묶으면 "AI 변경 검토/되돌리기"가 된다.
- 웹 툴은 아발로니아에서 headless로만 동작한다(Browser 페이지 미전환).
- 스킬(SkillSync, 슬래시 자동완성) 미전환: Windows 전용 셸 프로브에 묶여 있다.

### music-curator / voice-curator
- AgentBot 음성(mic/VAD/STT/TTS)은 ⏳ 상태다. 하지만 `Audio/IAudioCapturePlatform` + `WindowsAudioCapture`가 이미 플러그인용으로 내려와 있어서 Windows부터 바로 지을 수 있다.
- macOS `IAudioCapturePlatform`(AVAudioEngine/ScreenCaptureKit)와 `IWebViewFrameCapture`(WKWebView snapshot)는 착수 전이다.
- macOS 로컬 LLM(Metal)은 조사 대기 중이다.

### device-smith
- Wearable 호스트는 MSIX에서 빠져 있다(BLE 인증 문제). macOS BLE(CoreBluetooth)도 없다. 공식판 Store 사용자는 워치를 쓸 수 없다.

### build-doctor
- 버전 소스가 여전히 `Project/AgentZeroWpf/version.txt`이고, 아발로니아 csproj가 그 파일을 읽는다. 공식 호스트가 바뀌었으니 호스트 중립 위치로 옮기는 것을 권한다(agent-zero-build 스킬 포함).
- `harness/agents/build-doctor.md`가 이미 사라진 conpty.dll / NuGetPackageRoot 고정을 점검 대상으로 서술한다(stale).

### test-sentinel
- 아발로니아 테스트 18파일 중 UI 헤드리스 테스트가 없다(Avalonia.Headless 미사용).
- 테스트 없음: Worktrees VM, GitLogViewModel, McpSettingsViewModel, ResumeSessionDialog, 분리/도킹(Terminal/Plugin/Code window).
- macOS ⏳ "check by a person" 항목이 7개 이상이다.
- `test-sentinel.md`의 경계 규칙이 WPF/AgentTest 기준이다.

### tamer (정원 자체)
- `agentzero-cli` 스킬의 `find-cli.ps1`가 WPF exe만 찾는다. 또 스킬이 `orchestrate/automation/cost/os`를 안내하지만 아발로니아에는 이 동사들이 없다.
- 정원 에이전트 다수(build-doctor, test-sentinel, code-coach)가 WPF 중심으로 쓰여 있어 아발로니아 기준으로 재조준이 필요하다.

## 평가

- 코드 안전성: B — 파이프와 에셋 서버는 견고하다. 감사 로그 부재, AI 쓰기 무승인, 보안 지도 미갱신은 기능 추가 전에 메울 공백이다.
- 아키텍처 정합성: A — "로직은 ZeroCommon으로" 원칙이 잘 지켜져 상당수 기능이 UI 작업만으로 전환된다.
- 테스트 가능성: C — VM 단위 테스트는 있으나 UI 헤드리스와 macOS 자동 검증이 비어 있다.

## 다음 단계 제안

사용자가 선별해 미션(M0042~)으로 하나씩 추가한다. 우선순위 제안은 사용자 보고에 정리했다.

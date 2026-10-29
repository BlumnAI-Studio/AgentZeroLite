---
date: 2026-10-29T17:30:00+09:00
agent: meta:wearable-mcp
type: improvement
mode: execution
trigger: "웨러블 AI가 추가된 MCP(공통설정) 툴 이용도 할수있게 개선 — 크롬 CDP"
---

# 웨어러블 에이전트가 공통 MCP 툴을 쓰게 하고, Chrome을 CDP로 붙이기

## 실행 요약

후속 개선 요청: 시계에서 브라우저를 제어하려 했는데 툴이 없었다. 이 PC의 Chrome을 CDP로 제어할 수 있는
MCP가 있는지 확인하고, 웨어러블 호스트가 Settings → MCP(공통 설정)의 툴을 쓰도록 연결했다.

## 결과

- **환경**: Node 24.16, Chrome 156. 9222 포트에 CDP를 연 Chrome이 이미 떠 있음
  (`--user-data-dir=C:\Users\psmon\infra\tripo-profile`). 기존 MCP 설정은 memorizer 하나뿐.
- **MCP 두 개 모두 CDP로 연결됨** (Debug GUI `-cli mcp import` → `mcp call`):
  - `chrome` = `chrome-devtools-mcp@latest --browser-url http://127.0.0.1:9222 --no-usage-statistics --no-performance-crux` — 1.12.0, 32개 툴, 15개 허용
  - `playwright` = `@playwright/mcp@latest --cdp-endpoint http://127.0.0.1:9222 --output-dir …\mcp\playwright` — 1.64 alpha, 25개 툴, 15개 허용, **서버는 꺼 둠**(같은 브라우저를 다루는 동사가 두 배로 늘어나서)
  - 거부한 툴: evaluate/run_code_unsafe(임의 JS), 파일 업로드/드롭, 성능·힙·스크린캐스트, 네트워크·콘솔, emulate/resize
  - Debug와 릴리스 두 프로필의 `mcp-settings.json`에 같은 항목을 넣음
- **코드**: `WearableSettings.McpToolsEnabled`(기본 on), `WearableToolbelt.CallDynamicToolAsync` → `McpHub`,
  `WearableBrainFactory`에서 `DynamicTools`를 연결하고 MCP가 있으면 턴을 8 → 12로 늘림, 호스트 시작 로그에
  `mcp: N tools from …`, Avalonia Wearable 페이지에 체크박스 추가. WPF 코드는 건드리지 않았다(값은 그대로 보존됨).
- **검증**: `AgentZeroWearable.exe --ask` (Debug 프로필, gpt-6.1-terra)
  - "크롬 탭 제목?" → `chrome.list_pages` 1턴 → "Mixamo"
  - "새 탭으로 example.com 열고 제목 확인 후 닫아" → `new_page` → `close_page` → "Example Domain, 닫았어요"; 탭이 남지 않음
  - ZeroCommon.Tests 1307 통과(새 테스트 3개), Avalonia·WPF 빌드 오류 0

## 평가

| 축 | 등급 | 근거 |
|---|---|---|
| 코드 안전성 | B | MCP 허용 목록을 재사용하고 위험한 툴은 거부했지만, CDP 브라우저는 로그인된 세션 전체를 시계 에이전트에 넘긴다 |
| 아키텍처 정합성 | A | ZeroCommon의 McpHub 하나를 두 호스트와 시계가 같이 쓴다. 설정 파일 중복 없음 |
| 테스트 가능성 | A | 헤드리스 테스트(툴벨트 라우팅, 허용 목록) + 실제 --ask 왕복 |

## 다음 단계 제안

1. CDP Chrome이 없을 때는 MCP 호출이 연결 실패로 끝난다 — 시계 프롬프트에 "Chrome을 9222로 띄워 달라" 안내를 넣거나 기동 바로가기를 만들기
2. 웨어러블 호스트 로그가 Debug 프로필에서도 `AgentZeroLite\logs\wearable-host.log`(릴리스 쪽)에 쓰인다 — `HostLog` 경로를 프로필에 맞추기

## 후속 (18:10~18:30) — 브라우저 요청은 MCP Chrome이 먼저

### 로그에서 찾은 것 (`wearable-host.log`, 17:45 실행)
- #27 "크롬 창을 열어줘봐" → `os_launch chrome`. CDP가 없는 두 번째 Chrome이 기본 프로필로 떠서 프로필 선택 화면에서 멈춤
- #28 `os_list_windows` → `os_element_tree`(포팅 안 됨) → "읽을 수 없어요"
- #29 사용자가 "mcp 모드로"라고 직접 말한 뒤에야 `chrome.list_pages`를 사용. 이후 #32~#39는 `chrome.*`로 정상 동작
- MCP 이전(16:49~16:52): `web_open`이 GUI 브라우저 페이지에 열려 "나는 안 보이는데?" → os 키보드 입력으로 우회 → 클릭 불가

### 바꾼 것
- `ZeroCommon/Mcp/McpBrowser`: 허용된 툴 이름에서 브라우저 서버를 찾는다(chrome-devtools 우선, playwright 다음). CDP 엔드포인트를 읽고 `/json/version`으로 살아 있는지 확인, 브라우저 프로그램 이름 판별
- `WearablePromptFrame`: 브라우저가 있을 때만 규칙 추가 — 브라우저/크롬 요청은 `chrome.*`, `os_launch`로 브라우저 실행 금지, 화면에 보여 줄 페이지에 `web_open` 금지, 앞으로 가져올 때는 `select_page bringToFront`
- `WearableToolbelt.OsLaunchAsync`: MCP 브라우저가 살아 있으면 브라우저 실행을 **거부**하고 쓸 툴을 알려 준다. CDP가 죽어 있으면 그대로 실행한다
- 테스트 7개 추가(총 1320 통과)

### 재현 확인 (`--ask`, Debug 프로필)
- "크롬 창을 열어줘봐" → `chrome.list_pages` → `chrome.select_page` → "앞으로 가져왔어요" (이전에는 os_launch)
- "브라우저에서 네이버 열어서 보여줘" → `chrome.new_page` → `select_page`
- "os_launch로 chrome 실행해줘"(직접 지시) → 그래도 `chrome.*`를 사용. 거부 경로는 단위 테스트로 검증

## 후속 2 (18:40~19:15) — "크롬 창을 두 번째 화면에 보여줘": 모니터 툴 추가

### 바꾼 것
- `IOsControl`: `OsScreen` 레코드, `ListScreens()`, `MoveToScreen()`. `WindowsOsControl`은 EnumDisplayMonitors + GetMonitorInfo + SetWindowPos로 구현
- 번호 규칙: 1번이 주 모니터, 나머지는 왼쪽→오른쪽(`OsScreen.Numbered`). Windows의 DISPLAYn 번호는 사람이 말하는 "두 번째 화면"과 다를 수 있다
- 툴 카탈로그 확장(의도적): `os_list_screens`, `os_move_window` — GBNF, KnownTools, 프롬프트, `AgentToolCatalogTests`를 함께 갱신. 두 루프에서 디스패치, `IAgentToolbelt` 기본 구현은 "not available"(WPF 호스트는 그대로)
- `os_list_windows` 결과에 창마다 `screen` 필드 추가. 최소화된 창은 `GetWindowPlacement`의 복원 위치로 판단
  (첫 실측: "모니터 2개, 크롬은 최소화돼서 어느 화면인지 모름")
- 이동 결과는 보낸 곳이 아니라 **창이 실제로 있는 화면**으로 보고한다. 다른 화면에 있으면 실패
- 시계 프롬프트와 MCP 브라우저 규칙에 "다른 모니터로 옮기려면 os_list_screens → os_move_window" 추가

### 검증
- `--ask "크롬창을 두번째 화면에 보여줘"` → os_list_screens → os_list_windows → chrome.list_pages(어느 창인지 확인) → os_move_window(screen 2, maximize) → os_activate
- 독립 확인(PowerShell Win32): CDP Chrome(9222, tripo-profile) 창이 DISPLAY2(X -1920..0, 주 모니터 왼쪽)에서 최대화 상태
- ZeroCommon.Tests 1323 통과(새로 3개), Avalonia·WPF 빌드 오류 0

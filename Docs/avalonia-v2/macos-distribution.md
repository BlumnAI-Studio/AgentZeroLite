# AgentZero Lite (Avalonia) — macOS 배포 가이드 (Developer ID · Mac App Store)

Avalonia 호스트의 macOS(Apple Silicon) 빌드를 서명·공증해 배포하고, Mac App Store 등록 가능성을 따지는 절차와 기록.
Windows 쪽 짝은 `microsoft-store.md`. 서명 일반 지식은 `harness/knowledge/_shared/code-signing.md` §7.

---

## 0. 설계: 두 갈래 — Developer ID 먼저, App Store는 실측 뒤

| 경로 | 무엇 | 기능 | 상태 |
|---|---|---|---|
| **Developer ID 서명 + 공증** | 지금의 `.pkg`를 Apple 인증서로 서명하고 Apple 공증 티켓을 붙인다. 홈페이지·GitHub Releases 배포 | 제한 없음 | **1단계 — 진행 중** |
| **Mac App Store** | App Sandbox 안에서 도는 빌드를 App Store Connect에 제출 | 터미널 크게 제한 | 2단계 — 샌드박스 실측 후 판단 |

Mac App Store는 **App Sandbox가 필수**이고, 앱이 띄운 **자식 프로세스는 부모의 샌드박스를 그대로 물려받는다.**
Windows MSIX의 desktop app breakaway(`DesktopAppBreakaway.cs`) 같은 탈출구가 macOS에는 없다. Apple DTS 답변(포럼
thread 685544): "A child process always inherits its sandbox from its parent", "App Review requires that all code within
your app be sandboxed … you can't include a non-sandboxed helper app". 같은 스레드의 실측: 샌드박스 앱의 `forkpty` +
zsh는 `can't set tty pgrp: operation not permitted`, `ls /Users`는 `Operation not permitted`.

AgentZero에 대입하면 터미널 탭이 사용자 프로젝트 폴더, `/opt/homebrew`의 node·claude·codex, `~/.claude`에 닿지
못하고 `npm install -g`도 막힌다(가이드라인 2.4.5도 추가 코드 설치를 금지). 그래서 기능 그대로의 배포는
Developer ID로 하고(iTerm2·Warp·Ghostty와 같은 방식), App Store판은 샌드박스 빌드를 실측해 성립 여부부터 판단한다.

---

## 1. 사전 준비 — 사용자가 할 일

1. **Apple Developer Program 가입** — <https://developer.apple.com/programs/enroll/> (개인, $99/년).
   2단계 인증이 켜진 Apple ID 필요. 승인까지 보통 수 시간~48시간. 두 경로 모두 이것이 전제.
2. **App Store Connect API 키** (가입 승인 후) — App Store Connect → 사용자 및 액세스 → 통합 → **팀 키** →
   생성, 역할 **Admin**. `AuthKey_<KEYID>.p8`(한 번만 내려받을 수 있음), Key ID, Issuer ID 세 값을
   `.secret/`(git 제외)에 둔다. `asc`와 공증(`notarytool --key`)이 이 키 하나로 동작한다.
3. **Developer ID Installer 인증서** — `.pkg` 서명용. `asc`/API가 이 유형을 만들지 못하므로 웹에서 1회:
   developer.apple.com → Certificates → **+** → *Developer ID Installer* → CSR 업로드(CSR은 저장소 쪽에서 만들어 줌).

---

## 2. 도구 (CLI, 웹 전용 아님)

| 도구 | 설치 | 용도 |
|---|---|---|
| `asc` 5.16.0 (App Store Connect CLI, Rork) | `brew install asc` | 인증서·번들 ID·프로파일, 앱 레코드(`asc web apps create`), `.pkg` 업로드, 메타데이터·스크린샷, 심사 제출, 공증(`asc notarization`) |
| `xcrun notarytool` / `stapler` | Command Line Tools에 포함(이 맥에 있음) | 공증 제출·티켓 스테이플 |
| `codesign`, `pkgbuild`, `productbuild` | Command Line Tools | 서명·패키징 |

Xcode는 필요 없다. `xcrun altool`은 Xcode에만 들어 있지만 업로드는 `asc builds upload --pkg`가 대신한다.
인증: `asc auth login --name agentzero --key-id <KEYID> --issuer-id <ISSUER> --private-key .secret/AuthKey_<KEYID>.p8`
→ `asc auth status --validate`.

이 맥의 함정: PATH의 `dotnet`은 `/usr/local/share/dotnet`(9.0만)이라 `global.json`(10.0)을 못 찾는다.
.NET 10 SDK는 `~/.dotnet`에 있으므로 `export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`.

---

## 3. 빌드 → 서명 → 공증 (Developer ID)

```sh
# 0) 게시 + 자체 점검 (CI release.yml의 macos 잡과 같은 명령)
dotnet publish Project/AgentZeroAvalonia/AgentZeroAvalonia.csproj -c Release -r osx-arm64 \
  --self-contained true -p:UseAppHost=true -o publish-avalonia/osx-arm64
publish-avalonia/osx-arm64/AgentZeroLite -cli selftest all
v=$(cat publish-avalonia/osx-arm64/version.txt)

# 1) .app — SIGN_ID가 있으면 Developer ID 서명(중첩 Mach-O 먼저, 하드닝 런타임 + 타임스탬프), 없으면 ad-hoc
SIGN_ID="Developer ID Application: <이름> (<TEAMID>)" \
  sh Project/AgentZeroAvalonia/macos/build-app.sh publish-avalonia/osx-arm64 publish-avalonia/AgentZeroLite.app "$v"

# 2) .pkg — PKG_SIGN_ID로 Developer ID Installer 서명
PKG_SIGN_ID="Developer ID Installer: <이름> (<TEAMID>)" \
  sh Project/AgentZeroAvalonia/macos/build-pkg.sh publish-avalonia/AgentZeroLite.app \
  publish-avalonia/AgentZeroLite-$v-osx-arm64.pkg "$v"

# 3) 공증 + 스테이플 — .pkg 하나만 제출하면 안의 앱까지 덮는다
NOTARY_KEY=.secret/AuthKey_<KEYID>.p8 NOTARY_KEY_ID=<KEYID> NOTARY_ISSUER=<ISSUER> \
  sh Project/AgentZeroAvalonia/macos/notarize.sh publish-avalonia/AgentZeroLite-$v-osx-arm64.pkg
```

`build-app.sh`의 서명 모드는 `--deep`을 쓰지 않는다. `--deep`은 메인 실행 파일의 엔타이틀먼트를 모든 dylib에
붙이고, Apple은 공증용 서명에 권장하지 않는다. 엔타이틀먼트(`macos/entitlements.plist`)는 번들(메인 실행 파일)에만.
`notarytool submit --wait`는 결과가 Invalid여도 0으로 끝나므로, 스크립트는 `stapler staple` 실패를 판정으로 삼는다.

### 로컬 검증 결과

| 날짜 | 항목 | 결과 |
|---|---|---|
| 2026-10-21 | `dotnet publish -r osx-arm64` (SDK 10.0.402, Mac mini, Darwin 24.6) | ✅ |
| 2026-10-21 | `-cli selftest all` | ✅ ipc · secrets(AesGcm) · pty(Porta.Pty forkpty) |
| 2026-10-21 | `build-app.sh` ad-hoc 경로 (0.25.8) | ✅ `codesign --verify --deep --strict` 통과 |
| 2026-10-21 | `build-app.sh` Developer ID 경로 (375개 파일 개별 서명) | ✅ `valid on disk`, `satisfies its Designated Requirement` |
| 2026-10-21 | `build-pkg.sh` + `PKG_SIGN_ID` | ✅ Developer ID Installer, 신뢰 타임스탬프 |
| 2026-10-21 | `notarize.sh` (API 키) — submission `5094d9a2-a4d6-404e-ad73-3237cdaa5151` | ✅ **Accepted**, issues 없음, 스테이플 OK, `spctl`: `source=Notarized Developer ID` |

### 서명 환경 (이 맥에서 실제로 쓴 구성)

- 팀: **sang man park (개인), Team ID `HA39FQRUZG`**. 계정에 팀이 여럿이므로 웹에서 팀 선택을 확인한다.
- **Developer ID 인증서는 계정 소유자만 발급할 수 있다** — `asc certificates create --certificate-type DEVELOPER_ID_APPLICATION_G2`가
  "This operation can only be performed by the Account Holder"로 거부(Admin API 키). 그래서 웹(Certificates → +)에서
  CSR을 올려 발급했다. 개인 키는 `openssl`로 이 맥에서만 만들었다. 두 인증서 모두 G2 Sub-CA, 만료 2027-11-20.
- **서명 전용 키체인** `~/Library/Keychains/agentzero-signing.keychain-db` — `.p12` 두 개 + `DeveloperIDG2CA.cer`를 넣고
  `set-key-partition-list -S apple-tool:,apple:,codesign:`. 로그인 키체인에 넣었을 때는 codesign이 키 사용 확인 창을
  띄우고 멈췄다(SSH 세션에서는 누를 수 없음). CI도 같은 구성(임시 키체인 + p12)을 쓴다.
  재부팅 뒤에는 잠기므로 `security unlock-keychain -p "$(cat .secret/certs/signing-keychain-password.txt)" ~/Library/Keychains/agentzero-signing.keychain-db`.
- 비밀 위치(`.secret/`, git 제외): `AuthKey_6FJ8Q8Y7AA.p8`, `apple-asc.json`(Key ID·Issuer ID·Team ID), `certs/`(키·CSR·cer·p12,
  `p12-password.txt`, `signing-keychain-password.txt`). `asc`는 `agentzero` 프로필로 시스템 키체인에 로그인돼 있다.
- Playwright: 패스키 로그인은 Playwright 번들 Chromium에서 USB 키만 제시된다(휴대폰 QR 없음). `channel: 'chrome'` +
  별도 프로필 디렉터리로 띄운 정식 Chrome에서는 QR 패스키 로그인이 됐다.

---

## 4. Mac App Store (2단계)

### 4.1 샌드박스 실측 (2026-10-21, Mac mini, macOS Darwin 24.6, ad-hoc 서명)

방법: (a) 0.25.8 `.app` 사본에 번들 ID `…sandboxprobe`를 주고 기존 엔타이틀먼트 + `app-sandbox` +
`files.user-selected.read-write`로 서명해 `-cli selftest all`과 GUI 실행. (b) 같은 엔타이틀먼트로 서명한 작은 번들
(`posix_spawn("/bin/zsh","-c",…)`) — 터미널 탭의 셸과 똑같이 **샌드박스를 상속한 zsh**가 각 명령을 실행.
같은 명령을 샌드박스 없이 돌린 대조군은 모두 성공했다.

| 터미널 탭에서 하는 일 | 샌드박스 안 결과 |
|---|---|
| `$HOME` | `~/Library/Containers/<번들ID>/Data`로 바뀜 |
| 사용자 프로젝트 폴더 읽기·쓰기 (`~/code/…`) | ❌ `Operation not permitted` |
| `~/.claude`, `~/Documents`, 기존 `~/Library/Application Support/AgentZeroLite` | ❌ `Operation not permitted` |
| `/opt/homebrew/bin/node`, `claude`, `codex`, `npm` 실행 | ❌ `operation not permitted` (exec 자체가 거부) |
| `npm -g` 경로(`/opt/homebrew/lib/node_modules`)에 쓰기 | ❌ |
| `git` (`/usr/bin/git`, 컨테이너 안 저장소 포함) | ❌ `xcrun: error: cannot be used within an App Sandbox.` |
| `ps` | ❌ `operation not permitted` |
| `zsh -i`, `/usr/bin/ssh -V`, `curl https://…` | ✅ |
| 앱: `selftest pty`(forkpty + `/bin/sh`), `selftest secrets` | ✅ |
| 앱: `selftest ipc` (`-cli`가 쓰는 named pipe) | ❌ no reply over the pipe |
| 앱: GUI 기동 | ❌ 270 ms 만에 SIGABRT — `Sandbox: deny(1) mach-lookup com.apple.coreservices.launchservicesd`, `[NSApplication sharedApplication]` → `_RegisterApplication` → `abort` (메인 스레드). 같은 엔타이틀먼트의 최소 AppKit 앱은 정상 기동하므로 AgentZero 쪽 원인. 하드닝 런타임 유무·중첩 서명 방식을 바꿔도 같음 |

결론: 샌드박스판에서 AgentZero 터미널이 할 수 있는 것은 **컨테이너 안의 시스템 셸과 ssh**뿐이다. 핵심
사용처(사용자 폴더에서 claude/codex/git 실행, CLI 설치)는 막히고, `-cli` 자동화도 끊긴다. 사용자가 폴더를
고르는 권한(`user-selected`)을 넣어도 homebrew 바이너리 실행과 git은 풀리지 않는다.
App Store판을 만든다면 "SSH 원격 터미널 + AI 모드(외부 LLM) + 플러그인" 같은 **별도 기능 구성**이어야 하고,
그 전에 GUI 기동 크래시(launchservicesd 조회 거부)부터 해결해야 한다.

### 4.2 성립한다면 — 제출 절차

1. 위 결론을 보고 App Store판 범위를 정한다.
2. 그다음: 번들 ID 등록(`asc bundle-ids create --platform MAC_OS`), 앱 레코드(`asc web apps create`), 인증서
   `MAC_APP_DISTRIBUTION` · `MAC_INSTALLER_DISTRIBUTION`(`asc certificates create`), 프로비저닝 프로파일을
   `Contents/embedded.provisionprofile`로 넣고, 엔타이틀먼트에 `com.apple.application-identifier` ·
   `com.apple.developer.team-identifier` 추가 → `asc builds upload --pkg` → 메타데이터·스크린샷 → `asc publish appstore --submit`.
3. 개인정보 처리방침 URL은 Microsoft Store와 같은 페이지를 쓴다:
   https://psmon.github.io/AgentZeroLite/Home/privacy-policy.html

---

## 5. 기록

- 2026-10-21 — 경로 결정(Developer ID 먼저, App Store는 실측 후). `asc` 5.16.0 설치. `build-app.sh`에 Developer ID
  서명 모드(`SIGN_ID`), `notarize.sh` 추가. 로컬 게시·selftest·ad-hoc 번들 확인.
- 2026-10-21 — API 키(Admin) 발급·`asc` 로그인, Developer ID Application/Installer 인증서 발급(웹), 서명 전용 키체인 구성.
  0.25.8 `.pkg` 서명 → **공증 Accepted** → 스테이플. `build-app.sh`는 Mach-O만이 아니라 `Contents/MacOS`의 모든 파일을 서명하도록 수정.
- 2026-10-21 — 샌드박스 실측(§4.1): 터미널 핵심 기능·`-cli`·GUI 기동이 모두 막힘. App Store판은 기능을 줄인
  별도 구성으로만 성립하고, 그 전에 GUI 크래시 해결이 필요. 실측용 번들·컨테이너는 정리함.

## 참고

- Apple DTS, 샌드박스 앱의 forkpty와 자식 프로세스 상속: https://developer.apple.com/forums/thread/685544
- App Store Connect CLI (`asc`): https://github.com/rorkai/App-Store-Connect-CLI
- 업로드 도구 안내(altool/Transporter): https://developer.apple.com/help/app-store-connect/manage-builds/upload-builds

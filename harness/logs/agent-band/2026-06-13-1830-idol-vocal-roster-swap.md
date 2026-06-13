---
date: 2026-06-13T18:30:00+09:00
agent: tamer
type: review
mode: log-eval
trigger: "AgentZero 악단에 여가수를 영입및 교체 / pencil-creator 스프라이트 모두영입 / 새 스프라이터버전으로 교체"
---

# Agent Band — 아이돌 여가수 7명 영입 & 기존 여가수 전면 교체 (v0.8.0)

## 실행 요약

`C:\code\psmon\pencil-creator` 에서 **오늘(2026-06-13) 생성된 스프라이트**를 파악하여,
`agent-band` 플러그인의 여가수 풀을 신규 아이돌 7명으로 전면 교체했다.

### 1) 오늘 생성된 스프라이트 식별
- `design/sprite/output/vox7-1 … vox7-7` — **7명의 신규 아이돌 여가수**
  (각각 `idle` 6프레임 + `dance` 8프레임, png+json TexturePacker 아틀라스). 16:44~18:02 생성.
- `design/sprite/output/vocal-ex` — `idle`+`play` 단일 실험본. 아이돌(노래+댄스) 특성과
  불일치(`dance` 시트 없음)하여 영입 대상에서 제외.
- `design/xaml/output/sample15/…` 의 dance/orchestra 에셋은 여가수가 아니므로 범위 외.

→ **여가수 = vox7-1~7** 로 확정, 7명 전원 영입(모두영입).

### 2) 에셋 영입
`Project/Plugins/agent-band/assets/sprites/vox7-1 … vox7-7/` 로
`idle.{png,json}` + `dance.{png,json}` 복사 (총 +1.2MB, 28개 파일).
JSON 14개 파싱 검증 OK, PNG 14개 비어있지 않음 검증 OK.

### 3) 기존 여가수 교체 (퀄리티 업그레이드)
- `VOCAL_FEMALE` = `['vocal-1','vocal-3']` → `['vox7-1' … 'vox7-7']`
- 구 여가수 스프라이트 `vocal-1`, `vocal-3` 폴더 **삭제(은퇴)**.
- 남가수 풀 `vocal-2`, `vocal-4` 는 **변경 없음**(여가수만 교체 요청).

### 4) "노래하며 춤추는 아이돌" 특성 반영
아이돌은 기존 밴드(idle+play)와 달리 **노래+댄스** → 평상시 `idle`,
음 매칭(score ≥ `SCORE_ACTIVE`) 시 `dance` 시트로 전환.
- `IDOL_VOCALS` Set 도입, `stateSheet(id,state)` 가 idol 의 `'play'`→`'dance'` 파일만 리맵.
  (fps / glow / bob 등 'play' 논리 상태 로직은 그대로 재사용 — 변경 최소화)
- `ensureSpriteSet` 가 `stateSheet` 로 시트 파일명 해석.

### 5) "메인 가수 스테이지 영역" 보장
신규 id 가 `vocal-*` prefix 가 아니므로(`vox7-*`) 레이아웃이 악기 윙으로 오배치되는 문제를
`isVocal(id)` 헬퍼로 해결 — `computeLayout` 의 보컬/악기 분리를 prefix 대신 `isVocal` 기준으로 변경.
→ 아이돌은 항상 중앙 메인 보컬 클러스터에 배치.

### 6) 버전 / 메타
`manifest.json` `0.7.0` → `0.8.0`, description 갱신.
JS `node --check` 통과, 잔존 `vocal-1/vocal-3` 코드 참조 0건(주석/changelog만 잔존).

## 결과 (변경 파일)
- `Project/Plugins/agent-band/agent-band.js` — IDOL_VOCALS / isVocal / stateSheet, VOCAL_FEMALE 교체, 레이아웃/시트해석 수정, v0.8 changelog
- `Project/Plugins/agent-band/manifest.json` — 0.8.0
- `assets/sprites/vox7-1 … vox7-7/` — 신규 (28 files)
- `assets/sprites/vocal-1`, `vocal-3` — 삭제

## 평가 (3축)

| 축 | 판정 | 근거 |
|----|------|------|
| 코드 안전성 | **A** | 변경은 데이터(풀 배열)+얇은 헬퍼 2개로 국소화. 기존 'play' 상태머신/렌더 경로 무변경. `node --check` OK, 깨진 참조 0. |
| 아키텍처 정합성 | **A** | 시트 명명 차이를 `stateSheet` 한 곳으로 캡슐화(누수 없음). 레이아웃은 prefix 하드코딩 → `isVocal` 의도-표현으로 개선. 아틀라스 로더는 프레임 수 무관(6/8 프레임 모두 수용). |
| 테스트 가능성 | **B** | `isVocal`/`stateSheet` 는 순수 함수라 단위 테스트 용이하나, 플러그인 JS 에 자동 테스트 하네스 부재 → 실제 무대 렌더는 수동 검증 필요(아래 권고). |

## 다음 단계 제안
- **수동 검증**: AgentZero 실행 → Music LIVE → "Female singing/Singing" 라벨 유입 시
  중앙에 vox7 아이돌이 뜨고, score↑ 시 `dance` 애니메이션 전환되는지 확인(playwright-e2e 또는 육안).
- **재배포**: 플러그인 설치 경로(`%LOCALAPPDATA%/AgentZeroLite/Wasm/plugins/agent-band`) 또는
  공식 카탈로그(GitHub `Project/Plugins`) 기준 재설치 시 반영됨. 빌드 mirror(bin/…/Wasm)는 gitignore 아티팩트.
- **(선택) 남가수도 동급 업그레이드**: 여가수만 vox7 품질로 올라가 남가수(vocal-2/4)와 화질 격차 발생 가능 → 차기 영입 검토.
- **(선택) vocal-ex 처리**: 영입 보류분. 아이돌 사양(dance)로 재출력하거나 정리 권장.

---
date: 2026-06-13T19:30:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "메인보컬 = vocal-ex 영입확인체크 / 2열 댄스단 기능제거(리소스 유지 비활성, 퀄리티조정후 재투입)"
---

# Agent Band — 메인보컬 vocal-ex 확정 + 댄스단 비활성 (v0.10.0)

## 실행 요약

### 1) 메인보컬 = `vocal-ex` 영입 확인
이전(v0.9)엔 메인보컬을 vox7-1로 잠정 설정했으나, operator 가 메인보컬은
`C:\code\psmon\pencil-creator\design\sprite\output\vocal-ex` 라고 확정.
- **영입 완료**: `assets/sprites/vocal-ex/` 에 `idle.{png,json}` + `play.{png,json}` 복사.
- vocal-ex 는 `idle`+`play` 시트 → **노래(play)**. 백업 아이돌 vox7 는 `idle`+`dance` → **춤(dance)**.
  즉 "리드는 노래하고, 백댄서 아이돌은 좌우에서 춤춘다" 구도.
- 슬롯 구조: `VOCAL_FEMALE = ['vocal-ex', 'vox7-1'..'vox7-7']` (8인), `MAIN_VOCAL_ID = 'vocal-ex'` (슬롯0, 정중앙 고정).

연동 수정:
- `IDOL_VOCALS`(=play→dance 리맵 대상)에서 vocal-ex 제외 → 리드는 play 시트 유지.
- `FEMALE_POOL` Set 신설 → `centerVocals` 가 IDOL_VOCALS 대신 FEMALE_POOL 로 그룹 묶음
  (vocal-ex 가 male 취급되어 가장자리로 밀리던 버그 방지).
- peel-off 루프 대상도 `VOCAL_FEMALE` 로 변경 → 그룹 소멸 시 리드가 슬롯0이라 **마지막에** 사라짐.
- `MAX_PERFORMERS` 11 → 12 (8인 풀그룹 + 악기 여유).

### 2) 2열 댄스단 기능 제거 (비활성, 리소스 유지)
operator 요청: 댄스단(row-2 troupe)은 퀄리티 조정 후 재투입 예정 → **기능만 끄고 리소스 보존**.
- `DANCE_TROUPE_ENABLED = false` 플래그 1개 추가.
- 게이트 2곳: `onTick` 의 `upsertDancersFromLabels` 호출, 렌더 루프의 row-2 그리기.
- **유지(삭제 안 함)**: `upsertDancersFromLabels` / `drawDancer` / `ensureDanceMaster` /
  `labelToDance` / 모든 DANCE_* 상수 / `assets/dancers/_master/dance-master.png` + index.json.
- 재투입: 플래그만 `true` 로 되돌리면 즉시 복구.

## 결과 (변경 파일)
- `Project/Plugins/agent-band/agent-band.js`
  - 로스터: VOCAL_FEMALE(8인, vocal-ex 리드), MAIN_VOCAL_ID, FEMALE_POOL, IDOL_VOCALS(vox7만)
  - 레이아웃: centerVocals / peel-off → FEMALE_POOL·VOCAL_FEMALE 기준
  - 댄스단: DANCE_TROUPE_ENABLED=false + 게이트 2곳, MAX_PERFORMERS 12, v0.10 changelog
- `Project/Plugins/agent-band/manifest.json` — 0.9.0 → 0.10.0
- `Project/Plugins/agent-band/assets/sprites/vocal-ex/` — 신규 (idle/play, 4 files)
- `node --check` 통과

## 평가 (3축)

| 축 | 판정 | 근거 |
|----|------|------|
| 코드 안전성 | **A** | 리드/백댄서 역할 분리를 Set 멤버십(IDOL_VOCALS vs FEMALE_POOL)으로 명확화, play↔dance 리맵 누수 없음. 댄스단은 플래그 게이트로 비파괴 비활성(코드·에셋 보존) → 재투입 안전. syntax OK. |
| 아키텍처 정합성 | **A** | "리드=노래, 백댄서=춤"을 시트 명명 + IDOL_VOCALS 경계로 표현. centerVocals 그룹 기준을 FEMALE_POOL 로 통일해 vocal-ex 중앙 보장. 기능 토글을 단일 const 로 캡슐화(가역적). |
| 테스트 가능성 | **B+** | isVocal/stateSheet/centerVocals 순수 함수 유지. 댄스단 플래그는 부울 토글이라 추후 회귀 검증 단순. 실시간 무대는 여전히 수동/Playwright 검증 필요. |

## 다음 단계 제안
- **수동 검증(권장)**: Music LIVE → 여성 보컬 시 중앙에 **vocal-ex(노래 모션)** + 좌우로 vox7(춤) 점증,
  2열 댄스단이 더 이상 안 뜨는지 확인.
- **댄스단 재투입**: 퀄리티 조정 완료 시 `DANCE_TROUPE_ENABLED = true` 한 줄로 복구
  (필요하면 dance-master 시트 교체 후).
- (선택) 리드 vocal-ex 와 백댄서 vox7 의 화질/스타일 톤 일치 여부 점검.

---
date: 2026-06-13T21:00:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "음악파트 연출개선 — 담당자 모드(악기 유사), 여성싱어→메인보컬, 장르→백업 랜덤·균등, 스피치=랩, 재즈/발라드→메인 조명, 장르 a,b 동시 플레잉, 음량체계 대체, 싱어 1인/그룹 모드 옵션"
---

# Agent Band — 싱어 담당자(assignee) 연출 + 모드 옵션 (v0.12.0)

## 실행 요약
v0.11의 음량 climax 게이트가 "누가 연주하나"를 잘 분포시키지 못해, **악기와 유사한 담당자 모델**로 교체.
단, 그룹은 목소리 출처를 구분할 수 없으므로 장르→백업 배정을 **랜덤·균등(least-used)** 으로 분포.

### 연출 규칙 (요청 → 구현)
| 요청 | 구현 |
|---|---|
| 여성싱어 매칭 → 메인보컬 호출 | femaleVocalSignal present → 그룹 등장, 슬롯0 vocal-ex 중앙 |
| 재즈/발라드 → 메인보컬 조명 | `LEAD_STYLES={jazz,ballet}` 활성 시 main `playing` |
| 음악장르 → 댄스싱어 랜덤 할당 | `assignBackups()` 가 활성 비리드 장르를 백업에 least-used로 배정 |
| 장르 a,b 동시 → a,b 플레잉 | 장르별 담당 백업이 각각 play (담당 다르면 둘 다 춤) |
| 스피치 = 랩 | labelToDance 에 `speech\|speaking\|narration\|monologue` → hiphop |
| 골고루 분포(퍼센트) | backupUsage 누적 최소치 우선 배정 → 세션 내 균등 |
| 여성 우선 / 남성 한정 | female·neutral→그룹, 남성은 "Male singing"일 때만 labelToPerformer (현행) |
| 음량체계 개선/대체 | climax/volume 게이트 제거, 담당자 `playing` 플래그로 전환 |
| 싱어 1인/그룹 모드 옵션 | `#singerMode` select(group/solo). solo=메인만, group=걸그룹 연출 |

### 동작 모델
- **presence**: 여성/중성 보컬 OR 활성 댄스 장르 → 그룹 등장(무보컬 인스트 댄스도 그룹 소환). solo면 메인만.
- **performing(play/idle)**: 매 tick `upsertIdolGroup`이 `p.playing` 설정 →
  - main: `vocalPresent || leadActive` (노래 중이거나 재즈·발라드면 연주/조명)
  - backup: 자신에게 배정된 장르가 활성일 때만 play, 아니면 idle(무대엔 잔류)
- 악기/남성보컬: 기존 score 기반 p.state 유지.

### 구조 변경
- 신규: `STYLE_ACTIVE`, `LEAD_STYLES`, `styleScoresFromLabels()`, `assignBackups()`,
  상태 `singerMode`/`styleAssignee`/`backupUsage`, Performer.`playing`
- 제거: v0.11 `CLIMAX_VOL_*`/`volumeLevel`/`climaxActive`/`updateClimax`, `countGenres`, `IDOL_GENRE_BONUS_MAX`
- 수정: `labelToDance`(speech→rap), `upsertIdolGroup`(전면), `performState`(playing 기반),
  `onStop`(리셋), index.html(#singerMode), v0.12 changelog, manifest 0.12.0

## 평가 (3축)
| 축 | 판정 | 근거 |
|----|------|------|
| 코드 안전성 | A− | 제거 심볼 잔존 0(코드), syntax OK, onStop 리셋 정합. 다만 변경 폭이 커 회귀 검증(실시간) 권장. |
| 아키텍처 정합성 | A | "담당자=악기 유사"를 명시적으로 구현하면서 그룹 모호성은 분포로 해소. presence/animation/assignment 책임 분리 깔끔. 장르 헬퍼 공유. |
| 테스트 가능성 | B+ | styleScoresFromLabels/assignBackups/performState 순수성 높아 테이블 테스트 용이. 분포·모드 분기는 실시간 육안 검증 필요. |

## 다음 단계 제안
- **수동 검증(권장, 변경 큼)**:
  ① 여성싱어→중앙 메인 조명 ② kpop+hiphop 동시→백업 2명 각각 춤 ③ 같은 장르 반복 시 담당이 바뀌며 골고루 등장
  ④ 재즈/발라드→메인 조명 ⑤ Speech→랩(hiphop 담당) ⑥ Solo 모드→메인 1명만.
- **튜닝**: `STYLE_ACTIVE`(현 0.07=DANCE_PRESENT) 장르 발동 민감도, `IDOL_RAMP_TICKS_PER_MEMBER` 그룹 성장 속도.
- (관찰) Speech 라벨이 매우 흔해 hiphop 담당이 과활성될 수 있음 → 필요 시 speech 점수 가중 하향.
- (관찰) `\bhorn\b` 차량 경적 오매칭(이전 분석 발견) 잔존 — 차기 정리 후보.

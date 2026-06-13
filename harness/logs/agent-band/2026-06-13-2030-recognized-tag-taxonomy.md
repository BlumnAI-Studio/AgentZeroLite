---
date: 2026-06-13T20:30:00+09:00
agent: tamer
type: explanation
mode: log-eval
trigger: "로깅 분석 — 음성인식된 태깅을 분류해 나열"
---

# Agent Band — 인식 태깅(AudioSet 라벨) 분류 분석

## 실행 요약
런타임에 인식 라벨을 디스크로 적재하는 로그는 없음(플러그인 JS는 console.warn 뿐, C# Music
측도 라벨 영속화 안 함). 따라서 "악단이 반응하는 태깅"의 정본은 매핑 코드:
`labelToPerformer` / `femaleVocalSignal` / `labelToDance` (agent-band.js). 여기서 추출·분류함.

## 결과 (분류)

### 1) 악기 Tier 1 (구체, 순서 매칭)
cello / viola / violin·fiddle / contrabass·double bass / harp(¬harpsichord) / guitar /
flute / clarinet / oboe / french horn·horn / trumpet / trombone / tuba / piano /
drum(cymbal·tom-tom·hi-hat·tabla·gong)

### 2) 악기 Tier 2 (부모 폴백)
bowed string·orchestra·symphony·chamber→violin / plucked string→guitar /
woodwind·wind→flute / brass→trumpet / keyboard(musical)→piano / percussion→drum

### 3) 보컬 (3분류)
- 남성: `male sing|man sing`(¬female) → vocal-2/vocal-4
- 여성(명시): `female sing|woman sing` → 아이돌 그룹 + 그룹크기 +1
- 중성/일반: `singing?|choir|vocal|chant|yodel|rapping|hum` → 아이돌 그룹(여성풀 귀속)

### 4) 장르→댄스 6종 (troupe 비활성, countGenres로만 사용)
hiphop / waacking / jazz / ballet / kpop / cheer
→ 댄서 소환은 중단(DANCE_TROUPE_ENABLED=false)이나, "여자+장르n"의 그룹 크기 가산(최대 +3)에 계속 기여.

### 5) 미반응
Speech / 환경음 / SFX 등 비매칭 라벨 → null, 무대 미진입.

### 게이팅
spawn≥SCORE_PRESENT(0.05), keep≥SCORE_KEEP(0.025); 그룹크기=1+⌊지속틱/2⌋+min(장르,3)+(명시여성?1) (1~8);
연주/idle은 음량(climax) 기반(태그 score 아님).

## 평가 (3축)
| 축 | 판정 | 근거 |
|----|------|------|
| 코드 안전성 | A | 분석 작업, 코드 변경 없음. 정본(코드)에서 직접 추출해 추정 오류 최소화. |
| 아키텍처 정합성 | B+ | 태깅→연출 매핑이 한 파일(agent-band.js)에 응집. 단 인식 어휘가 정규식에 하드코딩되어 외부 가시성 낮음 → 본 분석 문서가 그 갭을 보완. |
| 테스트 가능성 | B | 라벨→매핑은 순수 함수라 테이블 기반 단위 테스트로 회귀 검증 가능(현재 미구현). |

## 발견 / 다음 단계
- **버그성 발견**: `\bhorn\b`가 비음악 라벨 "Vehicle horn, car horn, honking"/"Air horn"/"Foghorn"에도
  매칭 → 경적 소리에 horn 주자 오소환 가능. 의도 아니면 패턴 협소화 권장.
- (선택) 인식 어휘를 코드 밖 테이블/JSON로 추출하면 가시성·테스트·튜닝 용이.
- (선택) 실제 세션의 top-K 라벨 분포를 임시 수집하면 게이팅/임계값 실측 튜닝 가능.

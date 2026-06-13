---
date: 2026-06-13T19:00:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "여가수 인식 첫 가수=메인보컬, 계속 들리면 좌우로 그룹 증가, 메인보컬 중앙 유지 / 여자+장르n 그룹 풍부하게 연출 개선"
---

# Agent Band — 아이돌 그룹 연출 (v0.9.0)

## 실행 요약

v0.8에서 여가수 7명(vox7-1~7)을 영입한 데 이어, **무대 연출(staging)**을 개선했다.
기존엔 여가수가 "한 명씩 라벨이 동시 발생할 때만" 늘어나는 솔로 팬아웃이었다.
v0.9에서는 **메인보컬을 중심으로 좌우로 자라나는 아이돌 그룹**으로 바꿨다.

### 연출 규칙 (요청 → 구현 매핑)
1. **첫 여가수 인식 = 메인보컬** → `MAIN_VOCAL_ID = vox7-1`, 항상 중앙 고정(sticky).
   첫 present tick엔 target=1이라 메인보컬만 등장.
2. **여성 보컬이 계속·다양하게 들릴수록 그룹 증가** → `upsertIdolGroup`이 매 tick
   target 크기를 산출:
   `target = 1 + floor(연속present틱/2) + min(장르수,3) + (명시적 "Female singing" ? 1)`,
   1~7로 clamp. rendered 크기는 target을 향해 **틱당 ±1**로 따라가 한 명씩 합류.
3. **메인보컬 기준 좌우로 증가, 중앙 계속 노출** → `centerVocals`가 idol을 슬롯 순서로
   정렬해 index0(메인)을 정중앙에 두고 나머지를 오른쪽→왼쪽 교대 배치
   `[…L2 L1 MAIN R1 R2…]`. 메인은 절대 자리 이동 없음.
4. **"여자 + 장르n" → 풍부한 그룹** → `countGenres`가 동시 발생 장르 수를 세어
   그룹 크기에 직접 가산(최대 +3). 노래+여러 장르일수록 그룹이 커진다.
5. **소리 멈추면 한 명씩 빠짐** → present 아니면 target=0, rendered 틱당 -1.
   peel-off 루프가 그룹 크기를 벗어난 idol을 **즉시 fade 시작**(unseen 타이머 대기 없이),
   메인보컬이 마지막에 사라짐.

### 구조 변경
- `labelToPerformer`: 여성/중성 보컬 분기 제거(이제 null 반환) → 그룹 컨트롤러가 전담.
  남성 보컬(vocal-2/4)은 기존대로 단독 매핑 유지.
- `upsertPerformer(id, score, now)` 헬퍼 추출 → 악기 루프와 그룹 컨트롤러가 공유.
- `MAX_PERFORMERS` 8 → **11** (7인조 풀그룹 + 악기 몇 개 동시 수용).
- `onStop`에 `idolRenderedSize/idolPresentTicks` 리셋 추가(재시작 시 깨끗하게).

## 결과 (변경 파일)
- `Project/Plugins/agent-band/agent-band.js`
  - 신규: `femaleVocalSignal`, `countGenres`, `upsertIdolGroup`, `centerVocals`, `upsertPerformer`
  - 상태: `MAIN_VOCAL_ID`, `idolRenderedSize`, `idolPresentTicks`, 튜너블 3종
  - 수정: `labelToPerformer`(여성분기 제거), `computeLayout`(centerVocals), `MAX_PERFORMERS`, `onStop`, v0.9 changelog
- `Project/Plugins/agent-band/manifest.json` — 0.8.0 → 0.9.0
- `node --check` 통과, 잔존 `femaleCursor` 0건

## 평가 (3축)

| 축 | 판정 | 근거 |
|----|------|------|
| 코드 안전성 | **A** | 그룹 상태는 모듈 스코프 2개 정수 + sticky id로 국소화. `upsertPerformer` 추출로 spawn/evict 경로 단일화(중복 제거). peel-off가 size==0까지 포함해 누수 없이 메인 마지막 fade. syntax OK. |
| 아키텍처 정합성 | **A** | 라벨→스프라이트 단건 매핑과 "그룹 연출"을 분리(labelToPerformer는 악기/남성, upsertIdolGroup은 여성 그룹). 레이아웃 중앙정렬을 `centerVocals` 순수 함수로 캡슐화. 기존 hysteresis/fade/render 파이프라인 재사용. |
| 테스트 가능성 | **B+** | `femaleVocalSignal`/`countGenres`/`centerVocals`는 부수효과 없는 순수 함수라 단위 테스트 용이. 단 `upsertIdolGroup`은 시간/Map 상태 의존 → 모듈 추출 없이는 자동화 어려움. 실시간 무대는 수동/Playwright 검증 필요. |

## 다음 단계 제안
- **수동 검증(권장)**: Music LIVE에서
  ① "Female singing" 단독 → 중앙 1명 ② 지속+장르(Pop/EDM 등) 유입 → 좌우로 점증
  ③ 무음 → 한 명씩 빠지고 메인 마지막 fade, ④ 메인이 중앙을 유지하는지.
- **튜닝 후보**: `IDOL_RAMP_TICKS_PER_MEMBER`(증가 속도), `IDOL_GENRE_BONUS_MAX`(장르 가중),
  실사용 라벨 분포 보고 조정.
- **(선택) 그룹 라인업 다양화**: 현재 메인=vox7-1 고정 + 풀 순서 합류. 세션마다 멤버 순서를
  셔플하면 매번 다른 그룹 구성 연출 가능.
- 코드 추출(컨트롤러를 별 파일/모듈로) 시 단위 테스트 가능 → 테스트 가능성 A로 상향 여지.

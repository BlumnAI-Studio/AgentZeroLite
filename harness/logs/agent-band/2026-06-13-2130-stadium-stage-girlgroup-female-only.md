---
date: 2026-06-13T21:30:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "축구장 공연장 파일 배경선택 추가 / 걸그룹 모드일때 남자싱어 안나오게(악기는 무관)"
---

# Agent Band — Stadium 배경 추가 + 걸그룹 모드 여성 전용 (v0.12.1)

## 실행 요약
1. **Stadium(FIFA 26) 배경 추가**: 언커밋이던 `assets/stages/fifa26.png`를 배경 선택지로 등록.
   `pickStage`가 `assets/stages/{name}.png`를 로드하므로 index.html stagePicker에
   `<option value="fifa26">Stadium (FIFA 26)</option>` 한 줄 추가로 완료.
2. **걸그룹 모드 = 여성 전용**: `singerMode==='group'`일 때 `labelToPerformer`가 반환한
   남성 보컬 id(VOCAL_MALE = vocal-2/vocal-4)를 collapse 단계에서 스킵. 악기는 영향 없음.
   - 이미 무대에 있던 남성은 미갱신 → unseen 페이드로 자연 퇴장(모드 전환도 매끄럽게 처리).
   - Solo 모드는 기존 동작 유지(여성 그룹만 1인으로 축소, 남성 정책 별도 변경 없음).

## 결과 (변경 파일)
- `index.html` — stagePicker에 fifa26 옵션
- `agent-band.js` — collapse 루프에 group-모드 남성 스킵, v0.12.1 changelog
- `manifest.json` — 0.12.0 → 0.12.1
- `assets/stages/fifa26.png` — 신규(영입)
- `node --check` 통과

## 평가 (3축)
| 축 | 판정 | 근거 |
|----|------|------|
| 코드 안전성 | A | 변경 2곳(옵션 1줄 + 가드 1줄), 기존 경로 무영향. 모드 전환 시 기존 남성은 페이드로 정리. syntax OK. |
| 아키텍처 정합성 | A | 배경은 기존 `pickStage` 규약 그대로 재사용(코드 0변경). 남성 억제를 단일 진입점(collapse)에서 처리해 누수 없음. |
| 테스트 가능성 | A− | 가드는 순수 조건이라 검증 쉬움. 배경 로드·모드 전환 페이드는 실시간 육안 확인 권장. |

## 다음 단계 제안
- 수동 검증: Stadium 배경 선택 렌더 확인 / 걸그룹 모드에서 "Male singing" 유입 시 남성 미등장·악기 정상.
- (선택) Solo 모드의 남성 정책 명문화 필요 시 별도 결정(현재 solo는 남성 허용).

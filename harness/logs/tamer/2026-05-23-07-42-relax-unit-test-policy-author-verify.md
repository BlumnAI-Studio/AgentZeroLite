---
date: 2026-05-23T07:42:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "유닛테스트 수행을 완화 / 신규 테스트 작성 시 관련부만 자동수행 / 하네스 전체 점검해 지침업데이트"
---

# 유닛테스트 정책 완화 — Author-verify 예외 신설 (v1.7.0 → v1.7.1)

## 실행 요약

사용자 요청: 온디바이스 LLM의 CPU 오버헤드가 커서 유닛테스트 자동 수행을
더 강하게 억제하되, **Claude가 새 유닛테스트를 작성한 경우 그 신규 테스트만
자동 수행**해서 false-pass(컴파일만 되고 검증은 안 됨) 위험을 잡고 싶다는
지침. 하네스 전체 지침을 이 방향으로 업데이트.

수행 단계:
1. 현재 정책 파악 — `harness/knowledge/test-runner/unit-test-policy.md`,
   `harness/agents/test-runner.md`, `harness/agents/test-sentinel.md`,
   `harness/engine/pre-commit-review.md` 읽기. 기존 정책은 v1.3.0부터
   "explicit only"로 굳어져 있고 자동 트리거는 없었음.
2. 변경 골자 도출:
   - 기본(자동 금지)은 유지.
   - **신규 예외(Author-verify)**: Claude가 같은 태스크 안에서 새
     `[Fact]`/`[Theory]` 또는 새 테스트 클래스를 작성하면, 그 신규
     테스트만 `--filter`로 한 번 실행해 검증.
   - **LLM-스모크 가드**: 신규 테스트가 Whisper/Gemma/Webnori 같은
     온디바이스 LLM 경로를 건드리면, 자동 실행 대신 사용자에게 먼저
     물어봄. CPU/RAM 부담 원인은 그대로이므로 자동화 면제 대상.
   - 리네임/이동 등 순수 리팩터링은 트리거하지 않음.
   - 한 태스크에 최대 1회 실행. 신규 테스트 N개여도 한 번의 필터로 묶음.
3. 변경 적용:
   - `harness/knowledge/test-runner/unit-test-policy.md` — Rule 섹션에
     Author-verify 예외 단락 추가, "What still runs automatically"
     테이블에 두 행(non-LLM yes / LLM-smoke ask) 추가, 경계 사례 3건 추가.
   - `harness/agents/test-runner.md` — `auto_invoke_on:` 프론트매터
     신설, description 갱신, 모드 테이블 3→4, "Procedure — Author-verify"
     섹션 신설(LLM-스모크 가드 멘트 포함), "Does NOT do" 3건 추가,
     로그 mode enum 확장, 평가축 2개 추가.
4. 버전 갱신: `harness.config.json` 1.7.0 → 1.7.1, lastUpdated → 2026-05-23.
5. 체인지로그: `harness/docs/v1.7.1.md` 신규 작성.

## 결과

### 변경 파일

| 파일 | 변경 종류 |
|---|---|
| `harness/knowledge/test-runner/unit-test-policy.md` | Rule 단락 / 테이블 / 경계 사례 보강 |
| `harness/agents/test-runner.md` | auto_invoke_on 신설 + 4번째 모드 + 새 Procedure + 평가축 확장 |
| `harness/harness.config.json` | version 1.7.0 → 1.7.1, lastUpdated → 2026-05-23 |
| `harness/docs/v1.7.1.md` | 신규 체인지로그 |

### 변경되지 않은 것 (의도된 비변경)

- `pre-commit-review` — 여전히 테스트 호출 없음.
- `release-build-pipeline` — 여전히 테스트 단계 없음.
- `test-sentinel` — 여전히 실행 안 함, Author-verify는 test-runner 소관.
- `build-doctor` — `dotnet build`만.
- 기존 사용자 트리거 3종(Scoped/Full/History) — 변동 없음.

## 평가

### 정원지기 3축 평가

| 축 | 평가 | 코멘트 |
|----|------|--------|
| 워크플로우 개선도 | **B** | 자동화 정책의 사각지대(신규 테스트 false-pass)를 좁은 면적으로 정확히 메움. 다만 실제 효용은 신규 테스트가 작성되는 미션이 와봐야 검증 가능. 첫 발화 1~2회 안에 트레이드오프(LLM-스모크 ask-first)가 의도대로 동작하는지 재평가 필요. |
| Claude 스킬 활용도 | **3/5** | 본 변경은 test-runner / test-sentinel 두 스킬 사이의 경계를 더 또렷하게 만듦. 새 스킬 도입 없이 기존 스킬의 프론트매터 자동발견(`auto_invoke_on:`)을 활용 — pre-commit-review와 동일한 메커니즘을 일관되게 차용. |
| 하네스 성숙도 | **L4** | 정책-지식-에이전트-엔진-체인지로그가 모두 동기화. 평가축에 Author-verify-specific 측정치(narrowness, LLM guard)를 추가해 다음 회차의 감사 포인트도 확보. L5(자기개선 자동화)까지는 한 칸 더. |

### 위험과 완화

- **위험 1**: Author-verify가 "테스트 한 줄만 추가해도 무조건 자동실행"으로
  오해될 수 있음.
  → **완화**: 정책 본문 + agent 본문에 "한 태스크 최대 1회", "필터는
  신규 클래스/메서드로만 좁힘", "리네임 미해당"을 명시.
- **위험 2**: LLM-스모크 가드의 멘트를 잊고 자동실행해 CPU/RAM 폭주
  재발 가능.
  → **완화**: 평가축에 "LLM-smoke guard" Pass/Fail 추가, Procedure에
  실제 질문 문장 그대로 박음. 로그 mode가 `author-verify`인데 클래스가
  LLM-스모크면 감사에서 즉시 잡힘.
- **위험 3**: "신규" 판정 오류 — Claude가 이동/리네임을 새 테스트로
  착각해 실행.
  → **완화**: Procedure 도입부에서 "Pure renames, moves, or formatting
  changes do NOT fire it"를 명시. diff 분석 시 신규 메서드 시그니처/
  새 파일을 기준으로 판정.

## 다음 단계 제안

- **1차 검증**: 다음으로 Claude가 새 유닛테스트를 추가하는 미션이
  들어왔을 때, Author-verify가 의도대로 1회 / 좁은 필터 / LLM-가드를
  지키는지 로그(`harness/logs/test-runner/*-author-verify-*.md`)를
  점검한다.
- **2차 점검**: 1개월 이내(2026-06 말) "평가로그를 점검해" 시점에
  Author-verify 발화 로그를 모아 false-pass를 실제로 잡았는지,
  반대로 잘못 발화한 적 있는지 패턴 보고.
- **추후 확장 가능성** (지금은 안 함): 신규 테스트가 *기존 테스트와
  동일한 클래스 안에 추가된* 경우 batch 필터가 너무 넓어지는 케이스가
  관측되면, 메서드 단위 필터를 강제하는 추가 규칙 도입 검토.

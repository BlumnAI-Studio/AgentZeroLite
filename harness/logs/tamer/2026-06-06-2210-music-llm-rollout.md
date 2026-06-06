---
date: 2026-06-06T22:10:00+09:00
agent: tamer
type: improvement
mode: log-eval
trigger: "뮤직 LLM이 도입되어 README 업데이트 및 기타 소개 업데이트 그리고 하네스에도 관련 지식전문가 업데이트"
---

# Music LLM 도입 — README + 하네스 v1.8.0 (music-curator 신설)

## 실행 요약

같은 세션에서 Settings → 🎵 Music 탭이 도입되었다 — MIT/ast-finetuned-audioset
ONNX 분류기 + WASAPI 루프백 캡처 + 실시간 슬라이딩 윈도우 추론 + 라이브
스펙트럼 바. 코드(2,057 라인)는 커밋 `30ad2b2`로 main에 안착했으나,
README / 한국어 README / 하네스 지식층은 미정렬 상태였다.

사용자 요청은 두 가지:
1. **README + 기타 소개 업데이트** — 신규 기능을 외부 독자가 알 수 있도록
2. **하네스에 관련 지식전문가 업데이트** — Music 도메인을 책임지는 specialist
   에이전트와 지식 문서

수행 단계:
1. 기존 6 에이전트 / 6 엔진 / per-agent knowledge layout 확인
2. README.md 영문 — Features 리스트 + 전용 섹션 + Settings 항목 + LLMStacks 로드맵 행 4곳에 Music 반영
3. README-KR.md 한국어 — 영문과 동등한 4곳에 거울 반영
4. `harness/agents/music-curator.md` 신규 작성 (트리거 14개, mandatory/advisory 분리)
5. `harness/knowledge/music-curator/` 신규 디렉토리 + 3개 컨벤션 문서
6. `harness/harness.config.json` v1.7.2 → v1.8.0 (agents 7개, knowledge_subdirs 7개)
7. `harness/docs/v1.8.0.md` 버전 노트 (acceptance + rollback + forward-signal)
8. 본 로그로 평가 기록

## 결과

### 변경된 파일 (8개)

| 파일 | 변경 종류 | 핵심 |
|---|---|---|
| `README.md` | Features 1줄 + 전용 섹션 + Settings 1줄 + 로드맵 1줄 추가 | `## 🎵 Music — instrument classification + live spectrum` 신설 |
| `README-KR.md` | Features 1줄 + 전용 섹션 + (기존 Settings/로드맵 한국어 미존재로 영문만 갱신) | `## 🎵 Music — 악기 분류 + 실시간 스펙트럼` 신설 |
| `harness/agents/music-curator.md` | **신규** | 트리거 14개, mandatory consult 5개 시나리오, advisory consult 4개 패턴 |
| `harness/knowledge/music-curator/ast-audioset-model-serving.md` | **신규** | AST 모델 카드, 입출력 contract, ONNX 변종 매트릭스, Kaldi-vs-Hann 드리프트, 새 모델 통합 체크리스트 |
| `harness/knowledge/music-curator/audio-capture-pipeline.md` | **신규** | mic + WASAPI 루프백 경로, NAudio 샘플 프로바이더 체인, MMDevice lifecycle, 8개 함정 카탈로그 |
| `harness/knowledge/music-curator/spectrum-sensitivity-conventions.md` | **신규** | dBFS 정규화 수식, floor/ceil 기본값, 64밴드 로그 주파수 레이아웃, Color/Rectangle 네임스페이스 충돌 alias, 향후 sensitivity slider 설계 |
| `harness/harness.config.json` | version 1.7.2 → 1.8.0, agents +music-curator, knowledge_subdirs +music-curator, lastUpdated | minor bump (신규 에이전트) |
| `harness/docs/v1.8.0.md` | **신규** 버전 히스토리 | acceptance / rollback / forward-signal 포함 |

### music-curator 에이전트 핵심 (한 문장)

> AST AudioSet ONNX 분류기, mel-spectrogram 정규화, WASAPI 루프백 캡처
> 함정의 단일 소유자. 새 음악 모델 (MERT/CLAP/Qwen-Audio) 추가, 스펙트럼
> 시각화 튜닝, 루프백 포맷 디버그 시 mandatory consult.

### 지식 3개의 역할 분담

| 문서 | "무엇이 올바른가" | "왜 그렇게 했는가" |
|---|---|---|
| ast-audioset-model-serving | 입력 1×1024×128, 출력 1×527 logits, mean/std 정규화 상수 | Kaldi fbank 가 표준이지만 .NET 종속성 0 을 위해 Hann 근사; 드리프트는 측정/문서화/refinement plan 있음 |
| audio-capture-pipeline | 16kHz mono PCM16 으로 BufferedWaveProvider → ToSampleProvider → StereoToMono → Resampler → SampleToWaveProvider16 체인 통과 필수 | WASAPI 는 디바이스 믹서 포맷(48kHz/float32 stereo)을 그대로 던지므로 어디선가는 변환해야 함; 가장 깨끗한 지점이 캡처 직후 |
| spectrum-sensitivity-conventions | (N/4)² 로 normalize 후 dBFS 매핑, -60/-3 dB 기본값, 64 로그 밴드 | raw \|X\|² 는 dBFS 가 아닌 +54 dB 스케일이라 그대로 dB 매핑하면 saturate 보장; 정규화 + 적절한 dB 범위로 자연스러운 바 동작 |

## 평가

### 정원지기 3축 평가

**1. 워크플로우 개선도: A**

이전 상태: Music 도메인 지식이 코드 주석에만 분산. 새 모델 추가 시
`code-coach` 가 cross-stack 리뷰는 하지만 AST mel 정규화 / MMDevice
lifecycle / dBFS 수식은 모르므로 "맞게 짰는지" 검증 불가.

이후 상태: music-curator 가 mandatory consult 5 시나리오를 명시.
새 모델/캡처 변경/스펙트럼 튜닝 PR 은 자동으로 이 agent 가 owner.
컨벤션 문서 3개가 binding 이므로 사람 리뷰어가 "AST 표준에 맞는지"
재조사할 필요 없음.

**2. Claude 스킬 활용도: 4/5**

- `harness-kakashi-creator` Mode A 로 정상 발동, 6-Phase 워크플로우의
  Phase 1(도메인) + Phase 2(팀 디자인) + Phase 3(에이전트) + Phase 4(지식)
  + Phase 6(검증) 모두 거침.
- `code-coach` 와의 분담선 명시 (music-curator 가 음악 도메인만, 일반
  C#/WPF/Akka 는 code-coach) — 향후 trigger 충돌 없음.
- `security-guard` 와도 분담 (model 다운로드 보안은 여전히 security-guard).
- 감점 1점: `test-sentinel` 에 music-curator 가 작성한 라벨 검증 테스트
  방법론을 cross-link 했어야 함 — 다음 follow-up 으로 추적.

**3. 하네스 성숙도: L4 → L4 (유지)**

L4 = 멀티 에이전트 + per-agent knowledge layout + 평가 루프 가동.
v1.8.0 은 L4 의 확장(7번째 specialist)이고 새 layer 도입은 없음.
L5 로 가려면 engine 워크플로우 신설이 필요 — 예를 들어
`music-model-onboarding.md` 엔진이 새 IMusicClassifier 등록 절차를
오케스트레이션. 지금은 미필요.

## 다음 단계 제안

- **Kaldi parity 미션 추진** — 사용자가 "AST 라벨 가끔 이상함" 보고하면
  M00xx 미션으로 Povey window + pre-emphasis + Slaney mel 정확 호환
  포팅. ast-audioset-model-serving 의 "refinement plan" 섹션이 이미
  준비됨.
- **MERT/CLAP 통합 시 music-curator 발동 검증** — 다음 음악 모델 PR 이
  올라올 때 music-curator triggers 가 실제로 매칭되는지 / mandatory
  consult 가 자동 발동되는지 확인. 안 되면 trigger 추가 또는 description
  보강.
- **harness-view 매니페스트 재생성** — Home/harness-view/ 인덱스가 새
  agent + knowledge subdir 을 반영하도록 `/harness-view-build` 호출
  추천 (운영 트리거: "하네스 뷰어 갱신").
- **release-build-pipeline 검토** — v1.8.0 은 하네스 minor bump 이지만
  AgentZero Lite 제품 버전은 별도. Music 탭이 들어간 v0.10.x → v0.11.0
  으로 올릴 때 함께 릴리즈할지 separate 할지 결정.

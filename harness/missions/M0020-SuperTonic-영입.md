---
id: M0020
title: SuperTonic TTS 영입 (pip 기반 첫 사례)
operator: psmon
language: ko
status: done
priority: medium
created: 2026-05-17
---

# 요청 (Brief)

다음 TTS 기능을 AgentZero에 영입.

https://github.com/supertone-inc/supertonic

전제조건: 로컬 설치 방식으로 다음 명령어 수행. 설치 지원 없는 경우 설치 지원,
설치 확인된 경우 모델 선택 이용 가능.

```
pip install supertonic
```

pip로 인스톨을 하는 첫 케이스입니다. 설치된 CLI를 충분히 이해한 후 AgentZero의
VoiceLLM 선택가능 자산으로 영입.

이 CLI가 어떠한 기능을 지원하는지 아직 모두 파악 안 된 상태로 — TTS, STT 둘 다
되는지 하나만 되는지 검토 후 영입.

## Acceptance

- [ ] SuperTonic이 TTS/STT 중 어떤 기능을 지원하는지 조사 결과 명시
- [ ] `TtsProviderNames.Supertonic` 상수 + `VoiceSettings` 확장 필드
- [ ] `SuperTonicTts : ITextToSpeech` 구현 (`Project/ZeroCommon/Voice/`)
- [ ] `pip show supertonic` 기반 설치 확인 (`EnsureReadyAsync` 류)
- [ ] Settings 패널 Voice 탭에 Supertonic 선택지 + 보이스/언어 picker
- [ ] 미설치 시 사용자에게 `pip install supertonic` 안내
- [ ] 헤드리스 단위 테스트 (커맨드 빌더 + 런너 mock)
- [ ] `ZeroCommon` + `AgentZeroWpf` 빌드 0 오류

---
date: 2026-10-29T16:45:00+09:00
agent: device-smith
type: review
mode: execution
trigger: "웨러블 테스트를 했는데 음성입력이 안되었음 체크해봐줄래 기기문제인지 stt 문제인지"
---

# Avalonia(Debug)로 웨어러블 테스트 — 음성 입력 무응답은 STT가 아니라 기기 입력 레벨 문제

## 실행 요약

Avalonia Debug 빌드를 대표 버전으로 바꿔 웨어러블을 테스트했는데 음성 입력이 반응하지 않았다(WPF에서는 됐음).
"호스트 vs 기기" 1차 분리만 했다 — 플래시·시리얼 연결은 하지 않았다(COM7을 열면 보드가 리셋될 수 있음).

## 결과

### 1. 오디오는 호스트에 도착한다 — 끊김 없음
`capture #N ended: … 41~69 frames, 0 gaps`. BLE 경로와 ADPCM 프레임 수는 정상.

### 2. STT는 한 번도 돌지 않았다 — 무음 게이트에서 걸러짐
`[stt/info] capture is silence (peak …, rms …), not transcribing`.
게이트는 `WearableStt.cs:109` — `peak < SilencePeakDb(-45) || rms < SilenceRmsDb(-45)`.
Whisper는 매번 정상 로드됐다(`whisper ready … on vulkan:0`).

### 3. 같은 로그 파일 안의 비교 — 입력 레벨이 약 35 dB 떨어졌다

| 세션 | peak dBFS | rms dBFS | 결과 |
|---|---|---|---|
| 10-09 ~ 10-10 (WPF, 릴리스 프로필) | −0.4 ~ −12 | −24 ~ −34 | 전부 전사 성공 |
| 10-29 (Avalonia Debug, 오늘 13회) | −37 ~ −56 | −60 ~ −73 | 전부 무음 판정 |

말을 한 캡처와 안 한 캡처의 차이가 아니라, 오늘은 모든 캡처가 바닥 잡음 수준이다.

### 4. 호스트 쪽은 무죄
- `Project/ZeroWearable/Voice`, `ChatActor.cs`, `Ble/` — 10-10 작동 커밋(c63f45b) 이후 변경 없음.
- Avalonia가 띄우는 `bin/Debug/net10.0/wearable/AgentZeroWearable.exe`로 `--speak` → `--hear` 왕복:
  "오늘 서울 날씨 알려줘" → `heard: 오늘 서울 날씨 알려줘` (565 ms, vulkan). 디코드·STT 정상.
- PC 쪽(WPF·Avalonia·호스트 어디에도) 마이크 게인을 보내는 코드가 없다. 게인은 시계의 NVS `chat/gain`
  (기본 30 dB, 0~60)에만 있고 시계 Settings 앱 슬라이더/±5 버튼으로만 바뀐다(`settings_app.cpp:156,163`).

### 5. 결론
**STT 문제 아님. Avalonia 전환과도 무관.** 원인은 기기 쪽 입력 레벨이다. 약 30~35 dB 하락은 게인이 30 → 0 근처로
바뀐 것과 정확히 맞는다. 후보 순서는 (1) 시계 Settings의 마이크 게인이 낮아짐 (2) 마이크 구멍이 막혔거나 하드웨어 문제
(3) 10-09 21:27 빌드가 아닌 다른 펌웨어가 올라감.

## 평가

| 축 | 등급 | 근거 |
|---|---|---|
| 코드 안전성 | A | 읽기와 오프라인 왕복만 했고 기기 상태는 바꾸지 않았다 |
| 아키텍처 정합성 | A | 판정이 기기·호스트 경계(NVS 게인 vs 호스트 게이트)를 따라 나뉜다 |
| 테스트 가능성 | B | 호스트는 `--hear`로 재현했지만, 기기 게인 값은 시리얼 로그로 아직 확인하지 않았다 |

## 다음 단계 제안

1. 시계 Settings → 마이크 게인 확인(30 dB 근처로) → AskBot에서 한 번 더 말해 본다. 로그의 peak가 −15 dBFS 위로 올라오면 해결.
2. 그래도 낮으면 COM7 시리얼 모니터에서 `microphone ready: … gain N dB` 줄을 읽는다(보드 리셋 감수).
3. 개선 후보(host): 무음 판정 로그에 "평소 대비 30 dB 이상 낮음 — 기기 마이크 게인을 확인" 힌트를 붙이면 다음엔 로그만 보고도 판정할 수 있다.

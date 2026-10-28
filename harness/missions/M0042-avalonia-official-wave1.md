---
id: M0042
title: 아발로니아 공식판 1차 기능 묶음 (알림 · 비용 · Diff 리뷰 · 팔레트/단축키 · 정리)
operator: psmon
language: ko
status: in_progress
priority: high
created: 2026-10-28
related: [M0033, M0041]
---

# 요청 (Brief)

공식 AgentZero는 이제 WPF가 아니라 아발로니아다. 2026-10-28 전체 점검(`harness/logs/tamer/2026-10-28-12-51-avalonia-official-feature-sweep.md`)에서
뽑은 우선순위 1~5번을 워크트리 하나에서 통합 진행한다. 항목마다 완료 → 테스트 → 커밋, 5번 뒤에 푸시.
operator가 종합 e2e 로컬 테스트를 한 뒤 머지한다.

## Acceptance

- [x] 1. 에이전트 호출 알림 — blocked / 미확인 done 전환 시 구석 팝업 + 작업표시줄 깜빡임(Win) / Dock 바운스(mac), 설정 → Notifications, `-cli attention`
- [x] 2. 비용/예산 — 이미 수집 중인 토큰 사용량을 보여 줄 설정 → Budget, `-cli cost`, 상태바 이번 달 지출
- [ ] 3. Diff 리뷰 + AI 변경 검토 — 코드 패널 DIFF 탭, 줄 코멘트 → 에이전트로 전송, AI 모드 파일 쓰기 기록·되돌리기
- [ ] 4. 커맨드 팔레트(Ctrl+J) + 설정 → Shortcuts
- [ ] 5. 정리 — 버전 소스 호스트 중립화, `agentzero-cli` 스킬이 아발로니아 exe를 우선 찾고 없는 동사를 안내하지 않게

## Notes

- 변환 작업은 WPF 프로젝트를 건드리지 않는다(`git diff --stat main -- Project/AgentZeroWpf` 비어 있음). 5번의 버전 소스 이동만 예외 후보 — 그때 판단.
- 각 항목은 `README-Avalonia.md` / `README-Avalonia-KR.md` 표에 함께 기록.

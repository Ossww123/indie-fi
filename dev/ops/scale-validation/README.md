# 다중 Web·Worker 검증

로드맵 13-5의 같은 호스트 수평 확장 검증 자료다. `topology.md`가 공유·분리 상태와
판정 기준을 정의한다. 후속 단계에서 검증 전용 systemd template, Nginx 임시 upstream,
자동 원복과 사용자·동시성 runbook을 추가한다.

실제 결과는 `ops/validation/runs/<run-id>/scale/`에 저장하고 Git에 커밋하지 않는다.

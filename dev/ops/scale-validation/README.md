# 다중 Web·Worker 검증

로드맵 13-5의 같은 호스트 수평 확장 검증 자료다. `topology.md`가 공유·분리 상태와
판정 기준을 정의한다. 후속 단계에서 검증 전용 systemd template, Nginx 임시 upstream,
자동 원복과 사용자·동시성 runbook을 추가한다.

실제 결과는 `ops/validation/runs/<run-id>/scale/`에 저장하고 Git에 커밋하지 않는다.

## systemd instance template

- `systemd/indiefi-scale-web@.service`: base `web.env`에 `/run`의 port·instance id만 추가한다.
- `systemd/indiefi-scale-worker@.service`: base `worker.env`에 고유 instance id만 추가한다.
- `manage-scale-processes.sh`: 고정된 2×2 instance의 시작·중지·개별 restart·상태 조회를 담당한다.
- `install-scale-validation-tools.sh`: template과 도구를 설치하되 enable·start하지 않는다.

`start`는 다음 단계의 topology 전환기가 정확한 run id를 `/run/indiefi-scale/armed`에
기록한 뒤에만 허용된다. template을 직접 `systemctl start`하지 않는다.

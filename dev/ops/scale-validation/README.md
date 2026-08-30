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

## Nginx topology 전환

- `manage-scale-topology.sh`: 원본 config backup, 5101·5102 round robin, 45분 timer와 전체 원복
- `emergency-restore-nginx.sh`: 검증 중 재부팅하면 Nginx 시작 전에 single config 복원
- `nginx-distribution-runbook.md`: 두 upstream 확인, Web 1 제거·복귀와 사용자 영향 기록

검증 header와 Nginx 변경은 topology stop에서 제거된다. 45분보다 긴 검증은 허용하지 않고
필요하면 완전히 원복한 새 run으로 나눈다.

## 공유 경계

- `shared-boundaries-runbook.md`: cookie·cursor, SignalR Redis backplane과 Web 공통 upload 제한
- `key-drop-concurrency-runbook.md`: 다중 Redis 대기 순번과 마지막 key grant의 HTTP·DB 대조
- `worker-competition-runbook.md`: Worker 경쟁 소비·재시작과 Inbox·lease·부작용 수렴

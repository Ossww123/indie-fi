# 의존성 장애·재시작 복구 훈련

이 디렉터리는 로드맵 13-4를 같은 EC2에서 통제해 실행하기 위한 준비물이다.
`failure-matrix.md`가 기대 동작과 데이터 경계를, `run-controlled-failure.sh`가
허용된 중단과 자동 원복을 담당한다. 실제 결과는 `ops/validation/runs/` 아래에만
저장하고 Git에는 커밋하지 않는다.

## 로컬에서 할 일

```powershell
bash -n ops/failure-drills/run-controlled-failure.sh
bash -n ops/failure-drills/install-failure-drill-tools.sh
bash ops/failure-drills/run-controlled-failure.sh --plan redis-outage 60 local-check
```

`--plan`은 서비스나 컨테이너를 바꾸지 않는 읽기 전용 출력이다. 실제 EC2 실행은
복원 훈련이 통과하고 관측 gateway가 연결된 뒤에만 한다.

## EC2 설치와 실행 형태

EC2로 이 디렉터리를 복사한 다음 설치한다.

```bash
sudo ./install-failure-drill-tools.sh
/usr/local/lib/indiefi/failure-drills/run-controlled-failure.sh \
  --plan redis-outage 60 <run-id>
sudo /usr/local/lib/indiefi/failure-drills/run-controlled-failure.sh \
  redis-outage 60 <run-id> RUN-<run-id>-redis-outage
```

실행 명령은 장애 관찰 시간 동안 foreground에 머문다. 별도 SSH 창에서 사용자 흐름과
dashboard를 본다. SSH가 끊기거나 Ctrl+C를 눌러도 trap이 원복을 시도하지만, 완료 후
서비스·컨테이너 상태를 다시 확인하기 전에는 다음 시나리오를 시작하지 않는다.

## 금지 사항

- 운영 Compose의 `down`, volume 삭제, DB·queue·Redis key 수동 삭제
- `/etc/indiefi/*.env` 수정 또는 비밀값을 명령행에 복사
- 둘 이상의 장애 동시 실행
- 최대 시간 상향, 자동 원복을 우회한 수동 `docker stop`
- PostgreSQL 복원 검증 전 데이터 의존성 장애 실행

후속 세부 항목에서 시나리오별 준비·관측·정합성 검사 절차를 이 문서에 추가한다.

## 시나리오 runbook

- `web-restart-runbook.md`: Nginx 응답, readiness, 기존 로그인 쿠키와 SignalR·폴링 회복
- `worker-restart-runbook.md`: SIGKILL 뒤 systemd 재시작, RabbitMQ 재전달·Inbox와 media lease 수렴
- `postgresql-outage-runbook.md`: DB 실패 닫힘과 크레딧 원장·키 지급·Outbox before/after 감사
- `redis-outage-runbook.md`: AOF와 대기 순번 복구, PostgreSQL 최종 키 지급 경계

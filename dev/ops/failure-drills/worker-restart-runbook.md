# Worker 비정상 종료·재시작 훈련

## 검증 질문

1. 처리 중 Worker main process가 강제 종료되어도 systemd가 새 PID로 다시 시작하는가?
2. ack 전 RabbitMQ 메시지가 유실되지 않고 ready 또는 새 consumer에게 재전달되는가?
3. 같은 Message-ID의 재전달을 Inbox와 도메인 유일성 제약이 한 번만 확정하는가?
4. 처리 중 미디어 lease가 영구 고착되지 않고 만료 뒤 재획득되어 terminal 상태로 가는가?
5. 사용자 Web 요청은 Worker 재시작과 독립적으로 계속 확정되는가?

## 준비

- Web·Worker와 세 데이터 컨테이너가 정상이고 queue·Outbox·media in-flight 기준선을 기록한다.
- 테스트 계정으로 알림을 만드는 작업과 작은 이미지 업로드를 준비한다.
- RabbitMQ queue의 `consumers`, `messages_ready`, `messages_unacknowledged`를 볼 별도 창을 연다.
- Worker journal과 async dashboard를 같은 UTC 구간으로 연다.
- 기존 backlog가 있으면 새 장애를 시작하지 말고 먼저 0으로 수렴시킨다.

## 실행

알림 사건과 미디어 작업을 만든 직후 계획을 다시 확인하고 한 번만 실행한다.

```bash
TOOL=/usr/local/lib/indiefi/failure-drills/run-controlled-failure.sh
RUN_ID=<run-id>
"$TOOL" --plan worker-restart 60 "$RUN_ID"
sudo "$TOOL" worker-restart 60 "$RUN_ID" "RUN-${RUN_ID}-worker-restart"
```

제어 도구는 현재 `MainPID`를 기록하고 main process에 SIGKILL을 한 번 보낸다. systemd의
`Restart=on-failure`가 기존 PID와 다른 Worker를 시작할 때까지 기다린다. 서비스 전체를
disable하거나 unit·환경 파일을 변경하지 않는다.

## 관측과 기록

```bash
sudo systemctl show indiefi-worker.service \
  --property=ActiveState,SubState,MainPID,NRestarts,ExecMainStatus
docker exec indiefi-rabbitmq rabbitmqctl list_queues \
  name durable consumers messages_ready messages_unacknowledged
```

다음 값은 주입 직전, PID 종료, 새 PID active, 1분·5분·lease 만료 뒤에 기록한다.

- Worker PID·`NRestarts`, RabbitMQ queue별 consumer·ready·unacknowledged
- PostgreSQL message Outbox·notification delivery의 상태별 건수
- media 상태별 건수, processing due와 expired lease 건수
- 동일한 사용자 사건의 알림·전달·ledger 결과 건수(식별자와 내용은 출력하지 않음)
- Worker 재연결과 최종 수렴 UTC 시각

작업이 너무 빨리 끝나 unacknowledged를 관측하지 못했다면 메시지 유실 성공으로 해석하지
않고 `not observed`로 남긴다. payload를 크게 만들거나 DB 상태를 직접 바꿔 처리 시간을
인위적으로 늘리지 않는다. 후속 다중 Worker·부하 단계의 자연스러운 처리 중단 증거로 보완한다.

## 완료 기준

- Worker가 새 PID로 active이고 RabbitMQ consumer가 원래 수로 돌아온다.
- ready·unacknowledged, Outbox와 due 작업이 제한 시간 뒤 0 또는 기존 기준선으로 수렴한다.
- 알림·미디어·ledger에서 같은 사건이 두 번 확정되지 않는다.
- 미디어 lease는 기본 만료 경계를 지나 새 처리로 인계되고 `Ready` 또는 안전한 `Failed`가 된다.
- 영구 `_error` queue가 새로 생기면 통과로 처리하지 않고 원인과 제한 재처리 대상으로 남긴다.

# 다중 Worker 경쟁 소비와 재시작

## 로컬 회귀 기준

- `RabbitMq_TwoWorkerHosts_CompeteForOneUserResult`: consumer 2개가 같은 queue를 읽어도 알림은 한 번만 확정된다.
- `RabbitMq_QueuedMessage_IsConsumedAfterWorkerRestart`: consumer 0개일 때 쌓인 durable message가 재시작 뒤 한 번 처리된다.
- `TwoWorkers_DuplicateRestartAndStorageOutage_ConvergeToReady`: 중복 media message, 만료 lease와 저장소 장애가 최종 `Ready`로 수렴한다.
- `ConcurrentAcquisition_GrantsExactlyOneTenMinuteLease`: 같은 media lease를 동시에 잡아도 한 Worker만 획득한다.

## EC2 실행 전 경계

- 전용 run id와 테스트 계정·게임·media를 사용하고 실제 사용자 자료는 만들거나 수정하지 않는다.
- scale Worker 두 개의 consumer 수가 각 대상 queue에서 2인지 먼저 확인한다.
- 원본 `indiefi-worker.service`는 scale topology 전환 뒤 inactive여야 한다. 세 Worker를 동시에 비교하지 않는다.
- 재시작은 unit을 직접 `kill`하지 않고 준비된 관리 도구만 사용한다.

## 증거 수집과 재시작

실제 batch는 `loadtest/worker-competition.js`로 만든다. 전용 claimer 계정의 선택 알림 이메일을 먼저 끄고
드롭 오픈 알림을 구독한 뒤, 지정 계정만 1×1 PNG profile image를 4개 파동으로 올린다. 실제 사용자,
cookie·비밀번호·antiforgery token·presigned URL은 결과에 남기지 않는다. R2 PUT metric도 URL system tag를
제외하고 고정된 request name만 사용한다.

드롭 오픈 worker가 fan-out을 staged한 직후 Worker 1을 관리 도구로 한 번 재시작한다. 선택 알림 이메일을
끄는 이유는 `example.test` 합성 계정으로 SES 전송을 만들지 않고 in-app notification 경쟁 소비만 검증하기
위해서다. `AccountStatusChanged`는 필수 이메일이지만 이번 batch에서 해당 사건을 만들지 않는다.

```bash
RUN_ID=<run-id>
EVIDENCE="$HOME/indiefi-validation/runs/$RUN_ID/scale/workers"
SCALE=/usr/local/lib/indiefi/scale-validation
FAILURE=/usr/local/lib/indiefi/failure-drills
install -d -m 0700 "$EVIDENCE"

"$SCALE/capture-scale-runtime.sh" "$EVIDENCE/before-runtime.txt"
"$FAILURE/capture-authoritative-state.sh" "$EVIDENCE/before-db.tsv"
"$FAILURE/capture-rabbitmq-state.sh" "$EVIDENCE/before-rabbitmq.txt"

# 전용 알림·fan-out·media 작업 batch를 만든 뒤 ready 또는 unacknowledged가 보이는 구간에 실행한다.
sudo "$SCALE/manage-scale-processes.sh" \
  restart-worker-1 "$RUN_ID" "RESTART-WORKER-1-$RUN_ID"

"$SCALE/capture-scale-runtime.sh" "$EVIDENCE/restarted-runtime.txt"
"$FAILURE/capture-rabbitmq-state.sh" "$EVIDENCE/restarted-rabbitmq.txt"
```

재시작 직전·직후 UTC를 기록하고 `journalctl -u indiefi-scale-worker@1.service`의 해당 구간만
별도 파일로 저장한다. 환경변수나 SMTP·R2 자격 증명을 출력하는 명령은 사용하지 않는다.

## 수렴 판정

1분·5분과 media lease 만료 경계 뒤 같은 세 snapshot을 반복한다.

- queue의 consumer가 다시 2가 되고 `messages_ready`와 `messages_unacknowledged`가 기준선으로 돌아온다.
- `message_outbox_messages`, unresolved notification delivery와 media in-flight가 기준선으로 수렴한다.
- 같은 event·사용자 알림 row와 채널 delivery가 한 번만 존재한다.
- media는 같은 revision·객체 key로 `Ready` 또는 설명 가능한 terminal `Failed`가 되며 active/expired lease가 남지 않는다.
- 재시작하지 않은 Worker도 계속 소비했고 Web readiness와 동기 요청은 유지된다.

처리가 너무 빨라 `unacknowledged`를 포착하지 못해도 unit 재시작과 batch 도중 consumer 감소·복귀는
유효한 운영 증거다. 다만 이를 “정확히 handler 중간을 죽였다”라고 표현하지 않고, lease 만료 복구는
결정적 로컬 회귀와 별도로 연결해 기록한다.

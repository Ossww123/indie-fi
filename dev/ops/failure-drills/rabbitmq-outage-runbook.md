# RabbitMQ 중단·Outbox·error queue 복구 훈련

## 데이터 경계

- 사용자 트랜잭션과 발행 의도: PostgreSQL Transactional Outbox
- 전달 중 내구성·consumer 분배: RabbitMQ durable queue
- 중복 흡수: MassTransit Inbox와 도메인 유일성 제약
- 반복 실패 격리: 원 queue 이름 뒤 `_error`

broker 중단 중 Outbox row를 지우거나 delivered로 직접 바꾸지 않는다. 복구 뒤 원래
Message-ID로 발행되어야 Inbox가 재전달을 식별할 수 있다.

## 준비와 before 감사

```bash
RUN_ID=<run-id>
EVIDENCE="$HOME/indiefi-validation/runs/$RUN_ID/failures/rabbitmq-outage"
TOOL=/usr/local/lib/indiefi/failure-drills/run-controlled-failure.sh
RABBIT_AUDIT=/usr/local/lib/indiefi/failure-drills/capture-rabbitmq-state.sh
DB_AUDIT=/usr/local/lib/indiefi/failure-drills/capture-authoritative-state.sh
"$RABBIT_AUDIT" "$EVIDENCE/before-rabbitmq.txt"
"$DB_AUDIT" "$EVIDENCE/before-db.tsv"
"$TOOL" --plan rabbitmq-outage 60 "$RUN_ID"
```

기존 `messages_ready`, `messages_unacknowledged`, Outbox와 unresolved delivery가 0 또는
설명 가능한 기준선이어야 한다. 알 수 없는 `_error` queue가 있으면 새 장애를 시작하지 않는다.

## broker 중단과 Outbox 관측

```bash
sudo "$TOOL" rabbitmq-outage 60 "$RUN_ID" \
  "RUN-${RUN_ID}-rabbitmq-outage"
```

중단 창에서 테스트 계정으로 알림을 만드는 도메인 행동 한 건과 미디어 완료 한 건만
확정한다. Web 응답, PostgreSQL Outbox 증가 시각과 `rabbitmq_ready=0` 경보를 기록한다.
사용자 트랜잭션까지 실패하거나 같은 행동을 반복해야만 저장된다면 유효 실패다.

복구 뒤에는 아래를 다시 수집하고 1분·5분 cool-down에서 반복한다.

```bash
"$RABBIT_AUDIT" "$EVIDENCE/after-rabbitmq.txt"
"$DB_AUDIT" "$EVIDENCE/after-db.tsv"
```

consumer가 돌아오고 Outbox, ready·unacknowledged, notification·media가 한 번의 최종
결과로 수렴해야 한다. queue purge, payload 복사·재발행과 새 Message-ID 발급은 금지한다.

## `_error` queue 검증 경계

로컬 통합 테스트 `RabbitMq_PermanentConsumerFailure_RetriesThenMovesToErrorQueue`는 고유
prefix의 격리 consumer가 항상 예외를 내게 한다. 운영과 같은 1초·5초·30초 retry 뒤
총 4번 소비되고 원 queue가 비며 `_error` queue에 한 건이 남는지 검증한다. 테스트
contract와 queue는 Testcontainer 안에만 존재하며 운영 broker에 발행되지 않는다.

EC2에서는 임의 JSON이나 실제 사용자 사건을 poison message로 사용하지 않는다. 별도의
검증 전용 publisher를 배포하지 않은 현재 범위에서는 기존 `_error` queue의 조회·보존·
원인 수정·제한 재처리 절차만 확인하고, 실제 운영 queue 격리 재현은 `not executed`로
명시한다. 이는 안전을 위해 의도적으로 남긴 한계이지 통과 증거가 아니다.

## 완료 기준

- RabbitMQ가 running이고 local alarm이 없으며 모든 기대 queue에 consumer가 복귀한다.
- Outbox와 ready·unacknowledged가 제한 시간 안에 기준선으로 수렴한다.
- 장애 중 확정한 사건의 알림·미디어 결과가 유실·중복되지 않는다.
- 로컬 격리 테스트가 retry 4회와 `_error` queue 1건을 증명한다.
- EC2에서 실제 error queue를 발견하면 삭제하지 않고 원인·건수·재처리 승인 상태를 남긴다.

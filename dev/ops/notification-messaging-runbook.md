# 알림·메시징 운영 가이드

- 적용 범위: 알림 Transactional Outbox, RabbitMQ, 알림 Worker, SMTP 전달
- 최종 권위: 사용자 알림·전달 상태와 Outbox·Inbox는 PostgreSQL
- 전달 방식: 적어도 한 번 전달, 도메인 유일성 제약과 Inbox로 멱등 수렴
- 고정 버전: RabbitMQ `4.3.4-management-alpine`, MassTransit `8.5.10`

이 문서의 조회 명령과 로그에는 이메일 주소, 알림 제목·본문, 사용자·이벤트·Message-ID를 출력하지 않는다.

## 1. 배포 구성

Web과 모든 Worker는 같은 PostgreSQL, RabbitMQ virtual host, 자격 증명과 `EndpointPrefix`를 사용한다. 환경별 prefix는 서로 달라야 하며 운영 계정으로 `guest`를 사용하지 않는다.

| 구성요소 | 역할 | 시작 조건 |
|---|---|---|
| PostgreSQL | 도메인 원장, 알림, Outbox·Inbox, 이메일 전달 상태 | migration 완료와 앱 역할 연결 성공 |
| RabbitMQ | durable 사건 queue와 competing consumer | `rabbitmq-diagnostics check_running` 성공 |
| Web | 도메인 변경과 Outbox 발행 의도 저장 | RabbitMQ 중단 중에도 요청 확정 가능 |
| Worker | RabbitMQ 소비, 이메일 전달, 정리·운영 관측 | RabbitMQ 설정 필수, 앱과 같은 보호 키 필요 |
| SMTP | 알림 이메일 최종 전달 | 실패해도 Web·RabbitMQ 소비를 막지 않음 |

운영에서는 RabbitMQ 5672와 관리 포트를 사설망에만 노출하고 데이터 디렉터리를 영속 볼륨과 백업 대상에 포함한다. Web과 Worker의 비밀번호는 설정 파일에 커밋하지 않고 비밀 관리 수단으로 주입한다.

## 2. 배포 순서와 확인

1. PostgreSQL을 백업하고 migration을 적용한다.
2. RabbitMQ 노드·영속 볼륨과 virtual host를 준비한다.
3. 아래 진단이 모두 성공한 뒤 기존 queue의 durable 상태와 consumer 수를 확인한다.
4. Web을 배포한다. RabbitMQ가 일시 중단돼도 Outbox가 요청 트랜잭션과 함께 저장되는지 확인한다.
5. Worker를 한 인스턴스씩 배포하고 기존 인스턴스를 제거한다.
6. `rabbitmq_ready=1`, Outbox 최장 대기 감소와 각 알림 queue consumer 1개 이상을 확인한다.
7. Mailpit 또는 승인된 테스트 수신함으로 알림 이메일 한 건의 제목·평문 본문·안정적 `Message-ID`와 DB `Delivered` 전환을 확인한다.

```bash
docker exec indiefi-rabbitmq rabbitmq-diagnostics -q check_running
docker exec indiefi-rabbitmq rabbitmq-diagnostics -q check_local_alarms
docker exec indiefi-rabbitmq rabbitmqctl list_queues name durable consumers messages_ready messages_unacknowledged
```

Web의 `/health/ready`는 Web 요청에 필수인 의존성을 확인한다. RabbitMQ 중단은 Web의 도메인 쓰기를 막지 않고 Outbox에 보존해야 하므로, 알림 전달 readiness는 Worker의 `indiefi.notifications.rabbitmq.ready`와 RabbitMQ 진단으로 별도 판단한다.

## 3. 메트릭과 초기 경보 기준

수집기는 `IndieFi.Notifications` meter를 구독한다.

| 메트릭 | 의미 | 초기 경보 출발점 |
|---|---|---|
| `indiefi.notifications.consumer.messages` | 계약별 소비 성공·최종 실패 | `outcome=failed`가 5분에 1건 이상이면 경고 |
| `indiefi.notifications.consumer.retries` | 소비 재시도 시도 | 평시 기준선 초과가 5분 지속되면 경고 |
| `indiefi.notifications.consumer.duration` | 재시도 포함 소비 시간(ms) | p95가 30초 이상 5분 지속되면 경고 |
| `indiefi.notifications.email.deliveries` | sent·expired·rescheduled·failed·batch_fault | failed 또는 batch_fault가 1건 이상이면 경고 |
| `indiefi.notifications.email.batch.duration` | 이메일 배치 처리 시간(ms) | Worker 주기보다 길어지면 경고 |
| `indiefi.notifications.outbox.backlog` | PostgreSQL 미발행 메시지 수 | 최장 대기와 함께 증가하면 경고 |
| `indiefi.notifications.outbox.oldest_age` | 가장 오래된 Outbox 대기(초) | 60초 경고, 300초 긴급 |
| `indiefi.notifications.email.pending` | 미래 재시도 포함 Pending 이메일 | due와 함께 해석 |
| `indiefi.notifications.email.due` | 지금 처리해야 할 이메일 | 최장 due 지연과 함께 증가하면 경고 |
| `indiefi.notifications.email.failed` | 미해결 실패 보관 건수 | 1건 이상 경고 |
| `indiefi.notifications.email.oldest_due_age` | 가장 오래된 due 지연(초) | 120초 경고, 900초 긴급 |
| `indiefi.notifications.rabbitmq.ready` | 1 정상, 0 비정상, -1 관측 불가 | 1이 아닌 상태가 2분 지속되면 긴급 |
| `indiefi.notifications.operations.observation_failures` | 운영 상태 수집 실패 | 1건 이상 경고 |

임계값은 초기 출발점이다. 실제 처리량과 SMTP 지연의 기준선이 쌓이면 false positive와 복구 목표에 맞춰 조정한다. 메트릭에 사용자·게임·배포·이벤트·이메일 식별자 태그를 추가하지 않는다.

## 4. 구조화 로그

다음 로그를 메트릭과 같은 시각 범위에서 확인한다.

- `Notification operations rabbitmq_ready=... outbox_backlog=...`
- `Notification email batch sent=... expired=... rescheduled=... failed=...`
- `Notification email batch failed with {FailureType}`
- `Notification maintenance removed_content=...`
- `Notification maintenance failed with {FailureType}`
- `Notification projection kind=... outcome=...`

예외 형식과 안전한 오류 코드만 사용한다. SMTP 서버의 원문 응답, 수신 주소, 알림 내용, 암호문과 복호화 키는 로그에 남기지 않는다.

## 5. 안전한 PostgreSQL 상태 조회

아래 조회는 payload와 사용자 정보를 읽지 않는다.

```sql
SELECT count(*) AS outbox_backlog,
       min(sent_time) AS oldest_outbox_at
  FROM message_outbox_messages;

SELECT status,
       count(*) AS delivery_count,
       min(next_attempt_at) FILTER (
           WHERE status = 'Pending' AND next_attempt_at <= now()) AS oldest_due_at
  FROM notification_deliveries
 WHERE channel = 'Email'
 GROUP BY status
 ORDER BY status;
```

Outbox 행의 `body`, `headers`, `properties`와 알림의 제목·본문을 일반 장애 진단 목적으로 조회하지 않는다.

## 6. RabbitMQ 중단과 복구

1. Web의 도메인 쓰기를 임의로 롤백하거나 같은 사용자 작업을 재실행하지 않는다.
2. RabbitMQ 노드·디스크·메모리 alarm과 네트워크를 확인한다.
3. PostgreSQL에서 Outbox 건수와 최장 시각이 증가하는지 확인한다.
4. RabbitMQ를 복구하고 Worker가 `rabbitmq_ready=1`이 될 때까지 기다린다.
5. Outbox backlog와 oldest age가 감소해 0으로 수렴하는지 확인한다.
6. 영향을 받은 사건의 최종 알림은 사용자·event 유일성으로 한 건인지 확인하되 내용이나 주소를 로그에 출력하지 않는다.

브로커 중단 중 Outbox 행을 삭제하거나 수동으로 `delivered` 처리하지 않는다. 복구 후 MassTransit 발행자가 원래 Message-ID로 전달하게 둔다.

## 7. Worker 재시작과 scale-out

- 같은 환경의 Worker는 동일 prefix를 사용해 하나의 durable queue에서 competing consumer로 동작한다.
- 배포 전후 `list_queues`에서 각 알림 queue의 consumer 수를 확인한다.
- 한 인스턴스씩 중단하고 새 인스턴스가 준비된 뒤 다음 인스턴스를 교체한다.
- 모든 Worker가 내려가도 durable queue의 `messages_ready`는 보존된다. 복구 후 ready가 감소하고 최종 알림이 중복되지 않는지 확인한다.
- queue 이름이나 prefix를 바꾸는 배포는 새 queue를 만드는 변경이다. 이전 queue가 비었는지 확인하고 별도 이전 계획 없이 기존 queue를 삭제하지 않는다.

## 8. 소비 오류 queue

MassTransit은 1초·5초·30초 재시도 뒤에도 실패한 메시지를 원 endpoint의 `_error` queue로 격리한다.

1. 관리 화면이나 `list_queues`에서 정확한 `<endpoint>_error` queue와 건수를 확인한다.
2. 원본 queue, Message-ID, 발생 시각과 안전한 예외 분류만 운영 기록에 남긴다.
3. DB 권위 데이터와 consumer 코드를 확인해 원인을 먼저 수정한다.
4. error queue와 원 queue를 명시한 RabbitMQ Shovel 등 속성·헤더를 보존하는 broker 기능으로 제한된 건수만 재전달한다.
5. Inbox와 도메인 유일성 제약이 중복을 흡수하고 error queue·적체가 감소하는지 확인한다.

메시지 JSON을 복사해 새 Message-ID로 임의 발행하거나 error queue 전체를 삭제하지 않는다. 대량 재처리는 영향 건수와 중단 조건을 승인받은 뒤 작은 배치로 수행한다.

## 9. 이메일 실패 조회와 재처리

`NotificationEmailOperationsService`의 운영 경계는 Pending·due·Failed·Expired 요약과 민감정보 없는 실패 목록만 반환한다. 관리자 화면은 관리자 기능 단계에서 이 서비스에 연결한다.

- 재처리는 전달 ID와 현재 Message-ID가 모두 일치하는 Failed 행만 허용한다.
- 같은 행을 `Pending`으로 되돌리되 Message-ID, 누적 시도 횟수와 마지막 안전 오류 코드를 유지한다.
- Delivered·Expired는 재처리하지 않는다.
- SMTP 자격 증명, 수신 주소 오류나 차단 원인을 먼저 수정한 뒤 한 건씩 재처리한다.
- 재처리 후 `email.due`, `email.failed`, sent·failed 결과를 확인한다.

DB에서 status를 직접 UPDATE하거나 새 알림·새 전달 행을 만들지 않는다. 이는 동시 재처리 보호와 안정적인 `Message-ID`를 우회한다.

## 10. 보존과 정리

알림 정리 Worker는 기본 1시간마다 최대 500건씩 다음 규칙을 적용한다.

| 데이터 | 정리 기준 |
|---|---|
| 웹 알림 내용 | 생성 후 90일인 `expires_at`부터 내용 제거, 알림 식별 행 유지 |
| 완료 전달 | 알림 만료 후 30일, Pending·미해결 Failed 제외 |
| 완료 Outbox 상태 | 전달 후 7일, 연결된 메시지가 없을 때 |
| 소비 Inbox 상태 | 30일 중복 감지 만료 후, 처리 완료와 연결 메시지 없음 |
| 미해결 Failed | 해결 또는 폐기 전 유지 |

자동 정리가 밀렸을 때만 앱 역할로 제한된 함수를 작은 배치로 호출한다.

```sql
SELECT * FROM public.cleanup_notification_records(now(), 500);
```

반환된 네 건수를 기록하고 0으로 수렴할 때까지 DB 부하를 관찰하며 반복한다. 테이블 직접 DELETE, 광범위한 날짜 조건이나 Pending·Failed 삭제는 금지한다.

## 11. 최종 자동 검증

저장소 루트에서 실행한다.

```powershell
dotnet build IndieFi.slnx --no-restore
dotnet test IndieFi.slnx --no-build
```

RabbitMQ 장애·다중 Worker·재시작만 다시 확인할 때는 다음을 실행한다.

```powershell
dotnet test tests/IndieFi.Web.Tests/IndieFi.Web.Tests.csproj --no-build --filter "FullyQualifiedName~RabbitMqOutboxRecoveryTests"
```

릴리스 증거에는 시각, commit, 테스트 수, 실패·건너뜀 수, PostgreSQL·RabbitMQ·Mailpit 이미지 버전을 기록한다.

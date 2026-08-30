# IndieFi 의존성 장애 훈련 매트릭스

## 공통 안전 경계

- 한 번에 한 시나리오만 실행한다. 복합 장애와 EC2 재부팅은 이 훈련 범위가 아니다.
- 실제 실행 전 같은 commit의 Web·Worker와 PostgreSQL·Redis·RabbitMQ가 모두 정상이어야 한다.
- 지속 장애는 기본 60초, 최대 180초로 제한한다.
- 컨테이너·볼륨·queue·Redis key·DB 행은 삭제하지 않는다.
- `/run/systemd/system`의 임시 drop-in만 사용하며 영구 환경 파일과 비밀값은 수정하지 않는다.
- SSH 종료·Ctrl+C·오류에도 원복 함수를 실행한다. 원복 실패 시 다음 시나리오를 시작하지 않는다.
- 결과에는 사용자 ID·이메일·게임 키·쿠키·token·R2 서명 URL·메시지 payload를 남기지 않는다.

## 시나리오

| 시나리오 | 통제된 주입 | 예상 사용자 영향 | 반드시 보존할 것 | 기대 관측 | 자동 원복·완료 기준 |
|---|---|---|---|---|---|
| `web-restart` | `indiefi-web.service` 1회 restart | 진행 중 요청 일부 실패, Nginx 502/503 가능 | 로그인 쿠키를 해독하는 공유 Data Protection key, 확정 DB 데이터 | systemd 상태 전이, HTTP 오류, readiness 회복 | Web active, `/health/ready` 200, 기존 로그인 재사용 |
| `worker-restart` | `indiefi-worker.service` 1회 restart | 웹 요청은 유지, 비동기 알림·미디어 일시 지연 | RabbitMQ durable message, Inbox 멱등성, DB media lease | consumer 일시 0, queue 또는 due 증가, Worker 재연결 | Worker active, consumer 복귀, queue·lease 최종 수렴 |
| `postgresql-outage` | `indiefi-db`를 제한 시간 stop | DB가 필요한 readiness와 쓰기·조회 실패 | PostgreSQL volume, 확정 원장·키 지급·Outbox | `pg_up=0`, readiness 실패, DB 오류 trace | DB healthy, migration 일치, 원장·Outbox 감사 통과 |
| `redis-outage` | `indiefi-redis`를 제한 시간 stop | 대기실·SignalR backplane·공유 제한 기능 제한 | Redis AOF와 PostgreSQL의 최종 키 지급 | `redis_up=0`, 대기실 실패/제한, 재연결 | Redis healthy, AOF 순번 확인, 키 지급 정합성 유지 |
| `rabbitmq-outage` | `indiefi-rabbitmq`를 제한 시간 stop | 확정 요청은 유지하되 알림·미디어 전달 지연 | PostgreSQL Outbox, RabbitMQ volume, Message-ID | broker readiness 0, Outbox 증가, consumer 0 | broker healthy, consumer 복귀, Outbox·queue 0 수렴 |
| `r2-outage` | Web·Worker에 loopback 실패 endpoint 임시 주입 | 미디어 업로드·조회·처리 제한, 핵심 게임·리뷰·키는 유지 | media DB 상태·lease·재시도 횟수, 기존 R2 객체 | readiness/storage 오류, transient retry·due 증가 | 원 endpoint 복귀, media가 terminal 상태로 수렴 |
| `ses-outage` | Worker의 두 SMTP host를 loopback 실패 endpoint로 임시 주입 | 웹 알림 유지, 인증·알림 이메일 지연 | 인증 email Outbox, notification delivery와 Message-ID | SMTP 실패 분류, due/pending 증가 | 원 SMTP 복귀, Outbox·delivery가 중복 없이 전달 |

## 실행 판정

- **통과**: 예상 영향 안에서 실패하고 제한 시간이 끝난 뒤 서비스와 확정 데이터가 모두 수렴한다.
- **유효 실패**: 안전장치와 증거는 유효하지만 구현 결함 또는 목표 미달이 드러난다.
- **무효**: 시작 기준 불충족, 잘못된 seed·commit, 관측 단절 또는 둘 이상의 장애가 섞였다.
- **안전 중단**: 원복 실패, root disk 85% 이상, 사용 가능 메모리 512 MiB 미만 또는 예상 밖 데이터 변경이 보인다.

실제 명령과 관측 순서는 `README.md`를 따른다. 이 표의 완료는 도구 준비 완료를 뜻하며,
EC2에서 각 시나리오를 실행하고 증거를 남겨야 장애 훈련 자체가 완료된다.

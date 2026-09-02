# 운영 의존성 장애 훈련 최종 보고서

- 실행 구간: 2026-09-01~2026-09-02 UTC
- 대상: 단일 EC2의 Web, Worker, PostgreSQL, Redis, RabbitMQ, R2, SES
- 범위: 로드맵 13-4
- 원시 증거: Git에서 제외한 `ops/validation/runs/*/failures/*`

## 판정 방법

`감지`는 장애 주입부터 controller 또는 첫 HTTP·DB 실패 표본까지, `기술 RTO`는
의존성과 서비스가 다시 요청을 처리할 수 있을 때까지, `최종 수렴`은 Outbox·queue·media와
정합성 감사까지 정상화될 때까지다. 수동 브라우저 확인에는 사람의 대기 시간이 섞였으므로
정확히 자동 측정하지 못한 사용자 RTO는 숫자를 대신 넣지 않고 `미측정`으로 남겼다.

아래 시간은 보고서 생성기가 저장된 UTC 원시 timestamp로 다시 계산한 값이다. 기존 runner의
표시값과 Web 기술 RTO 0.002초, Worker 최종 수렴 0.031초, PostgreSQL 감지 0.002초 차이가
있으며, 반올림된 중간 duration보다 원시 timestamp 재계산값을 최종값으로 사용한다.

## 일곱 시나리오 결과

| 시나리오 | 최초 run 판정 | 감지 | 기술 RTO | 최종 수렴 | 사용자 RTO | 핵심 결과 |
|---|---|---:|---:|---:|---|---|
| Web 재시작 | Passed | 0.664초 | 5.139초 | 25.104초 | 미측정 | 연속 502 네 번 뒤 기존 로그인·실시간 화면 복구 |
| Worker SIGKILL | Passed | 0초 | 7.617초 | 21.191초 | 미측정 | 새 PID·consumer 복귀, 알림 한 건과 media 중복 없이 완료 |
| PostgreSQL 중단 | ValidFailure | 0.595초 | 82.424초 | 100.276초 | 미측정 | 확정 데이터 보존, 누락된 DB readiness를 발견·수정 후 별도 `200 → 503 → 200` 재검증 |
| Redis 중단 | ValidFailure | 1.170초 | 61.501초 | 163.341초 | 미측정 | AOF 대기 상태·DB 지급 보존, 대기실 HTTP 500을 발견·503 제한 응답으로 수정 후 재검증 |
| RabbitMQ 중단 | Passed | 0초 | 203초 | 251초 | 미측정 | DB Outbox에 사건 보존, 7개 consumer와 ready·unacknowledged 0으로 수렴 |
| R2 endpoint 실패 | Passed | 9초 | 182초 | 486초 | 미측정 | media가 1·5분 durable retry 뒤 세 번째 attempt에서 완료 |
| SES endpoint 실패 | Passed | 30초 | 63초 | 251초 | 미측정 | 인증 Outbox 보존·1분 retry·실제 한 계정 수신, 다른 한 계정은 전달 단계 미확정 |

`ValidFailure`는 실험 실패를 숨긴 표현이 아니다. 최초 실제 장애에서 요구 조건을 어긴 결함을
발견했고, 같은 단계 안에서 코드를 수정한 뒤 별도 실제 재검증까지 마쳤다는 뜻이다.

## 경보와 trace 증거

| 시나리오 | Prometheus query가 비지 않은 수 | 주요 관측 경보 | Tempo trace |
|---|---:|---|---:|
| Web | 17/17 | HTTP p95, RedisDown 오탐 | 32 |
| Worker | 10/17 | Rabbit consumer missing, HTTP p95, RedisDown 오탐 | 33 |
| PostgreSQL | 17/17 | PostgreSQL down, readiness failed, HTTP p95, RedisDown 오탐 | 32 |
| Redis | 17/17 | HTTP error ratio·p95, RedisDown 오탐 | 100 |
| RabbitMQ | 17/17 | notification backlog·readiness, lock-risk 점검 필요, RedisDown 오탐 | 100 |
| R2 | 1/17 | 관측 tunnel 공백 | 0 |
| SES | 1/17 | 관측 tunnel 공백 | 0 |

Web부터 RabbitMQ까지 반복된 `IndieFiRedisDown`은 실제 Redis 장애가 아니라
`redis_exporter` 전용 비밀번호가 Redis CLI에서는 동작하지만 exporter 연결에서는
`WRONGPASS`가 된 관측 계층 결함이었다. 전용 비밀번호를 영숫자 64자리 hex로 회전하고
설정 검증도 같은 형식으로 제한했다. 수정 뒤 exporter와 Prometheus의 `redis_up=1`,
PostgreSQL의 `pg_up=1`을 확인했다. 과거 경보는 삭제하지 않고 오탐으로 분류한다.

R2·SES 시나리오 전에 로컬 PC가 만든 SSH reverse tunnel이 종료돼 EC2 agent가 보낸 신호가
로컬 저장소에 도착하지 않았다. DB·journal·queue·브라우저 기능 증거는 유효하지만 해당 두
구간의 dashboard·alert·trace는 사후 복구할 수 없다. 따라서 일곱 장애 모두에서 경보가
정상 동작했다고 주장하지 않는다.

## 데이터 보존과 재처리 결론

- PostgreSQL migration, 음수 credit, 원장 불일치, 게임 키 암호화 shape, 중복 지급과 리뷰
  terminal shape 감사에서 최종 위반은 0건이었다.
- 인증 email Outbox, messaging Outbox, notification Pending·Failed, media in-flight·Failed와
  RabbitMQ ready·unacknowledged는 각 run의 최종 표본에서 0으로 수렴했다.
- Redis AOF의 대기 집계와 PostgreSQL의 최종 key grant를 분리해 확인했고 전후 값이 같았다.
- R2와 SES는 외부 의존성이 복구된 즉시 성공했다고 보지 않고 각자의 retry due와 terminal
  상태까지 추적했다.
- SES의 첫 수신 계정은 SMTP 성공 뒤 메일이 관측되지 않았다. per-message SES delivery event가
  없어 지연·필터·bounce 중 어느 단계인지 확정하지 않으며, 두 번째 계정의 실제 수신만
  end-to-end 성공 근거로 사용한다.

## 최종 결론과 남은 한계

단일 EC2에서 일곱 종류의 통제 장애, 자동 원복, 확정 데이터 보존과 최종 수렴을 실제로
수행했다. 이 결과는 프로세스·의존성 장애 복구 경험을 증명하지만 호스트 전체 장애,
다중 AZ, 월간 가용성 99.0%, 자동 경보 전달과 사용자 RTO 달성을 증명하지 않는다.

후속 개선 우선순위는 다음과 같다.

1. SSH tunnel 생존 감시와 `NoData` 경보를 추가해 관측 공백 자체를 장애로 감지한다.
2. R2·SES 장애를 tunnel이 유지된 상태에서 다시 실행해 alert·trace 증거를 보완한다.
3. SES event publishing으로 delivery·delay·bounce를 메시지 단위로 연결한다.
4. RabbitMQ run의 PostgreSQL lock-risk 경보가 실제 waiter였는지 확인하고 임계값을 보정한다.
5. 자동 브라우저 probe를 붙여 사용자 RTO를 사람의 확인 지연 없이 측정한다.

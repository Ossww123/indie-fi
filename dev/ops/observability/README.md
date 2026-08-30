# 포트폴리오 운영 관측 구조

## 1. 목적

Web·Worker 요청이 PostgreSQL·Redis·RabbitMQ와 R2·SES를 지나는 동안 로그,
메트릭과 트레이스를 같은 run id·trace id로 연결한다. 관측 저장·조회 부하는
부하 대상 EC2에서 분리하고 관리형 서비스 비용을 만들지 않는다.

## 2. 선택한 토폴로지

```text
EC2 t3.medium                                      로컬 PC

Web :5000 ─┐                                  ┌─ Prometheus (metrics)
Worker ────┼─ OTLP ─> Collector agent ────────┼─ Loki (logs)
host ──────┤             :4317                 ├─ Tempo (traces)
DB/Redis/  │                 │                 └─ Grafana :3000
RabbitMQ ──┘                 │
                    127.0.0.1:14317
                             │
                     SSH reverse tunnel
                             │
                    127.0.0.1:4317
                     Collector gateway
```

- EC2 Collector는 agent 역할로 애플리케이션 OTLP와 호스트·데이터 서비스 지표를
  수집하고 batch·memory limit 뒤 로컬 gateway로 전달한다.
- 로컬 Collector는 gateway 역할로 메트릭을 Prometheus scrape endpoint에,
  로그를 Loki에, 트레이스를 Tempo에 전달한다.
- Grafana는 로컬의 세 저장소만 조회한다.
- EC2 agent는 Linux host network에서 실행해 Web·Worker와 SSH tunnel의 loopback만
  사용한다. 관측용 수신·관리 포트를 보안 그룹이나 Nginx에 공개하지 않는다.

## 3. SSH tunnel 경계

로컬 PC가 EC2에 연결한 SSH 세션 하나가 EC2의 `127.0.0.1:14317`을 로컬
gateway의 `127.0.0.1:4317`로 전달한다. 정확한 명령은 첫 EC2 연결 단계에서
현재 IP와 key path를 받아 생성한다.

- remote bind는 `127.0.0.1`로 제한한다.
- `ExitOnForwardFailure=yes`, keepalive와 명시적 timeout을 사용한다.
- tunnel이 끊기면 agent의 bounded queue 이상 신호는 버릴 수 있다.
- 측정 구간의 핵심 신호가 10% 넘게 비면 해당 run은 무효다.
- 측정이 끝나면 tunnel을 종료하고 원격 포트가 사라졌는지 확인한다.

장기 운영용 고가용 수집 경로가 아니라 짧은 포트폴리오 실험용 구조다. 실제
서비스라면 TLS·인증된 고정 gateway 또는 관리형 관측 저장소를 다시 검토한다.

## 4. 신호 흐름

| 신호 | EC2 수집 | 로컬 저장·조회 |
|---|---|---|
| traces | ASP.NET Core, HttpClient, EF/Npgsql, Redis, MassTransit과 도메인 Activity | Tempo → Grafana Explore |
| metrics | .NET runtime·HTTP, 도메인 Meter, host·container·DB·Redis·RabbitMQ | Collector Prometheus exporter → Prometheus → Grafana |
| logs | Web·Worker 구조화 로그와 trace/span id | Loki → Grafana Explore |

기존 systemd journal은 계속 운영 로그의 원본 역할을 한다. Loki 전송 실패가
애플리케이션 실패를 만들지 않으며 장애 훈련에서는 journal과 Loki를 교차 확인한다.

## 5. 포트

### EC2

| 주소 | 용도 | 공개 여부 |
|---|---|---|
| `127.0.0.1:4317` | 애플리케이션 → Collector agent OTLP/gRPC | 비공개 |
| `127.0.0.1:14317` | agent → SSH reverse tunnel | 세션 중 loopback만 |
| Collector 내부 metrics | agent 자체 health·관측 | Docker host network/loopback만 |

### 로컬 PC

| 주소 | 용도 |
|---|---|
| `127.0.0.1:4317` | Collector gateway OTLP/gRPC |
| `127.0.0.1:3000` | Grafana |
| `127.0.0.1:9090` | Prometheus 점검용 |
| `127.0.0.1:3100` | Loki 점검용 |
| `127.0.0.1:3200` | Tempo 점검용 |

모든 Docker publish는 `127.0.0.1`을 명시한다. LAN과 인터넷에서 접근할 수 있게
`0.0.0.0`으로 바꾸지 않는다.

## 6. 보존과 자원 상한

- Prometheus·Loki·Tempo는 로컬 named volume을 사용한다.
- 초기 보존은 3일이며 저장소별 최대 약 2GiB를 넘기지 않는다.
- run 종료 시 필요한 dashboard JSON, 대표 trace와 metric·log 캡처를
  `dev/ops/validation/runs/<run-id>/`로 export한다.
- 최종 보고서 증거를 반출한 뒤 관측 Compose와 volume을 종료·정리한다.
- EC2 agent와 exporter의 CPU·메모리는 유휴 기준선과 함께 측정하고 본 부하의
  고정 조건으로 유지한다.

## 7. sampling과 카디널리티

- metric은 집계 손실 없이 수집하되 endpoint·result처럼 제한된 tag만 사용한다.
- smoke·장애 훈련은 trace 100%, 본 부하는 초기 10% parent-based ratio를 사용한다.
- 기준 부하와 개선 재측정은 같은 sampling을 사용한다.
- 사용자 id, 이메일, game/drop/media id, 원시 URL, SQL 매개변수와 오류 본문을
  metric label이나 공통 span tag로 사용하지 않는다.
- run id는 짧은 실험 동안만 붙이고 장기 시계열 label로 보존하지 않는다.

## 8. 비용·보안 결정

- Grafana Cloud, AWS Managed Prometheus와 CloudWatch 상세 모니터링을 사용하지 않는다.
- 관측을 위해 Elastic IP, 로드 밸런서, NAT Gateway, 추가 EBS를 만들지 않는다.
- Collector와 dashboard에는 운영 자격 증명을 넣지 않는다.
- 이미지와 NuGet package는 구현 단계에서 정확한 버전과 license를 확인해 고정한다.
- EC2가 중지되면 agent도 사라지고 로컬 관측 Compose만 사용자가 명시적으로 켠다.

## 9. 완료 경계

이 문서는 구조 결정만 소유한다. 다음 단계에서 순서대로 구현한다.

1. Web·Worker 공통 OpenTelemetry 등록과 비밀 필터
2. Web·Worker·도메인 계측
3. EC2 agent와 데이터 서비스 exporter
4. 로컬 Collector·Prometheus·Loki·Tempo·Grafana
5. dashboard·경보와 로컬 구성 검증
6. 첫 EC2 세션의 실제 trace·metric·log 연결 확인

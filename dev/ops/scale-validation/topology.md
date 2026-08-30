# 같은 호스트 다중 Web·Worker 검증 topology

## 목적과 비목적

이 구성은 단일 EC2에서 Web 2개와 Worker 2개를 잠깐 실행해 프로세스 로컬 의존성을
찾는 검증 환경이다. 한 EC2가 내려가면 네 프로세스가 모두 사라지므로 고가용성, 무중단
배포 또는 실제 여러 서버 확장을 증명하지 않는다.

## 프로세스와 포트

```text
Browser
   |
Nginx :443
   | round robin, sticky session 없음
   +--> indiefi-scale-web@1 : 127.0.0.1:5101
   `--> indiefi-scale-web@2 : 127.0.0.1:5102

RabbitMQ durable queues
   +--> indiefi-scale-worker@1
   `--> indiefi-scale-worker@2
```

원래 `indiefi-web.service`의 5000 port와 `indiefi-worker.service`는 검증 topology가 준비된
뒤 중지한다. 추가 인스턴스는 enable하지 않고 최대 45분 뒤 자동 원복한다.

## 공유·분리 상태

| 경계 | 공유 여부 | 이유와 판정 |
|---|---|---|
| immutable release `/opt/indiefi/current` | 공유 | 네 프로세스가 같은 commit의 DLL을 실행해야 코드 버전 차이가 없다. |
| `/etc/indiefi/web.env`, `worker.env` 비밀 | 역할 안에서 공유 | DB·Redis·RabbitMQ·R2·보호 키가 같아야 같은 서비스 상태를 본다. |
| PostgreSQL | 공유 | 계정·원장·리뷰·key grant·Outbox·Inbox의 최종 권위다. |
| Redis | 공유 | 대기실·SignalR backplane·업로드 제한을 모든 Web이 함께 본다. |
| RabbitMQ virtual host와 endpoint prefix | 공유 | Worker 두 개가 같은 durable queue의 competing consumer가 된다. |
| R2 bucket과 자격 증명 | 공유 | media 객체·결정적 key와 DB 상태가 Worker에 따라 달라지지 않아야 한다. |
| Data Protection key ring·인증서 | Web끼리 공유 | Web 1에서 만든 로그인 cookie를 Web 2도 해독해야 한다. Worker는 private key가 필요 없다. |
| Web listen port | 분리 | `5101`, `5102`로 bind 충돌을 막는다. 공인 interface에는 게시하지 않는다. |
| `Observability:InstanceId` | 분리 | `scale-web-1/2`, `scale-worker-1/2`로 요청·소비 주체를 구분한다. |
| PID·journal unit | 분리 | instance별 시작·중지·재시작과 로그를 독립 관측한다. |
| in-memory cache·lock·session | 공유 금지 | 결과 정합성이 프로세스 메모리에 의존하면 scale 검증 실패다. |

## 단계별 완료 기준

1. Web 두 개가 서로 다른 PID·loopback port에서 같은 release로 ready다.
2. Nginx 응답의 임시 `X-IndieFi-Upstream`과 trace instance id가 두 Web을 모두 보인다.
3. 한 Web 제거 뒤 새 요청·기존 로그인·SignalR이 남은 Web에서 회복한다.
4. 공유 요청 제한, 대기 순번과 마지막 key grant가 인스턴스 수로 우회·중복되지 않는다.
5. Worker 두 개가 같은 queue의 consumer 2개로 보이고 중간 재시작 뒤 메시지가 수렴한다.
6. 원래 single Web·Worker, Nginx upstream 5000과 port 상태로 돌아오고 임시 unit·환경·header가 없다.

## 안전 중단

- release·환경 파일·공유 key 지문 불일치
- Web 5101·5102 port가 이미 사용 중이거나 template unit이 예상 밖에 active
- Nginx 원본 backup 또는 45분 자동 원복 timer를 만들 수 없음
- 사용 가능 memory 512 MiB 미만, root disk 85% 이상, DB·Redis·RabbitMQ 비정상
- duplicate grant·ledger mismatch·영구 queue·media 적체 또는 원복 실패

실제 실행 결과에는 Web·Worker별 CPU·memory와 같은 호스트 경합을 기록한다. 성능이 좋아져도
호스트 장애 생존이나 EC2 두 대의 네트워크·배포·session 동작으로 일반화하지 않는다.

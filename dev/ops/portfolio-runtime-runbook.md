# 포트폴리오 배포 실행 구조

## 1. 목적과 결정

이 문서는 단기 운영·부하·복구 실험용 단일 EC2에서 공개 진입점, 애플리케이션 프로세스와 데이터 서비스의 책임을 고정한다. 실제 명령과 파일 설치는 각 배포 단계에서 추가하되 이후 구성은 이 연결과 포트 경계를 바꾸지 않는다.

- 도메인, Elastic IP, Load Balancer는 만들지 않는다.
- 실행 중 자동 할당된 공인 IPv4에 Let’s Encrypt 6일 수명 IP 인증서를 발급한다.
- EC2를 중지해 공인 IP가 해제되면 기존 인증서와 Origin은 폐기된 세션 값으로 취급한다.
- Google 웹 OAuth는 원시 IP redirect URI를 허용하지 않으므로 이 배포에서는 끄고 이메일·비밀번호 로그인만 검증한다.
- IP 인증서는 전송 암호화를 위한 것이며 공인 IP나 EC2의 비용을 줄이거나 고정하지 않는다.

## 2. 요청과 의존성 흐름

```text
Internet
  ├─ TCP 80  ──> Nginx ──> ACME HTTP-01 응답 또는 HTTPS 이동
  ├─ TCP 443 ──> Nginx ──> ASP.NET Core Web 127.0.0.1:5000
  └─ TCP 22  ──> sshd  ──> 현재 관리자 또는 배포 runner /32만 일시 허용

ASP.NET Core Web
  ├─> PostgreSQL 127.0.0.1:5432
  ├─> Redis      127.0.0.1:6379
  ├─> RabbitMQ   127.0.0.1:5672
  └─> Cloudflare R2 HTTPS

.NET Worker
  ├─> PostgreSQL 127.0.0.1:5432
  ├─> Redis      127.0.0.1:6379
  ├─> RabbitMQ   127.0.0.1:5672
  └─> Cloudflare R2 HTTPS
```

Nginx가 TLS를 종료한다는 것은 인터넷 HTTPS 연결의 인증서와 암호화를 Nginx가 처리하고, 같은 서버 안에서는 loopback HTTP로 Web에 전달한다는 뜻이다. Web은 loopback Nginx의 전달 헤더만 신뢰해 원래 요청이 HTTPS였음을 판단한다.

## 3. 프로세스와 포트 경계

| 구성요소 | 수신 주소 | 인터넷 공개 | 책임 |
|---|---|---|---|
| Nginx | `0.0.0.0:80`, `0.0.0.0:443` | 예 | ACME 검증, HTTPS 이동, TLS 종료, Web proxy |
| sshd | `0.0.0.0:22` | 조건부 | 관리자·배포 runner의 짧은 `/32` 접근 |
| IndieFi.Web | `127.0.0.1:5000` | 아니오 | HTTP 화면·API·SignalR·readiness |
| IndieFi.Worker | 수신 포트 없음 | 아니오 | 메시지 소비, 메일, 미디어 처리·복구·정리 |
| PostgreSQL | `127.0.0.1:5432` | 아니오 | 관계형 데이터와 Outbox·Inbox 권위 |
| Redis | `127.0.0.1:6379` | 아니오 | 대기실·SignalR·업로드 제한 공유 상태 |
| RabbitMQ | `127.0.0.1:5672` | 아니오 | durable 메시지 broker |
| RabbitMQ 관리 UI | 호스트에 게시하지 않음 | 아니오 | 필요 시 컨테이너 내부 CLI로 진단 |

AWS 보안 그룹에는 80·443과 필요한 동안의 제한된 22만 둔다. Docker의 데이터 서비스 포트는 loopback에만 게시하고 보안 그룹에는 추가하지 않는다. Worker와 Kestrel 5000도 인터넷에 직접 열지 않는다.

## 4. 데이터 서비스 실행 경계

운영 데이터 서비스는 `ops/portfolio-data-compose.yml` 하나로 PostgreSQL 18, Redis와 RabbitMQ만 실행한다. Mailpit은 로컬 SMTP 확인 도구이고 MinIO는 R2의 로컬 대체재이므로 이 구성에 포함하지 않는다. RabbitMQ 이미지는 진단 도구를 포함하지만 관리 UI 포트 15672는 호스트에 게시하지 않는다.

세 서비스 포트는 `127.0.0.1`에만 게시한다. 따라서 같은 EC2의 Web·Worker는 접근할 수 있지만 인터넷이나 다른 사설망 호스트에서는 접근할 수 없다. 서비스별 named volume에는 다음 데이터가 남으며, 단순 컨테이너 재생성이나 EC2 재부팅으로 삭제되지 않는다.

| named volume | 보존 데이터 |
|---|---|
| `indiefi-portfolio-postgres-data` | PostgreSQL 데이터베이스 |
| `indiefi-portfolio-redis-data` | Redis AOF 대기실·공유 상태 |
| `indiefi-portfolio-rabbitmq-data` | durable queue와 broker 상태 |

실제 비밀번호 파일은 저장소에 넣지 않는다. `ops/portfolio-infra.env.example`의 세 값을 각각 다른 난수로 채워 `/etc/indiefi/infra.env`에 설치한다. `docker compose config --quiet`는 파일 문법과 필수 비밀번호 존재 여부만 검사하고 컨테이너를 만들지 않는다.

```bash
docker compose \
  --env-file /etc/indiefi/infra.env \
  -f ops/portfolio-data-compose.yml \
  config --quiet

docker compose \
  --env-file /etc/indiefi/infra.env \
  -f ops/portfolio-data-compose.yml \
  up -d --wait

docker compose \
  --env-file /etc/indiefi/infra.env \
  -f ops/portfolio-data-compose.yml \
  ps
```

일반 중지는 `docker compose ... down`까지만 사용한다. `down -v`는 세 named volume을 함께 삭제하는 초기화 명령이므로, 보존하지 않기로 확정한 예전 DB를 정리하는 13-0-4-8 외에는 사용하지 않는다.

## 5. Web·Worker 환경과 비밀 파일

.NET 설정의 `:` 계층은 운영체제 환경 변수에서 `__`로 표현한다. 예를 들어 `Media:ObjectStorage:Enabled`는 `Media__ObjectStorage__Enabled=true`가 된다. 이 규칙 덕분에 저장소의 `appsettings.json`을 수정하지 않고 같은 게시물을 Web과 Worker의 서로 다른 역할로 실행할 수 있다.

| 설치 파일 | 권한 기준 | 포함하는 값 | 포함하지 않는 값 |
|---|---|---|---|
| `/etc/indiefi/infra.env` | `root:root`, `0600` | PostgreSQL owner, Redis, RabbitMQ 컨테이너 비밀번호 | 애플리케이션 DB 역할·R2·SMTP 비밀 |
| `/etc/indiefi/web.env` | `root:indiefi-web`, `0640` | Web DB·Redis·RabbitMQ·R2, 계정 보호 키 경로, Data Protection 경로 | 데이터베이스 owner 비밀번호·SMTP 비밀·Worker 활성화 |
| `/etc/indiefi/worker.env` | `root:indiefi-worker`, `0640` | Worker DB·Redis·RabbitMQ·R2, 계정 보호 키 경로, 인증·알림 SMTP 비밀 | 데이터베이스 owner 비밀번호·Data Protection private key·공개 수신 주소 |

실제 파일은 각각 `ops/portfolio-infra.env.example`, `ops/portfolio-web.env.example`, `ops/portfolio-worker.env.example`을 기준으로 만들되 저장소에 커밋하지 않는다. Web과 Worker는 같은 최소 권한 DB 연결, Redis 연결, RabbitMQ 값, R2 자격 증명과 `/etc/indiefi/keys/account-protection.json`을 사용한다. 검증 스크립트가 이 값들이 서로 다르게 입력되는 드리프트를 시작 전에 거부한다.

Web은 이메일 Outbox를 기록하지만 SMTP 비밀과 발송 Worker를 갖지 않는다. `IndieFi.Worker`만 인증 이메일·일반 알림 이메일과 미디어 후처리를 활성화한다. Web만 브라우저 세션용 Data Protection 인증서·private key를 읽고 Worker에는 주입하지 않는다.

```bash
sudo dev/ops/verify-portfolio-environment.sh \
  /etc/indiefi/infra.env \
  /etc/indiefi/web.env \
  /etc/indiefi/worker.env
```

검증은 비밀값을 출력하지 않고 다음 오류를 시작 전에 차단한다.

- 빈 값, 중복 키, 공백이 섞인 잘못된 환경 파일과 느슨한 파일 권한
- Web에서 Google·미디어 Worker를 켜거나 Worker에서 이메일·미디어 역할을 끈 구성
- Web·Worker의 DB·Redis·RabbitMQ·R2·계정 보호 키 불일치
- PostgreSQL owner 비밀번호를 애플리케이션 역할에 재사용한 구성
- loopback이 아닌 데이터 서비스 주소, HTTP R2 주소와 잘못된 공개 HTTPS Origin
- Worker에 Data Protection private key를 주거나 Web에 SMTP 비밀을 넣은 역할 교차
- 누락·상대 경로·과도한 권한의 암호화 키 파일

비밀번호나 키를 `systemctl status`, 명령행 인자 또는 작업 기록에 복사하지 않는다. 최종 systemd `EnvironmentFile` 연결과 서비스 사용자 생성은 13-0-4-5에서 수행한다.

## 6. 공인 IP와 인증서 생명주기

Let’s Encrypt IP 인증서는 공개 CA가 현재 공인 IP의 제어권을 HTTP-01로 확인한 뒤 발급하는 6일 수명 TLS 인증서다. Certbot 5.4 이상이 IP 주소와 webroot 발급을 지원하며, Nginx는 발급된 certificate chain과 private key를 읽는다.

한 배포 세션의 순서는 다음과 같다.

1. 비용·리전·인스턴스 종류·Standard CPU 크레딧을 확인하고 EC2를 시작한다.
2. 새 공인 IPv4를 기록하고 배포 대상 주소를 갱신한다.
3. 80의 ACME 경로가 외부에서 도달하는 상태에서 staging 인증서로 절차를 확인한다.
4. 같은 IP의 production 인증서를 발급하고 Nginx 443에 연결한다.
5. `https://<현재-공인-IP>`의 인증서 검증과 Web 응답을 확인한다.
6. 같은 HTTPS Origin을 R2 CORS에 추가한다.

EC2 중지 뒤 자동 할당 공인 IP는 해제된다. 다음 시작에서 IP가 달라지면 이전 인증서는 새 IP를 증명하지 못하므로 새로 발급하고 Nginx, R2 CORS와 GitHub 배포 대상 주소를 함께 갱신한다. 6일 안에 같은 IP로 계속 실행하는 동안에만 자동 갱신이 의미가 있다.

인증서 private key와 R2·DB·SMTP 비밀은 저장소, GitHub Actions 로그, worklog와 일반 애플리케이션 로그에 남기지 않는다.

## 7. 인증 기능 범위

제품은 Google 로그인 기능을 유지한다. 다만 Google 웹 OAuth는 localhost 외 원시 IP를 redirect URI host로 허용하지 않으므로 이 환경에서는 Google 공급자를 명시적으로 비활성화한다. 로그인 화면은 Google 진입점을 숨기고 이메일·비밀번호 가입·확인·로그인·재설정을 HTTPS와 운영 SMTP에서 검증한다.

Google 로그인의 실제 배포 검증은 소유하고 검증할 수 있는 도메인을 연결한 별도 환경의 완료 조건이다. 공인 IP 인증서만으로 Google의 redirect URI 도메인 요구를 충족했다고 간주하지 않는다.

## 8. 이 단계의 완료 조건

- 공개 80·443, 조건부 22와 내부 전용 포트가 문서에서 한 가지 연결로 수렴한다.
- Nginx TLS 종료와 Web의 forwarded headers 책임이 분리된다.
- 공인 IP 변경 시 인증서, R2 CORS와 배포 주소를 함께 갱신하는 순서가 명시된다.
- 원시 IP 환경의 Google 로그인 제외가 제품 기능 삭제와 구분된다.
- PostgreSQL·Redis·RabbitMQ만 별도 운영 Compose에 있고 모든 게시 포트가 loopback으로 제한된다.
- 세 서비스의 비밀번호 누락은 구성 검사에서 실패하고 health check와 named volume이 선언된다.
- 인프라·Web·Worker 비밀 파일이 분리되고 공유 값·역할 활성화·키 파일 권한이 시작 전에 검증된다.
- Web에는 SMTP 비밀이나 발송 Worker가 없고 Worker만 인증·알림 메일과 미디어 후처리를 실행한다.
- EC2를 시작하거나 유료 리소스를 추가하지 않는다.

## 9. 공식 확인 자료

- Let’s Encrypt IP 인증서와 Certbot 5.4 이상 webroot 발급: <https://letsencrypt.org/2026/03/11/shorter-certs-certbot/>
- Google OAuth redirect URI의 원시 IP 제한: <https://support.google.com/cloud/answer/15549257>

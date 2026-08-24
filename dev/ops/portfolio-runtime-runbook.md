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

### 4.1. migration owner와 애플리케이션 역할 분리

EF migration은 테이블·인덱스·DB 권한을 바꾸므로 PostgreSQL owner인 `indiefi`로 실행한다. 반대로 Web과 Worker는 migration이 만든 최소 권한 역할 `indiefi_app`으로만 접속한다. 앱이 침해되더라도 임의 테이블 생성이나 스키마 변경까지 이어지지 않게 하는 경계다.

빈 DB의 첫 준비 순서는 다음과 같다.

1. `ops/migrate-portfolio-database.sh`가 `/etc/indiefi/infra.env`의 owner 비밀번호를 root 프로세스 안에서만 읽어 현재 `efbundle`을 실행한다.
2. migration이 비밀번호 없는 `indiefi_app` 역할과 필요한 테이블·시퀀스·함수 권한을 만든다.
3. `ops/portfolio-database-bootstrap.env.example`을 기준으로 일회성 `/etc/indiefi/database-bootstrap.env`를 `root:root`, `0600`으로 만든다. 값은 `openssl rand -base64 32`로 새로 생성하며 owner 비밀번호와 달라야 한다.
4. `ops/configure-portfolio-database-role.sh`가 이 비밀번호를 역할에 설정하고 로그인, migration history 읽기, DDL 거부를 직접 검사한다.
5. 같은 애플리케이션 비밀번호를 Web·Worker의 `ConnectionStrings__Database`에 넣는다. 첫 동시 배포가 성공한 뒤에만 일회성 파일을 삭제한다.

```bash
sudo install -o root -g root -m 0600 /dev/null \
  /etc/indiefi/database-bootstrap.env
sudoedit /etc/indiefi/database-bootstrap.env

sudo <release>/ops/migrate-portfolio-database.sh \
  <release>/efbundle
sudo <release>/ops/configure-portfolio-database-role.sh
```

두 스크립트는 비밀번호를 명령행 인자나 성공 로그에 출력하지 않는다. `database-bootstrap.env`는 장기 운영 설정이 아니라 최초 역할 설정과 첫 배포 사이를 연결하는 임시 파일이다.

## 5. Web·Worker 환경과 비밀 파일

.NET 설정의 `:` 계층은 운영체제 환경 변수에서 `__`로 표현한다. 예를 들어 `Media:ObjectStorage:Enabled`는 `Media__ObjectStorage__Enabled=true`가 된다. 이 규칙 덕분에 저장소의 `appsettings.json`을 수정하지 않고 같은 게시물을 Web과 Worker의 서로 다른 역할로 실행할 수 있다.

| 설치 파일 | 권한 기준 | 포함하는 값 | 포함하지 않는 값 |
|---|---|---|---|
| `/etc/indiefi/infra.env` | `root:root`, `0600` | PostgreSQL owner, Redis, RabbitMQ 컨테이너 비밀번호 | 애플리케이션 DB 역할·R2·SMTP 비밀 |
| `/etc/indiefi/database-bootstrap.env` | `root:root`, `0600` | 최초 `indiefi_app` 비밀번호 한 개 | owner·Redis·RabbitMQ·R2·SMTP 비밀 |
| `/etc/indiefi/web.env` | `root:indiefi-web`, `0640` | Web DB·Redis·RabbitMQ·R2, 계정 보호 키 경로, Data Protection 경로 | 데이터베이스 owner 비밀번호·SMTP 비밀·Worker 활성화 |
| `/etc/indiefi/worker.env` | `root:indiefi-worker`, `0640` | Worker DB·Redis·RabbitMQ·R2, 계정 보호 키 경로, 인증·알림 SMTP 비밀 | 데이터베이스 owner 비밀번호·Data Protection private key·공개 수신 주소 |

실제 파일은 각각 `ops/portfolio-infra.env.example`, `ops/portfolio-database-bootstrap.env.example`, `ops/portfolio-web.env.example`, `ops/portfolio-worker.env.example`을 기준으로 만들되 저장소에 커밋하지 않는다. Web과 Worker는 같은 최소 권한 DB 연결, Redis 연결, RabbitMQ 값, R2 자격 증명과 `/etc/indiefi/keys/account-protection.json`을 사용한다. 검증 스크립트가 이 값들이 서로 다르게 입력되는 드리프트를 시작 전에 거부한다.

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

비밀번호나 키를 `systemctl status`, 명령행 인자 또는 작업 기록에 복사하지 않는다. `ops/install-portfolio-service-files.sh`가 서비스 사용자와 보호된 빈 환경 파일, systemd `EnvironmentFile` 연결을 설치한다. 실제 값은 이후 `sudoedit`으로 입력한다.

### 5.1. systemd 서비스 설치 경계

다음 명령은 서비스 계정과 디렉터리, 검증 도구와 unit 파일만 설치한다. 서비스를 활성화하거나 시작하지 않는다.

```bash
sudo dev/ops/install-portfolio-service-files.sh

systemctl cat indiefi-web
systemctl cat indiefi-worker
```

서비스 계정은 로그인이 불가능한 `indiefi-web`, `indiefi-worker`로 분리한다. 두 계정은 `/etc/indiefi/keys/account-protection.json`만 읽기 위한 `indiefi-secrets` 보조 그룹을 공유한다. Web 전용 Data Protection private key는 `indiefi-web:indiefi-web`, `0600`으로 설치해 Worker가 읽지 못하게 한다.

두 unit은 다음 고정 경계를 사용한다.

| 서비스 | 현재 배포 경로 | 환경 파일 | 공개 포트 |
|---|---|---|---|
| `indiefi-web.service` | `/opt/indiefi/current/web/IndieFi.Web.dll` | `/etc/indiefi/web.env` | 없음, `127.0.0.1:5000`만 수신 |
| `indiefi-worker.service` | `/opt/indiefi/current/worker/IndieFi.Worker.dll` | `/etc/indiefi/worker.env` | 없음 |

`current`는 같은 commit으로 게시한 Web·Worker를 담은 `/opt/indiefi/releases/<commit>`을 가리키는 symlink다. 각 서비스는 시작 전에 세 환경 파일의 역할·공유 값과 현재 DLL을 검사하고, 실패하면 잘못된 설정으로 프로세스를 열지 않는다. `Restart=on-failure`는 실행 중 비정상 종료만 재시도하고 로그는 journal에 남긴다.

```bash
journalctl -u indiefi-web -n 100 --no-pager
journalctl -u indiefi-worker -n 100 --no-pager
```

`systemctl enable --now`은 데이터 서비스, migration, 실제 환경 파일과 배포 산출물이 모두 준비되는 13-0-4-9에서만 실행한다.

### 5.2. Web·Worker 동시 배포

배포는 `.github/workflows/ci.yml`의 수동 `workflow_dispatch`로만 시작한다. 같은 배포 그룹에는 실행 하나만 들어갈 수 있어 두 runner가 동시에 migration이나 `current` 전환을 수행하지 않는다. EC2가 중지된 평상시 push·pull request는 테스트만 실행하고 배포 실패로 기록하지 않는다.

한 번의 수동 배포는 다음 순서를 따른다.

1. 전체 빌드와 테스트를 먼저 통과한다.
2. 같은 `GITHUB_SHA`에서 Web과 Worker를 framework-dependent `linux-x64` 폴더로 각각 게시한다.
3. 같은 migration의 `efbundle`, 해당 commit의 `ops` 파일과 파일별 SHA-256 manifest를 하나의 archive로 묶는다.
4. GitHub OIDC로 받은 임시 AWS 권한으로 현재 runner의 `/32`에만 SSH를 열고 archive를 `ubuntu` 계정 영역에 전송한다.
5. 서버에서 archive와 개별 파일 hash, .NET 10 runtime, 환경 파일과 systemd unit을 확인한 뒤 immutable release 디렉터리에 설치한다.
6. Worker를 먼저 멈춰 백그라운드 DB 쓰기를 차단하고 Web을 멈춘 뒤, `/etc/indiefi/infra.env`의 PostgreSQL owner로 migration을 적용한다.
7. `/opt/indiefi/current` symlink를 새 release로 원자적으로 바꾸고 Web을 시작한다.
8. loopback에서 실제 `AllowedHosts`와 HTTPS forwarded header로 `/health/ready`를 통과한 뒤 Worker를 시작해 10초 동안 active 상태를 확인한다.
9. 성공한 upload archive를 지우고, 성공·실패와 관계없이 runner의 임시 SSH 보안 그룹 규칙을 제거한다.

release 구조는 다음과 같다.

```text
/opt/indiefi/releases/<40자-commit>/
  ├─ web/             # IndieFi.Web 게시물
  ├─ worker/          # IndieFi.Worker 게시물
  ├─ ops/             # 같은 commit의 운영 템플릿·검증기
  ├─ efbundle         # linux-x64 migration bundle
  ├─ release.sha      # 기대하는 전체 Git commit
  └─ SHA256SUMS       # 위 파일들의 무결성 manifest

/opt/indiefi/current -> /opt/indiefi/releases/<현재-commit>
```

workflow는 `AWS_ROLE_ARN`, `AWS_SG_ID`, `SSH_PRIVATE_KEY`, 현재 공인 IP를 가진 `SSH_HOST` GitHub secret을 사용한다. 실제 앱 비밀은 GitHub로 전달하지 않고 `/etc/indiefi/*.env`에서 root 권한의 원격 배포 스크립트만 읽는다. migration bundle에는 `infra.env`에서 읽은 owner 연결만 주입하고, Web·Worker는 계속 별도 `indiefi_app` 연결을 사용한다. 두 비밀번호는 명령행 인자나 workflow 로그로 출력하지 않는다.

첫 분리 배포 전에 예전 단일 `indiefi.service`를 중지·비활성화해야 한다. workflow는 이를 자동으로 바꾸지 않고 active 상태면 중단한다. 또한 EC2 시작, 데이터 서비스 초기화, 실제 비밀과 인증서 생성은 CD가 수행하지 않으며 각각 13-0-4-7~9의 수동 준비가 선행되어야 한다.

배포 실패를 자동 rollback하지 않는다. migration 전 실패면 `current`는 그대로고, migration 또는 서비스 시작 실패면 journal과 이전 release 경로를 남긴다. 스키마가 이미 바뀌었을 수 있으므로 다음 명령으로 상태를 확인한 뒤 호환성을 판단하지 않고 이전 symlink로 되돌려서는 안 된다.

```bash
readlink -f /opt/indiefi/current
systemctl status indiefi-web indiefi-worker
journalctl -u indiefi-web -n 100 --no-pager
journalctl -u indiefi-worker -n 100 --no-pager
```

## 6. 공인 IP와 인증서 생명주기

Let’s Encrypt IP 인증서는 공개 CA가 현재 공인 IP의 제어권을 HTTP-01로 확인한 뒤 발급하는 6일 수명 TLS 인증서다. Certbot 5.4 이상이 IP 주소와 webroot 발급을 지원하며, Nginx는 발급된 certificate chain과 private key를 읽는다.

한 배포 세션의 순서는 다음과 같다.

1. 비용·리전·인스턴스 종류·Standard CPU 크레딧을 확인하고 EC2를 시작한다.
2. 새 공인 IPv4를 기록하고 배포 대상 주소를 갱신한다.
3. 80의 ACME 경로가 외부에서 도달하는 상태에서 staging CA를 쓰는 dry run으로 절차를 확인한다.
4. 같은 IP의 production 인증서를 발급하고 Nginx 443에 연결한다.
5. `https://<현재-공인-IP>`의 인증서 검증과 Web 응답을 확인한다.
6. 같은 HTTPS Origin을 R2 CORS에 추가한다.

EC2 중지 뒤 자동 할당 공인 IP는 해제된다. 다음 시작에서 IP가 달라지면 이전 인증서는 새 IP를 증명하지 못하므로 새로 발급하고 Nginx, R2 CORS와 GitHub 배포 대상 주소를 함께 갱신한다. 6일 안에 같은 IP로 계속 실행하는 동안에만 자동 갱신이 의미가 있다.

인증서 private key와 R2·DB·SMTP 비밀은 저장소, GitHub Actions 로그, worklog와 일반 애플리케이션 로그에 남기지 않는다.

### 6.1. HTTP 부트스트랩과 HTTPS 전환

인증서가 생기기 전에 HTTPS 설정부터 설치하면 Nginx가 존재하지 않는 인증서 파일 때문에 시작하지 못한다. 먼저 HTTP 부트스트랩 템플릿을 설치하고, 발급 뒤 HTTPS 템플릿으로 같은 파일을 교체한다.

```bash
INDIEFI_PUBLIC_IP=203.0.113.10
INDIEFI_OPS_DIR=/path/to/repository/dev/ops

"$INDIEFI_OPS_DIR/render-portfolio-nginx.sh" \
  http "$INDIEFI_PUBLIC_IP" \
  | sudo tee /etc/nginx/sites-available/indiefi >/dev/null

sudo ln -sfn /etc/nginx/sites-available/indiefi /etc/nginx/sites-enabled/indiefi
sudo nginx -t
sudo systemctl reload nginx
```

예시 IP `203.0.113.10`은 실제 EC2 공인 IPv4로 바꾼다. 기존 Nginx default site나 예전 IndieFi site가 같은 80·443의 `default_server`를 점유하면 먼저 목록을 확인하고 해당 symlink만 비활성화한다. ACME 확인 파일은 `/var/www/letsencrypt/.well-known/acme-challenge/` 아래에 두며, 반드시 EC2 바깥의 브라우저나 터미널에서 HTTP 200을 확인한다.

Certbot은 5.4 이상이어야 한다. `--dry-run`은 staging CA로 HTTP-01 경로를 시험하되 인증서를 저장하지 않으므로 production 인증서 이름과 충돌하지 않는다.

```bash
certbot --version

sudo certbot certonly --dry-run \
  --preferred-profile shortlived \
  --webroot \
  --webroot-path /var/www/letsencrypt \
  --ip-address "$INDIEFI_PUBLIC_IP" \
  --cert-name "$INDIEFI_PUBLIC_IP"

sudo certbot certonly \
  --preferred-profile shortlived \
  --webroot \
  --webroot-path /var/www/letsencrypt \
  --ip-address "$INDIEFI_PUBLIC_IP" \
  --cert-name "$INDIEFI_PUBLIC_IP" \
  --deploy-hook /usr/local/lib/indiefi/reload-nginx-after-certificate.sh
```

production 인증서가 `/etc/letsencrypt/live/<공인-IP>/`에 생긴 뒤 HTTPS 설정을 렌더링한다. 80의 ACME 경로는 그대로 유지하고 그 밖의 요청만 HTTPS로 308 이동한다. 443은 TLS를 종료한 뒤 일반 HTTP와 SignalR WebSocket 요청을 `127.0.0.1:5000`으로 전달하며, `/health/ready`는 같은 서버의 loopback 요청에만 공개한다.

```bash
"$INDIEFI_OPS_DIR/render-portfolio-nginx.sh" \
  https "$INDIEFI_PUBLIC_IP" \
  | sudo tee /etc/nginx/sites-available/indiefi >/dev/null

sudo nginx -t
sudo systemctl reload nginx
sudo certbot certificates
sudo certbot renew --dry-run --run-deploy-hooks
```

`nginx -t`가 실패하면 reload하지 않는다. 갱신 성공 때만 deploy hook이 먼저 Nginx 문법을 검사하고 설정을 reload하므로, 새 인증서가 디스크에 생겼는데 실행 중 Nginx는 예전 인증서를 계속 제공하는 상태를 피한다.

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
- migration은 PostgreSQL owner로만 실행하고 Web·Worker는 DDL이 거부되는 `indiefi_app` 역할만 사용한다.
- 인프라·Web·Worker 비밀 파일이 분리되고 공유 값·역할 활성화·키 파일 권한이 시작 전에 검증된다.
- Web에는 SMTP 비밀이나 발송 Worker가 없고 Worker만 인증·알림 메일과 미디어 후처리를 실행한다.
- 인증서 전 HTTP 부트스트랩과 발급 후 HTTPS 설정이 분리되고 ACME 경로는 redirect에서 제외된다.
- Nginx가 forwarded headers와 SignalR WebSocket을 loopback Web에 전달하고 readiness의 외부 접근을 막는다.
- Web·Worker unit이 서로 다른 서비스 계정·환경 파일을 사용하고 실패 재시작·journal 로그 경계를 가진다.
- 수동 CD가 같은 commit의 Web·Worker·migration·ops를 hash 검증된 immutable release 하나로 전달한다.
- 동시 배포를 막고 환경 검증, Worker→Web 중지, migration, 원자적 symlink 전환, Web readiness→Worker active 순서를 지킨다.
- 성공·실패 뒤 runner 전용 SSH 규칙을 제거하고 배포 실패를 자동 rollback으로 숨기지 않는다.
- EC2를 시작하거나 유료 리소스를 추가하지 않는다.

## 9. 공식 확인 자료

- Let’s Encrypt IP 인증서와 Certbot 5.4 이상 webroot 발급: <https://letsencrypt.org/2026/03/11/shorter-certs-certbot/>
- Certbot 5.7 명령행 옵션과 webroot·deploy hook 계약: <https://eff-certbot.readthedocs.io/en/stable/man/certbot.html>
- Nginx WebSocket reverse proxy의 Upgrade·Connection 전달: <https://nginx.org/en/docs/http/websocket.html>
- GitHub Actions가 제공하는 `GITHUB_SHA` 등 기본 변수: <https://docs.github.com/en/actions/reference/workflows-and-actions/variables>
- .NET의 RID 지정 framework-dependent 게시 방식: <https://learn.microsoft.com/en-us/dotnet/core/deploying/>
- Google OAuth redirect URI의 원시 IP 제한: <https://support.google.com/cloud/answer/15549257>

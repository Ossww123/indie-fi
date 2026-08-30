# IndieFi portfolio recovery plan

이 문서는 포트폴리오 배포의 영구 상태를 무엇과 함께 백업하고, 어떤 사용자
흐름까지 확인해야 복원이 끝났다고 판단하는지 정의한다. 파일이 열리거나
container가 실행되는 것만으로는 복구 성공으로 보지 않는다.

## 1. 복구 목표

| 항목 | 초기 목표 | 측정 방법 |
|---|---:|---|
| RPO (복구 시점 목표) | 최대 24시간 데이터 손실 | 마지막 검증 백업의 UTC 시각과 장애 가정 시각 차이 |
| RTO (복구 시간 목표) | 2시간 이내 | 빈 복원 환경 준비 시작부터 사용자 흐름 검증 완료까지 |

RPO는 "어느 시점까지의 데이터를 되찾는가", RTO는 "서비스를 다시 사용할
때까지 얼마나 걸리는가"다. 두 값은 13-3 실제 훈련 결과로 확정하며 지금은
검증 전 가설이다.

## 2. 복구 자산과 결합 관계

| 자산 | 운영 위치 | 필요한 이유 | 함께 복원할 자산 | 백업 증거 |
|---|---|---|---|---|
| PostgreSQL custom-format dump | `indiefi-db`의 `indiefi` DB | 사용자, 게임, 배포, 키 수령, ledger, review, notification, outbox, 미디어 상태와 Data Protection key ring의 권위 데이터 | 계정 보호 키, Data Protection 인증서·개인키, R2 객체 | PostgreSQL 버전, dump TOC, schema·핵심 table별 행 수, SHA-256 |
| R2 객체 | 비공개 media bucket의 `sources/`, `derived/`, 필요 시 유효한 `staging/` | `media_assets`가 참조하는 원본과 파생 이미지 | 같은 정합 시점의 PostgreSQL dump | bucket·endpoint 식별 지문, key·size·ETag 목록, 객체 수·총 크기, 파일 SHA-256 |
| 계정 보호 키 | `/etc/indiefi/keys/account-protection.json` | 이메일, 인증 메일 payload, 요청 IP와 게임 키의 암호화·검색 지문을 읽는다 | 해당 key id를 참조하는 PostgreSQL 행 | 목적별 current/read/write key id만 manifest에 기록, 파일 자체는 암호화 묶음에만 포함 |
| Data Protection 인증서 | `/etc/indiefi/keys/data-protection.crt` | DB의 ASP.NET Core Data Protection key ring을 해독할 공개 인증서다 | private key, PostgreSQL의 `data_protection_keys` | 인증서 SHA-256 fingerprint·유효기간 |
| Data Protection 개인키 | `/etc/indiefi/keys/data-protection.key` | 로그인 cookie·일회용 token 등에 쓰는 DB key ring을 실제로 해독한다 | 인증서, PostgreSQL의 `data_protection_keys` | 공개키 일치 여부만 평문 manifest에 기록, 파일 자체는 암호화 묶음에만 포함 |
| 복원 설정 지문 | release commit과 비밀 제외 runtime 설정 | 복원한 프로세스가 같은 schema·bucket·보호 키 경로를 사용했음을 증명한다 | 위 모든 자산 | commit, image/version, 환경 변수 이름과 민감값을 제외한 SHA-256 |

`account-protection.json`의 key 원문, Data Protection private key, DB dump,
R2 객체와 실제 환경 파일은 manifest에 평문으로 넣지 않는다. manifest에는
복원 대상을 구별할 수 있는 식별자·개수·hash만 남긴다.

### 계정 보호 키의 여섯 목적

- `account-email-encryption`: 사용자 이메일 복호화
- `account-email-lookup`: 이메일 동일성 검색용 지문
- `account-email-message-encryption`: 인증·복구 메일 outbox payload 복호화
- `account-request-ip-lookup`: 요청 IP 제한용 지문
- `game-key-encryption`: 배포할 게임 키 복호화
- `game-key-fingerprint`: 중복 게임 키 탐지

어느 한 목적의 이전 `ReadKeyIds`를 누락해도 예전 행은 복호화되지 않을 수
있다. 따라서 현재 key 한 개가 아니라 파일의 전체 key set을 한 자산으로
취급한다.

## 3. 영구 백업에서 제외하는 상태

| 제외 대상 | 이유 | 복구 방법 |
|---|---|---|
| Redis 대기실·cache | PostgreSQL이 권위 데이터이며 대기 순번은 일시 상태다 | 빈 Redis로 시작하고 신규 요청으로 재구성 |
| RabbitMQ queue 자체 | 처리 의도는 PostgreSQL outbox/inbox와 도메인 상태에 보존된다 | 빈 broker를 시작하고 outbox 재발행·consumer 수렴 확인 |
| 서비스 binary와 release directory | Git commit에서 재현 가능한 배포 산출물이다 | 기록한 commit을 다시 publish·deploy |
| Nginx·systemd template | 저장소에 버전 관리된다 | 기록한 commit에서 다시 설치 |
| TLS 인증서 | 짧은 수명의 공인 IP 인증서이며 새 IP에는 재사용할 수 없다 | 새 공인 IP에서 Certbot으로 재발급 |
| `/etc/indiefi/*.env` 원문 | 여러 외부 자격 증명이 섞여 있고 데이터 백업과 같은 묶음에 두면 침해 범위가 커진다 | 별도 비밀 보관소의 값으로 재작성하고 설정 지문만 대조 |

RabbitMQ를 비운 뒤 outbox가 재발행되는지는 반드시 확인한다. outbox에도
없는 broker 전용 메시지가 발견되면 이 제외 판단은 실패이며 백업 범위를
다시 설계한다.

## 4. 정합 백업 경계

1. UTC 시각, release commit과 서비스 상태를 기록한다.
2. 새 쓰기 유입을 막고 Web·Worker의 진행 중 요청과 queue가 수렴했는지 확인한다.
3. PostgreSQL custom-format dump를 EC2 디스크에 쓰지 않고 로컬 PC로 스트리밍한다.
4. 같은 경계에서 R2 inventory와 필요한 객체를 수집한다.
5. `/etc/indiefi/keys` 세 파일과 비밀 제외 설정 지문을 수집한다.
6. 로컬에서 manifest·hash·dump TOC·객체 수를 확인한 뒤에만 EC2를 중지한다.

R2는 PostgreSQL과 하나의 트랜잭션으로 snapshot할 수 없다. 따라서 쓰기를
통제한 짧은 구간에 DB와 객체를 함께 수집하고 `media_assets`의 활성 객체
참조가 inventory에 모두 존재하는지 대조한다.

## 5. 복원 완료 조건

### 자동 검증

- 빈 PostgreSQL에 dump가 오류 없이 복원되고 migration history가 기록한 release와 맞는다.
- 핵심 table의 행 수가 manifest와 같고 email·game key 암호화 열에 누락이 없다.
- R2/MinIO에 manifest의 객체가 모두 있으며 key·size·SHA-256이 맞는다.
- `media_assets`의 활성 source·derived 참조가 복원 객체에 모두 존재한다.
- 세 보호 키 파일의 hash·권한·certificate/private key 공개키 쌍이 맞는다.
- Web readiness가 HTTP 200이고 Worker와 message consumer가 시작된다.

### 사용자 흐름 검증

다음 항목을 모두 통과해야 복원 성공이다.

1. 기존 이메일·비밀번호 사용자가 로그인한다.
2. 기존 사용자의 암호화된 이메일을 화면 또는 안전한 검증 도구로 읽는다.
3. 기존에 수령한 게임 키를 정상 복호화해 조회한다.
4. 게임 cover·screenshot과 사용자 profile image를 조회한다.
5. 새 알림 또는 outbox 작업 한 건이 broker를 거쳐 Worker에서 처리되고 중복되지 않는다.

검증 증거에는 실제 이메일, 게임 키 원문, cookie, token, private key와 서명
URL을 남기지 않는다. 성공 여부와 비식별 record id, 개수, hash만 기록한다.

## 6. 실패 판정

다음 중 하나라도 발생하면 백업 파일이 존재해도 복원 실패다.

- dump TOC 또는 hash를 검증할 수 없다.
- DB 행이 참조하는 R2 객체가 없거나 다른 내용이다.
- 암호화된 이메일·게임 키 또는 Data Protection key ring을 해독하지 못한다.
- 사용자 흐름 중 하나가 현재 운영 release와 다르게 동작한다.
- 사용한 비밀이나 평문 백업을 정리했는지 증명할 수 없다.

실패한 복원에서는 운영 EC2의 PostgreSQL·Redis·RabbitMQ 장애 실험과 본 부하
테스트로 넘어가지 않는다. 원인을 로컬 도구에서 고치고 새 정합 백업으로
처음부터 다시 검증한다.

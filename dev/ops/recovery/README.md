# Portfolio recovery tools

`capture-portfolio-recovery.ps1`는 EC2의 PostgreSQL·보호 키와 Cloudflare R2
객체를 로컬 임시 directory로 모아 하나의 `age` 암호문으로 만든다. dump와
key archive는 SSH stdout으로 전송하므로 EC2 filesystem에 평문 backup 파일을
만들지 않는다.

## 필요한 로컬 도구

- Windows OpenSSH client
- Docker Desktop (`postgres:18`의 `pg_restore --list` 사용)
- AWS CLI v2 (Cloudflare R2 S3-compatible API 사용)
- age 1.3.2 이상
- bsdtar (Windows 기본 `tar.exe` 가능)

도구의 공식 배포본과 checksum을 확인한다. key와 credentials는 저장소 밖에
두며 실제 값은 명령 인자에 쓰지 않는다.

## 먼저 plan 확인

`-PlanOnly`는 EC2나 R2에 연결하지 않고 경로·도구·대상만 출력한다. 아직 없는
도구는 `<missing:...>`으로 보인다.

```powershell
./capture-portfolio-recovery.ps1 `
  -ServerAddress 203.0.113.10 `
  -SshIdentityPath $env:USERPROFILE\.ssh\indiefi-key.pem `
  -BackupRoot D:\indiefi-recovery-backups `
  -AgeRecipient (Get-Content D:\indiefi-recovery-recipient.txt -Raw).Trim() `
  -R2Endpoint https://example.r2.cloudflarestorage.com `
  -R2Bucket indiefi-media-portfolio `
  -R2CredentialsPath D:\indiefi-recovery-secret\r2-backup.env `
  -PlanOnly
```

## 실제 수집

1. Nginx에서 새 쓰기 요청을 막고 진행 중 HTTP 요청을 끝낸다.
2. Web·Worker, RabbitMQ queue와 PostgreSQL outbox가 수렴했는지 확인한다.
3. 위 명령에서 `-PlanOnly`를 빼고 아래 두 switch를 추가한다.

```powershell
  -ConfirmWriteIngressControlled `
  -ConfirmQueuesConverged
```

두 switch는 자동으로 상태를 바꾸지 않는다. 작업자가 바로 앞의 검증을 수행했음을
명시하는 안전장치다. 암호화 생성이 끝나도 EC2를 중지하기 전에 별도 검증한다.

```powershell
./verify-recovery-bundle.ps1 `
  -RunDirectory D:\indiefi-recovery-backups\<run-id> `
  -AgeIdentityPath D:\indiefi-recovery-secret\identity.txt `
  -WriteVerification
```

검증기는 암호문 SHA-256, age 복호화, tar 필수 항목과 내부 manifest의 모든
파일 size·SHA-256을 확인한다. 평문은 OS 임시 directory에만 만들고 `finally`에서
제거한다.

## 기존 AWS S3·KMS 스크립트

상위 `backup-postgres-to-s3.sh`와 `restore-postgres-from-s3.sh`는 별도 AWS S3
bucket과 KMS key를 사용하는 장기 운영 선택지다. 이번 단기 포트폴리오 검증은
새 유료 AWS 자원을 만들지 않으므로 사용하지 않고, EC2 밖 로컬 age bundle을
기본 경로로 사용한다. 두 스크립트는 향후 관리형 backup 설계 비교 자료로 남긴다.

## 격리 복원

`recovery-restore.env.example`을 실제 값이 든 ignored env 파일로 복사한다.
PostgreSQL·MinIO password는 서로 다른 난수로 만들고 평소 개발 환경 값이나
운영 값을 재사용하지 않는다.

```powershell
./restore-portfolio-recovery.ps1 `
  -RunDirectory D:\indiefi-recovery-backups\<run-id> `
  -AgeIdentityPath D:\indiefi-recovery-secret\identity.txt `
  -EnvironmentFile .\recovery-restore.env `
  -WorkspaceRoot D:\indiefi-recovery-workspaces `
  -StartApplication
```

격리 환경은 `127.0.0.1:55432` PostgreSQL, `127.0.0.1:59000` MinIO와
`127.0.0.1:5500` Web만 사용한다. `-StartApplication`을 지정하면 복원 전용
image를 build하고 `/health/ready` HTTP 200까지 확인한다. Redis·RabbitMQ·SMTP는
복원 readiness에서 끄며 outbox 수렴은 별도 사용자 흐름 검증에서 확인한다.
무료 community MinIO는 `RELEASE.2025-07-23T15-54-02Z` 이후 container가
게시되지 않았고, 이 버전에는 2026년에 공개된 unsigned-trailer write 취약점이
남아 있다. 수정된 AIStor image는 license 없이는 S3 작업을 거부하므로 이
포트폴리오 복원에서는 사용할 수 없다. 따라서 community image는 loopback,
매 실행마다 다른 자격 증명, 신뢰한 로컬 backup만 입력, 검증 직후 volume 폐기
조건의 일회성 실험실로만 사용한다. 인터넷·LAN·운영 환경에는 절대 공개하지 않는다.

격리 DB의 `pg_restore`는 source owner와 ACL을 복원하지 않는다. 운영 dump의
`indiefi_app`·monitoring 역할이 빈 실험실에 없더라도 schema와 data를 복원하고,
복원 전용 owner 한 명으로 사용자 흐름을 확인하기 위해서다. 실제 운영 재구축에서는
`configure-portfolio-database-role.sh`와 monitoring 계정 도구로 최소 권한을 다시
적용하고 별도로 검증한다.

복원 Web은 Production의 "object storage는 HTTPS" 정책을 낮추지 않기 위해
plain-HTTP MinIO integration을 끈 상태로 DB와 보호 키 구성을 검사한다. MinIO는
restore 도구가 object count·총 byte를 S3 API로 따로 검증한다. 복원 미디어를 Web
화면에서 조회하는 최종 사용자 흐름은 13-3-6 실행 증거에서 별도로 확인한다.

상태 확인과 정리는 다음처럼 한다.

```powershell
./manage-restore-environment.ps1 `
  -Action Status `
  -EnvironmentFile .\recovery-restore.env `
  -Workspace D:\indiefi-recovery-workspaces\<run-id>

./manage-restore-environment.ps1 `
  -Action Purge `
  -EnvironmentFile .\recovery-restore.env `
  -Workspace D:\indiefi-recovery-workspaces\<run-id> `
  -ConfirmPurge
```

`Purge`는 고정 Compose volume을 내린 뒤 marker의 절대 경로가 일치하고 reparse
point가 아닌 workspace만 삭제한다. 운영 또는 평소 개발 volume 이름과 겹치지
않는다.

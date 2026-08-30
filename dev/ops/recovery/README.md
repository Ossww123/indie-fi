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

# Recovery drill runbook

이 runbook은 첫 EC2 운영 세션에서 정합 backup을 만들고 즉시 로컬 빈 환경에
복원해 RPO·RTO를 측정하는 순서다. 명령을 빠르게 끝내는 것보다 쓰기 경계와
증거를 잃지 않는 것이 우선이다.

## 1. 세션 전 로컬 준비

- `age` 1.3.2 이상, AWS CLI v2, Docker Desktop, OpenSSH와 `tar`를 확인한다.
- age identity가 backup root와 다른 매체에 있고 test 암호문을 복호화하는지 확인한다.
- R2 backup token은 portfolio bucket `Object Read`만 허용하고 EC2에 저장하지 않는다.
- `recovery-restore.env`의 PostgreSQL·MinIO 난수 비밀번호가 서로 다른지 확인한다.
- backup root와 restore workspace는 저장소 밖 BitLocker volume을 사용한다.
- EC2 시작 전 비용·공인 IP·HTTPS·R2 CORS·SSH 제한 규칙을 13-0 runbook대로 확인한다.

## 2. RPO·RTO 시각 정의

| 시각 | 의미 | 기록 위치 |
|---|---|---|
| `last_known_good_backup_utc` | 직전 검증 성공 backup의 capture 완료 시각 | 이전 `verification.json` |
| `incident_assumed_utc` | 이번 훈련에서 장애가 발생했다고 가정한 시각 | validation run manifest |
| `write_fence_utc` | 새 HTTP 쓰기와 Worker 쓰기를 멈춘 시각 | 이번 evidence 메모 |
| `capture_completed_utc` | 암호화 bundle 생성이 끝난 시각 | `evidence.json` |
| `restore_started_utc` | 빈 복원 실행 시작 시각 | `restore-result.json` |
| `user_flow_completed_utc` | 마지막 사용자 흐름이 성공한 시각 | 수동 검증 worksheet |

- 측정 RPO = `incident_assumed_utc - last_known_good_backup_utc`
- 측정 RTO = `user_flow_completed_utc - restore_started_utc`

최초 훈련처럼 이전 backup이 없으면 RPO를 성공으로 만들지 않고 `not measured`로
기록한다. capture 소요 시간은 RTO와 분리해 함께 남긴다.

## 3. EC2 정합 경계 만들기

1. UTC 시각, 현재 release commit, Web·Worker restart 수와 data container health를 기록한다.
2. RabbitMQ `messages_ready/unacknowledged`, PostgreSQL outbox, media in-flight가 0인지 확인한다.
3. Nginx 접근을 중단하거나 security group으로 새 사용자 유입을 막는다.
4. 진행 중 요청이 끝난 뒤 `indiefi-web`을 중지한다.
5. queue와 outbox가 다시 0인지 확인한 뒤 `indiefi-worker`를 중지한다.
6. `write_fence_utc`와 서비스 상태를 기록한다.

```bash
sudo systemctl stop indiefi-web.service
# queue/outbox/media convergence evidence
sudo systemctl stop indiefi-worker.service
```

쓰기를 멈추기 전에 Worker를 먼저 내리면 outbox와 queue가 남을 수 있다. 수렴하지
않으면 `-ConfirmQueuesConverged`를 넣지 말고 원인을 해결한다.

## 4. 로컬 PC에서 실제 capture

먼저 `-PlanOnly` 결과를 저장한 다음 두 확인 switch로 실제 실행한다. 자세한 인자는
`README.md` 예시를 따른다.

```powershell
./capture-portfolio-recovery.ps1 <same arguments> -PlanOnly

./capture-portfolio-recovery.ps1 <same arguments> `
  -ConfirmWriteIngressControlled `
  -ConfirmQueuesConverged
```

완료 즉시 별도 identity로 검증한다.

```powershell
./verify-recovery-bundle.ps1 `
  -RunDirectory <new-run-directory> `
  -AgeIdentityPath <offline-identity> `
  -WriteVerification
```

SHA-256, age 복호화, dump TOC, R2 객체 수 또는 평문 임시 파일 정리가 실패하면
EC2를 중지하지 않고 새 정합 경계부터 다시 수집한다.

## 5. 빈 환경 restore

```powershell
./restore-portfolio-recovery.ps1 `
  -RunDirectory <new-run-directory> `
  -AgeIdentityPath <offline-identity> `
  -EnvironmentFile <restore-env> `
  -WorkspaceRoot <outside-repository-workspace> `
  -StartApplication
```

자동 결과에서 다음을 확인한다.

- row count 일치
- MinIO object count·총 byte 일치
- Data Protection certificate/private key 일치
- Production Web readiness `pass`
- `restore-result.json`의 elapsed seconds

## 6. 수동 사용자 흐름 worksheet

자체서명 경고를 일회성으로 통과해 `https://127.0.0.1:55443`에 접속한다. 실제
값은 화면 캡처나 로그에 남기지 않는다.

| 확인 | 결과 | 비식별 증거 |
|---|---|---|
| 기존 이메일·비밀번호 로그인 | TBD | 사용자 수·성공 시각 |
| 기존 암호화 이메일 조회 | TBD | 사용자 record id·성공/실패 |
| 기존 수령 게임 키 복호화 | TBD | grant id·성공/실패, key 원문 제외 |
| 게임·프로필 media 객체 존재 | TBD | media id·object key hash, URL 제외 |
| migration history·핵심 행 수 | TBD | 자동 restore 결과 파일 |

마지막 성공 시각을 `user_flow_completed_utc`로 기록하고 RTO를 계산한다. MinIO는
plain HTTP라 Web media integration을 켜지 않는다. DB의 media 참조와 복원 MinIO
inventory/hash를 대조한 결과를 media 복구 증거로 사용한다.

## 7. 원본 서비스 복귀와 정리

1. EC2 Worker를 시작하고 consumer·outbox 수렴을 확인한다.
2. Web을 시작하고 내부 readiness 후 Nginx/접근 제한을 원복한다.
3. 운영 사용자 흐름 한 건과 warning 이상 로그를 확인한다.
4. 로컬 증거에서 이메일·게임 키·token·비밀이 없는지 점검한다.
5. 복원 workspace와 volume을 `manage-restore-environment.ps1 -Action Purge`로 제거한다.
6. `prune-recovery-runs.ps1` preview를 검토하고 필요할 때만 `-ConfirmDeletion`한다.
7. EC2 증거 반출, SSH 규칙 제거, 인스턴스 중지와 비용 감사를 수행한다.

## 8. 실패·재시도 기준

- 복호화 또는 key pair 실패: identity·key bundle을 수정하지 말고 새 capture를 만든다.
- DB/R2 참조 불일치: 쓰기 경계를 다시 만들고 DB와 R2를 모두 새로 수집한다.
- restore schema/row 실패: 원본은 변경하지 않고 local tool을 수정한 뒤 같은 암호문으로 재현한다.
- 사용자 로그인·게임 키 복호화 실패: 보호 key id와 해당 DB 행을 확인하고 RTO를 성공 처리하지 않는다.
- 평문 정리 실패: 경로와 원인을 기록하고 EC2 종료 전에 수동 정리한다.

성공·실패 모두 시각, commit, 명령 버전과 결과를 남긴다. 실패한 실행을 지워
성공 시간만 짧게 보이게 하지 않는다.

## 9. 훈련 후 목표 확정

| 항목 | 초기 가설 | 실제 값 | 판정 |
|---|---:|---:|---|
| RPO | 24시간 이하 | TBD | TBD |
| capture 시간 | 별도 목표 없음 | TBD | 정보 |
| 자동 restore 시간 | 2시간에 포함 | TBD | 정보 |
| 사용자 흐름 포함 RTO | 2시간 이하 | TBD | TBD |

실제 값이 목표를 넘으면 원인을 `수집`, `전송`, `DB restore`, `객체 restore`,
`앱 기동`, `수동 검증`으로 나눠 가장 긴 구간 하나를 다음 개선 대상으로 정한다.


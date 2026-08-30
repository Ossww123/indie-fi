# 운영 검증 증거와 EC2 세션 체크리스트

## 목적

`runs/`에는 운영 검증 한 번의 입력·원시 결과·판정을 같은 run id로 모은다.
실제 결과는 로컬 전용이며 Git에 커밋하지 않는다. 저장소에는 이 형식과 비밀을
포함하지 않은 예시만 둔다.

## 실행 디렉터리

```text
runs/<run-id>/
├─ manifest.json
├─ load/
│  ├─ k6-summary.json
│  └─ console.txt
├─ observability/
│  ├─ dashboards/
│  ├─ alerts.json
│  └─ trace-ids.txt
├─ consistency/
│  ├─ before.json
│  └─ after.json
├─ recovery/
├─ failures/
└─ cost/
```

`manifest.json`은 `run-manifest.example.json`과 같은 top-level 필드를 사용한다.
각 증거 파일의 SHA-256과 상대 경로를 manifest `evidence`에 기록한다.

## 저장 금지

- 운영·테스트 계정 비밀번호와 password hash
- 쿠키, antiforgery·이메일 확인·비밀번호 재설정 token
- 이메일·게임 키 원문과 암호화 보호 키
- R2·AWS·SES 자격 증명과 서명 URL
- 전체 사용자 이메일, 요청 본문 또는 SQL 매개변수
- SSH private key와 환경 파일 원문

민감한 값이 발견되면 결과를 공유하지 않고 해당 파일을 폐기·재생성한다. 단순히
Git ignore에 들어 있다는 이유로 안전하다고 간주하지 않는다.

## 로컬 준비 완료 체크

- [ ] 기준 commit에서 전체 테스트가 통과한다.
- [ ] 관측 Compose와 Collector 설정의 정적 검증이 통과한다.
- [ ] 백업·복원, 장애 주입과 다중 인스턴스 도구가 dry-run을 지원한다.
- [ ] k6 smoke와 seed·audit가 로컬 격리 환경에서 통과한다.
- [ ] 실제 EC2에서 입력할 주소·instance id 외에는 미정 값이 없다.

## EC2 세션 시작

- [ ] 서울 리전, `t3.medium`, T3 Standard와 현재 단가·예상 실행시간을 기록한다.
- [ ] 금지한 Elastic IP·추가 EBS·snapshot·LB·NAT Gateway·RDS가 없는지 확인한다.
- [ ] EC2를 시작하고 현재 공인 IP, instance id와 CPUCreditBalance를 기록한다.
- [ ] 제한된 SSH `/32`, 80·443 규칙과 그 밖의 비공개 포트를 확인한다.
- [ ] 새 IP 인증서, Nginx HTTPS와 R2 CORS Origin을 갱신·검증한다.
- [ ] 같은 commit의 Web·Worker를 배포하고 release path·readiness를 기록한다.
- [ ] 로컬 관측 gateway와 SSH 터널을 시작한 뒤 EC2 agent 신호를 확인한다.
- [ ] EC2·로컬 시각 차이가 1초 이내인지 확인한다.

## 각 run 시작 전

- [ ] `run-manifest.example.json`을 복사해 새 run id와 전체 commit SHA를 기록한다.
- [ ] Git worktree가 깨끗하고 배포 release가 manifest commit과 같은지 확인한다.
- [ ] Web·Worker·PostgreSQL·Redis·RabbitMQ·R2 health와 재시작 수를 기록한다.
- [ ] queue·Outbox·미디어 처리 상태와 DB 정합성 before 감사를 저장한다.
- [ ] seed 인자·계정 pool·drop·key 수와 k6 scenario·stage를 기록한다.
- [ ] 중단 조건과 원복 명령을 실행 터미널에서 바로 확인할 수 있게 둔다.

## 각 run 종료 후

- [ ] 새 요청을 멈추고 cool-down과 비동기 수렴을 기다린다.
- [ ] k6 summary, dashboard·alert와 대표 trace id를 저장한다.
- [ ] DB·Redis·RabbitMQ·R2 after 감사와 서비스 restart·warning 로그를 저장한다.
- [ ] CPU·메모리·disk와 CPUCreditBalance의 시작·최저·종료 값을 기록한다.
- [ ] 통과·유효 실패·무효·안전 중단 중 하나로 판정하고 이유를 기록한다.
- [ ] 모든 증거 파일의 SHA-256을 manifest에 기록한다.

## 기본 세션 1 종료 게이트

- [ ] 운영 백업을 로컬 빈 환경에 실제 복원했다.
- [ ] 장애 훈련 뒤 모든 서비스와 확정 데이터가 수렴했다.
- [ ] 다중 Web·Worker 검증 뒤 단일 topology로 되돌렸다.
- [ ] calibration과 가능한 기준 부하의 증거를 로컬로 반출했다.
- [ ] 터널·임시 unit·SSH 규칙을 제거하고 EC2를 중지했다.
- [ ] AWS·R2 잔여 사용량과 계속 과금되는 새 자원이 없는지 확인했다.

복원에 실패하면 PostgreSQL·Redis·RabbitMQ 장애 훈련과 본 부하를 시작하지 않는다.

## 기본 세션 2 종료 게이트

- [ ] 병목 개선 commit을 같은 환경·seed·시나리오로 재측정했다.
- [ ] 개선 전후 결과가 모두 유효하고 정합성 감사에 통과했다.
- [ ] 최종 용량·RPO·RTO와 한계에 필요한 증거를 반출했다.
- [ ] 임시 관측·부하 접근과 EC2를 종료하고 비용 감사를 완료했다.

예비 세션은 기준 또는 재측정이 무효가 된 경우에만 사용하며 무효 사유와 필요한
재실행 범위를 먼저 manifest에 기록한다.

# IndieFi 운영 부하 검증 도구

이 폴더는 운영 EC2 밖의 로컬 PC에서 k6 부하를 만들고, 같은 run id로 입력·결과·관측 구간을 묶는
도구다. 로컬 수치는 개발 회귀 확인용이며 EC2 용량 수치로 사용하지 않는다.

## 구성

| 파일 | 역할 |
|---|---|
| `config/*.example.json` | 공개 실행 계약·단계 계획·자격 증명 형식 |
| `contract-check.js` | HTTPS·run id·commit·결과 경로와 비밀 분리 검사 |
| `seed-contract-check.js` | seed manifest의 계정 pool·catalog·drop 연결 검사 |
| `browse.js` | 공개 catalog·상세·검색·filter·cursor 일정 도착률 부하 |
| `keydrop.js` | 사전 대기·늦은 진입·동일 사용자 재시도를 포함한 오픈 집중 부하 |
| `Invoke-LoadValidation.ps1` | 계획 대조, Grafana annotation, 경보 감시, 안전 중단과 cool-down |
| `lib/` | contract·seed 검증과 민감하지 않은 summary 공통 코드 |

세부 계약은 `execution-contract.md`, 데이터 준비와 감사는 `seed-audit-runbook.md`, 두 시나리오는
`browse-runbook.md`·`keydrop-runbook.md`, 실행 안전선은 `load-safety-runbook.md`를 본다.

## 파일 경계

- example만 Git에 저장한다.
- 실제 contract·plan·credentials와 `results/`는 `.gitignore` 대상이다.
- credentials에는 테스트 계정 비밀번호만 두며 console·summary에 출력하지 않는다.
- 응답 cookie·antiforgery token·게임 키 원문은 어떤 결과 파일에도 저장하지 않는다.
- 매 실행은 새 run id를 사용하며 기존 seed나 runner evidence를 덮어쓰지 않는다.

## 기본 흐름

1. EC2에 실행 대상 commit을 배포하고 관측 SSH tunnel을 연다.
2. 같은 commit·origin으로 전용 seed와 before audit를 만든다.
3. example을 복사해 ignored contract·plan·credentials를 작성한다.
4. `Invoke-LoadValidation.ps1 -ValidateOnly`로 계획과 입력 연결을 검사한다.
5. 실제 실행 뒤 runner·k6 결과, after audit, Redis·RabbitMQ snapshot을 같은 run 폴더에 모은다.
6. 최소 60초와 자원·queue 수렴을 기다린 뒤 다음 사다리 단계로 간다.

Windows에서는 저장소 루트의 `loadtest.bat`이 PowerShell 실행기를 그대로 호출한다.

```powershell
.\loadtest.bat `
  -ContractPath dev/loadtest/config/<run>-contract.json `
  -SeedManifestPath dev/loadtest/results/<run>/seed-manifest.json `
  -BeforeAuditPath dev/loadtest/results/<run>/before-audit.json `
  -PlanPath dev/loadtest/config/load-plan.json `
  -ValidateOnly
```

`browse`는 credentials가 필요 없고, `keydrop`은 `-CredentialsPath`를 추가한다. 각 단계를 한 번씩
수동 승인해 실행하는 이유는 warning·정합성 감사 결과를 확인하지 않은 채 자동으로 더 높은 부하를
보내지 않기 위해서다.

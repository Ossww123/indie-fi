# 부하 사다리 실행과 안전 중단

## 왜 한 단계씩 실행하나

부하 사다리는 작은 smoke에서 시작해 RPS 또는 동시 사용자를 단계적으로 올리는 실행표다. 한 번에
큰 부하만 보내면 어느 지점부터 지연·오류·자원 포화가 시작됐는지 알 수 없다. 각 단계는 새 run id와
before audit를 사용하고, 단계가 끝난 뒤 cool-down과 after audit가 통과해야 다음 단계로 간다.

`load-plan.json`은 순서와 안전 시간을 정하고, 각 contract는 실제 한 단계의 값만 가진다. 실행기가
두 파일의 scenario·stage·parameters가 같은지 확인하므로 계획과 다른 임의 부하를 실수로 보내지 않는다.

## 실행 전

1. EC2·부하 PC 시각 차이 1초 이하, 배포 commit, topology와 CPU credit을 기록한다.
2. 로컬 관측 저장소와 SSH tunnel을 열어 Prometheus·Grafana가 모두 보이게 한다.
3. 새 seed를 만들고 before audit의 `passed=true`를 확인한다.
4. example을 복사한 ignored contract·plan·credentials에 실제 값을 채운다.
5. 먼저 `-ValidateOnly`로 파일 연결과 단계 일치를 검사한다.

PostgreSQL은 EC2 loopback에만 열려 있으므로 release에는 `seed/IndieFi.Seed.dll`과 root 소유 실행
래퍼가 포함된다. 테스트 비밀번호 JSON을 `/etc/indiefi/loadtest-credentials.json`에
`root:indiefi-web 640`으로 설치한 뒤 EC2에서 다음처럼 새 run을 만든다. 비밀번호 값은 명령행에 쓰지
않는다.

```bash
sudo -n /opt/indiefi/current/ops/run-load-validation-data.sh \
  prepare <run-id> https://<public-origin> <40-char-active-commit> \
  20 2000 200 100 5000 1200 900

sudo -n /opt/indiefi/current/ops/run-load-validation-data.sh \
  audit <run-id> before
```

숫자는 차례로 owner·claimer·latecomer·retry·game·key 수와 오픈까지의 초다. 래퍼는 활성 release
commit이 입력과 같은지 검사하고, `/var/lib/indiefi/loadtest/<run-id>` 밖에는 쓰지 않는다. transient
systemd service로 Web 사용자와 환경 파일을 사용하되 `NoNewPrivileges`·읽기 전용 system 영역을
적용한다. manifest와 before audit를 로컬의 같은 run 폴더로 내려받은 뒤 k6 실행기를 시작한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File dev/loadtest/Invoke-LoadValidation.ps1 `
  -ContractPath dev/loadtest/config/<run>-contract.json `
  -SeedManifestPath dev/loadtest/results/<run>/seed-manifest.json `
  -BeforeAuditPath dev/loadtest/results/<run>/before-audit.json `
  -PlanPath dev/loadtest/config/load-plan.json `
  -CredentialsPath dev/loadtest/config/credentials.json `
  -ValidateOnly
```

`browse`에는 `CredentialsPath`를 생략한다. 실제 실행에서는 `-ValidateOnly`만 제거한다.

## 자동으로 하는 일

- 시작 전 public target probe와 기존 critical Prometheus 경보를 확인한다.
- Grafana에 measurement 시작·종료 또는 중단 annotation을 같은 run id tag로 남긴다.
- k6를 별도 process로 실행하고 public target을 5초마다 확인한다.
- readiness 성격의 public probe가 15초 연속 실패하거나 critical 경보가 firing이면 k6를 종료한다.
- 최대 실행시간을 넘겨도 중단하며, 종료 뒤 60초 cool-down을 지킨다.
- `runner-start.json`, k6 stdout·stderr, k6 summary, `runner-result.json`을 run 폴더에 남긴다.

Grafana annotation은 그래프 위의 시간 표시다. 부하 시작·종료선을 메트릭과 겹쳐 보면 CPU나 DB lock이
어느 부하 구간에서 변했는지 같은 시계로 읽을 수 있다.

## 실행 뒤 문턱

`runner-result.json`의 exit·warning·abort를 확인하고 after audit와 RabbitMQ·Redis snapshot을 저장한다.
after audit의 지급·중복·적체와 queue 수렴이 통과하기 전에는 다음 단계를 시작하지 않는다. warning이
하나라도 firing했으면 현재 단계의 원인을 먼저 확인한다. critical 중단은 실패가 아니라 안전 장치가
정상 작동한 증거이며, 더 높은 단계는 실행하지 않는다.

실행 뒤 EC2에서 `audit <run-id> after`를 실행해 파일을 내려받는다. 전체 측정 세션이 끝나면 테스트
credentials 파일을 제거하고, run 증거를 내려받았는지 확인한 뒤 서버 측 보존 여부를 결정한다.

`-SkipMonitoring`, `-SkipCoolDown`은 loopback 로컬 검증에만 허용된다. HTTPS 운영 주소에서는 실행기가
이를 거부한다. contract·credentials·결과 파일에는 비밀번호, cookie, antiforgery token과 게임 키 원문을
남기지 않는다.

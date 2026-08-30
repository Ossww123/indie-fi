# 부하 데이터 seed와 감사

## 초기화 원칙

일반 실행은 운영 DB를 `DELETE`·`TRUNCATE`하지 않는다. 매번 새 run id로 전용 계정·게임·drop을
원자적으로 만들고, 같은 run id가 이미 있으면 실패한다. 계정은 owner·claimer·latecomer·retry
pool로 분리되며 각 VU는 자기 계정 하나만 사용한다.

## prepare 입력

- DB 연결 문자열과 이메일·게임 키 보호 키는 환경 설정 또는 User Secrets에서만 읽는다.
- 테스트 비밀번호는 ignored credentials JSON에서 읽고 명령행·manifest·console에 출력하지 않는다.
- `--base-url`과 `--commit`은 비밀이 아니며 seed와 측정 대상이 같은지 대조하는 값이다.
- `--output`은 기존 파일을 덮어쓰지 않는다.

소규모 calibration 뒤 기준 규모를 조정하고, baseline과 개선 재측정은 같은 규모를 사용한다.

```powershell
dotnet run --project dev/tools/IndieFi.Seed -- prepare `
  --run-id <run-id> `
  --output <ignored-run-dir>/seed-manifest.json `
  --credentials dev/loadtest/config/credentials.json `
  --base-url https://<target-origin> `
  --commit <40-character-deployed-sha> `
  --owners 20 --users 2000 --late-users 200 --retry-users 100 `
  --games 5000 --keys 1200 --opens-in 900
```

계정 비밀번호 해싱은 DB transaction 밖에서 수행한다. 저장 직전 PostgreSQL advisory lock으로 같은
run id의 동시 prepare를 직렬화하고, drop 오픈 시각은 해싱이 끝난 뒤 계산한다.

## before·after audit

```powershell
dotnet run --project dev/tools/IndieFi.Seed -- audit `
  --manifest <ignored-run-dir>/seed-manifest.json `
  --output <ignored-run-dir>/before-audit.json `
  --phase before
```

before는 marker 계정·로그인 준비·catalog·drop·암호화 key/fingerprint 수, 지급 0과 전역
authentication email·message Outbox·notification·media 무적체를 요구한다. after는 지급 0 대신
사용자·key 중복 0, 지급 수가 재고 이하이고 모든 적체가 수렴했음을 요구한다.

RabbitMQ ready·unacknowledged는 DB audit에 없으므로 같은 시각에 13-4의 RabbitMQ snapshot도
저장한다. audit 실패 run은 다음 측정의 seed로 재사용하거나 조용히 삭제하지 않는다.

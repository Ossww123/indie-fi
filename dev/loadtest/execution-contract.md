# 운영 부하 실행 계약

## 파일 경계

- `contract.json`: run id, 대상 Origin, commit, topology, 시나리오·단계·threshold와 결과 경로만 둔다.
- `credentials.json`: 계정 email pattern과 공통 테스트 비밀번호만 둔다.
- 두 실제 파일은 `.gitignore` 대상이고, `*.example.json`만 저장소에 둔다.
- cookie·antiforgery token은 k6 VU의 메모리에만 존재하며 summary·console·HTTP debug에 남기지 않는다.

실제 파일은 example을 복사해 로컬에서 만들고 ACL을 현재 Windows 사용자로 제한한다. 운영 계정이나
개인 계정은 사용하지 않고 13-6-2가 만든 전용 run 계정만 사용한다.

## 실행 위치와 입력

저장소 루트에서 로컬 k6를 실행한다. `CONTRACT_FILE`과 `CREDENTIALS_FILE`은 k6 script 기준
상대 경로이며 공통 loader(`lib/contract.js`) 기준이므로 `../config/...` 형식을 사용한다.

```powershell
k6 run `
  -e CONTRACT_FILE=../config/contract.json `
  -e CREDENTIALS_FILE=../config/credentials.json `
  dev/loadtest/contract-check.js
```

`baseUrl`은 경로가 없는 HTTPS Origin이어야 한다. 평문 HTTP는 `localhost`·`127.x.x.x` loopback만
허용한다. run id와 출력 경로에는 경로 이탈 문자열을 허용하지 않는다.

## 금지 사항

- `--http-debug` 또는 request/response body를 출력하는 확장 사용
- 실제 비밀번호·cookie·antiforgery 값의 console 출력
- EC2 안에서 k6 실행
- 개인 브라우저 session이나 실제 사용자 계정 재사용
- contract의 commit·topology·seed를 실행 도중 변경

결과 JSON은 공개 가능한 집계와 run metadata만 담지만 운영 IP·commit·성능 수치가 포함되므로
검토 전에는 ignored evidence 디렉터리에 보관한다.

# 로그인·SignalR·공유 요청 제한 검증

## 로컬 자동 검증 기준

EC2 전에 다음 테스트를 통과시킨다.

```powershell
dotnet test tests/IndieFi.Web.Tests/IndieFi.Web.Tests.csproj `
  --filter "FullyQualifiedName~DataProtectionKeyPersistenceTests|FullyQualifiedName~MediaMultiWebIntegrationTests"
```

- Production Web A가 발급한 로그인 cookie를 같은 DB·인증서를 쓰는 Web B가 읽는다.
- Web A의 Data Protection cursor를 Web B가 읽는다.
- 두 Web의 Redis upload limiter를 번갈아 호출해도 짧은 구간 20회가 하나의 제한으로 계산된다.
- 두 Web이 같은 PostgreSQL media 예약 상태를 본다.

SignalR backplane의 실제 socket 이동은 Nginx·브라우저가 필요한 EC2 절차에서 확인한다.

## 로그인 cookie 교차 instance

1. topology를 시작하고 한 브라우저에서 로그인한다.
2. 인증이 필요한 프로필·알림함·크레딧 화면을 오가며 임시 upstream header 5101·5102를 모두 기록한다.
3. Web 1에서 받은 응답 직후 Web 1을 중지하고 같은 cookie로 Web 2의 인증 화면을 연다.
4. 재로그인 요구, cookie 삭제 또는 401이 있으면 Data Protection DB key ring·인증서 경로·application name을 대조한다.

cookie 값·Set-Cookie 전체와 사용자 이메일은 증거에 저장하지 않는다. upstream·HTTP status,
인증 성공 여부와 trace id만 기록한다.

## SignalR과 Redis backplane

1. 브라우저 A에서 알림함과 대기실 화면을 열어 SignalR connected를 확인한다.
2. 브라우저 B의 테스트 계정 행동으로 A에게 웹 알림 또는 대기 상태 변경 한 건을 만든다.
3. trace의 `service.instance.id`로 publish를 처리한 Web과 socket이 연결된 Web이 달라도 알림이 오는지 본다.
4. socket을 가진 Web을 중지하고 client 자동 재연결 또는 polling 복구를 기다린다.
5. 다른 Web에서 같은 사건을 다시 만들지 않고 기존 상태가 최신으로 보이는지 확인한다.

Redis backplane은 socket 자체를 옮기지 않는다. 한 Web이 받은 hub publish를 Redis channel로
다른 Web에 전달한다. 프로세스가 사라진 socket은 browser client가 새 Web에 다시 연결해야 한다.

## 공유 upload URL 제한

고유 테스트 owner·pending media를 사용한다. 20회 허용과 21번째 `ShortWindowLimitReached`를
두 upstream에 걸쳐 확인하되 실제 이미지 21개를 R2에 올리지 않는다. EC2 실행 도구가 준비되기
전에는 기존 multi-Web integration test를 증거로 사용하고 운영 browser에서 수동 반복하지 않는다.

실제 EC2 검증에서는 최근 10분 동안 upload URL을 발급하지 않은 검증 계정으로
`/Account/Profile`에 로그인하고 개발자 도구 Console에서
`browser-shared-upload-limit.js` 전체를 실행한다. 스크립트는 현재 browser의 HttpOnly 로그인
cookie를 직접 읽지 않고 same-origin 요청에만 사용한다. profile replacement 예약 한 건을 만든 뒤
upload URL만 21회 요청하고 실제 R2 PUT은 하지 않으며, 마지막에 예약을 삭제한다.

성공 결과는 `SHARED_UPLOAD_LIMIT_RESULT`의 `passed: true`, `allowed: 20`, `rejected: 1`,
upstreams 5101·5102와 `SHARED_UPLOAD_LIMIT_CLEANUP`의 HTTP 202다. Console 표나 증거에는
antiforgery token, cookie, media id, presigned URL을 출력하지 않는다.

실제 검증에서는 요청별 upstream·결과만 기록하고 presigned URL, antiforgery token·cookie와
owner/media ID는 저장하지 않는다. Web을 바꿨다는 이유로 21번째가 허용되면 공유 counter 실패다.

## 완료 기준

- 같은 cookie·cursor가 5101·5102에서 모두 유효하다.
- 다른 Web에서 발생한 SignalR 사건이 전달되고 socket 소유 Web 중단 뒤 재연결·polling으로 회복한다.
- 공유 제한은 두 Web 합계 20회를 기준으로 21번째를 거부하며 Redis 실패 시 fail closed다.
- 로그인·알림·media DB 상태가 중복되지 않고 전체 topology audit가 정상이다.

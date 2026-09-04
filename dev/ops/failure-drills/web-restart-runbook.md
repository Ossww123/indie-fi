# Web 재시작 훈련

## 검증 질문

1. 재시작 중 Nginx가 무한 대기하지 않고 짧은 502·503 또는 정상 응답으로 끝나는가?
2. 새 Web이 active가 된 뒤 `/health/ready`가 200으로 돌아오는가?
3. 재시작 전에 로그인한 브라우저가 다시 로그인하지 않고 인증 화면을 사용할 수 있는가?
4. 알림·대기실 SignalR 연결이 재연결되거나 폴링으로 최신 상태를 다시 읽는가?
5. 재시작 전 확정된 DB 쓰기가 사라지거나 같은 요청이 두 번 확정되지 않는가?

## 준비

- `failure-matrix.md`의 공통 baseline을 통과한다.
- 브라우저 A로 로그인하고 프로필·알림함처럼 인증이 필요한 페이지가 열리는지 확인한다.
- 브라우저 B 또는 별도 터미널에서 공개 홈을 1초 간격으로 요청하되 부하 테스트로 확대하지 않는다.
- 대기실 또는 알림 SignalR 화면 하나를 열어 연결 상태를 볼 수 있게 한다.
- Grafana에서 service overview와 HTTP dashboard를 열고 UTC 시작 시각을 기록한다.

## 실행

먼저 변경 없는 계획을 확인한다.

```bash
TOOL=/usr/local/lib/indiefi/failure-drills/run-controlled-failure.sh
RUN_ID=<run-id>
"$TOOL" --plan web-restart 60 "$RUN_ID"
```

공개 요청 관측을 시작한 뒤 별도 SSH 창에서 한 번만 실행한다. `web-restart`는 지속
장애가 아니므로 duration 값은 입력 계약 검증에만 사용되고 서비스는 즉시 다시 시작한다.

```bash
sudo "$TOOL" web-restart 60 "$RUN_ID" "RUN-${RUN_ID}-web-restart"
```

## 기록

| 시각 | 기록할 값 |
|---|---|
| 주입 직전 | Web `NRestarts`, release 경로, readiness HTTP 상태, 로그인·SignalR 상태 |
| 첫 비정상 응답 | UTC 시각, HTTP 상태와 요청 시간만 기록; URL query·cookie는 기록하지 않음 |
| systemd active | 명령 완료 UTC 시각과 `ExecMainStatus` |
| readiness 200 | 첫 회복 UTC 시각 |
| 사용자 흐름 회복 | 기존 로그인 페이지 성공과 SignalR 재연결 또는 폴링 회복 시각 |

감지시간은 재시작 명령 시작부터 첫 비정상 응답·경보까지, 기술 RTO는 명령 시작부터
readiness 200까지, 사용자 RTO는 기존 로그인과 실시간 상태가 모두 회복될 때까지다.

## 완료 기준

- `indiefi-web.service`가 active이고 `ExecMainStatus=0`이다.
- Nginx HTTPS와 `/health/ready`가 200으로 회복된다.
- 재시작 전에 발급된 로그인 쿠키로 인증 페이지를 열 수 있다.
- SignalR은 자동 재연결하거나 제한 시간 안에 폴링으로 최신 상태를 읽는다.
- Web·Worker warning 로그와 DB 감사에서 예상 밖 중복·미완료 쓰기가 없다.

로그인 쿠키가 무효가 되면 Data Protection 인증서·key ring·application name이 같은
release에서 유지됐는지 먼저 본다. SignalR만 회복하지 않으면 브라우저 재연결 로그와
Redis backplane을 분리해 확인한다. 실제 EC2 결과가 없으면 이 항목은 준비 완료일 뿐이다.

# Nginx 분산과 Web 한 인스턴스 제거

## 시작

```bash
RUN_ID=<run-id>
TOPOLOGY=/usr/local/lib/indiefi/scale-validation/manage-scale-topology.sh
PROCESSES=/usr/local/lib/indiefi/scale-validation/manage-scale-processes.sh
"$TOPOLOGY" --plan
sudo "$TOPOLOGY" start "$RUN_ID" "START-MULTI-${RUN_ID}"
"$TOPOLOGY" status
```

도구는 기존 Nginx config를 보존하고 Web 5101·5102가 모두 응답한 뒤에만 원래 single
Web·Worker를 중지한다. 임시 `X-IndieFi-Upstream` header와 trace의 instance id로 두
Web이 모두 요청을 받는지 확인한다. 45분 timer 또는 재부팅 guard가 원본 config를 복원한다.

## sticky session 없는 분산

같은 로그인 browser에서 공개·인증 page를 20회 이상 이동하며 header에 5101·5102가 모두
나오는지 기록한다. cookie를 지우거나 upstream별 cookie를 만들지 않는다. 같은 client가
두 Web을 오가도 로그인과 페이지 결과가 유지되어야 한다.

## 한 Web 제거

Web 1을 중지하기 전에 진행 중 요청과 SignalR 화면을 준비한다.

```bash
sudo "$PROCESSES" stop-web-1 "$RUN_ID" "STOP-WEB-1-${RUN_ID}"
```

Nginx는 `max_fails=1 fail_timeout=5s` 뒤 새 요청을 Web 2로 보낸다. 다음을 기록한다.

- 중지 명령, 첫 502/503이 있다면 그 시각, Web 2 단독 정상 응답 시각
- 새 요청의 upstream header가 5102만 나타나는지
- 기존 로그인과 SignalR 재연결·폴링이 Web 2에서 회복하는지
- 진행 중 요청의 결과를 사람이 재전송하지 않았을 때 DB 중복이 없는지

확인 뒤 Web 1을 복귀한다.

```bash
sudo "$PROCESSES" start-web-1 "$RUN_ID" "START-WEB-1-${RUN_ID}"
```

두 upstream이 다시 보이지 않거나 Nginx 오류가 반복되면 다음 시나리오를 진행하지 않고
즉시 원복한다.

```bash
sudo "$TOPOLOGY" stop "$RUN_ID" "STOP-MULTI-${RUN_ID}"
```

# R2·SES 외부 의존성 실패 훈련

R2와 SES는 서로 다른 run으로 실행한다. R2는 Web·Worker의 media endpoint만, SES는
Worker의 인증·알림 SMTP endpoint만 loopback의 닫힌 port로 바꾼다. 설정은 `/run`의
임시 systemd drop-in이며 원래 `/etc/indiefi/*.env`와 자격 증명은 읽거나 수정하지 않는다.

## 공통 준비

```bash
RUN_ID=<run-id>
EVIDENCE="$HOME/indiefi-validation/runs/$RUN_ID/failures/<scenario>"
TOOL=/usr/local/lib/indiefi/failure-drills/run-controlled-failure.sh
RETRY_AUDIT=/usr/local/lib/indiefi/failure-drills/capture-external-retry-state.sh
DB_AUDIT=/usr/local/lib/indiefi/failure-drills/capture-authoritative-state.sh
"$RETRY_AUDIT" "$EVIDENCE/before-retry.tsv"
"$DB_AUDIT" "$EVIDENCE/before-db.tsv"
```

기존 authentication email, notification due·failed, media in-flight·failed와 messaging
Outbox가 0 또는 설명 가능한 기준선이어야 한다. 실제 endpoint·access key·SMTP username은
증거·명령행·journal 조회 결과에 복사하지 않는다.

## R2 실패 run

정상 R2에서 테스트 이미지 하나를 업로드하고 완료 요청을 보낼 준비를 한다. 너무 빨리
처리되어 fault와 겹치지 않으면 억지로 DB 상태를 바꾸지 않고 `retry not observed`로 남긴다.

```bash
"$TOOL" --plan r2-outage 60 "$RUN_ID"
sudo "$TOOL" r2-outage 60 "$RUN_ID" "RUN-${RUN_ID}-r2-outage"
```

관찰 창에서 다음을 한 번씩 확인한다.

- 홈·게임·리뷰·키 화면과 DB 기반 쓰기는 R2 실패 범위 밖에서 동작하는가?
- media upload URL·이미지 조회·처리는 제한되며 성공처럼 표시되지 않는가?
- readiness storage check, R2 client span과 transient failure가 약 3초 제한 안에서 끝나는가?
- media row가 사라지지 않고 `ProcessingPending`·다음 시도 또는 안전한 terminal 상태로 남는가?

원복 뒤 `RETRY_AUDIT`를 1분·5분과 media retry due 시점에 수집한다. 처리 due·expired lease가
감소하고 최종 `Ready` 또는 명시적 `Failed`가 되어야 한다. 객체·DB row를 직접 삭제하거나
새 media id로 같은 작업을 대체하지 않는다.

## SES 실패 run

SES Sandbox에서 이미 검증된 별도 수신 주소를 사용한다. R2 run이 완전히 수렴한 뒤 새
run id로 시작한다.

```bash
"$TOOL" --plan ses-outage 60 "$RUN_ID"
sudo "$TOOL" ses-outage 60 "$RUN_ID" "RUN-${RUN_ID}-ses-outage"
```

관찰 창에서 가입 확인 메일 한 건과 일반 알림 이메일 한 건을 만든다.

- 가입·도메인 행동 자체는 DB에 확정되고 웹 알림은 보이는가?
- 인증 email Outbox와 notification Pending/due가 보존되는가?
- SMTP connect failure가 3초 안에 끝나고 다른 delivery를 무한히 막지 않는가?
- recipient·본문·SMTP 응답 원문 없이 고정 오류 분류와 retry 시각만 남는가?

원복 뒤 1분·5분·10분에 retry 감사를 저장하고, 두 메일이 실제 수신함 또는 spam함에 한
번씩 도착하는지 확인한다. SES Sandbox의 미검증 수신 주소 실패는 서비스 장애 증거가
아니므로 run을 무효로 판정한다.

## 완료 기준

- 각 run 뒤 임시 drop-in이 없고 Web·Worker가 원래 environment file로 active다.
- R2 실패가 핵심 게임·리뷰·키 데이터 손실로 확대되지 않고 media 작업이 수렴한다.
- SES 실패 중 두 종류의 email 의도가 DB에 남고 원복 뒤 중복 없이 실제 전달된다.
- authoritative invariant 위반이 0이고 RabbitMQ·Outbox·media·email 적체가 기준선으로 돌아온다.

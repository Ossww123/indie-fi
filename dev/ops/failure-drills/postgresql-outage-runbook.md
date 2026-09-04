# PostgreSQL 일시 중단 훈련

## 검증 질문

1. DB가 없을 때 readiness가 실패하고 계정·리뷰·크레딧·키 수령이 성공처럼 보이지 않는가?
2. 실패한 요청이 크레딧만 차감하거나 키만 점유하는 부분 확정을 만들지 않는가?
3. DB가 돌아오면 Web·Worker가 영구 재시작 없이 새 연결을 얻고 readiness가 회복되는가?
4. migration, 크레딧 원장, 리뷰 terminal 상태, 키 지급 유일성과 Outbox가 일관적인가?

## 준비와 before 감사

복원 훈련을 통과한 뒤에만 실행한다. 인증된 테스트 계정으로 크레딧 잔액·리뷰 목록과
열린 키 배포 화면을 준비하되 실제 요청은 장애 관찰 창에서 한 번씩만 보낸다.

```bash
RUN_ID=<run-id>
EVIDENCE="$HOME/indiefi-validation/runs/$RUN_ID/failures/postgresql-outage"
TOOL=/usr/local/lib/indiefi/failure-drills/run-controlled-failure.sh
AUDIT=/usr/local/lib/indiefi/failure-drills/capture-authoritative-state.sh
"$AUDIT" "$EVIDENCE/before.tsv"
"$TOOL" --plan postgresql-outage 60 "$RUN_ID"
```

`before.tsv`는 집계값만 포함한다. 식별자·이메일·키·payload를 추가 조회하지 않는다.

## 실행과 관측

별도 SSH 창에서 아래 명령을 foreground로 실행하고 60초 관찰 창 동안 준비한 읽기·쓰기
요청을 각각 한 번만 수행한다.

```bash
sudo "$TOOL" postgresql-outage 60 "$RUN_ID" \
  "RUN-${RUN_ID}-postgresql-outage"
```

- Nginx HTTP 상태와 `/health/ready`, `pg_up`, Web·Worker DB 오류 분류를 본다.
- 회원가입·로그인 같은 계정 DB 요청, 리뷰 요청·수령, 키 수령은 성공 표시와 redirect
  결과를 기록한다. 동일한 POST를 수동 재전송하지 않는다.
- PostgreSQL volume, container 설정, DB 행과 migration 명령은 건드리지 않는다.
- 앱이 연결 오류를 반환하는 것은 예상 영향이며 부분 성공 또는 무한 대기는 결함이다.

## 복구와 after 감사

제어 도구가 컨테이너를 다시 시작하고 healthy를 확인한 다음 readiness 200과 Worker DB
작업 재개를 기다린다.

```bash
"$AUDIT" "$EVIDENCE/after.tsv"
diff -u "$EVIDENCE/before.tsv" "$EVIDENCE/after.tsv" || true
```

두 파일의 첫 시각 행은 당연히 다르다. migration 수, 음수 잔액·원장 불일치·잘못된
암호화 shape·키 중복·리뷰 terminal 위반은 0 또는 before와 같은 정상값이어야 한다.
장애 중 의도적으로 확정한 사건이 있다면 Outbox 건수 차이는 허용하되 cool-down 뒤 다시
감사해 0으로 수렴시킨다.

## 완료 기준

- PostgreSQL과 Web·Worker가 정상이고 readiness가 200이다.
- migration 수가 바뀌지 않고 무단 migration 실행이 없다.
- negative credit, ledger mismatch, key duplicate, invalid review shape가 0이다.
- 장애 중 실패한 단일 요청이 부분 확정되지 않았고 같은 요청을 보상하려고 직접 수정하지 않았다.
- Outbox·notification·media는 제한된 cool-down 뒤 기존 기준선으로 수렴한다.

# Redis 일시 중단·AOF 복구 훈련

## 데이터 경계

- Redis: 대기실 순번·입장 허용, SignalR backplane과 공유 요청 제한의 임시 상태
- PostgreSQL: key grant와 암호화된 게임 키, 크레딧·리뷰의 최종 확정 상태
- AOF: Redis 변경 명령을 디스크에 이어 써 재시작 때 임시 상태를 재구성하는 기록

Redis의 순번이 복구되어도 키 지급이 맞다는 뜻은 아니며, 반대로 대기 순번을 잃어도 이미
PostgreSQL에 확정된 key grant를 취소하거나 다시 지급해서는 안 된다.

## 준비와 before 기록

- 테스트 drop의 사전 대기실에 두 계정 이상을 넣고 각 브라우저의 표시 순번을 기록한다.
- 새 drop id만 사용하고 다른 운영 drop과 장애를 섞지 않는다.
- Redis AOF 상태와 해당 drop의 집계만 저장한다. entry 값·사용자 식별자는 출력하지 않는다.

```bash
RUN_ID=<run-id>
DROP_ID=<test-drop-id>
EVIDENCE="$HOME/indiefi-validation/runs/$RUN_ID/failures/redis-outage"
TOOL=/usr/local/lib/indiefi/failure-drills/run-controlled-failure.sh
REDIS_AUDIT=/usr/local/lib/indiefi/failure-drills/capture-redis-state.sh
DB_AUDIT=/usr/local/lib/indiefi/failure-drills/capture-authoritative-state.sh
"$REDIS_AUDIT" "$EVIDENCE/before-redis.tsv" "$DROP_ID"
"$DB_AUDIT" "$EVIDENCE/before-db.tsv"
"$TOOL" --plan redis-outage 60 "$RUN_ID"
```

## 실행과 관측

```bash
sudo "$TOOL" redis-outage 60 "$RUN_ID" "RUN-${RUN_ID}-redis-outage"
```

60초 동안 신규 대기실 진입·상태 polling을 각각 한 번 확인한다. 제한을 우회하려고
in-memory fallback, 다른 prefix 또는 DB 직접 수정을 쓰지 않는다. `redis_up`, readiness,
WaitingRoom failure metric과 브라우저의 안전한 오류 표시를 같은 UTC 구간에 기록한다.

## 복구와 대조

```bash
"$REDIS_AUDIT" "$EVIDENCE/after-redis.tsv" "$DROP_ID"
"$DB_AUDIT" "$EVIDENCE/after-db.tsv"
diff -u "$EVIDENCE/before-redis.tsv" "$EVIDENCE/after-redis.tsv" || true
diff -u "$EVIDENCE/before-db.tsv" "$EVIDENCE/after-db.tsv" || true
```

- `aof_enabled=1`, `loading=0`, `aof_last_write_status=ok`와 healthy를 확인한다.
- 장애 전 대기자에게 발급된 순번과 entries·pre·queue·admitted 집계가 AOF로 회복되는지 본다.
- 장애 중 브라우저가 재시도했더라도 한 사용자가 두 위치나 두 key grant를 갖지 않는지 본다.
- PostgreSQL의 key grant·원장 invariant는 Redis 결과와 무관하게 0 위반이어야 한다.

## AOF를 읽을 수 없을 때의 비파괴 판정

이 훈련에서는 AOF 파일·volume을 삭제하거나 `FLUSHALL`하지 않는다. Redis가 healthy로
돌아오지 않으면 자동 원복 실패로 안전 중단하고 volume을 보존한다. 다음 선택을 별도
복구 사건으로 남긴다.

1. AOF 검사·수리 또는 검증된 Redis backup 복원을 먼저 시도한다.
2. 복구가 불가능하면 기존 PostgreSQL key grant는 그대로 보존한다.
3. 유실된 대기 순번은 정확히 재구성할 근거가 없다고 사용자에게 알리고, 운영자가 새
   namespace로 재입장을 여는 절차는 기능 지원과 별도 승인 뒤 수행한다.
4. 기존 prefix 삭제·재사용이나 이미 지급된 키의 재고 복귀는 하지 않는다.

## 완료 기준

- Redis가 healthy이고 AOF write 상태가 정상이다.
- 관찰 중인 drop의 대기 상태가 기대한 경계로 회복되거나 손실을 명확히 유효 실패로 기록한다.
- PostgreSQL key grant·크레딧·Outbox 불변식이 유지된다.
- 대기실·SignalR이 다시 연결되고 새 사용자가 정상 경로로 진입한다.

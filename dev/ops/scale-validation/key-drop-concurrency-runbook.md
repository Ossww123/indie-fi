# 다중 Web 대기실과 마지막 키 동시 수령

## 로컬 회귀 기준

- `WaitingRoomMultiInstanceLoadTests`: Redis store 4개, 사용자 500명의 중복 join·순번·입장 10명 경계를 검증한다.
- `ClaimAsync_WhenManyUsersClaimAtOnceWithEnoughKeys_AllGetDistinctKeysWithoutConflict`: 20명·20키를 동시에 지급한다.
- `ClaimAsync_WhenManyUsersRaceForLastKey_GrantsExactlyOnce`: 20명이 마지막 1키에 경쟁해 정확히 1명만 지급된다.

## EC2 데이터와 요청 경계

- 다른 run에서 쓰지 않은 전용 drop·계정 pool을 사용한다.
- 계정별 로그인과 대기실 진입은 측정 전에 완료하고 claim만 같은 절대 UTC에 보낸다.
- 재고보다 5명 이상 많은 admitted 계정을 준비하되 부하 보정 전에는 20명·5키처럼 작게 시작한다.
- 현재 `loadtest/keydrop.js`는 로컬 고정 파일·공통 비밀번호와 오래된 판정 설명을 사용하므로
  13-6의 실행 계약·seed 개선 전에는 운영 EC2에 사용하지 않는다.

## before·after 감사

```bash
RUN_ID=<run-id>
DROP_ID=<test-drop-id>
EVIDENCE="$HOME/indiefi-validation/runs/$RUN_ID/scale/key-drop"
AUDIT=/usr/local/lib/indiefi/scale-validation/capture-key-drop-state.sh
"$AUDIT" "$EVIDENCE/before.tsv" "$DROP_ID"
# 로컬 부하 발생기에서 승인된 claim 시나리오 한 번 실행
"$AUDIT" "$EVIDENCE/after.tsv" "$DROP_ID"
```

별도로 Redis 대기 집계, k6 outcome·latency와 두 Web의 upstream·trace 비율을 같은 UTC
구간에 저장한다. key 원문, grant의 user/key ID, cookie·antiforgery token은 출력하지 않는다.

## 수식과 판정

```text
grant 증가 = after.grants.total - before.grants.total
재고 감소 = before.inventory.remaining - after.inventory.remaining
HTTP 지급 수 = k6 claim_granted

grant 증가 = 재고 감소 = HTTP 지급 수
grants.total = grants.distinct_users = grants.distinct_keys
grants.total <= inventory.keys = drop.total_keys
duplicate groups = 0, inventory_total_mismatch = 0, grants_over_inventory = 0
```

요청 수가 남은 재고보다 많으면 나머지는 sold out 또는 이미 지급된 동일 사용자의 멱등
결과여야 한다. `other`, 5xx와 conflict는 원인·trace를 분리하고 성공으로 합치지 않는다.

## 완료 기준

- 두 upstream을 통해 대기 순번과 admitted 수가 동일 Redis namespace에서 일치한다.
- 마지막 재고까지 지급되지만 그보다 많은 key grant는 생성되지 않는다.
- 사용자당·key당 중복이 0이고 HTTP 결과와 DB 증가가 일치한다.
- 처리 뒤 RabbitMQ·Outbox·notification 적체와 Redis 상태가 기준선으로 수렴한다.

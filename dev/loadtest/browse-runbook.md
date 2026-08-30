# 공개 조회·검색 지속 부하

`browse.js`는 초당 iteration 수를 고정하고 한 iteration마다 HTTP 요청 하나를 보낸다. 응답이
느려져 필요한 VU가 `maxVUs`를 넘으면 k6가 요청을 늦게 밀어 넣지 않고 `dropped_iterations`로
기록하므로 부하 발생기 부족과 서버 실패를 구분할 수 있다.

## 요청 비율

| 작업 | 비율 | 별도 metric |
|---|---:|---|
| 공개 catalog 첫 페이지 | 25% | `browse_catalog_duration` |
| 게임 상세 | 20% | `browse_detail_duration` |
| 결과 있는 검색 | 20% | `browse_search_duration` |
| 결과 없는 검색 | 10% | `browse_search_duration` |
| 장르 filter | 10% | `browse_filter_duration` |
| 태그 filter | 5% | `browse_filter_duration` |
| 플랫폼 filter | 5% | `browse_filter_duration` |
| keyset cursor 다음 페이지 | 5% | `browse_cursor_duration` |

setup의 catalog 1회에서 다음 cursor를 얻고, setup은 측정 iteration에 포함하지 않는다. cursor가
없으면 조용히 첫 페이지로 대체하지 않고 실패시켜 seed catalog 부족을 드러낸다.

## 실행

contract의 `ratePerSecond`, `durationSeconds`, `preAllocatedVUs`, `maxVUs`를 단계별로 바꾸고
seed manifest는 같은 run id·commit·Origin이어야 한다.

```powershell
k6 run `
  -e CONTRACT_FILE=../config/browse.json `
  -e SEED_MANIFEST_FILE=../results/<run-id>/seed-manifest.json `
  dev/loadtest/browse.js
```

공개 조회는 자격 증명을 읽지 않는다. 결과는 전체 HTTP 실패율과 dropped iteration, 작업별
p50·p95·p99를 저장한다. 실제 병목 판단은 같은 UTC 구간의 Web trace, PostgreSQL query·connection,
host CPU·memory와 함께 한다.

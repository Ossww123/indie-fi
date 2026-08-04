# 키 받기 경합 진단 (4-b)

부하 측정(4-a)에서 관측한 **conflict 64% · 받기 p95 460ms**의 원인을,
Postgres 내부 상태를 들여다봐서 **현장에서** 확인한다.

여기 쿼리들은 **락을 걸지 않는다.** `pg_stat_activity` 를 읽어 "지금 누가 무엇을 기다리나"를
보여주는 순수 관찰자다(대시보드 같은 것).

## 파일

| 파일 | 무엇 |
|---|---|
| `summary.sql` | 한 줄 요약 — 그 순간 `active / lock_waiters / on_xid / on_tuple` 개수 |
| `detail.sql` | 백엔드별 상세 — `pid / state / wait_event / blocked_by / query` |
| `sample.ps1` | 위 쿼리를 부하 동안 반복 실행해 `dump-*.txt` 에 표본을 쌓는 폴러 |

## 실행 순서 (통합 bat)

1. **터미널 A** — 앱을 로그와 함께 (`dev/loadtest/README.md` 참고):
   ```powershell
   cd dev/src/IndieFi.Web
   dotnet run --launch-profile http 2>&1 | Tee-Object -FilePath ..\..\loadtest\app.log
   ```
   `Now listening on: http://localhost:5258` 확인.
2. **저장소 루트에서** 한 방에:
   ```
   diagnose.bat
   ```
   이게 summary·detail 폴러를 각각 새 창으로 띄우고, 이어서 부하(`loadtest.bat`)를 돌린다.
   인자 없이 실행하면 4-a와 같은 조건(`--users 100 --late-users 50 --keys 30 --opens-in 60`)을 쓴다.
   다르게 재려면 인자를 그대로 넘긴다: `diagnose.bat --users 200 --keys 50 ...`
3. k6가 끝나면 **두 폴러 창을 닫는다**(또는 `Ctrl+C`). 그러면 덤프가 확정된다.

> 폴러만 따로 돌리고 싶으면 (`pwsh` 아님, `powershell`):
> ```
> powershell -NoProfile -ExecutionPolicy Bypass -File dev\loadtest\diagnose\sample.ps1
> powershell -NoProfile -ExecutionPolicy Bypass -File dev\loadtest\diagnose\sample.ps1 -Query detail
> ```

## 무엇을 볼까 (기대 증거)

`dump-summary.txt` 에서 오픈 순간 `lock_waiters` 와 `on_xid` 가 치솟는 구간:

```
ts            active  lock_waiters  on_xid  on_tuple
10:00:00.100    45        40          40       0
10:00:00.350    38        33          33       0
```

`dump-detail.txt` 의 같은 순간: 여러 백엔드가 `wevent=transactionid`, `blocked_by` 가
전부 **같은 리더 pid** → "다들 같은 유니크 인덱스 항목 앞에 줄 섬"의 사진.

## 메모

- `sample.ps1` 은 표본마다 `docker exec psql` 를 한 번씩 부른다 → 표본 간격은 대략 docker exec
  오버헤드(윈도우에서 0.2~0.4초)만큼. 버스트가 순간이라 첫 시도에 못 잡으면 다시 잰다.
- `dump-*.txt` 는 매 실행마다 덮어써지는 임시 산출물이다(커밋 대상 아님).

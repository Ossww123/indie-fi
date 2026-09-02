# 장애 후 전체 수렴과 복구시간 보고

## snapshot 순서

각 시나리오는 같은 `RUN_ID`, 장애 시작 UTC와 evidence directory를 사용한다.

```bash
RUN_ID=<run-id>
SCENARIO=<scenario>
START_UTC=$(date -u +%Y-%m-%dT%H:%M:%SZ)
EVIDENCE="$HOME/indiefi-validation/runs/$RUN_ID/failures/$SCENARIO"
SNAPSHOT=/usr/local/lib/indiefi/failure-drills/capture-failure-snapshot.sh

"$SNAPSHOT" "$EVIDENCE" before "$START_UTC" [drop-id]
# 별도 창에서 run-controlled-failure.sh와 사용자 흐름 실행
"$SNAPSHOT" "$EVIDENCE" fault "$START_UTC" [drop-id]
"$SNAPSHOT" "$EVIDENCE" recovered "$START_UTC" [drop-id]
# 새 요청을 멈추고 시나리오별 5~10분 또는 media lease 경계까지 기다린다.
"$SNAPSHOT" "$EVIDENCE" cooldown "$START_UTC" [drop-id]
```

`fault` snapshot에서 DB·Redis·RabbitMQ가 의도적으로 꺼져 있으면 해당 감사 파일 옆에
`*.unavailable.tsv`가 생긴다. 이는 capture 실패를 숨기지 않으면서 나머지 증거 수집을
계속하기 위한 표시다. `recovered`와 `cooldown`에는 unavailable이 없어야 한다.

Grafana의 경보·dashboard와 대표 trace는 로컬 관측 저장소에서 같은 UTC 구간으로 export해
해당 evidence directory의 `observability/`에 넣는다. EC2 snapshot은 로컬 Grafana 저장소를
직접 수정하거나 접근하지 않는다.

## 최종 수렴 게이트

- Web·Worker active, `ExecMainStatus=0`, 기대하지 않은 restart 반복 없음
- PostgreSQL·Redis·RabbitMQ running·healthy, failure drill 임시 drop-in 0개
- Nginx HTTPS readiness 200
- authoritative audit의 invariant 위반 0
- RabbitMQ ready·unacknowledged와 messaging Outbox가 before 기준선으로 복귀
- authentication email·notification due/failed와 media due·lease가 terminal 상태로 수렴
- Redis 시나리오는 AOF 정상과 지정 drop 집계·PostgreSQL key grant 대조 완료
- warning 로그의 원인이 설명되고 장애 종료 뒤 새로운 동일 warning이 반복되지 않음

하나라도 충족하지 않으면 단순히 서비스를 다시 restart하지 않는다. `ValidFailure` 또는
`SafetyStop`으로 판정하고 다음 시나리오를 시작하지 않는다.

## 로컬 보고서 생성

EC2 evidence를 로컬 `ops/validation/runs/<run-id>/failures/<scenario>`로 반출한 뒤 실행한다.

```powershell
./ops/failure-drills/New-FailureReport.ps1 `
  -RunId '<run-id>' `
  -Scenario 'redis-outage' `
  -Verdict 'Passed' `
  -InjectionStartedAtUtc '2026-09-01T01:00:00Z' `
  -FirstDetectedAtUtc '2026-09-01T01:00:16Z' `
  -TechnicalRecoveredAtUtc '2026-09-01T01:01:08Z' `
  -UserRecoveredAtUtc '2026-09-01T01:01:12Z' `
  -ConvergedAtUtc '2026-09-01T01:02:03Z' `
  -RunDirectory '<local-evidence-directory>' `
  -UserImpact 'Waiting-room entry was unavailable for the bounded fault window.' `
  -AutomaticRecovery 'Container restarted by the bounded drill tool','AOF replayed.'
```

보고서는 감지시간, 기술 RTO, 사용자 RTO와 최종 수렴 시간을 초 단위로 계산하고 모든
증거 파일의 상대 경로·SHA-256을 기록한다. user impact와 복구 설명에는 사용자 식별자,
이메일, 게임 키, token, cookie, endpoint 자격 증명을 쓰지 않는다.

수동 브라우저 확인에 사람의 대화 지연이 섞여 정확한 사용자 복구 시각을 측정하지 못한
run은 `-UserRecoveredAtUtc`를 생략한다. 보고서는 `userRecoveredAtUtc`와 사용자 RTO를
`null`로 기록하며 기술 RTO나 최종 수렴 시각을 대신 넣어 측정한 것처럼 만들지 않는다.

같은 UTC 구간의 Prometheus 경보·핵심 지표와 Tempo trace index는 다음처럼 반출한다.

```powershell
./ops/failure-drills/Export-FailureObservability.ps1 `
  -RunDirectory '<local-evidence-directory>' `
  -StartAtUtc '2026-09-01T01:00:00Z' `
  -EndAtUtc '2026-09-01T01:05:00Z'
```

trace 전체 span이나 로그 본문 대신 trace id·정규화된 root 이름과 집계 지표만 저장한다.
빈 metric도 `seriesCount=0`으로 남겨 관측 부재를 성공 값 0으로 오해하지 않는다.

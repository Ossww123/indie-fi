# 피드백 품질·리뷰어 평판 운영 가이드

- 적용 범위: 피드백 품질 평가, 리뷰어 평판 투영, 공개 리뷰어 등급
- 최종 권위: 완료·만료 리뷰 작업과 활성 품질 평가는 PostgreSQL 원본 테이블
- 파생 데이터: `reviewer_reputations`의 집계값과 애플리케이션에서 계산하는 등급

개별 평가 점수·사유, 신뢰도 원점수, 품질 점수, 완료 안정성 점수와 만료 수는 공개 정보가 아니다. 메트릭 태그나 일반 운영 기록에 리뷰어·사용자·피드백 식별자를 넣지 않는다. 특정 계정 조사가 필요하면 접근이 통제되는 incident 기록에 최소한으로 남긴다.

## 1. 동작과 복구 원칙

- 리뷰 작업의 생성·삭제·상태 전이와 평가의 생성·삭제·점수 변경은 PostgreSQL 트리거가 영향받은 리뷰어를 즉시 다시 집계한다.
- `reviewer_reputations`는 원본이 아니라 다시 만들 수 있는 투영이다. 불일치가 생겨도 배정·제출·평가·크레딧 원장을 투영값에 맞춰 수정하지 않는다.
- 단건·전체 재계산은 `refresh_reviewer_reputation(bigint)`을 사용하며 같은 리뷰어의 동시 변경은 transaction advisory lock으로 직렬화된다.
- 같은 원본을 반복 재계산하면 revision과 수정 시각이 변하지 않는다. 재계산으로 revision이 증가하면 해당 투영이 실제로 복구된 것이다.
- 등급은 저장하지 않고 집계값에서 계산한다. 등급을 직접 입력하거나 임의 숫자로 덮어쓰는 운영 기능은 없다.

## 2. 배포 전 확인

1. PostgreSQL 백업과 최신 migration 적용 결과를 확인한다.
2. 함수·트리거와 애플리케이션 역할의 실행 권한을 아래 조회로 확인한다.
3. Web 배포 뒤 평가 등록·수정 한 건과 공개 프로필의 완료 리뷰 수·등급 표시를 승인된 테스트 계정으로 확인한다.
4. 메트릭 수집기가 `IndieFi.Feedback` meter를 구독하는지 확인한다. 실제 OpenTelemetry exporter 연결과 장기 보존은 로드맵 12단계에서 확정한다.
5. 평가 수정 전후에 크레딧 잔액·원장 건수와 기존 리뷰 배정 순서가 변하지 않는지 자동 회귀 결과를 확인한다.

```sql
SELECT has_function_privilege(
           'indiefi_app',
           'refresh_reviewer_reputation(bigint)',
           'EXECUTE') AS app_can_refresh;

SELECT tgname
  FROM pg_trigger
 WHERE tgrelid IN (
           'review_assignments'::regclass,
           'feedback_ratings'::regclass)
   AND tgname LIKE '%refresh_reputation%'
   AND NOT tgisinternal
 ORDER BY tgname;
```

`app_can_refresh`는 `true`, 트리거는 다음 4개여야 한다.

- `review_assignments_refresh_reputation_ins_del`
- `review_assignments_refresh_reputation_update`
- `feedback_ratings_refresh_reputation_ins_del`
- `feedback_ratings_refresh_reputation_update`

## 3. 메트릭과 구조화 로그

| 메트릭 | 태그 | 의미와 초기 경보 출발점 |
|---|---|---|
| `indiefi.feedback.reputation.recalculations` | `operation=single|full`, `outcome=completed|failed|cancelled` | failed 1건부터 DB·함수·권한 확인, full cancelled는 작업자 중단 여부 확인 |
| `indiefi.feedback.reputation.reviewers` | `operation=single|full`, `result=repaired|unchanged` | 정기·수동 full에서 repaired가 1 이상이면 트리거 누락·직접 수정 여부 조사 |
| `indiefi.feedback.reputation.duration` | `operation`, `outcome` | 기준선보다 지속 상승하면 DB 잠금·후보 수·쿼리 지연 확인 |

메트릭에는 리뷰어 ID, 사용자 ID, 피드백 ID, 평가 점수와 등급을 태그로 추가하지 않는다. exporter가 아직 연결되지 않은 환경에서는 제한된 진단 시간 동안 다음처럼 meter를 직접 관찰할 수 있다.

```powershell
dotnet-counters monitor --process-id <web-pid> --counters IndieFi.Feedback
```

관련 구조화 로그는 다음과 같다.

- `ReviewerReputation recalculated outcome={Outcome} reviewer={ReviewerId} revision={Revision}`
- `ReviewerReputation recalculation failed reviewer={ReviewerId}`
- `ReviewerReputation full recalculation processed={ProcessedCount} repaired={RepairedCount}`
- `ReviewerReputation full recalculation failed processed={ProcessedCount} repaired={RepairedCount}`

단건 로그의 리뷰어 ID는 장애 추적용 구조화 필드다. 일반 dashboard의 label로 승격하거나 외부 공개 로그로 전달하지 않는다.

## 4. 개인정보 없는 불일치 탐지

아래 조회는 원본에서 기대 집계를 다시 계산하되 리뷰어별 값을 출력하지 않고 불일치한 투영의 총수만 반환한다.

```sql
WITH candidates AS (
    SELECT reviewer_id FROM review_assignments
    UNION
    SELECT reviewer_id FROM feedback_ratings
    UNION
    SELECT reviewer_id FROM reviewer_reputations
), expected AS (
    SELECT candidate.reviewer_id,
           (
               SELECT count(*)::integer
                 FROM review_assignments AS assignment
                WHERE assignment.reviewer_id = candidate.reviewer_id
                  AND assignment.status = 'Completed'
           ) AS completed_review_count,
           (
               SELECT count(*)::integer
                 FROM review_assignments AS assignment
                WHERE assignment.reviewer_id = candidate.reviewer_id
                  AND assignment.status = 'Expired'
           ) AS expired_assignment_count,
           (
               SELECT count(*)::integer
                 FROM feedback_ratings AS rating
                WHERE rating.reviewer_id = candidate.reviewer_id
           ) AS rating_count,
           (
               SELECT coalesce(sum(rating.score), 0)::integer
                 FROM feedback_ratings AS rating
                WHERE rating.reviewer_id = candidate.reviewer_id
           ) AS rating_score_sum
      FROM candidates AS candidate
)
SELECT count(*) AS mismatched_reviewer_count
  FROM expected
  LEFT JOIN reviewer_reputations AS reputation
    ON reputation.reviewer_id = expected.reviewer_id
 WHERE reputation.reviewer_id IS NULL
    OR ROW(
           reputation.completed_review_count,
           reputation.expired_assignment_count,
           reputation.rating_count,
           reputation.rating_score_sum)
       IS DISTINCT FROM ROW(
           expected.completed_review_count,
           expected.expired_assignment_count,
           expected.rating_count,
           expected.rating_score_sum);
```

정상 결과는 `0`이다. 0이 아니면 영향 시간, 최근 migration·권한·트리거 변경과 failed counter를 먼저 보존한 뒤 재계산한다. 개별 평가 사유나 사용자 이메일을 진단 쿼리에 추가하지 않는다.

## 5. 재계산 복구

### 단건 복구

대상이 이미 승인된 incident 안에서 확정된 경우 애플리케이션의 `ReviewerReputationService.RecalculateAsync`를 우선 사용한다. DB 비상 경로에서는 운영 계정으로 다음 함수를 호출한다.

```sql
SET lock_timeout = '5s';
SELECT refresh_reviewer_reputation(<reviewer-id>::bigint);
```

함수는 멱등이다. lock timeout이면 원본을 수정하지 말고 활성 평가·상태 변경이 끝난 뒤 같은 호출을 다시 실행한다.

### 전체 복구

애플리케이션의 `ReviewerReputationService.RecalculateAllAsync`는 후보를 리뷰어 ID 순서로 처리하고 각 호출을 짧은 트랜잭션으로 끝낸다. DB 비상 경로에서 `psql`을 사용할 때도 전체 후보를 하나의 `BEGIN`이나 `DO` 블록으로 묶지 않는다. transaction advisory lock이 작업 끝까지 누적되어 정상 평가·상태 변경을 오래 막을 수 있기 때문이다.

다음 `psql` 명령은 `\gexec`으로 리뷰어마다 별도 autocommit 문장을 실행한다.

```sql
\set ON_ERROR_STOP on
SET lock_timeout = '5s';

SELECT format(
           'SELECT refresh_reviewer_reputation(%L::bigint);',
           reviewer_id)
  FROM (
        SELECT reviewer_id FROM review_assignments
        UNION
        SELECT reviewer_id FROM feedback_ratings
        UNION
        SELECT reviewer_id FROM reviewer_reputations
       ) AS candidates
 ORDER BY reviewer_id
\gexec
```

연결 중단이나 lock timeout으로 멈추면 원인을 해소하고 전체 명령을 다시 실행한다. 이미 맞는 행은 revision을 올리지 않는다. DB 비상 경로는 애플리케이션 meter를 내보내지 않으므로 실행 시각, candidate 수, 성공·실패와 사후 불일치 수를 접근 제한된 운영 기록에 별도로 남긴다.

## 6. 복구 후 확인

1. 4절의 불일치 조회가 `0`인지 확인한다.
2. 애플리케이션 경로라면 full `completed`와 `repaired|unchanged` 합계가 processed 수와 같은지 확인한다.
3. repaired가 있었다면 함수·트리거·권한과 비승인 직접 수정 이력을 조사한다. 재계산 성공만으로 원인을 닫지 않는다.
4. 표본 미달 계정은 `등급 산정 전`, 표본 충족 계정은 계산된 등급으로 공개 프로필에 표시되는지 확인한다.
5. 영향 계정의 크레딧 원장과 기존 배정·제출 행이 바뀌지 않았는지 확인한다.

다음 작업은 금지한다.

- `reviewer_reputations`의 집계값·revision·수정 시각을 수동으로 맞추기
- 평판 투영에 맞추려고 리뷰 작업, 제출, 평가나 크레딧 원장을 생성·삭제하기
- 등급 또는 신뢰도 원점수를 별도 컬럼에 임의 저장하기
- 불일치 조사 결과에 이메일, 평가 사유나 개별 점수를 일반 로그로 남기기

## 7. 장애별 대응

| 장애 | 대응 |
|---|---|
| 함수·트리거 누락 | migration 적용 상태를 복구한 뒤 전체 재계산하고 불일치 0 확인 |
| app 역할 실행 권한 누락 | migration에 정의된 최소 `EXECUTE` 권한만 복구하고 전체 재계산 |
| DB 중단·lock timeout | 원본 쓰기를 추측해 보정하지 않고 DB·경합 복구 뒤 멱등 재실행 |
| full 작업 일부 실패 | processed·repaired 로그를 보존하고 전체 작업을 처음부터 재실행 |
| repaired 지속 발생 | 트리거 비활성화, 비승인 직접 수정, 향후 운영상 무효 필터 연결 오류 조사 |
| 잘못된 등급 배포 | 산식 코드를 롤백·수정한 뒤 저장 집계는 유지하고 공개 프로필을 재확인 |

숨김 또는 운영상 무효인 평가의 제외·복구 상태는 관리자 5단계에서 추가한다. 그때 `refresh_reviewer_reputation`의 평가 필터와 이 runbook의 기대 집계 쿼리를 반드시 같은 조건으로 변경한다.

## 8. 최종 자동 검증

`dev` 디렉터리에서 실행한다.

```powershell
dotnet format IndieFi.slnx --no-restore --include <changed-csharp-files> --verify-no-changes
dotnet build IndieFi.slnx --no-restore
dotnet test tests/IndieFi.Web.Tests/IndieFi.Web.Tests.csproj --no-build --no-restore
dotnet publish src/IndieFi.Web/IndieFi.Web.csproj -c Release -r linux-x64 --self-contained false
dotnet publish src/IndieFi.Worker/IndieFi.Worker.csproj -c Release -r linux-x64 --self-contained false
```

릴리스 증거에는 시각, commit, 전체 테스트·실패·건너뜀 수, PostgreSQL 테스트 이미지, Web·Worker artifact 식별자와 불일치 조회 결과를 남긴다. 장기 메트릭 보존, 정기 대사 주기와 경보 임계값은 로드맵 13단계의 실제 운영 기준선에서 확정한다.

# 미디어·게임 발견 운영 가이드

- 적용 범위: 게임·프로필 이미지 직접 업로드, R2 비공개 객체, 미디어 Worker, 공개 검색·목록·상세
- 최종 권위: 소유권·상태·현재 공개 연결·재시도 시각은 PostgreSQL
- 전달 방식: PostgreSQL Transactional Outbox와 RabbitMQ 적어도 한 번 전달, 결정적 사건·객체 키와 처리 임대로 멱등 수렴
- 객체 저장소: 운영 Cloudflare R2, 개발 MinIO

서명 URL, 쿼리 서명, 저장소 비밀값, 원본 파일명과 이미지 내용은 로그·메트릭·일반 장애 기록에 남기지 않는다. 미디어 ID·개정·고정 오류 코드는 필요한 Worker 진단에만 사용하고 메트릭 태그로 만들지 않는다.

## 1. 구성요소와 실행 역할

| 구성요소 | 역할 | 운영 필수 조건 |
|---|---|---|
| PostgreSQL | 미디어 상태, 업로드 시도, Outbox·Inbox와 검색 권위 | 모든 Web·Worker가 같은 DB와 migration 사용 |
| Redis | Web 전체의 업로드 URL 발급 제한 | 모든 Web이 같은 연결과 환경별 `Media:UploadRateLimit:KeyPrefix` 사용 |
| RabbitMQ | 처리·삭제 요청의 durable queue | Web·Worker가 같은 virtual host, 자격 증명과 `EndpointPrefix` 사용 |
| R2 | staging·고정 원본·WebP 파생본을 담는 비공개 버킷 | 공개 접근 차단, 앱 전용 자격 증명, `staging/` 2일 lifecycle |
| IndieFi.Web | 예약·URL 발급·완료, 공개 권한 판정과 검색 | 객체 저장소 활성화, `Media:Worker:Enabled=false` |
| IndieFi.Worker | 메시지 소비, 이미지 처리, 복구·삭제·정리·관측 | 객체 저장소와 `Media:Worker:Enabled=true` |

Web에 `Media:Worker:Enabled=true`를 넣거나 Worker에서 객체 저장소만 켜면 애플리케이션은 시작을 거부한다. 알림만 처리하는 Worker는 미디어 Worker와 객체 저장소를 모두 끈 상태로 실행할 수 있다.

## 2. 운영 설정과 비밀값

두 프로그램의 R2 설정은 같은 버킷을 가리킨다. 아래 값은 환경 변수 표기 예이며 실제 비밀값은 배포 비밀 관리 수단으로 주입한다.

```text
Media__ObjectStorage__Enabled=true
Media__ObjectStorage__ServiceUrl=https://<account-id>.r2.cloudflarestorage.com
Media__ObjectStorage__Region=auto
Media__ObjectStorage__BucketName=<private-bucket>
Media__ObjectStorage__AccessKeyId=<secret-reference>
Media__ObjectStorage__SecretAccessKey=<secret-reference>
Media__ObjectStorage__OperationTimeout=00:00:10
Media__ObjectStorage__ForcePathStyle=false
```

Web에만 다음 공유 제한 설정을 둔다.

```text
Media__Worker__Enabled=false
Media__UploadRateLimit__KeyPrefix=indiefi:<environment>:media-upload:v1
WaitingRoom__Redis__Enabled=true
WaitingRoom__Redis__ConnectionString=<secret-reference>
```

Worker에는 다음 설정을 둔다. 배치 크기는 1~500, 복구 주기는 1초~5분, 정리 주기는 10초~1시간, 관측 주기는 5초~10분 범위여야 한다.

```text
Media__Worker__Enabled=true
Media__Worker__RecoveryInterval=00:00:30
Media__Worker__RecoveryBatchSize=50
Media__Worker__CleanupInterval=00:01:00
Media__Worker__CleanupBatchSize=50
Media__Worker__ObservationInterval=00:00:30
```

R2 앱 자격 증명에는 해당 버킷의 상태 확인과 객체 읽기·쓰기·복사·삭제에 필요한 최소 권한만 부여한다. 버킷은 공개하지 않고, 브라우저 직접 PUT CORS는 실제 Web origin과 `PUT`, 필요한 요청 헤더만 허용한다. 개발·검증·운영은 서로 다른 버킷과 자격 증명을 사용한다.

## 3. R2 객체와 lifecycle

| prefix | 의미 | 자동 lifecycle |
|---|---|---|
| `staging/` | 15분 서명 PUT으로 받은 미확정 업로드 | **생성 2일 후 삭제** |
| `sources/` | ETag로 고정한 Worker 처리 원본 | lifecycle 금지, DB 정리 Worker가 삭제 |
| `derived/` | 공개 가능한 정규화 WebP 파생본 | lifecycle 금지, 교체·삭제 상태가 정리 |

R2 dashboard 또는 관리 중인 IaC에서 `staging/` prefix에만 2일 만료 규칙을 둔다. 이 규칙은 24시간 `UploadPending` 만료와 PostgreSQL 상태 전이를 대신하지 않는 이중 안전장치다. `sources/`나 `derived/`에 광범위한 만료 규칙을 적용하면 정상 공개 이미지가 사라질 수 있다.

운영 수명은 다음과 같다.

- 업로드 서명 URL: 15분
- 미완료 `UploadPending`: 24시간
- 공개 다운로드 서명 URL: 최대 5분
- 처리 임대: 10분
- 처리 재시도: 최초 포함 최대 6회, 1분·5분·30분·2시간·6시간 간격
- `Failed`·`Expired`·`Deleted` tombstone: 객체 삭제 확인 후 30일

## 4. 배포 순서

1. PostgreSQL 백업과 migration 적용 결과를 확인한다.
2. Redis, RabbitMQ와 R2 비공개 버킷·앱 자격 증명·CORS·staging lifecycle을 준비한다.
3. Web·Worker가 같은 PostgreSQL, RabbitMQ 환경과 R2 버킷을 가리키는지 확인한다.
4. Web을 배포하고 `/health/ready`가 HTTP 200인지 확인한다. 미디어 저장소가 켜졌다면 이 검사는 R2 `HeadBucket`까지 포함한다.
5. 업로드 예약 한 건이 Redis 제한을 통과하고 15분 URL을 반환하는지 승인된 테스트 계정으로 확인한다. 서명 URL 자체는 배포 기록에 복사하지 않는다.
6. Worker를 한 인스턴스씩 배포하고 아래 명령으로 미디어 queue에 consumer가 1개 이상 연결됐는지 확인한다.
7. 테스트 이미지를 완료한 뒤 `UploadPending → ProcessingPending → Processing → Ready`와 공개 파생본 표시를 확인한다.
8. `recovery.due`, `expired_leases`, `cleanup.backlog`가 정상 기준으로 돌아오는지 확인한 뒤 기존 Worker를 제거한다.

```bash
curl --fail --silent https://<web-origin>/health/ready
docker exec indiefi-rabbitmq rabbitmq-diagnostics -q check_running
docker exec indiefi-rabbitmq rabbitmqctl list_queues name durable consumers messages_ready messages_unacknowledged
```

Web은 RabbitMQ 일시 중단 중에도 미디어 완료 의도를 PostgreSQL Outbox에 보존한다. Worker에는 HTTP readiness endpoint가 없으므로 프로세스 상태, RabbitMQ consumer, 아래 미디어 지표와 R2 오류를 함께 본다.

## 5. 메트릭과 초기 경보 기준

수집기는 `IndieFi.Media` meter를 구독한다. exporter가 아직 연결되지 않은 환경에서는 제한된 진단 시간 동안 `dotnet-counters`로 같은 meter를 관찰한다.

| 메트릭 | 의미 | 초기 경보 출발점 |
|---|---|---|
| `indiefi.media.processing.attempts` | completed·transient_failure·permanent_failure 처리 시도 | transient 급증 또는 permanent 1건부터 원인 확인 |
| `indiefi.media.processing.duration` | 처리 시도 시간(ms) | p95가 30초 이상 5분 지속되면 경고 |
| `indiefi.media.processing.pending` | 전체 처리 대기 | 처리량 대비 계속 증가하면 경고 |
| `indiefi.media.processing.active_leases` | 유효 처리 임대 | Worker 수·처리량과 함께 해석 |
| `indiefi.media.processing.expired_leases` | 만료됐으나 아직 회수되지 않은 임대 | 관측 2회 연속 1 이상이면 경고 |
| `indiefi.media.recovery.items` | 복구 배치가 본 행 | 급증하면 Worker·저장소 장애 확인 |
| `indiefi.media.recovery.due` | 지금 복구할 행 | 두 복구 주기 뒤에도 감소하지 않으면 경고 |
| `indiefi.media.recovery.oldest_due_age` | 가장 오래된 due 지연(초) | `max(60초, RecoveryInterval 2회)` 초과 시 경고 |
| `indiefi.media.cleanup.items` | expired·ready_source_deleted·deletion_completed·tombstone_removed·storage_failure | storage_failure 1건부터 저장소 확인 |
| `indiefi.media.cleanup.backlog` | 정리 대상 행 | 두 정리 주기 뒤에도 감소하지 않으면 경고 |
| `indiefi.media.failed` | 보존 중인 실패 tombstone | 단건보다 증가 추세와 permanent_failure를 함께 확인 |
| `indiefi.media.operations.observation_failures` | 운영 스냅숏 수집 실패 | 1건부터 DB 연결 확인 |

메트릭 태그는 `outcome`처럼 제한된 값만 사용한다. 사용자·게임·미디어·업로드 시도·객체 키를 태그로 추가하지 않는다.

## 6. 안전한 상태 조회

아래 조회는 객체 키, 사용자 정보와 Outbox payload를 읽지 않는다.

```sql
SELECT status, count(*) AS media_count
  FROM media_assets
 GROUP BY status
 ORDER BY status;

SELECT count(*) FILTER (
           WHERE status = 'ProcessingPending'
             AND coalesce(next_processing_attempt_at, now()) <= now()) AS recovery_due,
       count(*) FILTER (
           WHERE status = 'Processing'
             AND processing_lease_expires_at <= now()) AS expired_leases,
       min(next_processing_attempt_at) FILTER (
           WHERE status = 'ProcessingPending'
             AND next_processing_attempt_at <= now()) AS oldest_due_at
  FROM media_assets;

SELECT count(*) AS cleanup_backlog
  FROM media_assets
 WHERE (status = 'UploadPending' AND upload_expires_at <= now())
    OR status = 'DeletePending'
    OR (status = 'Ready' AND source_deleted_at IS NULL)
    OR (status IN ('Failed', 'Expired', 'Deleted')
        AND (deletion_completed_at IS NULL OR tombstone_expires_at <= now()));
```

일반 진단에서 업로드 시도 ID, 미디어 ID, 객체 키, ETag, Outbox `body`·`headers`·`properties`를 출력하지 않는다.

## 7. 장애별 복구

### R2 지연·중단·권한 오류

1. Web `/health/ready`, Worker의 transient failure와 `storage_failure`, R2 공급자 상태를 확인한다.
2. endpoint·버킷·TLS·자격 증명·최소 권한 변경 이력을 확인한다. 로그에 비밀값이나 서명 URL을 붙이지 않는다.
3. R2 접근을 복구한다. 처리 중인 임대를 DB에서 임의 해제하거나 같은 이미지를 다시 업로드하지 않는다.
4. 백오프가 due가 되면 복구 Worker가 같은 미디어 ID·개정을 재발행하게 둔다.
5. `recovery.due`, oldest due와 cleanup backlog가 감소하고 최종 `Ready` 또는 안전한 `Failed`로 수렴하는지 확인한다.

### RabbitMQ 중단

1. Web 요청을 임의 롤백하거나 Outbox 행을 삭제하지 않는다.
2. RabbitMQ 디스크·메모리 alarm, virtual host와 자격 증명을 복구한다.
3. 미디어 처리·삭제 queue consumer가 돌아오고 `messages_ready`가 감소하는지 확인한다.
4. 같은 사건의 중복 전달은 Inbox, 현재 개정 검사와 처리 임대가 흡수하게 둔다. 새 Message-ID로 JSON을 수동 발행하지 않는다.

### Worker 중단·교체

- 같은 환경의 Worker는 같은 durable queue에서 competing consumer로 동작한다.
- 한 인스턴스씩 교체하고 새 consumer가 연결된 뒤 기존 인스턴스를 내린다.
- 처리 중 프로세스가 죽으면 10분 임대 만료 후 다른 Worker가 인계한다. DB에서 상태·임대·시도 횟수를 직접 수정하지 않는다.
- 모든 Worker가 내려가도 queue와 PostgreSQL due 상태를 보존하고, 복구 뒤 최종 파생본은 결정적 키 하나로 수렴한다.

### Redis 중단

- 새 업로드·재발급 URL 발급 제한은 우회하지 않고 실패 닫힘으로 동작한다.
- 기존 R2 객체와 PostgreSQL 완료·Worker 처리는 Redis를 권위로 사용하지 않는다.
- Redis를 복구한 뒤 같은 key prefix를 유지한다. 제한 우회를 위해 prefix를 바꾸거나 Redis 키를 일괄 삭제하지 않는다.

### 정리 backlog

- 객체 삭제 실패를 먼저 복구하고 정리 Worker의 제한 배치가 반복 수렴하게 둔다.
- 여러 정리 Worker는 `FOR UPDATE SKIP LOCKED`로 후보를 나누므로 같은 행을 수동 분배하지 않는다.
- R2 lifecycle이 staging 객체를 먼저 지운 경우에도 없는 객체 삭제는 성공이며 DB는 `Expired` tombstone으로 전환된다.
- `sources/`·`derived/` 객체를 prefix 전체 삭제하거나 DB tombstone을 먼저 삭제하지 않는다.

## 8. 공개 URL과 캐시 확인

- 공개 URL은 PostgreSQL에서 현재 연결된 `Ready` 파생본에만 발급한다.
- 홈 검색, 게임 목록·상세·미디어 관리와 프로필 화면은 서명 URL을 포함할 때 `no-store`, `no-cache`, `Referrer-Policy: no-referrer`를 유지한다.
- 교체·삭제 뒤 새 URL을 발급하지 않는다. 이미 발급된 URL은 최대 5분 뒤 만료된다.
- staging/source URL이나 `UploadPending`·`Processing*`·`Failed`·`DeletePending` 식별자가 공개 HTML에 나타나면 배포를 중단한다.

## 9. 최종 자동 검증

`dev` 디렉터리에서 실행한다.

```powershell
# <changed-csharp-files>에는 이번 변경의 C# 파일만 전달한다.
dotnet format IndieFi.slnx --no-restore --include <changed-csharp-files> --verify-no-changes
dotnet build IndieFi.slnx --no-restore
dotnet test IndieFi.slnx --no-build --no-restore
dotnet publish src/IndieFi.Web/IndieFi.Web.csproj -c Release -r linux-x64 --self-contained false
dotnet publish src/IndieFi.Worker/IndieFi.Worker.csproj -c Release -r linux-x64 --self-contained false
```

운영 경계만 다시 확인할 때는 다음 필터를 사용한다.

```powershell
dotnet test tests/IndieFi.Web.Tests/IndieFi.Web.Tests.csproj --no-build --filter "FullyQualifiedName~MediaModuleRegistrationTests|FullyQualifiedName~MediaMultiWebIntegrationTests|FullyQualifiedName~MediaWorkerIntegrationTests|FullyQualifiedName~MediaCleanupServiceTests|FullyQualifiedName~MediaHttpIntegrationTests|FullyQualifiedName~PublicGameDiscoveryIntegrationTests"
```

릴리스 증거에는 시각, commit, 전체 테스트·실패·건너뜀 수, PostgreSQL·Redis·RabbitMQ·MinIO 테스트 이미지 버전과 Web·Worker 배포 artifact 식별자를 남긴다. R2의 최종 백업·보존·복구 훈련과 운영 용량 기준은 로드맵 13단계에서 확정한다.

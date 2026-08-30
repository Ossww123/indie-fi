# Encrypted recovery bundle contract

이 계약은 EC2에서 수집한 PostgreSQL·R2·보호 키를 로컬 PC에 어떤 형식으로
남기고 언제 폐기할지 정의한다. 실제 bundle 생성·검증 명령은 같은 디렉터리의
도구가 이 계약을 구현한다.

## 1. 암호화 도구와 key 분리

- archive 암호화는 `age` 1.3.2 이상의 native X25519 recipient를 사용한다.
- 백업 스크립트에는 공개 recipient(`age1...`)만 전달한다.
- `AGE-SECRET-KEY-...` identity는 저장소, `runs/`, EC2와 cloud drive 동기화
  폴더 밖의 별도 매체에 보관한다.
- identity 파일 경로와 recipient는 서로 다른 값이며, identity 원문은 shell
  인자·환경 변수·manifest·로그에 넣지 않는다.
- passphrase mode는 자동화 중 passphrase 전달 경계가 불명확해질 수 있으므로
  기본 경로로 사용하지 않는다.

공개 recipient가 유출되어도 복호화할 수는 없지만 임의의 새 암호문을 만들
수는 있다. 따라서 복원 전에 별도로 기록한 archive SHA-256과 내부 manifest를
함께 확인한다.

### 최초 1회 key 생성

```powershell
age-keygen -o D:\indiefi-recovery-secret\identity.txt
age-keygen -y D:\indiefi-recovery-secret\identity.txt `
  | Set-Content -NoNewline D:\indiefi-recovery-recipient.txt
```

`identity.txt`는 backup directory와 다른 매체에도 한 부 보관하고 접근 권한을
현재 사용자로 제한한다. 실제 복원 훈련 전에 identity로 test bundle을 복호화해
사본이 읽히는지 확인한다.

## 2. run directory

기본 위치는 저장소 밖의 사용자가 지정한 절대 경로다.

```text
<backup-root>/
└── recovery-20260830T120000Z-002fc171/
    ├── bundle.tar.age
    ├── evidence.json
    ├── bundle.tar.age.sha256
    └── verification.json
```

- run id: `recovery-<UTC yyyyMMddTHHmmssZ>-<commit 8자>`
- `bundle.tar.age`: 인증 암호화된 실제 복구 자료
- `evidence.json`: 비밀을 포함하지 않는 수집 시각·버전·개수·암호문 hash
- `bundle.tar.age.sha256`: 전송 손상을 빠르게 확인하는 archive hash
- `verification.json`: 언제 어떤 `age`·`tar`로 무결성·목록을 확인했는지 기록

run directory가 이미 있으면 덮어쓰지 않는다. 같은 백업을 다시 시도해도 새
run id를 발급해 실패 흔적과 성공 자료를 구분한다.

## 3. 암호문 내부 구조

```text
bundle/
├── manifest.json
├── postgresql/
│   ├── indiefi.dump
│   ├── dump-list.txt
│   └── row-counts.json
├── r2/
│   ├── inventory.json
│   └── objects/
│       ├── sources/...
│       └── derived/...
├── keys/
│   ├── account-protection.json
│   ├── data-protection.crt
│   └── data-protection.key
└── configuration/
    └── fingerprints.json
```

유효한 `staging/` 객체가 있을 때만 `r2/objects/staging/`을 추가한다. `manifest`
에는 상대 경로, byte 크기와 SHA-256을 기록하지만 이메일·게임 키 원문,
환경 변수 값, presigned URL과 보호 key 원문은 기록하지 않는다.

## 4. manifest 최소 항목

- format version과 run id
- UTC capture 시작·종료 시각
- Git commit과 release path
- PostgreSQL server·dump version, DB name과 dump format
- R2 endpoint host·bucket·객체 수·총 byte (access key 제외)
- 계정 보호 목적별 key id (key 원문 제외)
- Data Protection certificate fingerprint·유효기간
- 파일별 상대 경로·size·SHA-256
- 핵심 table 행 수와 media 참조 대조 결과
- 수집 중 Web·Worker 쓰기 통제와 queue 수렴 상태

예시는 `recovery-bundle-manifest.example.json`에 있다. 실제 manifest는 암호문
안에만 넣고 바깥 `evidence.json`에는 민감하지 않은 요약만 남긴다.

## 5. 평문 생명주기

1. local OS가 제공하는 임시 디렉터리를 run마다 새로 만든다.
2. EC2 dump는 EC2 파일로 만들지 않고 SSH stdout에서 임시 파일로 직접 받는다.
3. R2 객체와 보호 키를 같은 임시 디렉터리에 수집한다.
4. manifest와 hash를 만든 뒤 tar stream을 `age`로 암호화한다.
5. age 복호화·tar 목록 검사가 성공한 뒤 `verification.json`을 쓴다.
6. `finally` 구간에서 평문 dump·객체·키·tar를 제거한다.

스크립트가 중단되어도 `finally`에서 정리를 시도한다. 다만 SSD의 wear leveling,
filesystem snapshot과 OS page file 때문에 일반 파일 삭제는 물리적 secure erase를
보장하지 않는다. 따라서 임시 디렉터리를 BitLocker가 켜진 로컬 volume에 두고
backup PC 자체 접근을 통제한다.

## 6. 보존과 폐기

이 단기 포트폴리오 검증의 기본 정책은 다음과 같다.

- 성공 bundle: 최근 3개 또는 생성 후 14일 중 더 엄격한 조건으로 보존
- 실패·미검증 bundle: 원인 기록 뒤 당일 폐기
- 복원용 평문과 격리 Docker volume: 검증 증거 반출 직후 폐기
- 프로젝트 종료: 최종 검증 bundle 1개만 30일까지 선택 보존한 뒤 수동 폐기
- identity: bundle보다 오래 보관하되 프로젝트 종료 후 bundle 폐기를 확인하고 폐기

자동 정리는 `verification.json`이 성공인 run만 대상으로 하며 symlink/reparse
point를 따라가지 않는다. 어떤 디렉터리를 지울지 미리 출력하는 preview가
기본이고 명시적 확인 없이는 삭제하지 않는다.

## 7. 실패 처리

- 수집·암호화·검증 중 하나라도 실패하면 성공 evidence를 쓰지 않는다.
- 암호문이 이미 생성됐더라도 SHA-256·복호화·tar 목록 검증 전에는 EC2를
  중지하거나 원본을 변경하지 않는다.
- identity를 분실한 bundle은 복구 불가능으로 표시하고 새 정합 백업을 만든다.
- 평문 정리 실패 시 해당 경로를 즉시 표시하고 다음 단계로 진행하지 않는다.
- restore 도구는 run directory와 identity를 읽기만 하며 원본 bundle을 수정하지 않는다.

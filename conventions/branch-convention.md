# 브랜치 컨벤션

> 이 저장소의 브랜치 규칙. **브랜치를 만들거나 병합할 때만 참고**한다(평소 컨텍스트에 로드하지 않음).
> 브랜치 이름은 **영어**.

## 큰 틀 (3계층)

```
main            안정 · 릴리스 가능 상태만
 └─ dev          통합 브랜치 · 작업 브랜치가 모이는 곳
     ├─ feat/*    기능 작업
     ├─ fix/*     버그 수정 작업
     └─ ...       (아래 표)
```

- **`main`** — 항상 동작·릴리스 가능. 직접 커밋 금지. `dev`에서만 병합해 올린다.
- **`dev`** — 통합 브랜치. 작업 브랜치(`feat/*` 등)를 여기로 병합. 여기서 통합 테스트가 green이면 `main`으로 올릴 후보.
- **작업 브랜치** — `dev`에서 따서 `dev`로 되돌린다. 짧게 유지(오래 두면 충돌·드리프트).

## 작업 브랜치 접두어

커밋 `type`과 결을 맞춘다.

| 접두어 | 용도 |
|---|---|
| `feat/` | 기능 |
| `fix/` | 버그 수정 |
| `refactor/` | 구조 개선(동작 불변) |
| `perf/` | 성능 |
| `test/` | 테스트만 |
| `docs/` | 문서만(커밋되는 `conventions/` 등) |
| `chore/` | 설정·잡일 |

## 이름 규칙

```
<접두어><짧은-설명-kebab-case>
```

- 소문자 kebab-case, 영어, 짧게. **브랜치 이름은 그 기능이 뭔지 그대로** 적는다(내부 용어 금지 — `commit-convention.md` 참조).
  - ✅ `feat/signup`
  - ✅ `feat/schedule-key-drop`
  - ✅ `fix/claim-before-open-time`
  - ❌ `feat/core-b` / `feat/slice-2` / `feat/adr-0007`

## 브랜치 = 기능 하나, 커밋 = 단계 하나

**브랜치는 기능 단위로만 딴다.** 단계(화면 1개 + 엔드포인트 1개)마다 브랜치를 따면 의식이 너무 무거워진다.

```
feat/schedule-key-drop  ← 기능 하나 = 브랜치 하나
 ├─ feat: 키 배포 정보를 담을 DB 테이블 추가      ← 단계 = 커밋
 ├─ feat: 키 배포 등록 기능 추가
 └─ feat: 키 배포 등록 화면 추가
```

## 흐름 (한 번에 하나)

작업 브랜치는 원칙적으로 **한 번에 하나**만 연다.

```
git switch dev
git switch -c feat/schedule-key-drop    # dev에서 딴다
# ... 단계별로 작업 · 커밋(commit-convention.md) ...
git switch dev
git merge --no-ff feat/schedule-key-drop   # dev로 병합 (머지 커밋 유지)
# dev에서 테스트 green 확인 후, 적당한 시점에 dev → main
```

- 작업이 끝난 브랜치는 병합 후 삭제.

## 병합 방식 (확정) — `--no-ff` merge commit

**작업 브랜치 → `dev` 병합은 항상 `--no-ff`.** squash·rebase 안 씀.

- **브랜치 안**: 따라 읽을 수 있는 크기로 **단계별 커밋**(보통 2~4개).
- **`dev`로**: `--no-ff` 병합 → 머지 커밋이 **기능 경계**를 표시.

두 해상도로 히스토리를 읽을 수 있어서다:

```bash
git log --first-parent dev   # 기능 단위(큰 그림)
git log dev                  # 단계별(따라 읽기)
```

squash는 단계 커밋을 하나로 뭉개 "따라 읽기"를 없애고, rebase는 히스토리를 다시 써서 솔로 작업엔 이득 없이 위험만 는다.

## 규율
- `main`에 직접 커밋 금지.
- 작업 브랜치는 짧게. 하나 끝내고 다음.
- 브랜치 이름과 그 안 커밋들이 서로 어긋나지 않게 한다.

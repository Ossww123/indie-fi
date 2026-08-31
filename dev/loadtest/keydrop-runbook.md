# 대기실 오픈·키 수령 집중 부하

## 사용자 군

- `claimers`: 오픈 전에 로그인·대기실 등록을 끝내고 오픈 시각부터 admission을 polling해 한 번 claim한다.
- `latecomers`: 로그인만 미리 하고 오픈 뒤 정해진 구간에 대기실에 들어와 claim한다.
- `retries`: claimer처럼 들어와 첫 claim 직후 같은 계정으로 다시 claim해 `AlreadyClaimed` 멱등 경계를 확인한다.

로그인 GET·POST와 claimer/retry의 사전 Join은 `phase=preparation`으로 표시하고 성능 threshold에서
제외한다. 오픈 뒤 snapshot·late Join·claim만 `phase=measurement`로 측정한다. 준비 실패는 별도
counter가 0이어야 하므로 성능 집계에서 제외했다고 조용히 무시되지는 않는다.

`preparationSpreadSeconds`는 k6 실행 시각이 아니라 UTC 오픈 시각 직전 구간에 고정한다. 실행기를
일찍 시작해도 로그인·사전 Join이 오픈보다 지나치게 일찍 끝나 유휴 연결이 한꺼번에 재연결되지 않는다.

## 결과 판정

- fresh claim은 ASCII `data-claim-result="granted"` marker로 분류한다.
- redirect query의 `claim=soldout|conflict|notopen|admission|waitingclosed|waitingunavailable`를 분리한다.
- retry 두 번째 응답이 granted marker를 보여도 fresh 지급으로 더하지 않고 `already_claimed`로 기록한다.
- 대기 중 sold out·terminal에 도달해 claim을 보내지 못한 사용자는 `waiting_terminal`로 따로 집계한다.

k6 `claim_granted`는 HTTP에서 본 fresh 지급 수다. after audit의 grant 증가량과 남은 재고 감소량이
이 값과 같아야 하며 사용자·key 중복은 0이어야 한다.

## 실행

```powershell
k6 run `
  -e CONTRACT_FILE=../config/keydrop.json `
  -e CREDENTIALS_FILE=../config/credentials.json `
  -e SEED_MANIFEST_FILE=../results/<run-id>/seed-manifest.json `
  dev/loadtest/keydrop.js
```

실행 전 before audit와 RabbitMQ·Redis snapshot을 저장한다. contract와 seed의 UTC 오픈 시각을
확인하고 로컬 PC·EC2 시각 차이가 1초를 넘으면 시작하지 않는다. 실제 run에서는 k6 console과
summary를 저장하되 cookie·antiforgery token·응답의 키 원문은 출력하지 않는다.

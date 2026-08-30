# 단일 운영 구성 복귀

## 정상 종료

다중 구성 검증을 끝낸 즉시 같은 run id로 원복한다. 45분 timer를 기다리는 것은 정상 종료가
아니며, timer는 SSH 단절·실수에 대비한 마지막 안전장치다.

```bash
RUN_ID=<run-id>
SCALE=/usr/local/lib/indiefi/scale-validation
sudo "$SCALE/manage-scale-topology.sh" stop "$RUN_ID" "STOP-MULTI-$RUN_ID"
sudo "$SCALE/verify-single-topology.sh"
```

검증기는 원래 Web·Worker와 Nginx, 5000 port, 5101·5102 부재, 임시 marker·환경·drop-in·timer
제거, PostgreSQL·Redis·RabbitMQ health와 HTTPS readiness 200을 함께 확인한다. 성공 출력을
run evidence에 저장하고 별도로 마지막 runtime·DB·queue snapshot도 남긴다.

## 실패했을 때

- 검증 실패 상태에서 임시 파일을 손으로 지워 초록불로 만들지 않는다.
- `manage-scale-topology.sh status`, `systemctl status`, `nginx -t`와 해당 UTC journal부터 저장한다.
- 원본 unit 또는 5000 readiness가 실패하면 서비스를 임의로 enable하지 말고 기존 배포 runbook으로 복구한다.
- Nginx에 5101·5102가 남았으면 저장된 원본 config 존재와 emergency guard 실행 기록을 먼저 확인한다.
- 데이터 컨테이너가 unhealthy면 scale 문제가 아니라 데이터 서비스 장애로 분리해 13-4 절차를 따른다.

## 증명 범위와 한계

같은 호스트의 2×2 검증으로 증명하는 것은 프로세스 간 상태 공유, Nginx 분산, competing consumer와
단일 프로세스 제거 복구다. 다음은 증명하지 않는다.

- EC2 host·가용 영역 장애를 견디는 고가용성
- 서로 다른 서버 사이의 network partition과 배포 일관성
- 공유 CPU·메모리·disk·NIC를 벗어난 선형 성능 향상
- 별도 load balancer, health routing과 자동 확장

따라서 결과에는 “수평 확장 준비 경계를 확인했다”고 기록하고 “고가용성을 달성했다”거나
“Web을 두 배로 늘려 처리량이 두 배가 됐다”고 일반화하지 않는다.

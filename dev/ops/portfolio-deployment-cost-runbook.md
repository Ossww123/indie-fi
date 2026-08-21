# 포트폴리오 배포 비용 안전장치

## 1. 적용 범위와 원칙

이 문서는 서울 리전의 기존 x86_64 EC2를 잠시 `t3.medium`으로 확장해 운영·부하·복구 실험을 수행할 때 사용한다. 목표는 무비용을 보장하는 것이 아니라 **의도한 EC2 실행 시간과 기존 EBS 외의 과금 자원이 생기지 않게 확인하는 것**이다.

AWS Budget과 비용 알림은 사용량을 즉시 차단하는 한도가 아니다. 작업 종료 시 인스턴스를 직접 중지하고, 비용을 만드는 리소스가 남지 않았는지 확인하는 절차가 최종 안전장치다.

## 2. 고정 배포 기준

| 항목 | 기준 |
|---|---|
| 리전 | 서울 `ap-northeast-2` |
| EC2 | 온디맨드 `t3.medium`, x86_64, 일반 Ubuntu Server |
| CPU 크레딧 | `standard` — Unlimited 잉여 크레딧 추가 요금 금지 |
| EBS | 기존 gp3 16GiB 우선, 사용률 75% 이상이 지속될 때만 25~30GiB로 확장 |
| 공인 IP | 실행 중 자동 할당 IPv4 1개, Elastic IP는 만들지 않음 |
| 공개 HTTPS | Nginx 80·443, Let’s Encrypt 6일 공인 IP 인증서, IP 변경 시 재발급 |
| 데이터 서비스 | 같은 EC2의 PostgreSQL·Redis·RabbitMQ |
| 객체 저장소 | Cloudflare R2 Standard 비공개 버킷 1개 |
| 만들지 않는 것 | NAT Gateway, Load Balancer, RDS, Auto Scaling, 유료 AMI, Savings Plan·Reserved Instance, 상세 모니터링 |

## 3. 인스턴스를 켜기 전

- [ ] AWS 콘솔이 서울 리전인지 확인한다.
- [ ] 현재 `t3.medium` 시간당 가격, 공인 IPv4와 gp3 가격을 콘솔 또는 공식 요금표에서 확인하고 작업 기록에 시각과 함께 남긴다.
- [ ] AWS Budget에 실제 비용과 예측 비용 이메일 알림이 있고 수신 주소가 현재 사용하는 주소인지 확인한다. 기존 예산이 있으면 중복 생성하지 않는다.
- [ ] 이번 달 누적 비용을 확인하고, 이번 실험에서 허용할 추가 비용을 미화 5달러로 잡는다면 월 예산을 `현재 누적 비용 + 5달러`로 맞춘 뒤 실제 비용 80%·100%와 예측 비용 100% 알림을 둔다.
- [ ] EC2와 EBS에 `Project=IndieFi`, `Environment=portfolio`, `ExpiresOn=<종료 예정일>` 태그가 있는지 확인한다.
- [ ] 일반 Ubuntu Server이며 Ubuntu Pro 또는 유료 Marketplace AMI가 아닌지 확인한다.
- [ ] 이전 세션의 공인 IP 인증서와 R2 CORS Origin을 새 IP에 재사용하지 않는지 확인한다.
- [ ] PostgreSQL `pg_dump`를 로컬 PC에 내려받고 빈 로컬 DB에 시험 복원한다.
- [ ] Elastic IP, NAT Gateway, Load Balancer, RDS, 불필요한 EBS 볼륨과 스냅숏이 없는지 확인한다.
- [ ] 부하 도구는 EC2 안에서 실행하지 않고 로컬 PC 또는 별도 통제 환경에서 실행한다.

T3 크레딧 모드는 인스턴스 중지 후 변경 작업과 함께 확인한다.

```bash
aws ec2 modify-instance-credit-specification \
  --region ap-northeast-2 \
  --instance-credit-specification InstanceId=<instance-id>,CpuCredits=standard

aws ec2 describe-instance-credit-specifications \
  --region ap-northeast-2 \
  --instance-ids <instance-id>
```

## 4. 실행 중

- [ ] `CPUCreditBalance`, CPU, 메모리, 스왑과 디스크 사용률을 기록한다.
- [ ] EBS는 75% 사용을 넘었다는 측정 없이 미리 확장하지 않는다. EBS는 확장 후 다시 줄일 수 없다는 전제로 판단한다.
- [ ] SSH 22번은 관리자 현재 IP 또는 배포 중인 GitHub Actions 러너 `/32`에만 잠시 연다.
- [ ] 인터넷에는 Nginx 80·443만 열고, 80은 ACME HTTP-01 검증과 HTTPS 이동에만 사용한다.
- [ ] PostgreSQL 5432, Redis 6379, RabbitMQ 5672·15672는 공인 인터페이스와 보안 그룹에 열지 않는다.
- [ ] AWS Cost Explorer와 Billing 화면은 반영이 늦을 수 있으므로 알림만 믿고 인스턴스를 계속 켜두지 않는다.

## 5. R2 비용 경계

- [ ] Standard 저장 등급을 사용한다. 무료 사용량이 적용되지 않고 최소 보관 기간이 있는 Infrequent Access는 사용하지 않는다.
- [ ] 버킷은 비공개로 유지하고 `r2.dev` 공개 URL, Cloudflare Workers, Images, Cache Reserve를 켜지 않는다.
- [ ] 애플리케이션 전용 최소 권한 토큰 하나만 만들고 계정 전체 관리 토큰을 앱에 넣지 않는다.
- [ ] 실제 웹 Origin만 허용하는 CORS와 `staging/` 2일 lifecycle을 설정한다.
- [ ] R2 대시보드에서 저장량, Class A 쓰기와 Class B 읽기 사용량을 세션 전후로 확인한다.
- [ ] 운영 실험 종료 후 객체가 필요 없으면 먼저 삭제하고 버킷을 제거한 뒤 토큰을 폐기한다.

2026-08-20에 공식 문서에서 확인한 R2 Standard 월 무료 사용량은 저장 10GB-month, Class A 100만 회, Class B 1,000만 회이며 R2 자체 인터넷 egress는 무료다. 단가와 무료 사용량은 변경될 수 있으므로 배포 당일 <https://developers.cloudflare.com/r2/pricing/>에서 다시 확인한다.

## 6. 세션 종료 체크리스트

- [ ] EC2를 `stop`하고 콘솔에서 상태가 `stopped`가 될 때까지 확인한다.
- [ ] 자동 할당 공인 IPv4가 해제됐고 별도 Elastic IP가 없는지 확인한다.
- [ ] 해제된 IP의 인증서·R2 CORS Origin·배포 대상 주소를 다음 세션에 재사용하지 않도록 기록한다.
- [ ] GitHub Actions가 추가한 임시 SSH 보안 그룹 규칙이 남지 않았는지 확인한다.
- [ ] 추가 EBS, 스냅숏, NAT Gateway, Load Balancer, RDS와 Auto Scaling 리소스가 없는지 확인한다.
- [ ] R2 객체·버킷·토큰을 계속 보존할지 삭제할지 기록하고 대시보드 사용량을 확인한다.
- [ ] AWS와 Cloudflare 비용 화면을 다음 날 다시 확인한다. 비용 데이터의 지연 때문에 종료 직후 0으로 보이는 것만으로 완료 처리하지 않는다.

EC2를 중지해도 EBS와 남겨 둔 스냅숏·외부 저장소는 계속 과금될 수 있다. 모든 실험을 마친 뒤 인스턴스를 종료할 때는 DB 복구본을 먼저 확인하고, 삭제 대상의 정확한 ID를 다시 확인한 후 별도 정리 단계로 수행한다.

## 7. 공식 확인 자료

- T3 사양과 CPU 크레딧: <https://aws.amazon.com/ec2/instance-types/general-purpose/>
- T3 Unlimited 잉여 크레딧 요금: <https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/burstable-performance-instances-unlimited-mode-concepts.html>
- 공인 IPv4 시간당 과금: <https://aws.amazon.com/vpc/pricing/>
- AWS Budget 운영 주의사항: <https://docs.aws.amazon.com/cost-management/latest/userguide/budgets-best-practices.html>
- Cloudflare R2 요금과 무료 사용량: <https://developers.cloudflare.com/r2/pricing/>
- Let’s Encrypt 6일 IP 인증서와 Certbot: <https://letsencrypt.org/2026/03/11/shorter-certs-certbot/>

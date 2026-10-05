# 2026-10-05 이동 및 발판 설계 작업 기록

## 범위

이 기록은 자유 이동 클라이언트의 수동 확인과 횡스크롤 설계를 완료한 첫 작업의 결과입니다. 이후 서버 발판 데이터·독립 물리와 실제 접속 Player의 Map tick, 입력·상태 패킷 및 Unity 예측·보정을 연결했습니다. 최신 구현과 자동 검증은 [PLATFORM_NETWORK.md](../MyMMORPGServer/docs/PLATFORM_NETWORK.md), 데이터·독립 물리는 [PLATFORM_MOVEMENT.md](../MyMMORPGServer/docs/PLATFORM_MOVEMENT.md)에 있습니다. 아래 자유 이동 수동 확인을 새 발판 이동의 수동 검증 결과로 해석하지 않습니다.

## 오늘 실행한 자동 검증

| 검증 | 결과 |
|---|---|
| 이동 단위 테스트 | PASS 5, 종료 코드 0 |
| 잘못된 맵 CSV 검사 | PASS 10, 종료 코드 0 |
| .NET 통합 테스트 프로젝트 빌드 | 성공, 경고 0 / 오류 0 |
| 실제 LoginServer/GameServer를 통한 Map-local 자동 테스트 | PASS 16, 종료 코드 0 |

서버·Unity 실행 파일은 2026-10-04 검증한 최신 빌드를 사용했습니다. 오늘 런타임 소스를 변경하지 않았으므로 서버 및 Unity 빌드를 다시 수행한 결과로 기록하지 않습니다.

자동 통합 테스트는 실제 패킷과 LocalMovementState를 검증합니다. Unity 화면, 실제 키 홀드, GameObject 보간 및 채팅 포커스 확인을 대신하지 않습니다.

## Unity 화면에서 확인한 항목

- 두 클라이언트에서 test의 Warrior(1001)와 test2의 Archer(2001)로 로그인 및 입장 성공
- 동일 맵 100000000에서 양쪽 remote Player 1명, Monster 1개 표시
- MapInfo의 경계 X -400..400 / Y -200..200, 속도 80, Burst 12 표시
- 사용자 입력 이후 Warrior 창의 Remote 2001이 (21,48)로 갱신되고 Warrior Local (0,0)은 유지되는 화면 확인
- 사용자 수동 확인 후 Warrior Local (-17,19)와 Archer Remote 1001 (-17,19), Archer Local (-22,48)와 Warrior Remote 2001 (-22,48)의 일치 확인
- Warrior 맵 변경 시 새 맵의 spawn (100,50), remote Player 0 / Monster 0과 기존 Archer 창의 remote Player 0 확인
- 서로 다른 맵에서 Archer의 map0-only-20261005 메시지가 Warrior에게 전달되지 않음
- 시작 맵 재입장 시 양쪽 remote Player 1 / Monster 1로 복구
- 정상 좌표 이동 (8,4)이 상대 Remote 1001에도 반영되고 Archer Local (-22,48)은 유지됨
- (120,4)는 SpeedExceeded, (401,4)는 OutOfBounds로 거절되며 Warrior 좌표 (8,4)와 상대 좌표 유지, 예측 표시 위치 복귀 확인
- 같은 맵의 same-map-20261005 메시지가 본인과 상대 채팅에 각각 한 번 표시됨
- 없는 Map 999999999 요청은 MapNotFound, 기존 Map ID·좌표·객체 수 유지
- 거절 이후 정상 이동 (9,4) 성공 및 상대 Remote 1001 (9,4) 일치
- Warrior 창 종료 후 Archer 창에서 remote Player 0 / Monster 1 확인

위 좌표 관찰만으로 방향키 지속 입력, 양쪽 최종 좌표 일치와 키 해제 이후 정지까지 통과했다고 판단하지 않습니다.

## 사용자 확인 결과

아래 수동 확인 1~4의 절차를 요청했고 사용자가 "확인 완료."로 회신했습니다. 지속 방향키 입력과 키 해제 후 정지, 양쪽 서버 좌표 일치, 채팅 입력 중 이동 차단 및 Enter 전송 후 이동 재개는 사용자 확인 완료로 기록합니다. 도구가 직접 키를 유지해 검증한 결과는 아닙니다. 별도의 문제 보고는 없었습니다.

## 수동 확인 절차와 결과

Windows 화면 자동화 도구는 순간 키 누르기만 지원하며 키를 지정 시간 동안 유지하는 기능은 없습니다. 사용자 입력이 감지된 시점에는 화면 조작을 중단했습니다. 1~4는 사용자 확인 완료이며 5~10에 대응하는 좌표·맵 전환 검증은 화면 조작으로 완료했습니다. 서로 다른 맵의 채팅은 Archer에서 Warrior 방향으로 확인했습니다. 최소화된 창은 사용자가 복원한 뒤 검증을 재개했습니다. 아래 표는 반복 확인 절차입니다.

| 순서 | 조작 | 통과 기준 |
|---|---|---|
| 1 | Warrior 창의 빈 게임 영역을 클릭하고 오른쪽 방향키를 1초 유지 후 해제 | Archer 창의 Remote 1001 갱신, Remote 2001에 대응하는 Archer 본인 좌표 유지, SpeedExceeded 반복 없음 |
| 2 | 키를 놓고 2초 대기 | Warrior Local과 Archer Remote 1001 일치, 추가 좌표 변경 없음 |
| 3 | Warrior 채팅 입력란에서 방향키를 1초 유지 | 캐릭터 서버 좌표 유지 |
| 4 | 메시지 입력 후 Enter, 빈 게임 영역을 클릭하지 않고 방향키 입력 | 양쪽에 채팅 1회 표시, 이동 재개 |
| 5 | Warrior를 (0,0)으로 맵 재입장시킨 뒤 (8,4) Send | 양쪽 Warrior 서버 좌표 (8,4), Archer 서버 좌표 유지 |
| 6 | Warrior에서 (120,4), 이후 (401,4) Send | 각각 SpeedExceeded / OutOfBounds, 양쪽 Warrior 좌표 (8,4) 유지, 화면 예측 위치가 복귀 |
| 7 | Warrior를 100000001로 변경 | Archer의 remote Player 0, Warrior spawn (100,50), remote Player 0 / Monster 0 |
| 8 | Archer가 기존 맵에서 채팅 전송 | 새 맵의 Warrior 창에 메시지 미표시 |
| 9 | Warrior를 100000000으로 복귀 | 양쪽 remote Player 1, Monster 1, Player 중복 없음 |
| 10 | Warrior에서 999999999로 맵 변경 요청 | MapNotFound, 기존 Map ID·좌표·객체 수 유지 |
| 11 | 거절 이후 Warrior에서 (9,4) Send | 이동 성공, 상대 Remote 1001 (9,4) 일치 |
| 12 | Warrior 창 종료 | Archer 창의 remote Player 0 / Monster 1 |

카메라는 로컬 Player를 계속 따라갑니다. 자기 창에서 화면상의 다른 사각형이 함께 움직이는 것만으로 다른 Player의 서버 위치가 변경되었다고 판단하지 않고 Local / Remote 좌표와 상대 창을 함께 비교합니다. 비활성 창 갱신은 한쪽만 조작하면서 다른 창의 Remote 좌표가 변경되는지 확인합니다.

## 실행 방법

환경 준비는 [TEST_CLIENT.md](TEST_CLIENT.md), 좌표 이동 검증 기준은 서버 저장소의 [MOVEMENT_VALIDATION.md](../MyMMORPGServer/docs/MOVEMENT_VALIDATION.md)를 따릅니다. 계정은 test / test2, 초기화 SQL의 테스트 비밀번호는 test1234입니다. 최신 실행 파일은 클라이언트 저장소의 Builds/Windows/MyMMORPGClient.exe이며 두 번 실행합니다.

자동 테스트 재실행은 두 Unity 클라이언트를 종료한 뒤 클라이언트 저장소 PowerShell에서 실행합니다.

```powershell
dotnet run --project .\Tests\MapLocalIntegration\MapLocalIntegration.csproj
```

PASS 16개와 종료 코드 0을 확인합니다. 필요하면 마지막에 -- --trace를 추가합니다.

## 테스트 환경

기존 MySQL 볼륨을 사용하지 않는 localhost 3307의 임시 MySQL 컨테이너에서 서버를 실행했습니다. 임시 컨테이너 이름은 mymmorpg-codex-20261005입니다. 검증 종료 후 Unity 두 창과 테스트 서버를 종료하고 임시 컨테이너를 중지해 제거했습니다. 기존 mymmorpg-mysql 컨테이너와 볼륨은 유지했습니다.

# Map-local 검증 기록: 2026-10-04

## 환경과 변경

이 문서는 이동 검증 추가 전의 화면 통합 검증 기록입니다. 이후 서버 이동 검증과 위치 보정을 추가했으며 최신 결과는 서버 저장소의 [MOVEMENT_VALIDATION.md](../MyMMORPGServer/docs/MOVEMENT_VALIDATION.md)에 있습니다. 아래의 실행 중 프로세스와 임시 환경 안내는 당시 기록이며 현재 실행 상태를 나타내지 않습니다.

- Unity `6000.3.7f1`, .NET SDK `9.0.318`, Windows 클라이언트 두 개 사용
- 서버는 현재 저장소의 `x64/Release/GameServer.exe`, `LoginServer.exe` 사용
- 기존 MySQL과 볼륨 대신 `mymmorpg-codex-20261004` 임시 컨테이너의 `127.0.0.1:3307` 사용
- 임시 DB는 `database/init/001_init.sql`로 초기화하고 `test`/1001, `test2`/2001로 검증
- 자동 테스트 프로젝트 복원, `.csproj` ignore 예외와 `bin`/`obj` 제외 확인
- `ChangeMap()`의 송신 완료 상태가 서버 응답 상태를 덮는 문제 수정

## 빌드와 자동 검증

| 검증 | 결과 |
|---|---|
| `dotnet build Tests/MapLocalIntegration/MapLocalIntegration.csproj` | 성공, 경고 0개, 오류 0개 |
| 첫 번째 자동 통합 테스트 | PASS 10개, 종료 코드 0 |
| GameServer와 LoginServer 재시작 후 `--trace` 실행 | PASS 10개, 종료 코드 0 |
| 수정 후 Unity Windows 배치 빌드 | `Build Finished, Result: Success`, 종료 코드 0 |
| Git 공백 오류 확인 | `git diff --check` 통과 |

자동 검증 범위는 로그인과 인증 티켓, 입장 초기 패킷 순서, UTF-8 채팅, 반복 이동과 정지 관찰, 양방향 절대 좌표 이동, 맵 이동과 재입장, 맵 간 이동·채팅 격리, 잘못된 맵 요청 후 기존 맵 이동·채팅 유지, 추가 패킷과 접속 종료 정리입니다.

추가 패킷 검사는 각 지점에서 300ms 동안 관찰합니다. 이 결과는 Unity의 입력 처리나 GameObject 상태를 대신 검증하지 않습니다.

## Unity 화면 확인

| 항목 | 실제 확인 결과 |
|---|---|
| 서로 다른 두 계정 로그인과 캐릭터 선택 | 양쪽 로그인 화면이 사라지고 게임 화면 표시 |
| 같은 맵 초기 상태 | 양쪽 Remote players 1, Monsters 1 |
| Warrior `(120, 45)`, Archer `(60, 45)` 이동 | 상대 `PlayerMove`와 Remote 좌표 갱신, 정지한 로컬 좌표 유지 |
| 비활성 창의 패킷 처리 | 다른 창을 조작하는 동안에도 이동·입장·채팅 수신 반영 |
| 캐릭터 표시 | 1001 청록색, 2001 노란색, 동일한 크기와 ID 표시 |
| 좌표 지정 이동 보간 | 이동 직후 중간 위치에서 목표 위치로 수렴하는 화면 확인 |
| 같은 맵 Enter 채팅 | 양쪽에 `Warrior: same map test` 한 번씩 표시 |
| Map `100000001` 이동 | 이동한 창 Remote 0/Monster 0, 기존 맵 창 Remote 0/Monster 1 |
| 다른 맵 Enter 채팅 | 기존 맵에서 `Archer: isolated map test` 표시, 이동한 창에는 표시되지 않음 |
| Map `100000000` 재입장 | Remote 1/Monster 1로 복구, 로컬 spawn `(0, 0)` |
| 잘못된 Map 요청 | 맵·위치·객체 수 유지 |
| 수정 빌드 성공·실패 상태 표시 | `Changed to map ...`, `Map change failed: MapNotFound` 표시 유지 |

두 창의 전체 흐름은 수정 전 최신 빌드에서 확인했고, 상태 표시 수정 후 다시 빌드하여 로그인, 맵 성공·실패, 시작 맵 복귀와 두 캐릭터 입장을 확인했습니다. 현재 최신 빌드는 `Builds/Windows/MyMMORPGClient.exe`입니다.

## 직접 입력이 필요한 항목

현재 화면 자동화 API는 키를 길게 누르는 기능을 제공하지 않습니다. 다음 항목은 아직 최종 통과로 표시하지 않았습니다.

1. 두 클라이언트에서 상하좌우 방향키를 각각 1초 동안 누르고, 상대 창의 Remote 좌표가 해당 Local 좌표와 일치하는지 확인합니다. 다른 캐릭터 자신의 좌표는 유지되어야 합니다.
2. 키를 놓고 2초 관찰합니다. 좌표가 유지되고 사각형은 보간을 마친 뒤 멈춰야 합니다.
3. 채팅 입력란에서 `focus test`를 입력한 채 오른쪽 방향키를 1초 누릅니다. 양쪽의 해당 캐릭터 좌표가 유지되어야 합니다.
4. Enter로 채팅을 전송하고 다른 곳을 클릭하지 않은 상태에서 오른쪽 방향키를 1초 누릅니다. 채팅은 양쪽에 한 번씩 표시되고 이동이 재개되어야 합니다.
5. 양쪽 창을 번갈아 활성화하면서 지속 이동 보간에 튀는 현상이 있는지 확인합니다. 카메라가 로컬 캐릭터를 따라가므로 실제 이동 여부는 패널의 ID별 좌표로 판단합니다.

상세 준비와 통과 기준은 [TEST_CLIENT.md](TEST_CLIENT.md)의 2절과 3절에 있습니다.

## 이번 테스트 환경 종료

직접 입력 확인을 위해 두 클라이언트와 임시 DB에 연결한 서버를 실행해 두었습니다. 첫 번째 창은 Warrior/1001, 두 번째 창은 Archer/2001이며 Map `100000000`에 있습니다. 테스트가 끝나면 두 클라이언트 창을 닫고 서버 저장소의 PowerShell에서 다음을 실행합니다. 프로세스 ID는 이번 실행에만 해당하며 실행 파일 경로도 확인합니다.

```powershell
$testServerPaths = @(
    'C:\Users\hjjan\Documents\Git\MyMMORPGServer\x64\Release\GameServer.exe',
    'C:\Users\hjjan\Documents\Git\MyMMORPGServer\x64\Release\LoginServer.exe'
)
Get-Process -Id 19836,2748 -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -in $testServerPaths } |
    Stop-Process
docker stop mymmorpg-codex-20261004
```

임시 컨테이너는 `--rm`으로 생성했으므로 종료 시 제거됩니다. 나중에 다시 검증하려면 `TEST_CLIENT.md`의 서버 준비 절차로 환경을 실행합니다.

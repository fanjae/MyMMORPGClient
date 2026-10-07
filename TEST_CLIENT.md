# Unity 2D 테스트 클라이언트

다른 장소 PC의 연결 거부(10061), 서버 수신 주소와 외부 접속 설정은 [REMOTE_ACCESS.md](../MyMMORPGServer/docs/REMOTE_ACCESS.md)를 참고합니다. SERVER_BIND_IP는 서버용이며 클라이언트 Host에는 서버의 실제 IP를 입력합니다.

2026-10-07 Game version 5와 입력 적용 단계 확인·미확인 입력 재실행 및 원격 버퍼 보간을 적용했습니다. Login version 2·UTF-8 이름 65바이트 형식은 유지합니다. 서버와 Windows 클라이언트를 함께 갱신해야 합니다. 반복 실행과 수동 지연 프록시는 [MOVEMENT_RECONCILIATION.md](../MyMMORPGServer/docs/MOVEMENT_RECONCILIATION.md)를 참고합니다.

좌표 지정 Send UI를 제거하고 전송 후 채팅 포커스 유지, 본인 렌더링 우선순위, `/m 캐릭터ID 메시지` 귓속말과 계정 채팅 제한을 유지합니다. 일반 채팅·귓속말은 계정별 연속 5회·초당 1회 회복 제한을 공유합니다.

최신 구현에서는 100000000이 발판 맵으로 전환되어 **좌우 방향키 + Space 점프**를 사용합니다. 100000001은 기존 자유 이동을 유지합니다. 최신 테스트 실행·PASS 기준·지형/입력 패킷은 [PLATFORM_NETWORK.md](../MyMMORPGServer/docs/PLATFORM_NETWORK.md)에 있습니다. 아래 기존 절차의 절대 좌표 테스트와 PASS 16 기본 실행은 문서에 설명한 임시 Free 설정에서 수행합니다. 최신 발판 테스트는 `dotnet run --project .\Tests\MapLocalIntegration\MapLocalIntegration.csproj -- --platform`입니다.

2026-10-05 자동 및 Unity 두 클라이언트 검증 결과와 반복 확인 절차는 [TEST_RESULTS_2026-10-05.md](TEST_RESULTS_2026-10-05.md)에 있습니다. 지속 방향키 입력과 채팅 포커스는 사용자 확인을 받아 기록했습니다.

이 절차는 `LoginServer → 캐릭터 선택 → GameServer 인증 → Map-local 상태`를 두 클라이언트로 확인합니다. 화면은 실제 게임 리소스 대신 사각형 Sprite를 사용합니다.

- 청록색 사각형: Character ID `1001`인 Player (어느 클라이언트에서 보아도 동일)
- 노란색 사각형: Character ID `2001`인 Player (어느 클라이언트에서 보아도 동일)
- 다른 Player도 Character ID에 따라 고정된 색상을 사용하며, 사각형 위에 ID를 표시함
- 빨간색 사각형: Monster
- 서버 좌표 20 단위: Unity 월드 좌표 1 단위
- 카메라: 로컬 Player를 가로 중앙과 세로 중앙보다 약간 아래에 두고 따라감

현재 버전은 서버의 맵 경계·속도 검증과 확정 위치 응답을 사용합니다. 이전 빌드는 새 `MoveRequest`와 `MapInfo` 수신 순서에 호환되지 않으므로 서버와 클라이언트를 함께 빌드합니다. 설정과 상세 테스트는 서버 저장소의 [MOVEMENT_VALIDATION.md](../MyMMORPGServer/docs/MOVEMENT_VALIDATION.md)에 있습니다.

## 1. 서버 준비 및 실행

1. `MyMMORPGServer` 폴더에서 `.env`가 없을 때만 `.env.example`을 복사합니다. 이미 `.env`가 있으면 아래 복사 명령은 실행하지 않습니다.

   ```powershell
   if (-not (Test-Path .env)) { Copy-Item .env.example .env }
   ```

2. `.env`의 `MYSQL_ROOT_PASSWORD`, `MYSQL_USER`, `MYSQL_PASSWORD`, `MYSQL_DATABASE` 값을 로컬 개발 환경에 맞게 설정합니다. 서버 프로세스에 설정할 `DB_*` 값도 같은 DB를 가리키게 합니다. 예시 설정은 다음과 같습니다.

   ```text
   MYSQL_ROOT_PASSWORD=change-me
   MYSQL_DATABASE=mymmorpg
   MYSQL_USER=game
   MYSQL_PASSWORD=change-me
   DB_HOST=tcp://127.0.0.1:3306
   DB_USER=game
   DB_PASSWORD=change-me
   DB_NAME=mymmorpg
   ```

3. 같은 폴더에서 MySQL을 시작하고 준비 완료를 확인합니다.

   ```powershell
   docker compose up -d mysql
   docker compose ps
   docker compose logs --tail 30 mysql
   ```

   로그에 MySQL의 연결 준비 완료가 보여야 합니다. `database/init/001_init.sql`은 MySQL 데이터 볼륨이 처음 생성될 때 계정과 캐릭터를 넣습니다. 이전에 초기화한 볼륨이면 이 SQL이 자동 재실행되지 않으므로 `test`와 `test2` 계정 및 캐릭터 행이 있는지 DB에서 확인합니다.

   기존 볼륨에 `mymmorpg`가 없고 테스트 데이터만 새로 필요하면 기존 볼륨을 건드리지 않는 임시 컨테이너를 사용할 수 있습니다. 서버 저장소 루트에서 아래 명령을 실행한 뒤 5단계의 `DB_HOST`만 `tcp://127.0.0.1:3307`로 바꿉니다. 컨테이너를 중지하면 이 임시 데이터는 사라집니다.

   ```powershell
   docker compose stop mysql
   $initDir = (Resolve-Path .\database\init).Path
   docker run --rm -d --name mymmorpg-integration-mysql --env-file .env --tmpfs /var/lib/mysql:rw,size=1g -p 127.0.0.1:3307:3306 --mount "type=bind,source=$initDir,target=/docker-entrypoint-initdb.d,readonly" mysql:8.4
   docker logs --tail 30 mymmorpg-integration-mysql
   ```

   임시 테스트가 끝나면 `docker stop mymmorpg-integration-mysql`로 종료합니다.

4. Visual Studio 2022에서 `MyMMORPGServer.sln`을 열고 구성 `Release`, 플랫폼 `x64`를 선택해 솔루션을 빌드합니다. 빌드 결과는 서버 저장소 루트의 다음 경로에 생성됩니다.

   ```text
   x64\Release\GameServer.exe
   x64\Release\LoginServer.exe
   ```

5. GameServer와 LoginServer를 각각 별도 PowerShell 창에서 실행합니다. 두 창 모두 서버 저장소 폴더에서 아래 환경 변수를 설정합니다. 비밀번호는 `.env`의 `MYSQL_PASSWORD`와 같은 값으로 넣습니다.

   ```powershell
   $env:DB_HOST = 'tcp://127.0.0.1:3306'
   $env:DB_USER = 'game'
   $env:DB_PASSWORD = 'change-me'
   $env:DB_NAME = 'mymmorpg'
   ```

   첫 번째 창에서 GameServer를 실행합니다.

   ```powershell
   .\x64\Release\GameServer.exe
   ```

   GameServer는 클라이언트용 `7777`과 LoginServer 티켓 등록용 `7778` 포트를 엽니다. GameServer가 실행 중인 상태에서 두 번째 창에 같은 `DB_*` 값을 설정하고 LoginServer를 실행합니다.

   ```powershell
   .\x64\Release\LoginServer.exe
   ```

   LoginServer는 `7776` 포트를 엽니다. Unity를 실행하기 전에 다음 명령 결과에서 세 포트가 모두 `True`인지 확인합니다.

   ```powershell
   Test-NetConnection 127.0.0.1 -Port 7776
   Test-NetConnection 127.0.0.1 -Port 7777
   Test-NetConnection 127.0.0.1 -Port 7778
   ```

## 2. 클라이언트 두 개 실행

1. Unity Hub에서 이 프로젝트를 Unity `6000.3.7f1`로 열고, 필요한 경우 `File > Build Profiles`에서 `SampleScene`을 포함해 Windows 빌드를 만듭니다. 이 저장소에서 만든 최신 빌드는 `Builds/Movement/MyMMORPGClient.exe`에 있습니다. 빌드 폴더의 실행 파일, `MyMMORPGClient_Data`, `UnityPlayer.dll` 등을 함께 둡니다.
2. 클라이언트 저장소 루트에서 Windows 빌드를 두 번 실행합니다.

   ```powershell
   $clientExe = (Resolve-Path .\Builds\Movement\MyMMORPGClient.exe).Path
   Start-Process -FilePath $clientExe -WorkingDirectory (Split-Path $clientExe)
   Start-Process -FilePath $clientExe -WorkingDirectory (Split-Path $clientExe)
   ```

   두 창을 나란히 배치합니다. Windows 빌드는 `960×540` 창 모드로 시작하며 창 크기를 조절할 수 있습니다. 예전 Desktop 빌드에는 분리된 로그인 화면과 채팅 UI가 없으므로 최신 빌드를 실행합니다. Windows 빌드 하나 대신 Unity Editor의 `Assets/Scenes/SampleScene.unity`를 Play 모드로 실행해도 됩니다.
3. 첫 번째 클라이언트에서 Host `127.0.0.1`, Port `7776`을 확인하고, 별도의 Login ID 입력란에 `test`, Password 입력란에 `test1234`를 입력한 뒤 `Connect and Login`을 누릅니다.
4. 캐릭터 목록에서 `Warrior (ID 1001)`을 선택합니다. 로그인 화면이 사라지고 게임 테스트 패널의 상태가 `Entered as Warrior (ID 1001).`로 바뀌면 GameServer 입장에 성공한 것입니다.
5. 두 번째 클라이언트에서 Login ID에 `test2`, Password에 `test1234`를 입력하고 같은 순서로 접속해 `Archer (ID 2001)`를 선택합니다.

입장 후 좌측 상단의 게임 테스트 패널은 현재 Map ID, 로컬 Character ID, Player별 서버 좌표, 원격 Player 수, Monster 수, 마지막 `PlayerMove` 수신값과 상태 메시지를 표시합니다. 좌측 하단에는 별도 Map Chat 입력창과 수신 기록이 있습니다. 비활성 창에서도 패킷 처리가 계속되므로 두 창을 번갈아 조작할 수 있습니다.

## 3. 확인할 시나리오와 통과 기준

### 입장 및 초기 Map 상태

두 클라이언트가 모두 Map `100000000`에 있을 때 다음을 확인합니다.

| 클라이언트 | Local ID | Remote players | Monsters |
|---|---:|---:|---:|
| 첫 번째, Warrior | 1001 | 1 | 1 |
| 두 번째, Archer | 2001 | 1 | 1 |

두 화면에 Player 사각형 두 개와 Monster 사각형 한 개가 보여야 합니다. 처음 로그인한 클라이언트는 두 번째 클라이언트가 입장한 뒤 `Remote players`가 1로 바뀌어야 합니다.
두 화면 모두 `1001`은 청록색, `2001`은 노란색이어야 합니다. 로컬/원격 여부가 바뀌어도 캐릭터의 색상과 사각형 위 ID는 유지되어야 합니다.

### 이동 브로드캐스트

1. 첫 번째 클라이언트의 게임 화면을 클릭해 입력 칸의 포커스를 해제합니다. `→`를 약 1초 누릅니다. 첫 번째 화면의 `Local position` X가 약 80 증가하고 Y는 유지되어야 합니다.
2. 첫 번째 클라이언트에서 `↑`를 약 1초 누릅니다. 이번에는 Y가 약 80 증가하고 X는 유지되어야 합니다. `←`와 `↓`도 각각 X 감소, Y 감소인지 확인합니다. 대각선 입력은 이동 속도가 두 배가 되지 않아야 합니다.
3. 두 번째 화면에서 `Last PlayerMove: 1001 (...)`와 `Remote 1001: (...)`가 갱신되고 청록색 원격 Player가 새 위치로 부드럽게 이동해야 합니다. 첫 번째 창에 포커스가 있는 동안에도 두 번째 창에서 처리가 진행되어야 합니다.
4. 화살표 키를 놓고 `Local position`과 `Remote 1001` 좌표가 더 이상 변하지 않는지 확인합니다. 두 화면의 원격 Player 수는 계속 1이어야 합니다.
5. 좌표 지정 Send UI는 제거했습니다. 경계·속도·큰 절대 좌표 요청 검증은 아래 자동 회귀 테스트에서 확인합니다.
6. 두 번째 클라이언트에서 화살표 키로 `2001`을 이동시킵니다. 첫 번째 화면에서는 노란색 `2001`이 움직이고 청록색 `1001`의 좌표는 유지되어야 합니다.

화살표 이동은 서버 `MapInfo`의 속도 설정으로 목표 좌표를 계산하며, 최대 0.05초 간격으로 이전 응답을 받은 뒤 다음 `MoveRequest`를 보냅니다. 기본 속도는 초당 서버 좌표 80단위입니다. 로컬 사각형은 요청 위치로 예측·보간하지만 `Local position`은 서버 응답으로만 갱신합니다. 다른 Player는 허용된 이동의 `PlayerMove`를 받을 때마다 보간합니다. 카메라가 로컬 Player를 따라갈 때 정지한 객체도 화면에서는 반대 방향으로 움직여 보일 수 있으므로 실제 이동은 ID별 서버 좌표로 판단합니다.

Free 서버는 요청자에게 `MoveResponse`로 결과와 확정 좌표를 전달합니다. `OutOfBounds`나 `SpeedExceeded`가 반환되면 입력 목표와 화면 위치를 서버 좌표로 보정하고 상대에게 이동을 전파하지 않습니다. 기본 Platformer 맵의 발판·충돌·점프·중력은 [PLATFORM_NETWORK.md](../MyMMORPGServer/docs/PLATFORM_NETWORK.md)의 절차로 확인합니다.

방향키는 실제 창을 활성화한 상태에서 1초 정도 누르고 확인합니다. 자동화된 짧은 키 입력 한 번은 프레임 사이에 끝날 수 있어 이 항목을 검증하지 못합니다.

지속 입력 확인 시에는 두 창의 좌표를 이동 전후에 적어 둡니다. 키를 놓은 뒤 2초 동안 좌표가 유지되는지 확인하고, 반대쪽 클라이언트에서도 같은 절차를 진행합니다. 보간이 끝날 때까지 사각형이 잠깐 움직이는 것과 서버 좌표가 계속 바뀌는 것은 구분합니다.

### Map 변경 및 재입장

1. 첫 번째 클라이언트의 Map ID에 `100000001`을 입력하고 `Change`를 누릅니다.
2. 첫 번째 화면은 Map `100000001`, 위치 `(100, 50)`에 도착해야 합니다. 첫 번째 화면은 `Remote players: 0`, `Monsters: 0`이어야 합니다.
3. 두 번째 화면은 시작 Map `100000000`에 남아 있어야 합니다. 첫 번째 Player가 떠났으므로 `Remote players: 0`이 되고, 자기 Map에 남아 있는 Monster는 계속 1개여야 합니다.
4. 첫 번째 클라이언트에서 Map ID `100000000`으로 돌아옵니다.
5. 두 화면에서 `Remote players: 1`이 되고, 각 화면에서 시작 Map의 Monster 한 개가 보여야 합니다. 첫 번째 Player의 위치는 서버가 돌려준 spawn `(0, 0)`이어야 합니다.

### 잘못된 Map ID

첫 번째 클라이언트에서 `999999999`로 Map 변경을 요청합니다. 상태가 `Map change failed: MapNotFound`로 바뀌고 Map ID, 위치, Player 및 Monster 수가 그대로면 통과입니다.

### 같은 Map 채팅

1. 두 클라이언트가 Map `100000000`에 있을 때 첫 번째 클라이언트의 `Chat` 입력란을 클릭해 `안녕하세요`를 입력하고 Enter를 누릅니다. `Send` 버튼을 눌러도 같은 패킷을 보냅니다.
2. 두 창에 `Warrior: 안녕하세요`가 각각 한 번씩 나타나야 합니다. 서버가 발신자에게도 `PlayerChat`을 보내므로 클라이언트가 전송 즉시 기록을 추가하지 않습니다.
3. 첫 번째 클라이언트를 Map `100000001`로 이동합니다. 첫 번째 화면의 일반 채팅 기록은 새 맵 입장 시 비우고 귓속말 기록은 유지합니다.
4. 두 번째 클라이언트에서 `still in first map`을 Enter로 보냅니다. 두 번째 화면에는 `Archer: still in first map`이 나타나고, 첫 번째 화면에는 나타나지 않아야 합니다.
5. 첫 번째 클라이언트를 Map `100000000`으로 돌려보낸 뒤 다시 채팅합니다. 양쪽 화면에 새 메시지가 나타나야 합니다.

채팅 입력란에 포커스가 있는 동안에는 방향키 이동이 멈춥니다. Enter·키패드 Enter·Send 전송 후에도 포커스를 유지해 클릭 없이 다음 메시지를 입력할 수 있습니다. Esc 또는 빈 게임 영역 클릭으로 포커스를 해제합니다. 메시지는 최대 127 UTF-8 바이트이며 빈 문자열과 공백만 있는 문자열은 전송하지 않습니다.

### 귓속말·제한·겹침 렌더링

1. 서로 다른 맵에서 A가 `/m 2001 안녕하세요`를 보내면 양쪽에 발신자·대상 이름과 ID가 포함된 귓속말을 한 번씩 표시합니다. 일반 채팅과 달리 다른 맵에서도 전달됩니다.
2. B는 `/m 1001 답장`을 보냅니다. 없는 ID나 종료한 대상에게 보내면 미접속 알림을 표시하며 연결은 유지합니다. `/m 1001`처럼 본문이 없으면 입력 형식 오류를 표시합니다.
3. 일반 채팅과 귓속말을 연속 5회 보낸 뒤 다음 요청은 제한 알림과 재시도 시간을 표시합니다. 초당 1회씩 회복하고 맵 이동·재접속으로 제한을 초기화하지 않습니다.
4. 같은 위치에서는 본인 사각형과 ID가 다른 Player보다 앞에 표시됩니다. 본인·원격 색상과 이동 좌표는 유지됩니다.

입력 충돌은 다음 순서로 직접 확인합니다. 첫 번째 창의 Local position과 두 번째 창의 Remote 1001 좌표를 적고, 첫 번째 창의 채팅 입력란에서 `focus test`를 입력한 채 `→`를 1초 누릅니다. 두 좌표가 모두 유지되어야 합니다. Enter로 채팅을 전송한 뒤 다른 곳을 클릭하지 않고 `→`를 1초 누릅니다. 이번에는 양쪽에서 1001의 X가 증가하고 2001의 좌표는 유지되어야 합니다.

## 4. 프로토콜 자동 통합 테스트

Unity 창을 모두 닫고 서버 두 개가 실행 중인 상태에서 클라이언트 저장소 루트에서 다음을 실행합니다. 동일 캐릭터가 이미 접속 중이면 서버가 중복 입장을 거절하므로 창을 먼저 닫아야 합니다. .NET 9 SDK가 필요합니다.

```powershell
dotnet run --project .\Tests\MapLocalIntegration\MapLocalIntegration.csproj
```

종료 코드 `0`과 `PASS` 16개가 기준입니다. 기존 Map-local 흐름에 더해 서버 이동 응답, 경계·극단 좌표 거절, 과속 거절과 복구, 요청 번호와 Map ID 검사, 고빈도 요청의 누적 허용량, 경계 끝 좌표와 맵 전환 후 초기화, 클라이언트 위치 보정과 이전 응답 무시를 검증합니다. Unity 화면과 방향키 입력·보간은 2절과 3절의 수동 테스트로 확인합니다.

테스트는 `Network/TcpSession`, `PacketReader`, `PacketWriter`, `PacketOpcode`, `Protocol`과 `Game/LocalMovementState` 소스를 직접 컴파일합니다. 테스트 내부의 Player/Monster 컬렉션은 패킷 순서와 중복을 확인하기 위한 상태이며 Unity의 `WorldManager`와 GameObject 제거를 검증하지 않습니다. 패킷 대기는 5초, 추가 패킷이 없는지 확인하는 관찰 시간은 300ms입니다. 이동 반복은 응답 확인 후 50ms씩 대기하며 실제 방향키 입력을 사용하지 않습니다.

실패하면 `FAIL`에 단계, 클라이언트와 서버 구분, 예상 및 실제 opcode 또는 타임아웃이 표시됩니다. 송수신 opcode와 payload 크기를 더 자세히 확인하려면 다음을 실행합니다. 로그인 비밀번호와 인증 티켓 내용은 출력하지 않습니다.

```powershell
dotnet run --project .\Tests\MapLocalIntegration\MapLocalIntegration.csproj -- --trace
```

반복 검증은 첫 실행이 종료된 뒤 두 서버를 종료하고, 같은 DB 환경으로 GameServer와 LoginServer를 다시 시작한 후 위 명령을 재실행합니다. 두 실행 모두 `PASS` 16개와 종료 코드 `0`이어야 합니다. Unity 창은 자동 테스트 동안 닫아 둡니다.

`.gitignore`는 Unity가 생성하는 일반 `*.csproj`를 제외하지만 `Tests/MapLocalIntegration/MapLocalIntegration.csproj`는 예외로 둡니다. 테스트의 `Program.cs`와 `.csproj`는 함께 버전 관리하고 `bin`, `obj`는 제외합니다. 저장소에 반영할 파일을 확인할 때는 다음을 실행합니다.

```powershell
git status --short -- Tests TEST_CLIENT.md .gitignore
git check-ignore Tests/MapLocalIntegration/MapLocalIntegration.csproj
git check-ignore Tests/MapLocalIntegration/bin/Debug/net9.0/MapLocalIntegration.dll
```

프로젝트 파일의 `check-ignore` 결과는 비어 있어야 하고, 빌드 산출물은 경로가 출력되어야 합니다.

## 5. 실행이 안 될 때 확인할 항목

- `LoginServer` 연결 실패: LoginServer 콘솔, `DB_*` 환경 변수, 포트 `7776`을 확인합니다.
- 로그인은 되지만 캐릭터 목록이 비거나 서버 오류: MySQL의 `accounts`, `characters` 테이블에 `test`/`Warrior`와 `test2`/`Archer` 데이터가 있는지 확인합니다. 기존 데이터 볼륨을 사용하면 init SQL은 자동 재적용되지 않습니다.
- 서버가 `Unknown database 'mymmorpg'`로 종료됨: 연결한 MySQL 인스턴스의 데이터베이스 목록과 볼륨 상태를 확인합니다. 기존 볼륨을 삭제하거나 초기화하지 말고 필요한 데이터가 있으면 먼저 보존합니다. 테스트만 필요하면 별도 임시 데이터베이스를 초기화해 서버의 `DB_HOST`를 그 포트로 지정합니다.
- 캐릭터 선택 뒤 GameServer 연결 실패: GameServer 콘솔과 포트 `7777`을 확인합니다. LoginServer가 GameServer에 auth ticket을 등록하므로 `7778`도 열려 있어야 합니다.
- 두 번째 클라이언트에서 다른 Player가 안 보임: 두 클라이언트의 상태에 Map ID `100000000`이 표시되는지 확인합니다. 서버는 현재 두 포트를 localhost에 바인딩하므로 두 클라이언트는 같은 컴퓨터에서 실행해야 합니다.
- 화살표 키가 동작하지 않음: 해당 클라이언트 창을 활성화하고 게임 화면의 빈 공간을 클릭해 입력 칸의 포커스를 해제합니다. `Project Settings > Player > Active Input Handling`은 `Input System Package (New)`로 설정되어 있어야 합니다.
- Enter 채팅이 전송되지 않음: 게임 입장 후 화면 하단의 `Chat` 입력란을 클릭해 포커스를 둡니다. 상대에게 보이지 않으면 두 창의 Map ID가 같은지 확인합니다.
- Unity 코드 컴파일 오류: Console의 첫 번째 오류를 확인하고, 프로젝트가 Unity `6000.3.7f1`로 열렸는지 확인합니다.
- Unity 명령행 빌드가 라이선스 오류 `198`과 `com.unity.editor.headless`를 표시함: `-nographics` 옵션을 빼고 `-batchmode`로 다시 실행합니다. 이 프로젝트에서는 해당 옵션을 제외한 Windows 배치 빌드가 완료됐습니다.

현재 검증 범위는 패킷 왕복, Map-local 객체 수명, 서버 이동 범위·속도·확정 좌표, 키보드 이동과 화면 보간입니다. 실제 충돌·지형·발판 검증은 포함하지 않습니다.

로그인·캐릭터 선택 화면은 `Assets/Scripts/TestClient/LoginScreenController.cs`, 이동·맵 변경·Map Chat 테스트 UI는 `Assets/Scripts/TestClient/TestClientController.cs`에 있습니다. 재사용할 패킷 송수신과 메인 스레드 전달은 `Network`, 직렬화는 `Protocol`, 화면의 Player·Monster 상태와 보간은 `Game`에 둡니다. 실제 게임 UI를 만들 때는 테스트용 컨트롤러 대신 별도 입력·화면 코드를 연결합니다.

## 6. 테스트 코드와 실제 게임 코드의 책임

| 영역 | 현재 책임 | 실제 게임 구현 시 처리 |
|---|---|---|
| `Network` | TCP 누적 수신, 송신 직렬화, Unity 메인 스레드 이벤트 전달 | UI와 분리해 재사용 |
| `Protocol` | opcode 및 packing에 맞춘 명시적 직렬화 | 서버 프로토콜 변경 시 함께 수정 |
| `WorldManager`, `PlayerView`, `MonsterView` | Map-local 객체 수명 및 위치 표현 | 테스트 사각형 생성과 카메라 코드를 실제 표현 계층으로 분리한 뒤 재사용 |
| `LocalMovementState` | 서버 확정 좌표, 대기 중 요청과 이전 응답 구분 | 실제 입력 계층에서도 재사용 |
| `LoginScreenController` | IMGUI 테스트 로그인과 캐릭터 선택 | 실제 로그인 UI와 접속 흐름 계층을 별도로 구성 |
| `TestClientController` | 방향키 테스트 입력, Map 변경, 채팅·귓속말 및 진단 표시 | 실제 입력/UI 계층으로 교체 |
| `Tests/MapLocalIntegration` | Unity 없이 실제 서버의 패킷 흐름 검증 | 새로운 Map-local 패킷 시나리오를 추가 |

현재 `TestClientController.CreateTestClient()`는 `RuntimeInitializeOnLoadMethod`를 통해 씬 로딩 후 테스트 객체를 자동 생성합니다. 실제 게임 UI를 추가하기 전에 이 진입점을 테스트 씬 또는 명시적인 테스트 실행 설정으로 제한해야 합니다. 실제 UI와 테스트 UI를 동시에 자동 생성하지 않도록 먼저 진입점을 정리합니다.

`WorldManager`는 현재 테스트 사각형 생성과 카메라 추적도 포함하므로 전부 완성된 실제 게임 로직으로 취급하지 않습니다. 테스트용 절대 좌표 UI와 상태 출력은 `TestClient`에 두고, 실제 게임 입력에서 서버 이동 검증과 위치 보정이 필요해질 때 이동 프로토콜을 함께 검토합니다.

2026-10-04에 수행한 검증과 직접 입력이 필요한 잔여 항목은 [검증 기록](TEST_RESULTS_2026-10-04.md)에 있습니다.

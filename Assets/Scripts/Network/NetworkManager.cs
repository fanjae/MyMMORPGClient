using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;

public sealed class NetworkManager : MonoBehaviour
{
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();

    private TcpSession _loginSession;
    private TcpSession _gameSession;

    public event Action<LoginResult> LoginCompleted;
    public event Action<CharacterListData> CharacterListReceived;
    public event Action<CharacterSelectData> CharacterSelected;

    public event Action<EnterGameData> EnterGameReceived;
    public event Action<PlayerEnterData> PlayerEntered;
    public event Action<uint> PlayerLeft;
    public event Action<PlayerMoveData> PlayerMoved;
    public event Action<MonsterEnterData> MonsterEntered;
    public event Action<ChangeMapData> MapChanged;

    private void Update()
    {
        // 네트워크 수신 스레드에서 등록한 작업을 Unity 메인 스레드에서 처리
        while (_mainThreadQueue.TryDequeue(out Action action))
            action();
    }

    public async Task ConnectLoginServerAsync(string host, int port)
    {
        // 로그인 서버 전용 세션 생성 및 패킷 수신 콜백 등록
        _loginSession = new TcpSession();
        _loginSession.PacketReceived += OnLoginPacketReceived;

        await _loginSession.ConnectAsync(host, port);
    }

    public Task SendLoginAsync(string loginId, string password)
    {
        byte[] payload = LoginProtocol.CreateLoginRequest(loginId, password);
        return _loginSession.SendAsync((ushort)LoginPacketOpcode.LoginRequest, payload);
    }

    public Task RequestCharacterListAsync()
    {
        return _loginSession.SendAsync((ushort)LoginPacketOpcode.CharacterListRequest, Array.Empty<byte>());
    }

    public Task SelectCharacterAsync(uint characterId)
    {
        byte[] payload = LoginProtocol.CreateCharacterSelectRequest(characterId);
        return _loginSession.SendAsync((ushort)LoginPacketOpcode.CharacterSelectRequest, payload);
    }

    public async Task ConnectGameServerAsync(string host, ushort port, ulong authKey)
    {
        // 게임 서버 전용 세션 생성 및 연결
        _gameSession = new TcpSession();
        _gameSession.PacketReceived += OnGamePacketReceived;

        await _gameSession.ConnectAsync(host, port);

        // 로그인 서버에서 발급받은 인증 키로 게임 서버 입장 요청
        byte[] payload = GameProtocol.CreateEnterGameRequest(authKey);
        await _gameSession.SendAsync((ushort)GamePacketOpcode.EnterGameRequest, payload);
    }

    public Task SendMoveAsync(int x, int y)
    {
        byte[] payload = GameProtocol.CreateMoveRequest(x, y);
        return _gameSession.SendAsync((ushort)GamePacketOpcode.MoveRequest, payload);
    }

    public Task ChangeMapAsync(uint mapId)
    {
        byte[] payload = GameProtocol.CreateChangeMapRequest(mapId);
        return _gameSession.SendAsync((ushort)GamePacketOpcode.ChangeMapRequest, payload);
    }

    private void OnLoginPacketReceived(ushort opcode, byte[] payload)
    {
        // Unity API와 이벤트 구독자가 메인 스레드에서 실행되도록 큐에 등록
        _mainThreadQueue.Enqueue(() =>
        {
            switch ((LoginPacketOpcode)opcode)
            {
                case LoginPacketOpcode.LoginResponse:
                    LoginCompleted?.Invoke(LoginProtocol.ReadLoginResponse(payload));
                    break;

                case LoginPacketOpcode.CharacterListResponse:
                    CharacterListReceived?.Invoke(LoginProtocol.ReadCharacterListResponse(payload));
                    break;

                case LoginPacketOpcode.CharacterSelectResponse:
                    CharacterSelected?.Invoke(LoginProtocol.ReadCharacterSelectResponse(payload));
                    break;
            }
        });
    }

    private void OnGamePacketReceived(ushort opcode, byte[] payload)
    {
        // 게임 서버 패킷도 메인 스레드에서 역직렬화 후 이벤트로 전달
        _mainThreadQueue.Enqueue(() =>
        {
            switch ((GamePacketOpcode)opcode)
            {
                case GamePacketOpcode.EnterGameResponse:
                    EnterGameReceived?.Invoke(GameProtocol.ReadEnterGameResponse(payload));
                    break;

                case GamePacketOpcode.PlayerEnterMap:
                    PlayerEntered?.Invoke(GameProtocol.ReadPlayerEnterMap(payload));
                    break;

                case GamePacketOpcode.PlayerLeaveMap:
                    PlayerLeft?.Invoke(GameProtocol.ReadPlayerLeaveMap(payload));
                    break;

                case GamePacketOpcode.PlayerMove:
                    PlayerMoved?.Invoke(GameProtocol.ReadPlayerMove(payload));
                    break;

                case GamePacketOpcode.ChangeMapResponse:
                    MapChanged?.Invoke(GameProtocol.ReadChangeMapResponse(payload));
                    break;

                case GamePacketOpcode.MonsterEnterMap:
                    MonsterEntered?.Invoke(GameProtocol.ReadMonsterEnterMap(payload));
                    break;
            }
        });
    }

    private void OnDestroy()
    {
        // NetworkManager 파괴 시 열려 있는 세션 정리
        _loginSession?.Dispose();
        _gameSession?.Dispose();
    }
}

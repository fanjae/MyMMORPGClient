using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;

public sealed class NetworkManager : MonoBehaviour
{
    private readonly MainThreadPacketQueue _mainThreadQueue = new();
    private readonly ConcurrentQueue<Action> _disconnectQueue = new();
    private const int MaxPacketsPerFrame = 256;

    private TcpSession _loginSession;
    private TcpSession _gameSession;
    private MapGeometryData _pendingGeometry;
    private MapInfoData _mapBounds;

    public event Action<LoginResult> LoginCompleted;
    public event Action<CharacterListData> CharacterListReceived;
    public event Action<CharacterSelectData> CharacterSelected;

    public event Action<EnterGameData> EnterGameReceived;
    public event Action<PlayerEnterData> PlayerEntered;
    public event Action<uint> PlayerLeft;
    public event Action<PlayerMoveData> PlayerMoved;
    public event Action<MoveResponseData> MoveCompleted;
    public event Action<MapInfoData> MapInfoReceived;
    public event Action<MapGeometryData> GeometryReceived;
    public event Action<MovementSnapshot> MovementReceived;
    public event Action<PlayerChatData> PlayerChatReceived;
    public event Action<WhisperData> WhisperReceived;
    public event Action<ChatResponseData> ChatRejected;
    public event Action<MonsterEnterData> MonsterEntered;
    public event Action<ChangeMapData> MapChanged;
    public event Action LoginDisconnected;
    public event Action GameDisconnected;

    private void Update()
    {
        while (_disconnectQueue.TryDequeue(out Action disconnected))
            disconnected();

        // 한 프레임에 처리할 패킷 수를 제한해 누적 수신이 화면 갱신을 막지 않도록 한다.
        for (int count = 0; count < MaxPacketsPerFrame && _mainThreadQueue.TryDequeue(out Action action); ++count)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }
    }

    public async Task ConnectLoginServerAsync(string host, int port)
    {
        // 로그인 서버 전용 세션 생성 및 패킷 수신 콜백 등록
        _loginSession?.Dispose();
        TcpSession session = new();
        _loginSession = session;
        session.PacketReceived += (opcode, payload) => OnLoginPacketReceived(session, opcode, payload);
        session.Disconnected += () => _disconnectQueue.Enqueue(() => DisconnectSession(session, false));

        await session.ConnectAsync(host, port);
        if (!ReferenceEquals(_loginSession, session))
            throw new OperationCanceledException();
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
        _gameSession?.Dispose();
        TcpSession session = new();
        _gameSession = session;
        _pendingGeometry = null;
        session.PacketReceived += (opcode, payload) => OnGamePacketReceived(session, opcode, payload);
        session.Disconnected += () => _disconnectQueue.Enqueue(() => DisconnectSession(session, true));

        await session.ConnectAsync(host, port);
        if (!ReferenceEquals(_gameSession, session))
            throw new OperationCanceledException();

        // 로그인 서버에서 발급받은 인증 키로 게임 서버 입장 요청
        byte[] payload = GameProtocol.CreateEnterGameRequest(authKey);
        await session.SendAsync((ushort)GamePacketOpcode.EnterGameRequest, payload);
    }

    public Task SendMoveAsync(MoveRequestData data)
    {
        byte[] payload = GameProtocol.CreateMoveRequest(data);
        return _gameSession.SendAsync((ushort)GamePacketOpcode.MoveRequest, payload);
    }

    public Task ChangeMapAsync(uint mapId)
    {
        byte[] payload = GameProtocol.CreateChangeMapRequest(mapId);
        return _gameSession.SendAsync((ushort)GamePacketOpcode.ChangeMapRequest, payload);
    }

    public Task SendMovementInputAsync(MovementInputData data)
    {
        return _gameSession.SendAsync((ushort)GamePacketOpcode.MovementInput, PlatformProtocol.CreateInput(data));
    }

    public Task SendChatAsync(string message)
    {
        byte[] payload = GameProtocol.CreateChatRequest(message);
        return _gameSession.SendAsync((ushort)GamePacketOpcode.ChatRequest, payload);
    }

    public Task SendWhisperAsync(uint targetCharacterId, string message)
    {
        return _gameSession.SendAsync((ushort)GamePacketOpcode.WhisperRequest, GameProtocol.CreateWhisperRequest(targetCharacterId, message));
    }

    private void OnLoginPacketReceived(TcpSession session, ushort opcode, byte[] payload)
    {
        // Unity API와 이벤트 구독자가 메인 스레드에서 실행되도록 큐에 등록
        EnqueuePacket(session, false, payload.Length + TcpSession.HeaderSize, () =>
        {
            if (!ReferenceEquals(_loginSession, session))
                return;

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

    private void OnGamePacketReceived(TcpSession session, ushort opcode, byte[] payload)
    {
        // 게임 서버 패킷도 메인 스레드에서 역직렬화 후 이벤트로 전달
        EnqueuePacket(session, true, payload.Length + TcpSession.HeaderSize, () =>
        {
            if (!ReferenceEquals(_gameSession, session))
                return;

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

                case GamePacketOpcode.MoveResponse:
                    MoveCompleted?.Invoke(GameProtocol.ReadMoveResponse(payload));
                    break;

                case GamePacketOpcode.MapInfo:
                    _mapBounds = GameProtocol.ReadMapInfo(payload);
                    _pendingGeometry = null;
                    MapInfoReceived?.Invoke(_mapBounds);
                    break;

                case GamePacketOpcode.MapGeometry:
                    _pendingGeometry = PlatformProtocol.ReadGeometry(payload);
                    if (_pendingGeometry.MapId != _mapBounds.MapId)
                        throw new System.IO.InvalidDataException("Geometry without matching MapInfo");

                    _pendingGeometry.Bounds = _mapBounds;
                    break;

                case GamePacketOpcode.Foothold:
                    PlatformProtocol.AddFoothold(_pendingGeometry, payload);
                    break;

                case GamePacketOpcode.Collider:
                    PlatformProtocol.AddCollider(_pendingGeometry, payload);
                    break;

                case GamePacketOpcode.GeometryEnd:
                    PlatformProtocol.Complete(_pendingGeometry, payload);
                    GeometryReceived?.Invoke(_pendingGeometry);
                    _pendingGeometry = null;
                    break;

                case GamePacketOpcode.MovementState:
                    MovementReceived?.Invoke(PlatformProtocol.ReadState(payload));
                    break;

                case GamePacketOpcode.PlayerChat:
                    PlayerChatReceived?.Invoke(GameProtocol.ReadPlayerChat(payload));
                    break;
                case GamePacketOpcode.WhisperMessage:
                    WhisperReceived?.Invoke(GameProtocol.ReadWhisper(payload));
                    break;
                case GamePacketOpcode.ChatResponse:
                    ChatRejected?.Invoke(GameProtocol.ReadChatResponse(payload));
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

    private void EnqueuePacket(TcpSession session, bool game, int bytes, Action action)
    {
        if (!_mainThreadQueue.TryEnqueue(() =>
        {
            try { action(); }
            catch (System.IO.InvalidDataException exception)
            {
                DisconnectSession(session, game);
                Debug.LogException(exception);
            }
        }, bytes))
        {
            // 큐에 넣지 못한 연결만 종료하고 이전에 누적된 패킷은 세션 검사로 무시한다.
            session.Dispose();
            _disconnectQueue.Enqueue(() => DisconnectSession(session, game));
        }
    }

    private void DisconnectSession(TcpSession session, bool game)
    {
        if (game && ReferenceEquals(_gameSession, session))
        {
            _gameSession = null;
            _pendingGeometry = null;
            session.Dispose();
            GameDisconnected?.Invoke();
        }
        else if (!game && ReferenceEquals(_loginSession, session))
        {
            _loginSession = null;
            session.Dispose();
            LoginDisconnected?.Invoke();
        }
    }

    public void DisconnectAll()
    {
        if (_loginSession != null)
            DisconnectSession(_loginSession, false);
        if (_gameSession != null)
            DisconnectSession(_gameSession, true);
    }

    private void OnDestroy()
    {
        // NetworkManager 파괴 시 열려 있는 세션 정리
        _loginSession?.Dispose();
        _gameSession?.Dispose();
    }
}

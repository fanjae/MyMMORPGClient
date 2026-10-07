using System.Threading.Channels;

internal static class Program
{
    private static string _stage = "startup";

    private static async Task<int> Main(string[] args)
    {
        bool trace = args.Contains("--trace");
        if (args.Contains("--proxy"))
            return await ManualLatencyProxy.RunAsync(args);
        if (args.Contains("--proxy-smoke"))
            return await ManualProxySmoke.RunAsync();
        if (args.Contains("--reconciliation"))
            return ReconciliationTests.Run();
        if (args.Contains("--latency"))
            return await LatencyIntegration.RunAsync(args.FirstOrDefault(arg => arg.StartsWith("--latency-trace="))?.Substring("--latency-trace=".Length));
        if (args.Contains("--network"))
            return await NetworkReliability.RunAsync();
        if (args.Contains("--chat-unit"))
            return ChatTests.Run();
        if (args.Contains("--chat"))
            return await ChatIntegration.RunAsync();
        if (args.Contains("--load"))
            return await ServerLoadIntegration.RunAsync(args.FirstOrDefault(arg => arg.StartsWith("--db-delay-container="))?.Substring("--db-delay-container=".Length));
        if (args.Contains("--platform"))
            return await PlatformIntegration.RunAsync(args.FirstOrDefault(arg => arg.StartsWith("--physics-trace="))?.Substring("--physics-trace=".Length));

        try
        {
            _stage = "local correction and stale response isolation";
            CheckLocalMovement();
            Pass();

            using ClientPeer playerA = new("A", trace);
            using ClientPeer playerB = new("B", trace);

            _stage = "login and initial map packets";
            await playerA.EnterAsync("test", 1001);
            await playerA.ReadMonsterAsync();
            await playerB.EnterAsync("test2", 2001);
            await playerA.ReadPlayerAsync(playerB);
            await playerB.ReadPlayerAsync(playerA);
            await playerB.ReadMonsterAsync();
            Check(playerA.Players.Count == 1 && playerB.Players.Count == 1, "Initial remote player count");
            Pass();

            _stage = "UTF-8 chat in the same map";
            await playerA.SendChatAsync("안녕하세요");
            await playerA.ReadChatAsync(playerA.CharacterId, "안녕하세요");
            await playerB.ReadChatAsync(playerA.CharacterId, "안녕하세요");
            Pass();

            _stage = "50ms movement and idle";
            for (int i = 1; i <= 4; ++i)
            {
                await playerA.MoveAsync(i * 4, 0);
                await playerB.ReadMoveAsync(playerA);
                await Task.Delay(50);
            }
            await playerA.ExpectNoPacketsAsync();
            await playerB.ExpectNoPacketsAsync();
            Check(playerA.X == 16 && playerB.Players[1001].X == 16 && playerB.X == 0, "Idle positions");
            Pass();

            _stage = "absolute movement in both directions";
            await playerA.MoveAsync(24, 4);
            await playerB.ReadMoveAsync(playerA);
            await playerB.MoveAsync(8, -4);
            await playerA.ReadMoveAsync(playerB);
            Check(playerA.X == 24 && playerA.Y == 4, "Player B movement changed Player A position");
            Check(playerB.X == 8 && playerB.Y == -4, "Player A movement changed Player B position");
            Pass();

            _stage = "bounds and extreme coordinate rejection";
            await playerA.MoveAsync(401, 4, MoveResult.OutOfBounds);
            await playerA.MoveAsync(int.MaxValue, int.MinValue, MoveResult.OutOfBounds);
            await playerA.MoveAsync(int.MinValue, int.MaxValue, MoveResult.OutOfBounds);
            await playerB.ExpectNoPacketsAsync();
            Check(playerA.X == 24 && playerA.Y == 4, "Rejected movement changed position");
            Pass();

            _stage = "speed rejection and recovery";
            await playerA.MoveAsync(120, 45, MoveResult.SpeedExceeded);
            await playerB.ExpectNoPacketsAsync();
            await playerA.MoveAsync(28, 4);
            await playerB.ReadMoveAsync(playerA);
            Pass();

            _stage = "map and request sequence validation";
            await playerA.MoveAsync(28, 4, MoveResult.MapMismatch, 100000001);
            await playerA.MoveAsync(28, 4, MoveResult.InvalidSequence, sequence: 1);
            await playerB.ExpectNoPacketsAsync();
            Pass();

            _stage = "map leave and object state reset";
            await playerA.ChangeMapAsync(100000001, ChangeMapResult.Success);
            await playerB.ReadLeaveAsync(playerA.CharacterId);
            Check(playerA.X == 100 && playerA.Y == 50, "New map spawn");
            Check(playerA.Players.Count == 0 && playerA.Monsters.Count == 0, "Old map objects remain");
            Check(playerB.Players.Count == 0 && playerB.Monsters.Count == 1, "Remaining map state");
            Pass();

            _stage = "chat isolation between maps";
            await playerA.SendChatAsync("different map chat");
            await playerA.ReadChatAsync(playerA.CharacterId, "different map chat");
            await playerB.ExpectNoPacketsAsync();
            Pass();

            _stage = "movement isolation and map reentry";
            await playerA.MoveAsync(104, 50);
            await playerB.ExpectNoPacketsAsync();
            _stage = "bounded movement burst";
            await playerA.CheckBurstAsync();
            Pass();
            _stage = "movement isolation and map reentry";
            await playerA.ChangeMapAsync(100000000, ChangeMapResult.Success);
            await playerA.ReadPlayerAsync(playerB);
            await playerA.ReadMonsterAsync();
            await playerB.ReadPlayerAsync(playerA);
            Check(playerA.X == 0 && playerA.Y == 0, "Reentry spawn");
            Check(playerA.Players.Count == 1 && playerA.Monsters.Count == 1, "Reentry objects");
            Pass();

            _stage = "inclusive boundary and map reset";
            for (int x = 4; x <= 400; x += 4)
            {
                await playerA.MoveAsync(x, 0);
                await playerB.ReadMoveAsync(playerA);
                await Task.Delay(55);
            }
            await playerA.MoveAsync(401, 0, MoveResult.OutOfBounds);
            await playerB.ExpectNoPacketsAsync();
            await playerA.ChangeMapAsync(100000001, ChangeMapResult.Success);
            await playerB.ReadLeaveAsync(playerA.CharacterId);
            await playerA.ChangeMapAsync(100000000, ChangeMapResult.Success);
            await playerA.ReadPlayerAsync(playerB);
            await playerA.ReadMonsterAsync();
            await playerB.ReadPlayerAsync(playerA);
            Pass();

            _stage = "chat after reentry";
            await playerB.SendChatAsync("back together");
            await playerA.ReadChatAsync(playerB.CharacterId, "back together");
            await playerB.ReadChatAsync(playerB.CharacterId, "back together");
            Pass();

            _stage = "invalid map preserves state";
            await playerA.ChangeMapAsync(999999999, ChangeMapResult.MapNotFound);
            Check(playerA.MapId == 100000000 && playerA.X == 0 && playerA.Y == 0, "Invalid map changed local state");
            Check(playerA.Players.Count == 1 && playerA.Monsters.Count == 1, "Invalid map changed objects");
            await playerB.ExpectNoPacketsAsync();
            // 실패 응답 이후에도 기존 Map의 이동과 채팅이 유지되는지 서버 패킷으로 확인한다.
            await playerA.MoveAsync(10, 5);
            await playerB.ReadMoveAsync(playerA);
            await playerA.SendChatAsync("still in same map");
            await playerA.ReadChatAsync(playerA.CharacterId, "still in same map");
            await playerB.ReadChatAsync(playerA.CharacterId, "still in same map");
            Pass();

            _stage = "no duplicate packets and disconnect cleanup";
            await playerA.ExpectNoPacketsAsync();
            await playerB.ExpectNoPacketsAsync();
            playerA.Dispose();
            await playerB.ReadLeaveAsync(playerA.CharacterId);
            Check(playerB.Players.Count == 0 && playerB.Monsters.Count == 1, "Disconnect state");
            await playerB.ExpectNoPacketsAsync();
            Pass();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[FAIL] {_stage}: {exception.Message}");
            return 1;
        }
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private static void Pass()
    {
        Console.WriteLine($"[PASS] {_stage}");
    }

    private static void CheckLocalMovement()
    {
        LocalMovementState state = new();
        state.EnterMap(100000000, 0, 0);
        Check(state.TryBeginMove(100, 0, out MoveRequestData first), "Begin movement");
        Check(!state.TryBeginMove(200, 0, out _), "More than one outstanding move");
        MoveResponseData rejected = new() { MapId = first.MapId, Sequence = first.Sequence, Result = MoveResult.SpeedExceeded, X = 0, Y = 0 };
        Check(state.TryApplyResponse(rejected) && state.ServerX == 0 && !state.HasPendingMove, "Rejected movement correction");
        Check(state.TryBeginMove(4, 0, out MoveRequestData second), "Resume movement");
        Check(!state.TryApplyResponse(rejected) && state.HasPendingMove, "Previous response changed new request");
        MoveResponseData accepted = new() { MapId = second.MapId, Sequence = second.Sequence, Result = MoveResult.Success, X = 4, Y = 0 };
        Check(state.TryApplyResponse(accepted) && state.ServerX == 4, "Authoritative movement position");
        Check(state.TryBeginMove(8, 0, out MoveRequestData oldMapRequest), "Begin before map switch");
        state.EnterMap(100000001, 100, 50);
        state.EnterMap(100000000, 0, 0);
        Check(state.TryBeginMove(4, 0, out MoveRequestData newMapRequest), "Begin after map reentry");
        accepted.Sequence = oldMapRequest.Sequence;
        Check(!state.TryApplyResponse(accepted) && state.ServerX == 0 && state.HasPendingMove, "Old map response changed reentry position");
        Check(!state.CancelMove(oldMapRequest.Sequence) && state.CancelMove(newMapRequest.Sequence), "Old send completion canceled new request");
    }
}

internal sealed class PacketConnection : IDisposable
{
    private readonly TcpSession _session = new();
    private readonly Channel<(ushort Opcode, byte[] Payload)> _packets = Channel.CreateUnbounded<(ushort, byte[])>();
    private readonly string _name;
    private readonly bool _login;
    private readonly bool _trace;

    private string _lastSent = "none";

    internal PacketConnection(string name, bool login, bool trace)
    {
        _name = name;
        _login = login;
        _trace = trace;
        _session.PacketReceived += (opcode, payload) => _packets.Writer.TryWrite((opcode, payload));
        _session.Disconnected += () => _packets.Writer.TryComplete(new IOException($"{_name}: disconnected"));
    }

    internal async Task ConnectAsync(int port)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await _session.ConnectAsync("127.0.0.1", port).WaitAsync(timeout.Token);
    }

    internal async Task SendAsync(ushort opcode, byte[] payload)
    {
        _lastSent = OpcodeName(opcode);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await _session.SendAsync(opcode, payload).WaitAsync(timeout.Token);

        if (_trace)
            Console.WriteLine($"[TRACE] {_name} send {_lastSent} ({payload.Length} bytes)");
    }

    internal async Task<byte[]> ReceiveAsync(ushort expectedOpcode)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        try
        {
            var packet = await _packets.Reader.ReadAsync(timeout.Token);
            Program.Check(packet.Opcode == expectedOpcode, $"{_name}: expected {OpcodeName(expectedOpcode)}, received {OpcodeName(packet.Opcode)} ({packet.Payload.Length} bytes), last sent {_lastSent}");

            if (_trace)
                Console.WriteLine($"[TRACE] {_name} receive {OpcodeName(packet.Opcode)} ({packet.Payload.Length} bytes)");

            return packet.Payload;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"{_name}: waiting for {OpcodeName(expectedOpcode)}, last sent {_lastSent}");
        }
    }

    internal async Task ExpectNoPacketsAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(300));

        try
        {
            if (await _packets.Reader.WaitToReadAsync(timeout.Token))
            {
                _packets.Reader.TryRead(out var packet);
                throw new InvalidDataException($"{_name}: unexpected {OpcodeName(packet.Opcode)} ({packet.Payload.Length} bytes), last sent {_lastSent}");
            }

            throw new IOException($"{_name}: disconnected during idle check");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private string OpcodeName(ushort opcode)
    {
        return _login ? ((LoginPacketOpcode)opcode).ToString() : ((GamePacketOpcode)opcode).ToString();
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}

internal sealed class ClientPeer : IDisposable
{
    private readonly PacketConnection _game;
    private readonly string _name;
    private readonly bool _trace;
    private ulong _sequence;
    private MapInfoData _mapInfo;

    internal uint CharacterId { get; private set; }
    internal uint MapId { get; private set; }
    internal int X { get; private set; }
    internal int Y { get; private set; }
    internal Dictionary<uint, PlayerMoveData> Players { get; } = new();
    internal HashSet<uint> Monsters { get; } = new();

    internal ClientPeer(string name, bool trace)
    {
        _name = name;
        _trace = trace;
        _game = new PacketConnection($"{name}/Game", false, trace);
    }

    internal async Task EnterAsync(string loginId, uint characterId)
    {
        using PacketConnection login = new($"{_name}/Login", true, _trace);
        await login.ConnectAsync(7776);
        await login.SendAsync((ushort)LoginPacketOpcode.LoginRequest, LoginProtocol.CreateLoginRequest(loginId, "test1234"));
        LoginResult loginResult = LoginProtocol.ReadLoginResponse(await login.ReceiveAsync((ushort)LoginPacketOpcode.LoginResponse));
        Program.Check(loginResult == LoginResult.Success, $"{_name}: login result {loginResult}");
        await login.SendAsync((ushort)LoginPacketOpcode.CharacterListRequest, Array.Empty<byte>());
        CharacterListData list = LoginProtocol.ReadCharacterListResponse(await login.ReceiveAsync((ushort)LoginPacketOpcode.CharacterListResponse));
        Program.Check(list.Result == CharacterListResult.Success && list.Characters.Any(character => character.CharacterId == characterId), $"{_name}: missing character {characterId}");
        await login.SendAsync((ushort)LoginPacketOpcode.CharacterSelectRequest, LoginProtocol.CreateCharacterSelectRequest(characterId));
        CharacterSelectData ticket = LoginProtocol.ReadCharacterSelectResponse(await login.ReceiveAsync((ushort)LoginPacketOpcode.CharacterSelectResponse));
        Program.Check(ticket.Result == CharacterSelectResult.Success, $"{_name}: character select result {ticket.Result}");
        await _game.ConnectAsync(ticket.GameServerPort);
        await _game.SendAsync((ushort)GamePacketOpcode.EnterGameRequest, GameProtocol.CreateEnterGameRequest(ticket.AuthKey));
        EnterGameData entered = GameProtocol.ReadEnterGameResponse(await _game.ReceiveAsync((ushort)GamePacketOpcode.EnterGameResponse));
        Program.Check(entered.Result == EnterGameResult.Success && entered.CharacterId == characterId, $"{_name}: enter result {entered.Result}, character {entered.CharacterId}");
        CharacterId = entered.CharacterId;
        X = entered.X;
        Y = entered.Y;
        Program.Check(X == 0 && Y == 0, $"{_name}: initial spawn ({X}, {Y})");
        await ReadMapInfoAsync(100000000);
    }

    internal async Task ReadPlayerAsync(ClientPeer player)
    {
        PlayerEnterData data = GameProtocol.ReadPlayerEnterMap(await _game.ReceiveAsync((ushort)GamePacketOpcode.PlayerEnterMap));
        Program.Check(data.CharacterId == player.CharacterId && data.X == player.X && data.Y == player.Y, $"{_name}: unexpected player entry {data.CharacterId} ({data.X}, {data.Y})");
        Program.Check(Players.TryAdd(data.CharacterId, new PlayerMoveData { CharacterId = data.CharacterId, X = data.X, Y = data.Y }), $"{_name}: duplicate player {data.CharacterId}");
    }

    internal async Task ReadMonsterAsync()
    {
        MonsterEnterData data = GameProtocol.ReadMonsterEnterMap(await _game.ReceiveAsync((ushort)GamePacketOpcode.MonsterEnterMap));
        Program.Check(data.MonsterId == 1 && data.X == 50 && data.Y == 20, $"{_name}: unexpected monster {data.MonsterId} ({data.X}, {data.Y})");
        Program.Check(Monsters.Add(data.MonsterId), $"{_name}: duplicate monster {data.MonsterId}");
    }

    internal async Task MoveAsync(int x, int y, MoveResult expectedResult = MoveResult.Success, uint? mapId = null, ulong? sequence = null)
    {
        MoveRequestData request = new() { MapId = mapId ?? MapId, Sequence = sequence ?? ++_sequence, X = x, Y = y };
        await _game.SendAsync((ushort)GamePacketOpcode.MoveRequest, GameProtocol.CreateMoveRequest(request));
        MoveResponseData data = GameProtocol.ReadMoveResponse(await _game.ReceiveAsync((ushort)GamePacketOpcode.MoveResponse));
        Program.Check(data.Sequence == request.Sequence && data.MapId == MapId && data.Result == expectedResult, $"{_name}: move result {data.Result}, expected {expectedResult}, sequence {data.Sequence}");
        int expectedX = expectedResult == MoveResult.Success ? x : X;
        int expectedY = expectedResult == MoveResult.Success ? y : Y;
        Program.Check(data.X == expectedX && data.Y == expectedY, $"{_name}: authoritative position ({data.X}, {data.Y}), expected ({expectedX}, {expectedY})");
        X = data.X;
        Y = data.Y;
    }

    internal async Task CheckBurstAsync()
    {
        await Task.Delay(300);
        int startX = X;
        int startY = Y;
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        ulong firstSequence = _sequence + 1;

        // 응답 대기 없이 요청을 모아 보내 고빈도 요청에도 허용량 상한이 유지되는지 확인한다.
        for (int i = 1; i <= 20; ++i)
        {
            MoveRequestData request = new() { MapId = MapId, Sequence = ++_sequence, X = startX + i * 4, Y = startY };
            await _game.SendAsync((ushort)GamePacketOpcode.MoveRequest, GameProtocol.CreateMoveRequest(request));
        }

        int rejected = 0;
        for (int i = 0; i < 20; ++i)
        {
            MoveResponseData data = GameProtocol.ReadMoveResponse(await _game.ReceiveAsync((ushort)GamePacketOpcode.MoveResponse));
            Program.Check(data.Sequence == firstSequence + (ulong)i && data.MapId == MapId, "Burst response sequence or map");
            Program.Check(data.Result == MoveResult.Success || data.Result == MoveResult.SpeedExceeded, $"Unexpected burst result {data.Result}");
            if (data.Result == MoveResult.SpeedExceeded)
                ++rejected;

            X = data.X;
            Y = data.Y;
        }

        Program.Check(rejected > 0 && X - startX <= _mapInfo.MoveBurst + _mapInfo.MoveSpeed * elapsed.Elapsed.TotalSeconds, "Burst exceeded total allowed distance");
        await MoveAsync(startX + 80, startY, MoveResult.SpeedExceeded);
    }

    private async Task ReadMapInfoAsync(uint expectedMapId)
    {
        _mapInfo = GameProtocol.ReadMapInfo(await _game.ReceiveAsync((ushort)GamePacketOpcode.MapInfo));
        Program.Check(_mapInfo.MapId == expectedMapId && _mapInfo.MoveSpeed == 80 && _mapInfo.MoveBurst == 12, $"{_name}: map settings");
        Program.Check(_mapInfo.MinX <= X && X <= _mapInfo.MaxX && _mapInfo.MinY <= Y && Y <= _mapInfo.MaxY, $"{_name}: spawn outside bounds");
        MapId = _mapInfo.MapId;
        MapGeometryData geometry = PlatformProtocol.ReadGeometry(await _game.ReceiveAsync((ushort)GamePacketOpcode.MapGeometry));
        Program.Check(geometry.Mode == MovementMode.Free, "Legacy movement tests require the documented Free fixture; use --platform for production geometry");
        Program.Check(geometry.MapId == MapId, "Geometry map mismatch");
        for (int i = 0; i < geometry.FootholdCount; ++i)
            PlatformProtocol.AddFoothold(geometry, await _game.ReceiveAsync((ushort)GamePacketOpcode.Foothold));

        for (int i = 0; i < geometry.ColliderCount; ++i)
            PlatformProtocol.AddCollider(geometry, await _game.ReceiveAsync((ushort)GamePacketOpcode.Collider));

        PlatformProtocol.Complete(geometry, await _game.ReceiveAsync((ushort)GamePacketOpcode.GeometryEnd));
    }

    internal async Task ReadMoveAsync(ClientPeer player)
    {
        PlayerMoveData data = GameProtocol.ReadPlayerMove(await _game.ReceiveAsync((ushort)GamePacketOpcode.PlayerMove));
        Program.Check(data.CharacterId == player.CharacterId && data.X == player.X && data.Y == player.Y, $"{_name}: unexpected movement {data.CharacterId} ({data.X}, {data.Y})");
        Program.Check(Players.ContainsKey(data.CharacterId), $"{_name}: movement before player entry");
        Players[data.CharacterId] = data;
    }

    internal async Task ChangeMapAsync(uint mapId, ChangeMapResult expectedResult)
    {
        await _game.SendAsync((ushort)GamePacketOpcode.ChangeMapRequest, GameProtocol.CreateChangeMapRequest(mapId));
        ChangeMapData data = GameProtocol.ReadChangeMapResponse(await _game.ReceiveAsync((ushort)GamePacketOpcode.ChangeMapResponse));
        Program.Check(data.Result == expectedResult, $"{_name}: map result {data.Result}, expected {expectedResult}");

        if (data.Result != ChangeMapResult.Success)
        {
            Program.Check(data.MapId == MapId && data.X == X && data.Y == Y, $"{_name}: failed map response position");
            return;
        }

        Program.Check(data.MapId == mapId, $"{_name}: unexpected map {data.MapId}");
        MapId = data.MapId;
        X = data.X;
        Y = data.Y;
        // Unity 화면과 별개로 패킷 기준 상태를 관리해 Map-local 수신 순서와 중복을 검증한다.
        Players.Clear();
        Monsters.Clear();
        await ReadMapInfoAsync(mapId);
    }

    internal async Task ReadLeaveAsync(uint characterId)
    {
        uint leftId = GameProtocol.ReadPlayerLeaveMap(await _game.ReceiveAsync((ushort)GamePacketOpcode.PlayerLeaveMap));
        Program.Check(leftId == characterId && Players.Remove(leftId), $"{_name}: unexpected player leave {leftId}");
    }

    internal Task SendChatAsync(string message)
    {
        return _game.SendAsync((ushort)GamePacketOpcode.ChatRequest, GameProtocol.CreateChatRequest(message));
    }

    internal async Task ReadChatAsync(uint characterId, string message)
    {
        PlayerChatData data = GameProtocol.ReadPlayerChat(await _game.ReceiveAsync((ushort)GamePacketOpcode.PlayerChat));
        Program.Check(data.CharacterId == characterId && data.Message == message, $"{_name}: chat sender or message mismatch");
    }

    internal Task ExpectNoPacketsAsync()
    {
        return _game.ExpectNoPacketsAsync();
    }

    public void Dispose()
    {
        _game.Dispose();
    }
}

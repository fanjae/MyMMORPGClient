using System.Diagnostics;
using System.Threading.Channels;

internal static class PlatformIntegration
{
    private static string _stage = "startup";

    internal static async Task<int> RunAsync(string tracePath)
    {
        try
        {
            using PlatformPeer a = new();
            using PlatformPeer b = new();
            _stage = "protocol mismatch preserves authentication ticket";
            await a.EnterAsync("test", 1001, true);
            Pass();

            if (tracePath != null)
            {
                _stage = "2000 C++ and C# physics ticks agree";
                CheckPhysicsTrace(a.Geometry, tracePath);
                Pass();
            }
            _stage = "geometry completion before world state and same-map entry";
            await b.EnterAsync("test2", 2001, false);
            await a.WaitAsync(() => a.Players.Contains(2001));
            await b.WaitAsync(() => b.Players.Contains(1001) && b.Monsters.Contains(1));
            Program.Check(a.Geometry.Mode == MovementMode.Platformer && a.Geometry.Footholds.Count == 5 && a.Geometry.Colliders.Count == 1, "Initial geometry");
            Program.Check(a.Local.X == 0 && b.Local.X == 0, "Initial spawn");
            Pass();

            _stage = "authoritative movement and remote isolation";
            await a.InputAsync(1, false);
            await a.WaitAsync(() => a.Local.X >= 8);
            await a.InputAsync(0, false);
            await a.PumpAsync(200);
            await b.PumpAsync(80);
            Program.Check(a.Local.X > 0 && a.Local.X <= 24 && b.Local.X == 0 && b.States[1001].X == a.Local.X, "Movement changed another player or remote state missing");
            double stopped = a.Local.X;
            await a.PumpAsync(150);
            Program.Check(a.Local.X == stopped && a.Local.VelocityX == 0, "Released input kept moving");
            Pass();

            _stage = "input lease expiry without heartbeat";
            await a.InputAsync(1, false);
            await a.PumpAsync(500);
            double expired = a.Local.X;
            Program.Check(expired > stopped && expired - stopped <= 21 && a.Local.VelocityX == 0 && a.SawExpiry, "Input lease did not stop movement");
            await a.PumpAsync(120);
            Program.Check(a.Local.X == expired, "Expired input continued moving");
            Pass();

            _stage = "invalid direction, sequence and generation rejection";
            await a.InputAsync(2, false);
            await a.WaitAsync(() => a.Rejections >= 1);
            await a.InputAsync(1, false, sequence: 1);
            await a.WaitAsync(() => a.Rejections >= 2);
            await a.InputAsync(1, false, generation: a.Geometry.Generation - 1);
            await a.WaitAsync(() => a.Rejections >= 3);
            await a.PumpAsync(100);
            Program.Check(a.Local.X == expired, "Invalid input changed position");
            Pass();

            _stage = "absolute movement disabled on platform map";
            await a.SendAsync(GamePacketOpcode.MoveRequest, GameProtocol.CreateMoveRequest(new MoveRequestData { MapId = a.Geometry.MapId, Sequence = 1, X = 200, Y = 100 }));
            await a.WaitAsync(() => a.LastMove.HasValue);
            Program.Check(a.LastMove.Value.Result == MoveResult.WrongMovementMode && a.Local.X == expired, "Absolute movement bypassed physics");
            Pass();

            _stage = "jump, landing and C# prediction match server ticks";
            await b.InputAsync(0, true);
            await b.WaitAsync(() => !b.Local.Grounded);
            MovementSnapshot previous = b.Local;
            PlatformSimulation simulation = new(b.Geometry);
            double highest = previous.Y;
            int compared = 0;
            Stopwatch jumping = Stopwatch.StartNew();
            while (jumping.ElapsedMilliseconds < 1400)
            {
                await b.InputAsync(0, true);
                await b.PumpAsync(45);
                MovementSnapshot current = b.Local;
                if (current.ServerTick > previous.ServerTick)
                {
                    PlatformState predicted = new() { X = previous.X, Y = previous.Y, VelocityX = previous.VelocityX, VelocityY = previous.VelocityY, FootholdId = previous.FootholdId, Grounded = previous.Grounded, JumpHeld = true };
                    for (ulong tick = previous.ServerTick; tick < current.ServerTick; ++tick)
                        simulation.Step(ref predicted, 0, true);

                    Program.Check(Math.Abs(predicted.X - current.X) < 0.01 && Math.Abs(predicted.Y - current.Y) < 0.01 && Math.Abs(predicted.VelocityY - current.VelocityY) < 0.01 && predicted.FootholdId == current.FootholdId && predicted.Grounded == current.Grounded, "Prediction differs from server physics");
                    ++compared;
                }

                highest = Math.Max(highest, current.Y);
                previous = current;
            }

            Program.Check(compared >= 10 && highest > 40 && b.Local.Grounded && b.Local.Y == 0, "Jump did not land or held key repeated jump");
            await b.InputAsync(0, false);
            Pass();

            _stage = "short jump press and release preserved between ticks";
            await b.InputAsync(0, true);
            await b.InputAsync(0, false);
            await b.WaitAsync(() => !b.Local.Grounded && b.Local.Y > 0);
            await b.PumpAsync(1200);
            Program.Check(b.Local.Grounded, "Short jump did not land");
            Pass();

            _stage = "solid wall stops held movement and remote agrees";
            Stopwatch walking = Stopwatch.StartNew();
            while (walking.ElapsedMilliseconds < 3000)
            {
                await a.InputAsync(1, false);
                await a.PumpAsync(45);
            }

            await a.InputAsync(0, false);
            await a.PumpAsync(120);
            await b.PumpAsync(100);
            Program.Check(a.Local.X == 214 && a.Local.VelocityX == 0 && b.States[1001].X == 214, "Body crossed wall or remote differs");
            Pass();

            _stage = "map transition, free movement and chat isolation";
            ulong oldGeneration = a.Geometry.Generation;
            await a.ChangeAsync(100000001);
            await b.WaitAsync(() => !b.Players.Contains(1001));
            Program.Check(a.Geometry.Mode == MovementMode.Free && a.Players.Count == 0 && a.Monsters.Count == 0, "Map transition retained old world objects");
            await a.SendAsync(GamePacketOpcode.MoveRequest, GameProtocol.CreateMoveRequest(new MoveRequestData { MapId = 100000001, Sequence = 2, X = 104, Y = 50 }));
            await a.WaitAsync(() => a.LastMove.HasValue);
            Program.Check(a.LastMove.Value.Result == MoveResult.Success, "Free map movement regression");
            await a.SendAsync(GamePacketOpcode.ChatRequest, GameProtocol.CreateChatRequest("isolated"));
            await a.WaitAsync(() => a.Chat.Count == 1);
            await b.PumpAsync(150);
            Program.Check(b.Chat.Count == 0 && !b.States.ContainsKey(1001), "Cross-map state/chat leak");
            Pass();

            _stage = "same-map reentry rejects previous generation";
            await a.ChangeAsync(100000000);
            await a.WaitAsync(() => a.Players.Contains(2001) && a.Monsters.Contains(1) && a.States.ContainsKey(1001));
            await b.WaitAsync(() => b.Players.Contains(1001));
            Program.Check(a.Geometry.Generation > oldGeneration && a.Local.X == 0 && a.Local.Y == 0, "Reentry generation/spawn");
            await a.InputAsync(1, false, generation: oldGeneration);
            await a.WaitAsync(() => a.Rejections >= 4);
            await a.PumpAsync(100);
            Program.Check(a.Local.X == 0, "Previous entry input replayed");
            Pass();

            _stage = "invalid map preserves geometry and current state";
            ulong generation = a.Geometry.Generation;
            await a.ChangeAsync(999999999, ChangeMapResult.MapNotFound);
            Program.Check(a.Geometry.Generation == generation && a.Geometry.MapId == 100000000 && a.Local.X == 0 && a.Players.Count == 1 && a.Monsters.Count == 1, "Failed map change reset world");
            await a.SendAsync(GamePacketOpcode.ChatRequest, GameProtocol.CreateChatRequest("together"));
            await b.WaitAsync(() => b.Chat.Count == 1);
            Pass();

            _stage = "disconnect removes remote player";
            a.Dispose();
            await b.WaitAsync(() => !b.Players.Contains(1001));
            Program.Check(!b.States.ContainsKey(1001) && b.Monsters.Count == 1, "Disconnect left stale state");
            Pass();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[FAIL] {_stage}: {exception.Message}");
            return 1;
        }
    }

    private static void Pass()
    {
        Console.WriteLine($"[PASS] {_stage}");
    }

    private static void CheckPhysicsTrace(MapGeometryData geometry, string path)
    {
        PlatformSimulation simulation = new(geometry);
        PlatformState state = default;
        int previousCase = -1;
        int count = 0;
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] fields = line.Split(',');
            int scenario = int.Parse(fields[0]);
            if (scenario != previousCase)
                simulation.Reset(ref state);

            previousCase = scenario;
            simulation.Step(ref state, int.Parse(fields[2]), fields[3] == "1");
            double[] actual = { state.X, state.Y, state.VelocityX, state.VelocityY };
            for (int i = 0; i < actual.Length; ++i)
                Program.Check(Math.Abs(actual[i] - double.Parse(fields[i + 4], System.Globalization.CultureInfo.InvariantCulture)) < 0.000001, $"Physics trace case {scenario} tick {fields[1]} field {i}");

            Program.Check(state.FootholdId == uint.Parse(fields[8]) && state.Grounded == (fields[9] == "1"), "Trace support state");
            ++count;
        }

        Program.Check(count == 2000, "Missing physics trace rows");
    }
}

internal sealed class PlatformPeer : IDisposable
{
    private readonly TcpSession _session = new();
    private readonly Channel<(GamePacketOpcode Opcode, byte[] Payload)> _packets = Channel.CreateUnbounded<(GamePacketOpcode, byte[])>();
    private ulong _sequence;
    private MapGeometryData _pending;
    private MapInfoData _bounds;
    private ChangeMapData? _change;
    internal uint CharacterId;
    internal MapGeometryData Geometry;
    internal readonly HashSet<uint> Players = new();
    internal readonly HashSet<uint> Monsters = new();
    internal readonly Dictionary<uint, MovementSnapshot> States = new();
    internal readonly List<PlayerChatData> Chat = new();
    internal MoveResponseData? LastMove;
    internal int Rejections;
    internal bool SawExpiry;
    internal MovementSnapshot Local => States[CharacterId];

    internal PlatformPeer()
    {
        _session.PacketReceived += (opcode, payload) => _packets.Writer.TryWrite(((GamePacketOpcode)opcode, payload));
        _session.Disconnected += () => _packets.Writer.TryComplete(new IOException("Game disconnected"));
    }

    internal async Task EnterAsync(string loginId, uint characterId, bool testVersion)
    {
        using PacketConnection login = new("platform/login", true, false);
        await login.ConnectAsync(7776);
        await login.SendAsync((ushort)LoginPacketOpcode.LoginRequest, LoginProtocol.CreateLoginRequest(loginId, "test1234"));
        Program.Check(LoginProtocol.ReadLoginResponse(await login.ReceiveAsync((ushort)LoginPacketOpcode.LoginResponse)) == LoginResult.Success, "Login");
        await login.SendAsync((ushort)LoginPacketOpcode.CharacterSelectRequest, LoginProtocol.CreateCharacterSelectRequest(characterId));
        CharacterSelectData ticket = LoginProtocol.ReadCharacterSelectResponse(await login.ReceiveAsync((ushort)LoginPacketOpcode.CharacterSelectResponse));
        Program.Check(ticket.Result == CharacterSelectResult.Success, "Character selection");
        await _session.ConnectAsync("127.0.0.1", ticket.GameServerPort);
        if (testVersion)
        {
            await SendAsync(GamePacketOpcode.EnterGameRequest, BitConverter.GetBytes(ticket.AuthKey));
            var rejected = await ReceiveAsync();
            Program.Check(rejected.Opcode == GamePacketOpcode.EnterGameResponse && GameProtocol.ReadEnterGameResponse(rejected.Payload).Result == EnterGameResult.ProtocolMismatch, "Old protocol not rejected explicitly");
        }

        await SendAsync(GamePacketOpcode.EnterGameRequest, GameProtocol.CreateEnterGameRequest(ticket.AuthKey));
        var packet = await ReceiveAsync();
        Program.Check(packet.Opcode == GamePacketOpcode.EnterGameResponse, "Enter response order");
        EnterGameData enter = GameProtocol.ReadEnterGameResponse(packet.Payload);
        Program.Check(enter.Result == EnterGameResult.Success && enter.CharacterId == characterId, "Entry");
        CharacterId = characterId;
        await WaitAsync(() => Geometry != null && States.ContainsKey(characterId));
    }

    internal Task SendAsync(GamePacketOpcode opcode, byte[] payload)
    {
        return _session.SendAsync((ushort)opcode, payload);
    }

    internal Task InputAsync(sbyte horizontal, bool jump, ulong? sequence = null, ulong? generation = null)
    {
        MovementInputData input = new() { MapId = Geometry.MapId, Generation = generation ?? Geometry.Generation, Sequence = sequence ?? ++_sequence, Horizontal = horizontal, JumpHeld = jump };
        return SendAsync(GamePacketOpcode.MovementInput, PlatformProtocol.CreateInput(input));
    }

    internal async Task ChangeAsync(uint mapId, ChangeMapResult result = ChangeMapResult.Success)
    {
        _change = null;
        await SendAsync(GamePacketOpcode.ChangeMapRequest, GameProtocol.CreateChangeMapRequest(mapId));
        await WaitAsync(() => _change.HasValue && _change.Value.Result == result && (result != ChangeMapResult.Success || Geometry != null));
    }

    internal async Task WaitAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
            Handle(await _packets.Reader.ReadAsync(timeout.Token));
    }

    internal async Task PumpAsync(int milliseconds)
    {
        using CancellationTokenSource timeout = new(milliseconds);
        try
        {
            while (true)
                Handle(await _packets.Reader.ReadAsync(timeout.Token));
        }
        catch (OperationCanceledException) { }
    }

    private async Task<(GamePacketOpcode Opcode, byte[] Payload)> ReceiveAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        return await _packets.Reader.ReadAsync(timeout.Token);
    }

    private void Handle((GamePacketOpcode Opcode, byte[] Payload) packet)
    {
        switch (packet.Opcode)
        {
            case GamePacketOpcode.MapInfo:
                _bounds = GameProtocol.ReadMapInfo(packet.Payload);
                break;
            case GamePacketOpcode.MapGeometry:
                _pending = PlatformProtocol.ReadGeometry(packet.Payload);
                Program.Check(_pending.MapId == _bounds.MapId, "Geometry order");
                _pending.Bounds = _bounds;
                break;
            case GamePacketOpcode.Foothold:
                PlatformProtocol.AddFoothold(_pending, packet.Payload);
                break;
            case GamePacketOpcode.Collider:
                PlatformProtocol.AddCollider(_pending, packet.Payload);
                break;
            case GamePacketOpcode.GeometryEnd:
                PlatformProtocol.Complete(_pending, packet.Payload);
                Geometry = _pending;
                _pending = null;
                break;
            case GamePacketOpcode.MovementState:
                MovementSnapshot state = PlatformProtocol.ReadState(packet.Payload);
                Program.Check(Geometry != null && state.MapId == Geometry.MapId && state.Generation == Geometry.Generation, "State before geometry or wrong entry");
                Program.Check(state.CharacterId == CharacterId || Players.Contains(state.CharacterId), "State before PlayerEnter");
                if (States.TryGetValue(state.CharacterId, out MovementSnapshot previous))
                    Program.Check(state.ServerTick >= previous.ServerTick, "Tick regressed");

                States[state.CharacterId] = state;
                if (state.CharacterId == CharacterId && state.Reason == MovementStateReason.InputRejected)
                    ++Rejections;
                if (state.CharacterId == CharacterId && state.Reason == MovementStateReason.InputExpired)
                    SawExpiry = true;
                break;
            case GamePacketOpcode.PlayerEnterMap:
                Program.Check(Geometry != null && Players.Add(GameProtocol.ReadPlayerEnterMap(packet.Payload).CharacterId), "Duplicate entry or world before geometry");
                break;
            case GamePacketOpcode.PlayerLeaveMap:
                uint id = GameProtocol.ReadPlayerLeaveMap(packet.Payload);
                Program.Check(Players.Remove(id), "Duplicate leave");
                States.Remove(id);
                break;
            case GamePacketOpcode.MonsterEnterMap:
                Program.Check(Geometry != null && Monsters.Add(GameProtocol.ReadMonsterEnterMap(packet.Payload).MonsterId), "Duplicate monster");
                break;
            case GamePacketOpcode.ChangeMapResponse:
                _change = GameProtocol.ReadChangeMapResponse(packet.Payload);
                if (_change.Value.Result == ChangeMapResult.Success)
                {
                    Geometry = null;
                    Players.Clear();
                    Monsters.Clear();
                    States.Clear();
                    LastMove = null;
                    Chat.Clear();
                }
                break;
            case GamePacketOpcode.MoveResponse:
                LastMove = GameProtocol.ReadMoveResponse(packet.Payload);
                break;
            case GamePacketOpcode.PlayerChat:
                Chat.Add(GameProtocol.ReadPlayerChat(packet.Payload));
                break;
            default:
                throw new InvalidDataException($"Unexpected opcode {packet.Opcode}");
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}

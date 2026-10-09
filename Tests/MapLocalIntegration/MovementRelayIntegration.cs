using System.Diagnostics;
using System.Threading.Channels;

internal static class MovementRelayIntegration
{
    internal static async Task<int> RunAsync(int port, int delay = 0, int jitter = 0)
    {
        try
        {
            await using DelayedGameProxy proxyA = new(delay, jitter, targetPort: port);
            await using DelayedGameProxy proxyB = new(delay, jitter, targetPort: port);
            using RelayPeer a = new(), b = new();
            await a.ConnectAsync(proxyA.Port); await b.ConnectAsync(proxyB.Port);
            int settle = 400 + delay * 4 + jitter * 2;
            await Pump(settle);
            Program.Check(a.CharacterId == 1001 && b.States.ContainsKey(1001), "Bootstrap identity or peer snapshot missing");
            ClientMovementActions owner = new(); owner.Configure(a.Geometry);
            RemoteMovementActions remote = new(); remote.Configure(b.Geometry, b.States[1001], 0);
            Stopwatch clock = Stopwatch.StartNew();
            b.ActionReceived += relay => { if (relay.CharacterId == 1001) remote.Add(relay, clock.Elapsed.TotalSeconds); };
            owner.SetInput(0, true);
            double previous = 0, remotePeak = 0;
            List<double> sent = new();
            while (clock.Elapsed.TotalSeconds < 1.6)
            {
                double now = clock.Elapsed.TotalSeconds;
                owner.SetInput(0, now < 0.4); // 공중에서 반복 눌림이 와도 점프는 한 번이다.
                owner.Advance(now - previous); previous = now;
                if (owner.TryCreateBatch(now, out var batch)) { await a.SendAsync(batch); sent.Add(now); }
                a.Pump(); b.Pump(); remote.Sample(now, out _, out double y); remotePeak = Math.Max(remotePeak, y);
                await Task.Delay(10);
            }
            await Pump(settle);
            Program.Check(owner.JumpId == 1 && owner.State.Grounded && a.States[1001].Grounded && b.States[1001].Grounded,
                "Client landing did not update both server-relayed views");
            Program.Check(remotePeak > 10 && b.Events.Count(e => e.Action.Kind == MovementActionKind.Jump) == 1 &&
                b.Events.Count(e => e.Action.Kind == MovementActionKind.Land) == 1, "Remote jump simulation or exactly-once events failed");
            foreach (double start in sent)
                Program.Check(sent.Count(t => t >= start && t < start + 1 - 1e-8) <= 5, "Client exceeded five messages/second");
            Console.WriteLine("[PASS] TCP client-owned jump, remote simulation, landing relay and five-message budget");

            ulong tick = owner.Tick + 1;
            MovementActionData jump = new()
            {
                Sequence = 1000, ClientTick = tick, JumpId = 2, Kind = MovementActionKind.Jump,
                State = new PlatformState { Y = 3.04, VelocityY = 152, Grounded = false }
            };
            await a.SendManualAsync(jump, 1000); await Pump(settle);
            Program.Check(!b.States[1001].Grounded && b.States[1001].Sequence == 1000, "Second jump missing");
            int messagesBeforeSilence = b.MovementPackets;
            await Pump(1200);
            Program.Check(!b.States[1001].Grounded && b.MovementPackets == messagesBeforeSilence,
                "Server computed landing or generated periodic states without owner report");
            jump.Sequence = 1001; jump.ClientTick = ++tick; jump.JumpId = 3;
            await a.SendManualAsync(jump, 1001); await Pump(settle);
            Program.Check(b.States[1001].Sequence == 1000, "Airborne re-jump was broadcast");
            Console.WriteLine("[PASS] TCP server keeps jump locked through silence and rejects airborne re-jump");

            MovementActionData land = new()
            {
                Sequence = 1002, ClientTick = ++tick, JumpId = 2, Kind = MovementActionKind.Land,
                State = new PlatformState { Grounded = true, FootholdId = 1 }
            };
            await a.SendManualAsync(land, 1002); await Pump(settle);
            Program.Check(b.States[1001].Grounded && b.States[1001].Sequence == 1002, "Landing report rejected");
            jump.Sequence = 1003; jump.ClientTick = ++tick; jump.JumpId = 2;
            await a.SendManualAsync(jump, 1003); await Pump(settle);
            Program.Check(b.States[1001].Sequence == 1002, "Old jump replayed after landing");
            jump.Sequence = 1004; jump.ClientTick = ++tick; jump.JumpId = 3;
            await a.SendManualAsync(jump, 1004); await Pump(settle);
            Program.Check(b.States[1001].Sequence == 1004 && !b.States[1001].Grounded, "Fresh post-landing jump rejected");
            Console.WriteLine("[PASS] TCP matching landing unlocks a new jump and never resurrects the previous jump");
            var sentTraffic = a.SendTraffic;
            var receivedTraffic = b.ReceiveTraffic;
            Program.Check(sentTraffic.SentPackets == a.SentPackets && sentTraffic.SentBytes == a.SentBytes && sentTraffic.QueuedSendBytes == 0 &&
                receivedTraffic.ReceivedPackets == b.MovementPackets && receivedTraffic.ReceivedBytes == b.MovementBytes,
                "TCP movement traffic differs from complete sent/parsed frames");
            Console.WriteLine("[PASS] TCP movement byte and packet metrics match actual sent and parsed frames");
            Console.WriteLine($"[METRICS] delayMs={delay} jitterMs={jitter} txPackets={sentTraffic.SentPackets} txBytes={sentTraffic.SentBytes} " +
                $"rxPackets={receivedTraffic.ReceivedPackets} rxBytes={receivedTraffic.ReceivedBytes} queuedBytes={sentTraffic.QueuedSendBytes} peakQueuedBytes={sentTraffic.PeakQueuedSendBytes}");
            Console.WriteLine($"PASS 4 movement relay TCP tests ({delay}±{jitter}ms per proxy direction)");
            return 0;

            async Task Pump(int milliseconds)
            {
                Stopwatch wait = Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < milliseconds) { a.Pump(); b.Pump(); await Task.Delay(5); }
                a.Pump(); b.Pump();
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private sealed class RelayPeer : IDisposable
    {
        private readonly TcpSession _session = new();
        private readonly Channel<(GamePacketOpcode Opcode, byte[] Payload)> _packets = Channel.CreateUnbounded<(GamePacketOpcode, byte[])>();
        private MapGeometryData _pending;
        private MapInfoData _bounds;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _nextSendAt;
        internal uint CharacterId;
        internal MapGeometryData Geometry;
        internal int MovementPackets;
        internal long MovementBytes, SentPackets, SentBytes;
        internal TcpSession.TrafficSnapshot SendTraffic => _session.GetTraffic((ushort)GamePacketOpcode.MovementActions);
        internal TcpSession.TrafficSnapshot ReceiveTraffic => _session.GetTraffic((ushort)GamePacketOpcode.MovementActionsBroadcast);
        internal readonly Dictionary<uint, MovementSnapshot> States = new();
        internal readonly List<RelayedMovementAction> Events = new();
        internal event Action<RelayedMovementAction> ActionReceived;

        internal RelayPeer()
        {
            _session.PacketReceived += (opcode, payload) => _packets.Writer.TryWrite(((GamePacketOpcode)opcode, payload));
            _session.Disconnected += () => _packets.Writer.TryComplete(new IOException("Fixture disconnected"));
        }

        internal async Task ConnectAsync(int port)
        {
            await _session.ConnectAsync("127.0.0.1", port);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            while (Geometry == null || !States.ContainsKey(CharacterId)) Handle(await _packets.Reader.ReadAsync(timeout.Token));
        }

        internal void Pump() { while (_packets.Reader.TryRead(out var packet)) Handle(packet); }

        internal async Task SendAsync(MovementActionBatch batch)
        {
            double delay = _nextSendAt - _clock.Elapsed.TotalSeconds;
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay));
            _nextSendAt = _clock.Elapsed.TotalSeconds + 0.2;
            byte[] payload = MovementActionProtocol.CreateBatch(batch);
            await _session.SendAsync((ushort)GamePacketOpcode.MovementActions, payload);
            ++SentPackets;
            SentBytes += TcpSession.HeaderSize + payload.Length;
        }

        internal Task SendManualAsync(MovementActionData action, ulong batchSequence)
        {
            MovementActionBatch batch = new()
            {
                MapId = Geometry.MapId, Generation = Geometry.Generation,
                BatchSequence = batchSequence, LatestClientTick = action.ClientTick
            };
            batch.Actions.Add(action); return SendAsync(batch);
        }

        private void Handle((GamePacketOpcode Opcode, byte[] Payload) packet)
        {
            switch (packet.Opcode)
            {
                case GamePacketOpcode.EnterGameResponse: CharacterId = GameProtocol.ReadEnterGameResponse(packet.Payload).CharacterId; break;
                case GamePacketOpcode.MapInfo: _bounds = GameProtocol.ReadMapInfo(packet.Payload); break;
                case GamePacketOpcode.MapGeometry: _pending = PlatformProtocol.ReadGeometry(packet.Payload); _pending.Bounds = _bounds; break;
                case GamePacketOpcode.Foothold: PlatformProtocol.AddFoothold(_pending, packet.Payload); break;
                case GamePacketOpcode.Collider: PlatformProtocol.AddCollider(_pending, packet.Payload); break;
                case GamePacketOpcode.GeometryEnd: PlatformProtocol.Complete(_pending, packet.Payload); Geometry = _pending; _pending = null; break;
                case GamePacketOpcode.MovementState:
                    var state = PlatformProtocol.ReadState(packet.Payload); States[state.CharacterId] = state; break;
                case GamePacketOpcode.MovementActionsBroadcast:
                    var batch = MovementActionProtocol.ReadBroadcast(packet.Payload);
                    Program.Check(batch.MapId == Geometry.MapId && batch.Generation == Geometry.Generation, "Wrong broadcast map generation");
                    ++MovementPackets;
                    MovementBytes += TcpSession.HeaderSize + packet.Payload.Length;
                    foreach (var relay in batch.Actions)
                    {
                        Events.Add(relay);
                        States[relay.CharacterId] = new MovementSnapshot
                        {
                            CharacterId = relay.CharacterId, Sequence = relay.Action.Sequence, Grounded = relay.Action.State.Grounded,
                            X = relay.Action.State.X, Y = relay.Action.State.Y
                        };
                        ActionReceived?.Invoke(relay);
                    }
                    break;
                case GamePacketOpcode.PlayerEnterMap: case GamePacketOpcode.PlayerLeaveMap: break;
                default: throw new InvalidDataException("Unexpected fixture opcode " + packet.Opcode);
            }
        }
        public void Dispose() => _session.Dispose();
    }
}

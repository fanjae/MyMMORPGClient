internal static class ReconciliationTests
{
    internal static MapGeometryData Geometry(ulong generation = 1)
    {
        MapGeometryData geometry = new()
        {
            MapId = 100000000, Generation = generation, Version = 1, Mode = MovementMode.Platformer,
            HalfWidth = 6, HalfHeight = 6, HorizontalSpeed = 80, JumpSpeed = 160, Gravity = 400, MaxFallSpeed = 300,
            SpawnFootholdId = 1, Bounds = new MapInfoData { MapId = 100000000, MinX = -400, MaxX = 400, MinY = -200, MaxY = 200 }
        };
        geometry.Footholds.Add(1, new FootholdData { Id = 1, X1 = -400, X2 = 400, Y1 = -6, Y2 = -6 });
        return geometry;
    }

    internal static MovementSnapshot Snapshot(PlatformState state, ulong tick, ulong sequence, uint inputTicks, ulong generation = 1, MovementStateReason reason = MovementStateReason.Normal)
        => new()
        {
            MapId = 100000000, Generation = generation, CharacterId = 1001, ServerTick = tick, Sequence = sequence,
            InputTicks = inputTicks, X = state.X, Y = state.Y, VelocityX = state.VelocityX, VelocityY = state.VelocityY,
            FootholdId = state.FootholdId, Grounded = state.Grounded, JumpHeld = state.JumpHeld, Reason = reason
        };

    private static MovementReconciliation Start(MapGeometryData geometry = null)
    {
        geometry ??= Geometry();
        MovementReconciliation prediction = new();
        prediction.Configure(geometry, 1001);
        prediction.Apply(Snapshot(prediction.State, 0, 0, 0, geometry.Generation));
        return prediction;
    }

    internal static int Run()
    {
        try
        {
            Test("manual proxy port is opt-in and rejects ambiguous or invalid overrides", () =>
            {
                Program.Check(TestConnectionOptions.GamePort(7777, Array.Empty<string>()) == 7777 && TestConnectionOptions.GamePort(7777, new[] { "--test-game-port=27777" }) == 27777, "Default or proxy port changed");
                foreach (string[] arguments in new[] { new[] { "--test-game-port=0" }, new[] { "--test-game-port=-1" }, new[] { "--test-game-port=65536" }, new[] { "--test-game-port=27777", "--test-game-port=27778" } })
                {
                    bool rejected = false; try { TestConnectionOptions.GamePort(7777, arguments); } catch (ArgumentException) { rejected = true; }
                    Program.Check(rejected, "Invalid proxy port accepted");
                }
            });
            Test("unconfirmed fixed steps replay without resetting delayed movement", () =>
            {
                var prediction = Start();
                prediction.SetInput(1, false); prediction.CreateInput();
                for (int i = 0; i < 100; ++i) prediction.Advance(0.02);
                PlatformState confirmed = prediction.State; confirmed.X = 96;
                Program.Check(prediction.Apply(Snapshot(confirmed, 60, 1, 60)), "Snapshot rejected");
                Program.Check(Math.Abs(prediction.State.X - 160) < 0.001 && prediction.PendingSteps == 40 && prediction.LastReplayedSteps == 40 && prediction.LastCorrection < 0.001 && Math.Abs(prediction.LastResetCorrection - 64) < 0.001, "Delayed replay reset or duplicated movement");
            });
            Test("acknowledged age and heartbeat sequences do not replay confirmed steps twice", () =>
            {
                var prediction = Start(); prediction.SetInput(1, false); prediction.CreateInput();
                for (int i = 0; i < 5; ++i) prediction.Advance(0.02);
                PlatformState first = prediction.State;
                prediction.CreateInput();
                for (int i = 0; i < 3; ++i) prediction.Advance(0.02);
                var ack = Snapshot(first, 5, 1, 5);
                Program.Check(prediction.Apply(ack) && prediction.PendingSteps == 3 && Math.Abs(prediction.State.X - 12.8) < 0.001, "Heartbeat replay");
                Program.Check(!prediction.Apply(ack), "Duplicate snapshot applied");
                first.X = 9.6;
                Program.Check(prediction.Apply(Snapshot(first, 6, 2, 1)) && prediction.PendingSteps == 2 && Math.Abs(prediction.State.X - 12.8) < 0.001, "Applied stage replayed twice");
            });
            Test("short jump survives a frame without a fixed step and replays once", () =>
            {
                var geometry = Geometry(); var prediction = Start(geometry);
                prediction.SetInput(0, true, true); prediction.CreateInput();
                prediction.Advance(0.008);
                prediction.SetInput(0, false); prediction.CreateInput();
                prediction.Advance(0.012);
                Program.Check(!prediction.State.Grounded && prediction.State.Y > 0 && !prediction.State.JumpHeld, "Short local jump was lost");
                PlatformState confirmed = prediction.State;
                prediction.Advance(0.02);
                PlatformState before = prediction.State;
                Program.Check(prediction.Apply(Snapshot(confirmed, 1, 2, 1)) && Math.Abs(prediction.State.Y - before.Y) < 0.001 && Math.Abs(prediction.State.VelocityY - before.VelocityY) < 0.001, "Short jump replayed twice");
            });
            Test("held jump acknowledgement does not cause a second landing jump", () =>
            {
                var prediction = Start(); prediction.SetInput(0, true); prediction.CreateInput(); prediction.Advance(0.02);
                PlatformState confirmed = prediction.State;
                for (int i = 0; i < 60; ++i) prediction.Advance(0.02);
                Program.Check(prediction.Apply(Snapshot(confirmed, 1, 1, 1)) && prediction.State.Grounded && prediction.State.Y == 0, "Held jump repeated after replay");
            });
            Test("snapshot reception preserves fractional fixed-step accumulation", () =>
            {
                var prediction = Start(); prediction.SetInput(1, false); prediction.CreateInput();
                Program.Check(prediction.Advance(0.016) == 0, "Early step");
                prediction.Apply(Snapshot(prediction.State, 1, 0, 1));
                Program.Check(prediction.Advance(0.004) == 1 && prediction.State.X > 0, "Snapshot discarded frame remainder");
            });
            Test("chat and inactive-window neutral input discard pending jump and stop prediction", () =>
            {
                foreach (string gate in new[] { "chat", "inactive" })
                {
                    var prediction = Start(); prediction.SetInput(1, true, true); prediction.Advance(0.005);
                    prediction.StopInput(); MovementInputData input = prediction.CreateInput(); prediction.Advance(0.02);
                    Program.Check(input.Horizontal == 0 && !input.JumpHeld && prediction.State.X == 0 && prediction.State.Grounded, $"{gate} retained movement or short jump");
                }
            });
            Test("map change and reconnect reject old generations and clear history", () =>
            {
                var prediction = Start(); prediction.SetInput(1, true); prediction.CreateInput(); prediction.Advance(0.02);
                var old = Snapshot(prediction.State, 10, 1, 1);
                prediction.Configure(Geometry(2), 1001);
                Program.Check(!prediction.Apply(old) && prediction.PendingSteps == 0 && prediction.Sequence == 0 && !prediction.Ready && !prediction.WireJump, "Map retained old inputs");
                prediction.Reset(); prediction.Configure(Geometry(), 1001);
                Program.Check(!prediction.Apply(old) && prediction.Sequence == 0 && prediction.PendingSteps == 0, "Old connection future sequence applied");
            });
            Test("respawn clears old unconfirmed jump steps", () =>
            {
                var prediction = Start(); prediction.SetInput(1, true); prediction.CreateInput(); prediction.Advance(0.04);
                PlatformState spawn = new() { Grounded = true, FootholdId = 1 };
                Program.Check(prediction.Apply(Snapshot(spawn, 10, 1, 2, reason: MovementStateReason.Respawned)) && prediction.PendingSteps == 0 && prediction.State.X == 0 && prediction.LastReplayedSteps == 0, "Respawn replayed old history");
            });
            Test("prediction history has a limit and resumes only after a fresh input acknowledgement", () =>
            {
                var prediction = Start(); prediction.SetInput(0, false); prediction.CreateInput();
                for (int i = 0; i < 110; ++i) prediction.Advance(0.1);
                Program.Check(prediction.WaitingForFreshInput && prediction.PendingSteps <= MovementReconciliation.MaxHistorySteps, "Unbounded history");
                prediction.Apply(Snapshot(prediction.State, 600, 1, 600));
                Program.Check(prediction.WaitingForFreshInput, "Incomplete old history resumed");
                MovementInputData fresh = prediction.CreateInput();
                prediction.Apply(Snapshot(prediction.State, 601, fresh.Sequence, 1));
                Program.Check(!prediction.WaitingForFreshInput && prediction.Advance(0.02) == 1, "Fresh input did not resume prediction");
            });
            Test("remote jitter interpolation keeps render time monotonic and never extrapolates", () =>
            {
                RemoteMovementBuffer buffer = new();
                var state = new PlatformState { Grounded = true, FootholdId = 1 };
                buffer.Add(Snapshot(state, 0, 0, 0), 0);
                state.X = 4.8; buffer.Add(Snapshot(state, 3, 1, 3), 0.08);
                buffer.Sample(0.15, out double first, out _);
                state.X = 9.6; buffer.Add(Snapshot(state, 6, 2, 3), 0.2);
                buffer.Sample(0.2, out double jitter, out _);
                buffer.Sample(0.8, out double frozen, out _);
                Program.Check(Math.Abs(first - 2.4) < 0.001 && jitter >= first && frozen == 9.6, "Remote jitter regressed or extrapolated");
                Program.Check(!buffer.Add(Snapshot(state, 5, 2, 2), 0.9), "Old remote snapshot accepted");
            });
            Test("remote respawn clears interpolation and snapshot storage remains bounded", () =>
            {
                RemoteMovementBuffer buffer = new();
                for (ulong i = 0; i < 100; ++i) buffer.Add(Snapshot(new PlatformState { X = i }, i, i, 1), i * 0.02);
                Program.Check(buffer.Count <= RemoteMovementBuffer.MaxSnapshots, "Unbounded remote storage");
                buffer.Add(Snapshot(new PlatformState { X = -100 }, 100, 100, 1, reason: MovementStateReason.Respawned), 2);
                buffer.Sample(2, out double x, out _);
                Program.Check(x == -100 && buffer.Count == 1, "Respawn interpolated across maps or world");
            });
            Test("movement acknowledgement layout is 75 bytes and rejects invalid jump flags", () =>
            {
                byte[] packet = new byte[75]; packet[68] = 1; BitConverter.GetBytes(3u).CopyTo(packet, 70); packet[74] = 1;
                MovementSnapshot snapshot = PlatformProtocol.ReadState(packet);
                Program.Check(snapshot.Grounded && snapshot.InputTicks == 3 && snapshot.JumpHeld, "Acknowledgement layout");
                packet[74] = 2;
                bool rejected = false; try { PlatformProtocol.ReadState(packet); } catch (InvalidDataException) { rejected = true; }
                Program.Check(rejected, "Invalid jump latch accepted");
            });
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine($"[FAIL] reconciliation: {exception.Message}"); return 1; }
    }

    private static void Test(string name, Action test) { test(); Console.WriteLine($"[PASS] {name}"); }
}

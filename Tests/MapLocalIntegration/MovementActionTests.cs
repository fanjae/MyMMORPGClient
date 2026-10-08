using System.IO;

internal static class MovementActionTests
{
    internal static int Run()
    {
        try
        {
            Test("Client rejects repeated airborne jump presses and emits one matching landing", () =>
            {
                ClientMovementActions owner = new(); owner.Configure(ReconciliationTests.Geometry());
                owner.SetInput(0, true); owner.Advance(0.02);
                Program.Check(!owner.CanJump && !owner.State.Grounded && owner.JumpId == 1, "Jump did not start");
                for (int i = 0; i < 15; ++i) { owner.SetInput(0, true); owner.Advance(0.02); }
                Program.Check(owner.JumpId == 1 && !owner.State.Grounded, "Airborne jump restarted");
                for (int i = 0; i < 35; ++i) owner.Advance(0.02);
                Program.Check(owner.CanJump && owner.State.Grounded && owner.JumpId == 1, "Landing did not unlock");
                owner.TryCreateBatch(1, out var batch);
                Program.Check(batch.Actions.Count(a => a.Kind == MovementActionKind.Jump) == 1 &&
                    batch.Actions.Count(a => a.Kind == MovementActionKind.Land) == 1 &&
                    batch.Actions.All(a => a.JumpId == 1), "Jump/landing identity lost");
                owner.Advance(0.02);
                Program.Check(owner.JumpId == 1 && owner.State.Grounded, "Old press queued a post-landing jump");
                owner.SetInput(0, true); owner.Advance(0.02);
                Program.Check(owner.JumpId == 2, "New jump after landing rejected");
            });
            Test("Rapid changes, jump and landing share the five-message sliding-window budget", () =>
            {
                ClientMovementActions owner = new(); owner.Configure(ReconciliationTests.Geometry());
                List<double> sent = new(); List<MovementActionData> events = new();
                for (int frame = 0; frame < 600; ++frame)
                {
                    double now = frame / 100.0;
                    owner.SetInput((frame / 3) % 2 == 0 ? 1 : -1, frame % 17 == 0);
                    owner.Advance(0.01);
                    if (owner.TryCreateBatch(now, out var batch)) { sent.Add(now); events.AddRange(batch.Actions); }
                }
                for (int i = 1; i < sent.Count; ++i)
                    Program.Check(sent[i] - sent[i - 1] >= 0.2 - 1e-8, "Send interval violated");
                foreach (double start in sent)
                    Program.Check(sent.Count(t => t >= start && t < start + 1 - 1e-8) <= 5, "More than five sends in one second");
                Program.Check(events.Any(a => a.Kind == MovementActionKind.Jump) && events.Any(a => a.Kind == MovementActionKind.Land), "Event preservation missing");
                owner.Configure(ReconciliationTests.Geometry(2));
                owner.SetInput(0, true); owner.Advance(0.02);
                Program.Check(!owner.TryCreateBatch(sent.Last() + 0.05, out _), "Map reset bypassed rate limit");
            });
            Test("Unchanged movement uses one-second checkpoints instead of five periodic messages", () =>
            {
                ClientMovementActions owner = new(); owner.Configure(ReconciliationTests.Geometry());
                owner.SetInput(1, false); List<double> sent = new();
                for (int i = 0; i < 250; ++i)
                {
                    owner.Advance(0.02);
                    if (owner.TryCreateBatch(i * 0.02, out _)) sent.Add(i * 0.02);
                }
                Program.Check(sent.Count <= 6 && sent.Count >= 5, "Unchanged movement sent excessively or lost checkpoints");
            });
            Test("Remote simulates jump from relayed state and converges after delayed landing", () =>
            {
                var geometry = ReconciliationTests.Geometry();
                ClientMovementActions owner = new(); owner.Configure(geometry);
                RemoteMovementActions remote = new();
                remote.Configure(geometry, ReconciliationTests.Snapshot(owner.State, 0, 0, 0), 0);
                owner.SetInput(1, true); owner.Advance(0.02);
                owner.TryCreateBatch(0, out var jump);
                var relay = new RelayedMovementAction { CharacterId = 1001, LatestClientTick = jump.LatestClientTick, Action = jump.Actions[0] };
                Program.Check(remote.Add(relay, 0) && !remote.Add(relay, 0), "Duplicate jump accepted");
                remote.Sample(0.35, out _, out double height);
                Program.Check(height > 0 && !remote.State.Grounded, "Remote did not simulate jump without updates");
                for (int i = 0; i < 60; ++i) owner.Advance(0.02);
                owner.SetInput(0, false); owner.Advance(0.02);
                owner.TryCreateBatch(1.3, out var landed);
                foreach (var action in landed.Actions)
                    remote.Add(new RelayedMovementAction { CharacterId = 1001, LatestClientTick = landed.LatestClientTick, Action = action }, 1.3);
                remote.Sample(1.6, out double x, out double y);
                Program.Check(remote.State.Grounded && Math.Abs(x - owner.State.X) < 0.001 && Math.Abs(y - owner.State.Y) < 0.001, "Landing/stop did not converge");
            });
            Test("Action wire format agrees with packed C++ sizes and rejects malformed batches", () =>
            {
                ClientMovementActions owner = new(); owner.Configure(ReconciliationTests.Geometry());
                owner.SetInput(0, true); owner.Advance(0.02); owner.TryCreateBatch(0, out var batch);
                byte[] bytes = MovementActionProtocol.CreateBatch(batch);
                Program.Check(bytes.Length == 30 + batch.Actions.Count * 63, "Client wire layout mismatch");
                using PacketWriter writer = new();
                writer.Write(batch.MapId); writer.Write(batch.Generation); writer.Write(1UL); writer.Write((ushort)1);
                writer.Write(1001U); writer.Write(batch.LatestClientTick); MovementActionProtocol.WriteAction(writer, batch.Actions[0]);
                byte[] broadcast = writer.ToArray();
                var decoded = MovementActionProtocol.ReadBroadcast(broadcast);
                Program.Check(decoded.Actions[0].Action.Kind == MovementActionKind.Jump && decoded.Actions[0].Action.JumpId == 1, "Decoded jump mismatch");
                foreach (byte[] invalid in new[] { broadcast[..^1], new byte[22], Array.Empty<byte>() })
                {
                    bool rejected = false; try { MovementActionProtocol.ReadBroadcast(invalid); } catch (InvalidDataException) { rejected = true; }
                    Program.Check(rejected, "Malformed broadcast accepted");
                }
            });
            Test("Falling off a foothold reports Fall and client respawn releases the same airborne identity", () =>
            {
                var geometry = ReconciliationTests.Geometry();
                var support = geometry.Footholds[1]; support.X2 = 5; geometry.Footholds[1] = support;
                ClientMovementActions owner = new(); owner.Configure(geometry); owner.SetInput(1, false);
                for (int i = 0; i < 5; ++i) owner.Advance(0.02);
                Program.Check(!owner.CanJump && !owner.State.Grounded && owner.JumpId == 1, "Fall did not lock jump");
                owner.StopInput();
                for (int i = 0; i < 100; ++i) owner.Advance(0.02);
                owner.TryCreateBatch(2.2, out var batch);
                Program.Check(owner.CanJump && batch.Actions.Any(a => a.Kind == MovementActionKind.Fall) &&
                    batch.Actions.Any(a => a.Kind == MovementActionKind.Respawn && a.JumpId == 1), "Client respawn report missing");
            });
            Test("Chat or focus neutralization clears an unconsumed jump press", () =>
            {
                ClientMovementActions owner = new(); owner.Configure(ReconciliationTests.Geometry());
                owner.SetInput(1, true); owner.StopInput(); owner.Advance(0.02);
                Program.Check(owner.State.Grounded && owner.JumpId == 0 && owner.State.VelocityX == 0, "Neutral input preserved pending jump");
            });
            Console.WriteLine("PASS 7 movement action tests"); return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void Test(string name, Action action) { action(); Console.WriteLine("[PASS] " + name); }
}

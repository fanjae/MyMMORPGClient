using System.Diagnostics;

internal static class MovementActionGameIntegration
{
    internal static async Task<int> RunAsync(bool latency, string physicsTrace = null, string csv = null)
    {
        try
        {
            using StreamWriter trace = csv == null ? null : new(csv, false, new System.Text.UTF8Encoding(false));
            trace?.WriteLine("delayMs,jitterMs,timeMs,reportedX,reportedY,clientX,clientY,remoteX,remoteY,sentMessages,pendingActions");
            var conditions = latency ? new[] { (Delay: 0, Jitter: 0), (Delay: 80, Jitter: 35), (Delay: 150, Jitter: 60) }
                : new[] { (Delay: 0, Jitter: 0) };
            foreach (var condition in conditions)
            {
                await using DelayedGameProxy proxyA = new(condition.Delay, condition.Jitter);
                await using DelayedGameProxy proxyB = new(condition.Delay, condition.Jitter);
                using PlatformPeer a = new(), b = new();
                await a.EnterAsync("test", 1001, true, proxyA.Port);
                await b.EnterAsync("test2", 2001, false, proxyB.Port);
                await a.WaitAsync(() => a.Players.Contains(2001));
                await b.WaitAsync(() => b.States.ContainsKey(1001));
                if (physicsTrace != null) { PlatformIntegration.CheckPhysicsTrace(a.Geometry, physicsTrace); physicsTrace = null; }
                RemoteMovementActions remote = new(); remote.Configure(b.Geometry, b.States[1001], 0);
                Stopwatch clock = Stopwatch.StartNew();
                b.ActionReceived += relay => { if (relay.CharacterId == 1001) remote.Add(relay, clock.Elapsed.TotalSeconds); };
                double peak = 0;
                await a.InputAsync(0, true);
                await Drive(600, true);
                await Drive(1200, false);
                await Task.WhenAll(a.PumpAsync(800), b.PumpAsync(800));
                Program.Check(a.ClientMovement.State.Grounded && a.Local.Grounded && b.States[1001].Grounded,
                    "Client landing did not update server or peer");
                Program.Check(peak > 10 && b.Actions.Count(e => e.CharacterId == 1001 && e.Action.Kind == MovementActionKind.Jump) == 1,
                    "Remote jump failed or held key repeated jump");
                Console.WriteLine($"[PASS] {condition.Delay}±{condition.Jitter}ms client-owned jump, remote simulation, landing and held-key lock");
                int beforeIdle = b.MovementPackets;
                await Task.WhenAll(a.PumpAsync(600), b.PumpAsync(600));
                Program.Check(b.MovementPackets == beforeIdle, "Server emitted unsolicited movement updates");
                Console.WriteLine("[PASS] Server never simulates movement or periodically broadcasts idle state");
                await a.InputAsync(0, true);
                await Drive(1300, false);
                await Task.WhenAll(a.PumpAsync(800), b.PumpAsync(800));
                Program.Check(b.Actions.Count(e => e.CharacterId == 1001 && e.Action.Kind == MovementActionKind.Jump) == 2,
                    "Fresh jump after landing missing");
                Console.WriteLine("[PASS] Fresh jump after client landing accepted");
                await a.ChangeAsync(100000001);
                await b.WaitAsync(() => !b.Players.Contains(1001));
                await a.ChangeAsync(100000000);
                await a.WaitAsync(() => a.Players.Contains(2001));
                await b.WaitAsync(() => b.Players.Contains(1001));
                Program.Check(a.ClientMovement.JumpId == 0 && a.ClientMovement.State.Grounded, "Map transition retained jump lock");
                Console.WriteLine("[PASS] Map transition resets client/server jump generation");

                async Task Drive(int milliseconds, bool held)
                {
                    Stopwatch drive = Stopwatch.StartNew();
                    while (drive.ElapsedMilliseconds < milliseconds)
                    {
                        await a.InputAsync(0, held);
                        await Task.WhenAll(a.PumpAsync(20), b.PumpAsync(20));
                        remote.Sample(clock.Elapsed.TotalSeconds, out double x, out double y); peak = Math.Max(peak, y);
                        trace?.WriteLine(FormattableString.Invariant($"{condition.Delay},{condition.Jitter},{clock.Elapsed.TotalMilliseconds:F3},{a.Local.X:F3},{a.Local.Y:F3},{a.ClientMovement.State.X:F3},{a.ClientMovement.State.Y:F3},{x:F3},{y:F3},{a.ClientMovement.PacketsSent},{a.ClientMovement.PendingActions}"));
                    }
                }
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("[FAIL] Action game integration: " + exception); return 1; }
    }
}

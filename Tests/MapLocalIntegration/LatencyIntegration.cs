using System.Diagnostics;

internal static class LatencyIntegration
{
    private static string _stage = "latency startup";

    internal static async Task<int> RunAsync(string csv)
    {
        try
        {
            using StreamWriter trace = csv == null ? null : new(csv, false, new System.Text.UTF8Encoding(false));
            trace?.WriteLine("delayMs,jitterMs,timeMs,serverX,serverY,predictedX,predictedY,remoteX,remoteY,pendingSteps,replaySteps,correction,resetCorrection");
            foreach (var condition in new[] { (Delay: 0, Jitter: 0), (Delay: 80, Jitter: 35), (Delay: 150, Jitter: 60) })
            {
                await using DelayedGameProxy proxyA = new(condition.Delay, condition.Jitter);
                await using DelayedGameProxy proxyB = new(condition.Delay, condition.Jitter);
                using PlatformPeer a = new(), b = new();
                await a.EnterAsync("test", 1001, false, proxyA.Port);
                await b.EnterAsync("test2", 2001, false, proxyB.Port);
                await a.WaitAsync(() => a.Players.Contains(2001));
                await b.WaitAsync(() => b.Players.Contains(1001) && b.States.ContainsKey(1001));
                MovementReconciliation prediction = new();
                prediction.Configure(a.Geometry, a.CharacterId); prediction.Apply(a.Local);
                RemoteMovementBuffer remote = new();
                Stopwatch clock = Stopwatch.StartNew();
                double correctionSum = 0, resetSum = 0, peak = 0, remoteStepMax = 0;
                int samples = 0, replayed = 0, maxPending = 0;
                bool measure = true;
                double previousRemoteX = 0, previousRemoteY = 0;
                bool hadRemote = false;
                a.SnapshotReceived += snapshot =>
                {
                    if (!prediction.Apply(snapshot) || !measure) return;
                    correctionSum += prediction.LastCorrection; resetSum += prediction.LastResetCorrection;
                    replayed += prediction.LastReplayedSteps; ++samples;
                };
                b.SnapshotReceived += snapshot =>
                {
                    if (snapshot.CharacterId == 1001 && measure) remote.Add(snapshot, clock.Elapsed.TotalSeconds);
                };
                remote.Add(b.States[1001], 0);
                double nextSend = 0, lastFrame = clock.Elapsed.TotalSeconds;
                int sentHorizontal = 0; bool sentJump = false;

                _stage = $"{condition.Delay}±{condition.Jitter}ms one-way movement, jump and landing";
                await Drive(700, 1, false);
                await Drive(1100, 0, true);
                await Drive(350, 0, false);
                Program.Check(peak > 20 && a.Local.Grounded && prediction.State.Grounded, "Delayed jump or landing");
                Pass();

                _stage = $"{condition.Delay}±{condition.Jitter}ms one-way wall and direction change";
                await Drive(3000, 1, false);
                await Drive(650, 0, false);
                Program.Check(a.Local.X == 214 && prediction.State.X <= 214.01, "Delayed prediction crossed wall");
                await Drive(450, -1, false);
                Pass();

                _stage = $"{condition.Delay}±{condition.Jitter}ms chat focus neutral input stops both views";
                prediction.StopInput();
                await Send();
                await Drive(800, 0, false);
                await CheckStopped();
                Pass();

                _stage = $"{condition.Delay}±{condition.Jitter}ms inactive-window neutral input stops both views";
                await Drive(180, 1, false);
                prediction.SetInput(1, true, true);
                prediction.StopInput();
                await Send();
                await Drive(800, 0, false);
                await CheckStopped();
                Program.Check(a.Local.Grounded && prediction.State.Grounded, "Inactive window leaked pending jump");
                Pass();

                _stage = $"{condition.Delay}±{condition.Jitter}ms replay metrics and remote interpolation";
                Program.Check(samples > 10 && replayed > 0 && maxPending < MovementReconciliation.MaxHistorySteps && remoteStepMax < 80, "History overflow, no replay or remote teleport");
                if (condition.Delay > 0) Program.Check(correctionSum < resetSum, "Replay did not reduce total correction");
                Console.WriteLine($"[METRIC] delay={condition.Delay} jitter={condition.Jitter} samples={samples} replayed={replayed} pendingMax={maxPending} correctionMean={correctionSum / samples:F3} resetMean={resetSum / samples:F3} remoteStepMax={remoteStepMax:F3}");
                Pass();
                measure = false;

                _stage = $"{condition.Delay}±{condition.Jitter}ms map transition clears old prediction";
                MovementSnapshot old = a.Local;
                await a.ChangeAsync(100000001);
                prediction.Configure(a.Geometry, a.CharacterId);
                Program.Check(prediction.PendingSteps == 0 && !prediction.Ready && !prediction.Apply(old), "Free map retained history");
                await a.ChangeAsync(100000000);
                await a.WaitAsync(() => a.States.ContainsKey(1001));
                prediction.Configure(a.Geometry, a.CharacterId); prediction.Apply(a.Local);
                Program.Check(prediction.State.X == 0 && prediction.PendingSteps == 0 && !prediction.Apply(old), "Previous map replayed");
                Pass();

                _stage = $"{condition.Delay}±{condition.Jitter}ms disconnect and reconnect clear pending input";
                await b.PumpAsync(condition.Delay * 2 + condition.Jitter * 2 + 100);
                await b.WaitAsync(() => b.Players.Contains(1001));
                a.Dispose(); prediction.Reset();
                await b.WaitAsync(() => !b.Players.Contains(1001));
                await using DelayedGameProxy reconnectProxy = new(condition.Delay, condition.Jitter);
                using PlatformPeer reconnect = new();
                await reconnect.EnterAsync("test", 1001, false, reconnectProxy.Port);
                prediction.Configure(reconnect.Geometry, reconnect.CharacterId); prediction.Apply(reconnect.Local);
                Program.Check(prediction.Sequence == 0 && prediction.PendingSteps == 0 && prediction.State.X == 0 && !prediction.Apply(old), "Reconnect retained old input");
                Pass();

                async Task Send()
                {
                    MovementInputData input = prediction.CreateInput();
                    sentHorizontal = input.Horizontal; sentJump = input.JumpHeld;
                    nextSend = clock.Elapsed.TotalSeconds + 0.05;
                    await a.SendAsync(GamePacketOpcode.MovementInput, PlatformProtocol.CreateInput(input));
                }

                async Task Drive(int milliseconds, int horizontal, bool jump)
                {
                    double finish = clock.Elapsed.TotalSeconds + milliseconds / 1000.0;
                    while (clock.Elapsed.TotalSeconds < finish)
                    {
                        double now = clock.Elapsed.TotalSeconds;
                        prediction.SetInput(horizontal, jump);
                        if (horizontal != sentHorizontal || prediction.WireJump != sentJump || now >= nextSend) await Send();
                        prediction.Advance(now - lastFrame); lastFrame = now;
                        await a.PumpAsync(5); await b.PumpAsync(5);
                        peak = Math.Max(peak, a.Local.Y);
                        maxPending = Math.Max(maxPending, prediction.PendingSteps);
                        remote.Sample(clock.Elapsed.TotalSeconds, out double rx, out double ry);
                        if (hadRemote) remoteStepMax = Math.Max(remoteStepMax, Math.Sqrt(Math.Pow(rx - previousRemoteX, 2) + Math.Pow(ry - previousRemoteY, 2)));
                        previousRemoteX = rx; previousRemoteY = ry; hadRemote = true;
                        trace?.WriteLine(FormattableString.Invariant($"{condition.Delay},{condition.Jitter},{clock.Elapsed.TotalMilliseconds:F2},{a.Local.X:F3},{a.Local.Y:F3},{prediction.State.X:F3},{prediction.State.Y:F3},{rx:F3},{ry:F3},{prediction.PendingSteps},{prediction.LastReplayedSteps},{prediction.LastCorrection:F3},{prediction.LastResetCorrection:F3}"));
                    }
                }

                async Task CheckStopped()
                {
                    double stopped = a.Local.X;
                    await Drive(250, 0, false);
                    remote.Sample(clock.Elapsed.TotalSeconds, out double rx, out double ry);
                    Program.Check(a.Local.VelocityX == 0 && prediction.State.VelocityX == 0 && a.Local.X == stopped && Math.Abs(prediction.State.X - stopped) < 0.01 && Math.Abs(rx - stopped) < 0.01 && Math.Abs(ry - a.Local.Y) < 0.01, "Neutral input did not stop or converge");
                }
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine($"[FAIL] {_stage}: {exception.Message}"); return 1; }
    }

    private static void Pass() => Console.WriteLine($"[PASS] {_stage}");
}

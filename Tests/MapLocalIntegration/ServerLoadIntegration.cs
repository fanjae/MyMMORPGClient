using System.Diagnostics;

internal static class ServerLoadIntegration
{
    internal static async Task<int> RunAsync(string delayContainer)
    {
        List<PlatformPeer> peers = new();
        try
        {
            using (PacketConnection login = new("old-login", true, false))
            {
                await login.ConnectAsync(7776);
                byte[] request = LoginProtocol.CreateLoginRequest("load1", "test1234");
                await login.SendAsync((ushort)LoginPacketOpcode.LoginRequest, request[..96]);
                Program.Check(LoginProtocol.ReadLoginResponse(await login.ReceiveAsync((ushort)LoginPacketOpcode.LoginResponse)) == LoginResult.ProtocolMismatch, "Old login version not rejected");
                await login.SendAsync((ushort)LoginPacketOpcode.LoginRequest, request);
                Program.Check(LoginProtocol.ReadLoginResponse(await login.ReceiveAsync((ushort)LoginPacketOpcode.LoginResponse)) == LoginResult.Success, "Version rejection broke current login");
                await login.SendAsync((ushort)LoginPacketOpcode.CharacterListRequest, Array.Empty<byte>());
                CharacterListData list = LoginProtocol.ReadCharacterListResponse(await login.ReceiveAsync((ushort)LoginPacketOpcode.CharacterListResponse));
                Program.Check(list.Characters.Single().Name == "가나다라마바사아자차카타파하가나", "16-character login name changed");
            }
            Console.WriteLine("[PASS] Old login version rejected and 16-character Korean name preserved");

            for (uint i = 1; i <= 32; ++i)
            {
                PlatformPeer peer = new();
                peers.Add(peer);
                await peer.EnterAsync($"load{i}", 30000 + i, false);
            }
            await Task.WhenAll(peers.Select(peer => peer.WaitAsync(() => peer.Players.Count == 31)));
            Program.Check(peers[0].Name == "가나다라마바사아자차카타파하가나", "Game name differs from login name");
            Console.WriteLine("[PASS] 32 accounts entered the same map with consistent names and presence");

            ulong initialTick = peers[0].Local.ServerTick;
            Stopwatch movement = Stopwatch.StartNew();
            while (movement.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.WhenAll(peers.Select(peer => peer.InputAsync(1, false)));
                await Task.WhenAll(peers.Select(peer => peer.PumpAsync(50)));
            }
            await Task.WhenAll(peers.Select(peer => peer.InputAsync(0, false)));
            await Task.WhenAll(peers.Select(peer => peer.PumpAsync(150)));
            Program.Check(peers.All(peer => peer.Local.X > 0 && peer.Local.VelocityX == 0 && peer.Players.Count == 31), "Load movement or stop failed");
            Program.Check(peers[0].Local.ServerTick - initialTick >= 100, "Server tick stalled under 32-player load");
            Console.WriteLine("[PASS] 32 moving players preserve server tick, stop and map presence");

            if (delayContainer != null)
            {
                Program.Check(delayContainer.StartsWith("mymmorpg-priority-"), "DB delay test requires a dedicated temporary container");
                CharacterSelectData ticket;
                using (PacketConnection login = new("delay-login", true, false))
                {
                    await login.ConnectAsync(7776);
                    await login.SendAsync((ushort)LoginPacketOpcode.LoginRequest, LoginProtocol.CreateLoginRequest("test", "test1234"));
                    Program.Check(LoginProtocol.ReadLoginResponse(await login.ReceiveAsync((ushort)LoginPacketOpcode.LoginResponse)) == LoginResult.Success, "Delay test login failed");
                    await login.SendAsync((ushort)LoginPacketOpcode.CharacterSelectRequest, LoginProtocol.CreateCharacterSelectRequest(1001));
                    ticket = LoginProtocol.ReadCharacterSelectResponse(await login.ReceiveAsync((ushort)LoginPacketOpcode.CharacterSelectResponse));
                }

                ProcessStartInfo options = new("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "exec", delayContainer, "sh", "-c", "MYSQL_PWD=\"$MYSQL_PASSWORD\" mysql --unbuffered -N -u\"$MYSQL_USER\" \"$MYSQL_DATABASE\" -e \"LOCK TABLES characters WRITE; SELECT 'locked'; SELECT SLEEP(2); UNLOCK TABLES;\"" })
                    options.ArgumentList.Add(argument);
                using Process delay = Process.Start(options);
                Program.Check(await delay.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) == "locked", "DB lock not acquired");
                ulong beforeDelay = peers[0].Local.ServerTick;
                using (PacketConnection pending = new("pending-game", false, false))
                {
                    await pending.ConnectAsync(7777);
                    await pending.SendAsync((ushort)GamePacketOpcode.EnterGameRequest, GameProtocol.CreateEnterGameRequest(ticket.AuthKey));
                    await pending.ExpectNoPacketsAsync();
                }
                await Task.WhenAll(peers.Select(peer => peer.PumpAsync(1000)));
                Program.Check(peers[0].Local.ServerTick - beforeDelay >= 40, "DB delay blocked existing players' ticks");
                await delay.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Program.Check(delay.ExitCode == 0, "DB lock test failed");
                using PlatformPeer fresh = new();
                await fresh.EnterAsync("test", 1001, false);
                Console.WriteLine("[PASS] Delayed DB load preserves map ticks and disconnected load creates no ghost player");
            }

            peers[^1].Dispose();
            await Task.WhenAll(peers.Take(31).Select(peer => peer.WaitAsync(() => peer.Players.Count == 30)));
            using PlatformPeer reconnect = new();
            await reconnect.EnterAsync("load32", 30032, false);
            await Task.WhenAll(peers.Take(31).Select(peer => peer.WaitAsync(() => peer.Players.Count == 31)));
            Console.WriteLine("[PASS] Disconnect and fresh authentication restore 32-player presence");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[FAIL] Server load integration: {exception.Message}");
            return 1;
        }
        finally
        {
            foreach (PlatformPeer peer in peers)
                peer.Dispose();
        }
    }
}

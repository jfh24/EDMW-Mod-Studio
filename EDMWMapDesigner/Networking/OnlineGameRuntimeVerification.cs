using System.Reflection;
using System.Windows.Forms;
using EDMWMapDesigner.Generation;
using EDMWMapDesigner.UI;

namespace EDMWMapDesigner.Networking;

/// <summary>Runs the two-client room setup against the installed game without inspecting its rendered UI.</summary>
internal static class OnlineGameRuntimeVerification
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run(string endpoint, string gamePath)
    {
        VerifyLocalMappingCandidateSelection();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Game-start verification requires an isolated loopback relay.", nameof(endpoint));
        var root = Path.GetFullPath(gamePath);
        if (!GameInstallationLocator.IsValid(root) || !File.Exists(Path.Combine(root, "TCP Protocol.dll")))
            throw new InvalidOperationException("Choose the supported EDMW game folder containing its TCP/IP provider.");
        if (ProcessExists())
            throw new InvalidOperationException("Close Empires: Dawn of the Modern World before running the room-setup verification.");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        ApplicationConfiguration.Initialize();
        Exception? failure = null;
        using var driver = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new(-32000, -32000),
            Size = new(1, 1),
            Opacity = 0,
        };
        driver.Shown += async (_, _) =>
        {
            driver.Hide();
            try { await RunScenarioAsync(endpoint, root).ConfigureAwait(true); }
            catch (Exception error) { failure = error; }
            finally { driver.Close(); }
        };
        Application.Run(driver);
        if (failure is not null) throw new InvalidOperationException("Room setup runtime verification failed.", failure);
        return 0;
    }

    private static void VerifyLocalMappingCandidateSelection()
    {
        const string localIpv4 = "192.168.56.1";
        const string hostCandidate = "candidate:1 1 udp 2122260223 192.168.56.1 51000 typ host";
        if (!OnlineGameIceSession.TryGetLocalMappingCandidate(hostCandidate, out var hostAddress, out var hostPriority) ||
            hostAddress != localIpv4 || hostPriority != 2122260223u)
            throw new InvalidDataException("The IPv4 host candidate was not selected as the local game-mapping interface.");

        const string reflexiveCandidate = "candidate:2 1 udp 1686052607 203.0.113.20 62000 typ srflx raddr 192.168.56.1 rport 51000";
        if (!OnlineGameIceSession.TryGetLocalMappingCandidate(reflexiveCandidate, out var reflexiveBase, out _) ||
            reflexiveBase != localIpv4)
            throw new InvalidDataException("The server-reflexive candidate did not resolve to its local base interface.");

        const string noBaseCandidate = "candidate:3 1 udp 1686052607 203.0.113.20 62000 typ srflx";
        if (OnlineGameIceSession.TryGetLocalMappingCandidate(noBaseCandidate, out _, out _))
            throw new InvalidDataException("The STUN-mapped public IPv4 address was incorrectly accepted as a local adapter address.");

        const string ipv6Candidate = "candidate:4 1 udp 2122260223 2001:db8::1 51000 typ host";
        if (OnlineGameIceSession.TryGetLocalMappingCandidate(ipv6Candidate, out _, out _))
            throw new InvalidDataException("An IPv6-only selected candidate was incorrectly accepted for the game's IPv4 adapter mapping.");

        var localId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var hostId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var gatheredRoutes = new Dictionary<Guid, (string Address, uint Priority)>
        {
            [hostId] = (localIpv4, 2122260223u),
        };
        if (OnlineGameIceSession.SelectPreferredLocalIpv4(localId, hostId,
                new Dictionary<Guid, string>(), gatheredRoutes) != localIpv4)
            throw new InvalidDataException("A joining player could not resolve the designated host route from a gathered IPv4 fallback.");
        var selectedRoutes = new Dictionary<Guid, string> { [hostId] = "192.168.56.2" };
        if (OnlineGameIceSession.SelectPreferredLocalIpv4(localId, hostId, selectedRoutes, gatheredRoutes) != "192.168.56.2")
            throw new InvalidDataException("A gathered IPv4 fallback overrode the host's selected ICE interface.");

        Console.WriteLine("ICE PASS: IPv4 base selection rejects public STUN addresses, supports gathered fallback routes, and prefers the selected host route.");
    }

    private static async Task RunScenarioAsync(string endpoint, string gamePath)
    {
        MainForm? hostForm = null;
        MainForm? clientForm = null;
        OnlineRelayClient? host = null;
        OnlineRelayClient? client = null;
        try
        {
            hostForm = CreateHiddenStudio(gamePath);
            clientForm = CreateHiddenStudio(gamePath);
            host = GetOnlineClient(hostForm);
            client = GetOnlineClient(clientForm);
            host.Preferences.PersistIdentity = false;
            client.Preferences.PersistIdentity = false;

            await host.ConnectAsync(endpoint, "EDMW runtime host");
            await client.ConnectAsync(endpoint, "EDMW runtime player");
            var room = await host.CreateGameRoomAsync("Room network verification", "public", 2);
            var joined = await client.JoinGameRoomAsync(room.RoomId);
            if (joined.Members.Length != 2 || joined.GameHostUserId != host.User!.UserId)
                throw new InvalidDataException("The runtime verification players did not enter the host-designated two-player room.");
            Console.WriteLine("GROUP PASS: host and player joined the same two-member public matchmaking room.");
            await VerifyPeerDatagramsAsync(host, client, joined).ConfigureAwait(true);

            var start = typeof(MainForm).GetMethod("PrepareOnlineGameNetworkAsync", PrivateInstance)
                ?? throw new MissingMethodException(typeof(MainForm).FullName, "PrepareOnlineGameNetworkAsync");
            var progress = new Action<string>(message => Console.WriteLine("ROOM: " + message));
            var task = start.Invoke(hostForm, [progress]) as Task
                ?? throw new InvalidOperationException("Mod Studio did not start its matchmaking network workflow.");
            using (var diagnostics = new CancellationTokenSource())
            {
                var diagnosticTask = LogGameProcessStateAsync(hostForm, clientForm, task, diagnostics.Token);
                try { await task.WaitAsync(TimeSpan.FromMinutes(8)).ConfigureAwait(true); }
                finally
                {
                    diagnostics.Cancel();
                    await diagnosticTask.ConfigureAwait(true);
                }
            }

            var processes = System.Diagnostics.Process.GetProcessesByName("Empires_DMW");
            try
            {
                if (processes.Length != 2)
                    throw new InvalidOperationException($"The room setup completed, but {processes.Length} EDMW game processes are active instead of two.");
            }
            finally { foreach (var process in processes) process.Dispose(); }

            var hostBridge = GetBridge(hostForm);
            var clientBridge = GetBridge(clientForm);
            if (ReadProperty(hostBridge, "IsAdapterMappingValidated") is not true ||
                ReadProperty(clientBridge, "IsAdapterMappingValidated") is not true)
                throw new InvalidOperationException("The two game processes launched, but one or both failed the native virtual-adapter transform self-check.");
            if (ReadProperty(hostBridge, "IsRouteMappingSelfCheckPassed") is not true ||
                ReadProperty(clientBridge, "IsRouteMappingSelfCheckPassed") is not true)
                throw new InvalidOperationException("The two game processes launched and adapter mapping passed, but one or both failed the native route-table transform self-check.");
            Console.WriteLine("GAME PASS: host and player adapter and route-table mapping transforms passed native self-checks.");
            await WaitForProviderHooksAsync(hostBridge, "host").ConfigureAwait(true);
            await WaitForProviderHooksAsync(clientBridge, "player").ConfigureAwait(true);
            Console.WriteLine("GAME PASS: TCP Protocol.dll adapter and Winsock IAT hooks installed in both game processes.");
            await WaitForGlobalAddressDetoursAsync(hostBridge, "host").ConfigureAwait(true);
            await WaitForGlobalAddressDetoursAsync(clientBridge, "player").ConfigureAwait(true);
            Console.WriteLine("GAME PASS: global GetAdaptersInfo and GetIpForwardTable entry detours installed in both game processes.");

            Console.WriteLine("GAME PASS: both room members launched the supported game through the network-only setup flow.");
            Console.WriteLine("GAME NOTE: game multiplayer and Join by IP controls remain under EDMW's native UI.");
        }
        finally
        {
            if (host is { CurrentGameRoom: not null })
                try { await host.LeaveGameRoomAsync(); } catch (Exception) { }
            if (client is { CurrentGameRoom: not null })
                try { await client.LeaveGameRoomAsync(); } catch (Exception) { }
            if (hostForm is not null) CloseStudio(hostForm);
            if (clientForm is not null) CloseStudio(clientForm);
            StopVerificationGameProcesses(gamePath);
        }
    }

    private static MainForm CreateHiddenStudio(string gamePath)
    {
        var form = new MainForm(verificationMode: true) { ShowInTaskbar = false };
        var gamePathField = typeof(MainForm).GetField("_selectedGamePath", PrivateInstance)
            ?? throw new MissingFieldException(typeof(MainForm).FullName, "_selectedGamePath");
        gamePathField.SetValue(form, gamePath);
        form.Show();
        form.Hide();
        return form;
    }

    private static OnlineRelayClient GetOnlineClient(MainForm form) =>
        typeof(MainForm).GetField("_online", PrivateInstance)?.GetValue(form) as OnlineRelayClient
        ?? throw new InvalidOperationException("The Mod Studio online client was not initialized.");

    private static object? ReadProperty(object? instance, string property) =>
        instance?.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);

    private static async Task VerifyPeerDatagramsAsync(OnlineRelayClient host, OnlineRelayClient player, OnlineGameRoom room)
    {
        var hostId = host.User?.UserId ?? throw new InvalidOperationException("The runtime host has no online identity.");
        var playerId = player.User?.UserId ?? throw new InvalidOperationException("The runtime player has no online identity.");
        var hostMember = room.Members.Single(member => member.Profile.UserId == hostId);
        var playerMember = room.Members.Single(member => member.Profile.UserId == playerId);
        var hostToPlayerPayload = new byte[] { 0x45, 0x44, 0x4D, 0x57, 0x01, 0xA1 };
        var playerToHostPayload = new byte[] { 0x45, 0x44, 0x4D, 0x57, 0x02, 0xB2 };
        var roomBroadcastPayload = new byte[] { 0x45, 0x44, 0x4D, 0x57, 0x03, 0xC3 };
        var hostToPlayer = new TaskCompletionSource<OnlineIceDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);
        var playerToHost = new TaskCompletionSource<OnlineIceDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);
        var roomBroadcast = new TaskCompletionSource<OnlineIceDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<OnlineIceDatagram> captureAtPlayer = (_, packet) =>
        {
            if (packet.RoomId == room.RoomId && packet.FromUserId == hostId && packet.SourcePort == 31001 &&
                packet.TargetPort == 31002 && packet.Payload.AsSpan().SequenceEqual(hostToPlayerPayload))
                hostToPlayer.TrySetResult(packet);
            if (packet.RoomId == room.RoomId && packet.FromUserId == hostId && packet.SourcePort == 33001 &&
                packet.TargetPort == 33002 && packet.Payload.AsSpan().SequenceEqual(roomBroadcastPayload))
                roomBroadcast.TrySetResult(packet);
        };
        EventHandler<OnlineIceDatagram> captureAtHost = (_, packet) =>
        {
            if (packet.RoomId == room.RoomId && packet.FromUserId == playerId && packet.SourcePort == 32001 &&
                packet.TargetPort == 32002 && packet.Payload.AsSpan().SequenceEqual(playerToHostPayload))
                playerToHost.TrySetResult(packet);
        };

        player.GameIceSession.DatagramReceived += captureAtPlayer;
        host.GameIceSession.DatagramReceived += captureAtHost;
        try
        {
            await Task.WhenAll(
                host.GameIceSession.WaitForPeersReadyAsync([playerId], TimeSpan.FromSeconds(60)),
                player.GameIceSession.WaitForPeersReadyAsync([hostId], TimeSpan.FromSeconds(60))).ConfigureAwait(true);
            var hostSelectedLocalIp = host.GameIceSession.PreferredLocalIpv4
                ?? throw new InvalidOperationException("The host ICE connection has no local IPv4 interface for game network mapping.");
            var playerSelectedLocalIp = player.GameIceSession.PreferredLocalIpv4
                ?? throw new InvalidOperationException("The player ICE connection has no local IPv4 interface for game network mapping.");
            Console.WriteLine($"ICE ROUTE: local IPv4 mapping interfaces host={hostSelectedLocalIp}; player={playerSelectedLocalIp}.");

            if (!host.GameIceSession.SendDatagram(playerMember.RelayPort, 31001, 31002, hostToPlayerPayload))
                throw new IOException("The host could not send a UDP probe over its direct ICE data channel to the player's virtual route.");
            await WaitForProbeAsync(hostToPlayer.Task, "host-to-player unicast", TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            if (!player.GameIceSession.SendDatagram(hostMember.RelayPort, 32001, 32002, playerToHostPayload))
                throw new IOException("The player could not send a UDP probe over its direct ICE data channel to the host's virtual route.");
            await WaitForProbeAsync(playerToHost.Task, "player-to-host unicast", TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            if (!host.GameIceSession.SendDatagram(0, 33001, 33002, roomBroadcastPayload))
                throw new IOException("The host could not send a UDP room-broadcast probe over its direct ICE data channel.");
            await WaitForProbeAsync(roomBroadcast.Task, "room broadcast", TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }
        finally
        {
            player.GameIceSession.DatagramReceived -= captureAtPlayer;
            host.GameIceSession.DatagramReceived -= captureAtHost;
        }

        Console.WriteLine("ICE PASS: directed UDP probes and a room-broadcast probe reached the expected member over direct ICE data channels.");
    }

    private static async Task<T> WaitForProbeAsync<T>(Task<T> probe, string direction, TimeSpan timeout)
    {
        try { return await probe.WaitAsync(timeout).ConfigureAwait(true); }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"The {direction} UDP probe was sent but the expected peer did not receive it within {timeout.TotalSeconds:0} seconds.", error);
        }
    }

    private static async Task WaitForProviderHooksAsync(object? bridge, string member)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (ReadProperty(bridge, "IsProviderHooked") is true) return;
            await Task.Delay(100).ConfigureAwait(true);
        }
        var status = ReadProperty(bridge, "ProviderStatusMessage") as string ?? "No provider-hook status was received.";
        throw new TimeoutException($"TCP Protocol.dll hooks did not install for the {member} process: {status}");
    }

    private static async Task WaitForGlobalAddressDetoursAsync(object? bridge, string member)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (ReadProperty(bridge, "HasGlobalAddressMappingDetours") is true) return;
            await Task.Delay(100).ConfigureAwait(true);
        }
        var status = ReadProperty(bridge, "LatestAutomationStatus") as string ?? "No IP Helper detour status was received.";
        throw new InvalidOperationException($"Global IP Helper detours did not install for the {member} process: {status}");
    }

    private static async Task LogGameProcessStateAsync(MainForm hostForm, MainForm clientForm, Task startTask, CancellationToken cancellationToken)
    {
        while (!startTask.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            var hostBridge = GetBridge(hostForm);
            var clientBridge = GetBridge(clientForm);
            var game = hostBridge?.GetType().GetField("_gameProcess", PrivateInstance)?.GetValue(hostBridge) as System.Diagnostics.Process;
            var launcher = hostBridge?.GetType().GetField("_launcher", PrivateInstance)?.GetValue(hostBridge) as System.Diagnostics.Process;
            var nativeStatus = hostBridge?.GetType().GetProperty("LatestAutomationStatus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(hostBridge) as string;
            Console.WriteLine($"PROCESS: bridge={(hostBridge is null ? "stopped" : "active")}; " +
                $"game={(game is null ? "not attached" : FormatProcess(game))}; " +
                $"launcher={(launcher is null ? "not attached" : FormatProcess(launcher))}; " +
                $"native={(nativeStatus ?? "no native status received")}; " +
                $"host traffic={GetTrafficSummary(hostBridge)}; client traffic={GetTrafficSummary(clientBridge)}");
            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    private static object? GetBridge(MainForm form) =>
        typeof(MainForm).GetField("_onlineGameTrafficBridge", PrivateInstance)?.GetValue(form);

    private static string GetTrafficSummary(object? bridge) =>
        bridge?.GetType().GetProperty("TrafficSummary", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(bridge) as string
        ?? "bridge stopped";

    private static string FormatProcess(System.Diagnostics.Process process)
    {
        try { return $"pid {process.Id}, exited={process.HasExited}" + (process.HasExited ? $", code={process.ExitCode}" : ""); }
        catch (InvalidOperationException) { return "already exited"; }
    }

    private static bool ProcessExists()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("Empires_DMW");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static void StopVerificationGameProcesses(string gamePath)
    {
        var expectedPath = Path.GetFullPath(Path.Combine(gamePath, "Empires_DMW.exe"));
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("Empires_DMW"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
    }

    private static void CloseStudio(MainForm form)
    {
        try { form.Close(); }
        catch (Exception) { }
        try { form.Dispose(); }
        catch (Exception) { }
    }
}

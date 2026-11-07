using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace EDMWMapDesigner.Networking;

/// <summary>Connects the game's Winsock traffic to direct ICE data channels through a local pipe.</summary>
internal sealed class OnlineGameTrafficBridge : IAsyncDisposable
{
    private const byte StreamOpen = 1;
    private const byte StreamAccepted = 2;
    private const byte StreamData = 3;
    private const byte StreamClosed = 4;
    private const byte StreamRejected = 5;
    private const int NativeTcpConnectHeaderBytes = 12;

    private sealed class GameTcpStream(Guid id, Guid remoteUserId, TcpClient client, int sourcePort, int targetPort)
    {
        public Guid Id { get; } = id;
        public Guid RemoteUserId { get; } = remoteUserId;
        public TcpClient Client { get; } = client;
        public NetworkStream Stream { get; } = client.GetStream();
        public int SourcePort { get; } = sourcePort;
        public int TargetPort { get; } = targetPort;
        public TaskCompletionSource<bool> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Closed;
    }

    private const int MaximumDatagramBytes = OnlineRelayClient.MaximumGamePacketBytes;
    private const int PipeFrameLimit = 2048;
    private const int SwRestore = 9;
    private const string NativeLibrary = "EDMWRelayBridge.dll";
    private const string NativeLauncher = "EDMWRelayLauncher.exe";
    private static readonly object TrafficDiagnosticLock = new();
    private readonly OnlineRelayClient _client;
    private readonly string _launchRole;
    private readonly NamedPipeServerStream _pipe;
    private readonly NamedPipeServerStream _eventPipe;
    private readonly NamedPipeServerStream _statusPipe;
    private readonly Socket _gameSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Process _launcher;
    private readonly TcpListener _tcpProxyListener;
    private Process? _gameProcess;
    private readonly string _sessionDirectory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _pipeWrite = new(1, 1);
    private readonly ConcurrentDictionary<int, IPAddress> _boundGameAddresses = new();
    private readonly ConcurrentDictionary<int, IPAddress> _boundGameTcpAddresses = new();
    private readonly ConcurrentDictionary<(int LocalPeerPort, int ListenerPort), TaskCompletionSource<bool>> _tcpPeerMappingAcks = new();
    private readonly ConcurrentDictionary<(Guid Peer, Guid Stream), GameTcpStream> _tcpStreams = new();
    private readonly TaskCompletionSource<string> _adapterMappingReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<OnlineIceDatagram> _incoming = Channel.CreateBounded<OnlineIceDatagram>(new BoundedChannelOptions(4096)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<OnlineIceTcpFrame> _incomingStreams = Channel.CreateBounded<OnlineIceTcpFrame>(new BoundedChannelOptions(256)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private Task? _pipeReader;
    private Task? _statusPipeReader;
    private Task? _pipeWriter;
    private Task? _tcpProxyListenerTask;
    private Task? _streamDispatcherTask;
    private Task? _launcherMonitor;
    private Task? _gameExitMonitor;
    private int _disposed;
    private int _providerHookState;
    private int _faulted;
    private int _diagnosticDatagramTraces;
    private string? _providerStatusMessage;
    private string? _latestAutomationStatus;
    private string? _adapterMappingStatus;
    private string? _routeMappingStatus;
    private string? _globalAddressMappingStatus;
    private long _registeredGameSockets;
    private long _packetsSentDirect;
    private long _broadcastPackets;
    private long _packetsReceivedDirect;
    private long _packetsInjectedToGame;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    public event EventHandler<string>? StatusChanged;
    public event EventHandler? Exited;
    public bool IsProviderHooked => Volatile.Read(ref _providerHookState) == 1;
    public bool IsAdapterMapped => Volatile.Read(ref _adapterMappingStatus) == "active";
    public bool IsAdapterMappingValidated => Volatile.Read(ref _adapterMappingStatus) is "validated" or "active";
    public bool IsRouteMappingSelfCheckPassed => Volatile.Read(ref _routeMappingStatus) is "self-check" or "active";
    public bool HasGlobalAddressMappingDetours => Volatile.Read(ref _globalAddressMappingStatus) == "installed";
    public bool HasRouteTableHook => Volatile.Read(ref _routeMappingStatus) is "hooked" or "active" or "no-match";
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;
    public bool LauncherHasExited => _launcher.HasExited;
    public string? ProviderStatusMessage => Volatile.Read(ref _providerStatusMessage);
    public string? LatestAutomationStatus => Volatile.Read(ref _latestAutomationStatus);
    public string TrafficSummary
    {
        get
        {
            var state = IsFaulted ? "bridge stopped" : IsProviderHooked ? "hook active" :
                Volatile.Read(ref _providerHookState) == 2 ? "provider not loaded" : "waiting for provider";
            var room = _client.CurrentGameRoom;
            var peers = room?.Members.Length - 1 ?? 0;
            var ready = _client.GameIceSession.ReadyPeerCount;
            var local = room?.Members.FirstOrDefault(member => member.Profile.UserId == _client.User?.UserId);
            var virtualIp = local is null ? "IP pending" : OnlineGameAddress.VirtualIpv4(local.RelayPort);
            var adapterMapping = Volatile.Read(ref _adapterMappingStatus) switch
            {
                "active" => "game adapter mapped",
                "validated" => "GetAdaptersInfo detour verified; waiting for EDMW query",
                "mismatch" => "adapter mismatch",
                _ => "adapter mapping pending",
            };
            var routeMapping = Volatile.Read(ref _routeMappingStatus) switch
            {
                "active" => "route table mapped",
                "no-match" => "no matching route row",
                "hooked" => "route hook active",
                "self-check" => "GetIpForwardTable detour verified; waiting for EDMW query",
                "self-check-no-match" => "route query verified; no matching row",
                _ => "route hook pending",
            };
            var globalMapping = Volatile.Read(ref _globalAddressMappingStatus) switch
            {
                "installed" => "global IP Helper detours active",
                "incomplete" => "global IP Helper detours incomplete",
                _ => "global IP Helper detours pending",
            };
            return $"{state} · {globalMapping} · {adapterMapping} · {routeMapping} · IP {virtualIp} · ICE {ready}/{Math.Max(0, peers)} peers · UDP {Interlocked.Read(ref _registeredGameSockets)} · TX {Interlocked.Read(ref _packetsSentDirect)} (broadcast {Interlocked.Read(ref _broadcastPackets)}) · RX {Interlocked.Read(ref _packetsReceivedDirect)} · game {Interlocked.Read(ref _packetsInjectedToGame)}";
        }
    }

    public async Task<bool> BringGameWindowToFrontAsync(CancellationToken cancellationToken = default)
    {
        var game = _gameProcess;
        if (game is null) return false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (game.HasExited) return false;
                game.Refresh();
                var window = game.MainWindowHandle;
                if (window != IntPtr.Zero)
                {
                    _ = AllowSetForegroundWindow(checked((uint)game.Id));
                    _ = ShowWindow(window, SwRestore);
                    _ = SetForegroundWindow(window);
                    return IsWindowVisible(window);
                }
            }
            catch (InvalidOperationException) { return false; }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private OnlineGameTrafficBridge(OnlineRelayClient client, NamedPipeServerStream pipe,
        NamedPipeServerStream eventPipe, NamedPipeServerStream statusPipe, Process launcher,
        TcpListener tcpProxyListener, string sessionDirectory, string launchRole)
    {
        _client = client;
        _launchRole = launchRole;
        _pipe = pipe;
        _eventPipe = eventPipe;
        _statusPipe = statusPipe;
        _launcher = launcher;
        _tcpProxyListener = tcpProxyListener;
        _sessionDirectory = sessionDirectory;
    }

    public static async Task<OnlineGameTrafficBridge> LaunchAsync(OnlineRelayClient client, string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.CurrentGameRoom is not { } room || client.User is not { } user)
            throw new InvalidOperationException("Join a relay game room before launching EDMW.");
        if (!room.Members.Any(member => member.Profile.UserId == user.UserId))
            throw new InvalidDataException("The relay room does not list this device as a member.");
        var peerIds = room.Members.Where(member => member.Profile.UserId != user.UserId)
            .Select(member => member.Profile.UserId).Distinct().ToArray();
        if (peerIds.Length == 0)
            throw new InvalidOperationException("Join at least one other player before preparing the game network.");
        await client.GameIceSession.WaitForPeersReadyAsync(peerIds, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        var physicalIpv4Text = client.GameIceSession.PreferredLocalIpv4;
        if (!IPAddress.TryParse(physicalIpv4Text, out var physicalIpv4) || physicalIpv4.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("The selected ICE routes and gathered candidates did not expose a usable local IPv4 interface for GameRanger-style mapping.");
        var executable = Path.Combine(Path.GetFullPath(gameDirectory), "Empires_DMW.exe");
        if (!File.Exists(executable) || !File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "TCP Protocol.dll")))
            throw new FileNotFoundException("The selected game folder needs Empires_DMW.exe and TCP Protocol.dll.", executable);

        var sessionDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EDMW Mod Studio", "Game Relay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDirectory);
        TcpListener? tcpProxyForCleanup = null;
        NamedPipeServerStream? pipeForCleanup = null;
        NamedPipeServerStream? eventPipeForCleanup = null;
        NamedPipeServerStream? statusPipeForCleanup = null;
        try
        {
            ExtractResource("EDMWMapDesigner.Native." + NativeLibrary, Path.Combine(sessionDirectory, NativeLibrary));
            ExtractResource("EDMWMapDesigner.Native." + NativeLauncher, Path.Combine(sessionDirectory, NativeLauncher));
            var pipeName = "EDMWRelay-" + Guid.NewGuid().ToString("N");
            var pipe = pipeForCleanup = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            var eventPipeName = pipeName + "-events";
            var eventPipe = eventPipeForCleanup = new NamedPipeServerStream(eventPipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            var statusPipeName = pipeName + "-status";
            var statusPipe = statusPipeForCleanup = new NamedPipeServerStream(statusPipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            var tcpProxyListener = tcpProxyForCleanup = new TcpListener(IPAddress.Loopback, 0);
            tcpProxyListener.Start();
            var start = new ProcessStartInfo(Path.Combine(sessionDirectory, NativeLauncher))
            { WorkingDirectory = sessionDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add(Path.Combine(sessionDirectory, NativeLibrary));
            start.ArgumentList.Add(pipeName);
            start.Environment["EDMW_RELAY_MODE"] = "menu";
            start.Environment["EDMW_RELAY_EVENT_PIPE"] = eventPipeName;
            start.Environment["EDMW_RELAY_STATUS_PIPE"] = statusPipeName;
            var launcher = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the relay launcher.");
            var bridge = new OnlineGameTrafficBridge(client, pipe, eventPipe, statusPipe, launcher, tcpProxyListener, sessionDirectory,
                "menu");
            pipeForCleanup = null;
            eventPipeForCleanup = null;
            statusPipeForCleanup = null;
            client.GameIceSession.DatagramReceived += bridge.GameDatagramReceived;
            client.GameIceSession.TcpFrameReceived += bridge.GameTcpFrameReceived;
            client.GameIceSession.StatusChanged += bridge.IceStatusChanged;
            var gameProcessId = 0;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var launcherError = launcher.StandardError.ReadToEndAsync(timeout.Token);
                var processIdLine = await launcher.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(processIdLine))
                    throw new InvalidOperationException(await launcherError.ConfigureAwait(false) is { Length: > 0 } error
                        ? error.Trim() : "The relay launcher did not start the game process.");
                if (!int.TryParse(processIdLine, out gameProcessId)) throw new InvalidDataException("The relay launcher returned an invalid game process ID.");
                bridge._gameProcess = Process.GetProcessById(gameProcessId);
                bridge._gameExitMonitor = bridge.MonitorGameProcessAsync(bridge._gameProcess);
                try
                {
                    await Task.WhenAll(pipe.WaitForConnectionAsync(timeout.Token), eventPipe.WaitForConnectionAsync(timeout.Token),
                        statusPipe.WaitForConnectionAsync(timeout.Token)).ConfigureAwait(false);
                }
                catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
                {
                    var missingPipes = new List<string>();
                    if (!pipe.IsConnected) missingPipes.Add("game-control");
                    if (!eventPipe.IsConnected) missingPipes.Add("game-events");
                    if (!statusPipe.IsConnected) missingPipes.Add("setup-status");
                    throw new TimeoutException("The injected EDMW bridge did not connect its " +
                        string.Join(" and ", missingPipes) + " pipe within 30 seconds.", error);
                }
                bridge._statusPipeReader = bridge.ReadStatusPipeAsync();
                WriteTrafficDiagnostic($"status pipe reader started role={bridge._launchRole}");
                bridge._pipeReader = bridge.ReadPipeAsync();
                WriteTrafficDiagnostic($"game-control pipe reader started role={bridge._launchRole}");
                var configuration = new byte[9];
                configuration[0] = 3;
                BinaryPrimitives.WriteUInt16BigEndian(configuration.AsSpan(1), checked((ushort)room.RelayPort));
                BinaryPrimitives.WriteUInt16BigEndian(configuration.AsSpan(3), checked((ushort)((IPEndPoint)tcpProxyListener.LocalEndpoint).Port));
                physicalIpv4.GetAddressBytes().CopyTo(configuration, 5);
                await bridge.WritePipeFrameAsync(configuration, timeout.Token).ConfigureAwait(false);
                WriteTrafficDiagnostic($"native launch configuration written relay={room.RelayPort} physical={physicalIpv4}");
                bridge._pipeWriter = bridge.WriteGamePacketsAsync();
                bridge._tcpProxyListenerTask = bridge.AcceptTcpProxyConnectionsAsync();
                bridge._streamDispatcherTask = bridge.DispatchGameStreamFramesAsync();
                bridge._launcherMonitor = bridge.MonitorLauncherAsync();
                var mappingStatus = await bridge._adapterMappingReady.Task.WaitAsync(TimeSpan.FromSeconds(15), timeout.Token).ConfigureAwait(false);
                var iceStatus = client.GameIceSession.LatestStatus ?? "Negotiating direct ICE/STUN connections.";
                var processStatus = $"EDMW process {bridge._gameProcess.Id} launched.";
                bridge.StatusChanged?.Invoke(bridge, $"{processStatus} {mappingStatus} {iceStatus}");
                return bridge;
            }
            catch
            {
                await bridge.DisposeAsync().ConfigureAwait(false);
                if (gameProcessId != 0)
                    try { using var game = Process.GetProcessById(gameProcessId); game.Kill(entireProcessTree: true); } catch (Exception) { }
                throw;
            }
        }
        catch
        {
            try { tcpProxyForCleanup?.Stop(); } catch (SocketException) { }
            pipeForCleanup?.Dispose();
            eventPipeForCleanup?.Dispose();
            statusPipeForCleanup?.Dispose();
            try { if (Directory.Exists(sessionDirectory)) Directory.Delete(sessionDirectory, true); } catch (IOException) { }
            throw;
        }
    }

    private static void ExtractResource(string name, string path)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The internet gameplay bridge is not bundled in this build.");
        using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }

    private async Task ReadPipeAsync()
    {
        WriteTrafficDiagnostic($"game-control pipe read loop entered role={_launchRole} diagnostics={Environment.GetEnvironmentVariable("EDMW_RELAY_DIAGNOSTICS_PATH") ?? "<unset>"}");
        try
        {
            var prefix = new byte[sizeof(int)];
            while (!_lifetime.IsCancellationRequested)
            {
                await _eventPipe.ReadExactlyAsync(prefix, _lifetime.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (length is < 1 or > PipeFrameLimit) throw new InvalidDataException("The game bridge sent an invalid frame length.");
                var body = new byte[length];
                await _eventPipe.ReadExactlyAsync(body, _lifetime.Token).ConfigureAwait(false);
                if (body[0] != 1) WriteTrafficDiagnostic($"native pipe frame role={_launchRole} type={body[0]} bytes={length}");
                if (body[0] == 1) await HandleGameDatagramAsync(body).ConfigureAwait(false);
                else if (body[0] == 4) RegisterGameSocket(body);
                else if (body[0] == 5) HandleProviderHookStatus(body);
                else if (body[0] == 7) HandleTcpPeerMappingAck(body);
                else if (body[0] == 8) RegisterGameTcpListener(body);
                else if (body[0] == 9) HandleMapAutomationStatus(body);
                else throw new InvalidDataException("The game bridge sent an unknown frame type.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Abort("Game traffic bridge stopped: " + error.Message); }
    }

    private async Task ReadStatusPipeAsync()
    {
        WriteTrafficDiagnostic($"status pipe read loop entered role={_launchRole}");
        try
        {
            var prefix = new byte[sizeof(int)];
            while (!_lifetime.IsCancellationRequested)
            {
                await _statusPipe.ReadExactlyAsync(prefix, _lifetime.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (length is < 2 or > PipeFrameLimit) throw new InvalidDataException("The game sent an invalid status frame length.");
                var body = new byte[length];
                await _statusPipe.ReadExactlyAsync(body, _lifetime.Token).ConfigureAwait(false);
                if (body[0] != 9) throw new InvalidDataException("The game sent a non-status frame on its status pipe.");
                HandleMapAutomationStatus(body);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Abort("Game setup status pipe stopped: " + error.Message); }
    }

    private void HandleProviderHookStatus(byte[] body)
    {
        if (body.Length != 2) throw new InvalidDataException("The game bridge sent an invalid provider status.");
        if (body[1] == 0)
        {
            const string message = "EDMW's TCP/IP provider hooks are active. Address mapping status will appear when the game queries its adapter list.";
            Volatile.Write(ref _providerHookState, 1);
            Volatile.Write(ref _providerStatusMessage, message);
            StatusChanged?.Invoke(this, message);
            return;
        }
        if (body[1] == 3)
        {
            const string message = "The room bridge is active and waiting for EDMW's TCP/IP provider to load.";
            Volatile.Write(ref _providerHookState, 2);
            Volatile.Write(ref _providerStatusMessage, message);
            StatusChanged?.Invoke(this, message);
            return;
        }
        var failure = body[1] == 1
            ? "The supported TCP/IP provider could not be hooked; room network mapping is unavailable."
            : "The native network mapping hook could not start.";
        Abort(failure);
    }

    private void HandleMapAutomationStatus(byte[] body)
    {
        if (body.Length < 2 || body.Length > 2048 || body[1] is not (5 or 8 or 9))
            throw new InvalidDataException("The game sent an invalid map automation status.");
        var message = body.Length == 2 ? "EDMW map automation completed." : Encoding.UTF8.GetString(body, 2, body.Length - 2);
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidDataException("The game sent an empty map automation status.");
        Volatile.Write(ref _latestAutomationStatus, message);
        if (message.StartsWith("Network mapping self-check passed:", StringComparison.Ordinal))
        {
            Volatile.Write(ref _adapterMappingStatus, "validated");
            _adapterMappingReady.TrySetResult(message);
        }
        else if (message.StartsWith("Network mapping active:", StringComparison.Ordinal))
        {
            Volatile.Write(ref _adapterMappingStatus, "active");
            _adapterMappingReady.TrySetResult(message);
        }
        else if (message.StartsWith("Network mapping self-check failed:", StringComparison.Ordinal) ||
                 message.StartsWith("Network mapping did not find", StringComparison.Ordinal))
        {
            Volatile.Write(ref _adapterMappingStatus, "mismatch");
            _adapterMappingReady.TrySetException(new InvalidOperationException(message));
        }
        else if (message.StartsWith("Global GetAdaptersInfo and GetIpForwardTable entry detours are installed", StringComparison.Ordinal))
            Volatile.Write(ref _globalAddressMappingStatus, "installed");
        else if (message.StartsWith("Global IP Helper entry detours are incomplete", StringComparison.Ordinal))
            Volatile.Write(ref _globalAddressMappingStatus, "incomplete");
        else if (message.StartsWith("Route table self-check passed:", StringComparison.Ordinal))
            Volatile.Write(ref _routeMappingStatus, "self-check");
        else if (message.StartsWith("Route table self-check completed; no route row matched", StringComparison.Ordinal))
            Volatile.Write(ref _routeMappingStatus, "self-check-no-match");
        else if (message.StartsWith("GetIpForwardTable interception is installed", StringComparison.Ordinal))
            Volatile.Write(ref _routeMappingStatus, "hooked");
        else if (message.StartsWith("Route table mapping active:", StringComparison.Ordinal))
            Volatile.Write(ref _routeMappingStatus, "active");
        else if (message.StartsWith("Route table hook ran, but no route row matched", StringComparison.Ordinal))
            Volatile.Write(ref _routeMappingStatus, "no-match");
        StatusChanged?.Invoke(this, message);
    }

    private async Task HandleGameDatagramAsync(byte[] body)
    {
        if (body.Length < 10) throw new InvalidDataException("The game bridge sent a truncated datagram.");
        var targetRelayPort = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1, 2));
        var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(3, 2));
        var targetPort = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(5, 2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(7, 2));
        if (body[0] != 1 || sourcePort == 0 || targetPort == 0 || length is < 1 or > MaximumDatagramBytes || body.Length != 9 + length)
            throw new InvalidDataException("The game bridge sent invalid datagram routing data.");
        var trace = Interlocked.Increment(ref _diagnosticDatagramTraces);
        if (!_client.GameIceSession.SendDatagram(targetRelayPort, sourcePort, targetPort, body.AsMemory(9, length)))
        {
            if (trace <= 16) WriteTrafficDiagnostic($"game UDP relay rejected role={_launchRole} route={targetRelayPort} source={sourcePort} target={targetPort} bytes={length} ICEpeers={_client.GameIceSession.ReadyPeerCount}");
            return;
        }
        Interlocked.Increment(ref _packetsSentDirect);
        if (targetRelayPort == 0) Interlocked.Increment(ref _broadcastPackets);
        if (trace <= 16) WriteTrafficDiagnostic($"game UDP relayed role={_launchRole} route={targetRelayPort} source={sourcePort} target={targetPort} bytes={length}");
    }

    private void RegisterGameSocket(byte[] body)
    {
        if (body.Length != 7 || body[0] != 4) throw new InvalidDataException("The game bridge sent an invalid socket registration.");
        var port = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1, 2));
        if (port == 0) throw new InvalidDataException("The game bridge registered an invalid UDP port.");
        var address = new IPAddress(body.AsSpan(3, 4));
        if (_boundGameAddresses.TryAdd(port, address.Equals(IPAddress.Any) ? IPAddress.Loopback : address))
            Interlocked.Increment(ref _registeredGameSockets);
    }

    private void RegisterGameTcpListener(byte[] body)
    {
        if (body.Length != 7 || body[0] != 8) throw new InvalidDataException("The game bridge sent an invalid TCP listener registration.");
        var port = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1, 2));
        if (port == 0) throw new InvalidDataException("The game bridge registered an invalid TCP port.");
        var address = new IPAddress(body.AsSpan(3, 4));
        var localAddress = address.Equals(IPAddress.Any) ? IPAddress.Loopback : address;
        _boundGameTcpAddresses[port] = localAddress;
        WriteTrafficDiagnostic($"TCP listener registered role={_launchRole} endpoint={localAddress}:{port}");
    }

    private void HandleTcpPeerMappingAck(byte[] body)
    {
        if (body.Length != 5 || body[0] != 7) throw new InvalidDataException("The game bridge sent an invalid TCP peer acknowledgement.");
        var localPort = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1, 2));
        var listenerPort = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(3, 2));
        if (localPort == 0 || listenerPort == 0) throw new InvalidDataException("The game bridge acknowledged an invalid TCP peer mapping.");
        if (_tcpPeerMappingAcks.TryGetValue((localPort, listenerPort), out var completion)) completion.TrySetResult(true);
    }

    private void GameTcpFrameReceived(object? sender, OnlineIceTcpFrame frame)
    {
        if (frame.RoomId != _client.CurrentGameRoom?.RoomId || Volatile.Read(ref _disposed) != 0) return;
        if (!_incomingStreams.Writer.TryWrite(frame)) Abort("The direct TCP queue is full; gameplay was stopped to protect state.");
    }

    private void GameDatagramReceived(object? sender, OnlineIceDatagram packet)
    {
        if (packet.RoomId != _client.CurrentGameRoom?.RoomId || Volatile.Read(ref _disposed) != 0) return;
        var trace = Interlocked.Increment(ref _packetsReceivedDirect);
        if (trace <= 16)
            WriteTrafficDiagnostic($"game UDP received over ICE role={_launchRole} from={packet.FromUserId:N} source={packet.SourcePort} target={packet.TargetPort} bytes={packet.Payload.Length} head={Convert.ToHexString(packet.Payload.AsSpan(0, Math.Min(packet.Payload.Length, 24)))}");
        if (!_incoming.Writer.TryWrite(packet)) Abort("The local game bridge queue is full; direct gameplay was stopped to protect state.");
    }

    private void IceStatusChanged(object? sender, string message)
    {
        if (Volatile.Read(ref _disposed) == 0) StatusChanged?.Invoke(this, message);
    }

    private async Task AcceptTcpProxyConnectionsAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _tcpProxyListener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                client.NoDelay = true;
                _ = HandleTcpProxyClientAsync(client);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
        catch (SocketException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Abort("The local TCP gameplay listener stopped: " + error.Message); }
    }

    private async Task HandleTcpProxyClientAsync(TcpClient client)
    {
        Guid streamId = Guid.Empty;
        Guid remoteUserId = Guid.Empty;
        try
        {
            var preamble = new byte[NativeTcpConnectHeaderBytes];
            await client.GetStream().ReadExactlyAsync(preamble, _lifetime.Token).ConfigureAwait(false);
            if (!preamble.AsSpan(0, 6).SequenceEqual("EDWTCP"u8)) throw new InvalidDataException("The game sent an invalid TCP gameplay preamble.");
            var relayTargetPort = BinaryPrimitives.ReadUInt16BigEndian(preamble.AsSpan(6, 2));
            var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(preamble.AsSpan(8, 2));
            var targetPort = BinaryPrimitives.ReadUInt16BigEndian(preamble.AsSpan(10, 2));
            WriteTrafficDiagnostic($"TCP proxy preamble role={_launchRole} relay={relayTargetPort} source={sourcePort} target={targetPort}");
            var remote = _client.CurrentGameRoom?.Members.FirstOrDefault(member => member.RelayPort == relayTargetPort &&
                member.Profile.UserId != _client.User?.UserId);
            if (remote is null || sourcePort == 0 || targetPort == 0)
                throw new InvalidDataException($"No room peer matches TCP route {relayTargetPort} (source {sourcePort}, target {targetPort}).");
            remoteUserId = remote.Profile.UserId;
            streamId = Guid.NewGuid();
            var stream = new GameTcpStream(streamId, remoteUserId, client, sourcePort, targetPort);
            if (!_tcpStreams.TryAdd((remoteUserId, streamId), stream)) throw new InvalidOperationException("Could not register the direct TCP stream.");
            if (!SendGameStreamFrame(stream, StreamOpen, ReadOnlyMemory<byte>.Empty))
                throw new IOException("The direct TCP data channel to the selected player is not ready.");
            WriteTrafficDiagnostic($"TCP stream open sent role={_launchRole} remote={remote.Profile.DisplayName} stream={streamId:N}");
            if (!await stream.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token).ConfigureAwait(false))
                throw new IOException("The remote game rejected the TCP connection.");
            WriteTrafficDiagnostic($"TCP stream accepted role={_launchRole} remote={remote.Profile.DisplayName} stream={streamId:N}");
            await PumpLocalTcpToPeerAsync(stream).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            WriteTrafficDiagnostic($"TCP proxy stream failed role={_launchRole} remote={remoteUserId:N} stream={streamId:N}: {error.GetType().Name}: {error.Message}");
            if (streamId != Guid.Empty && _tcpStreams.TryGetValue((remoteUserId, streamId), out var stream))
            {
                try { SendGameStreamFrame(stream, StreamClosed, ReadOnlyMemory<byte>.Empty); } catch (Exception) { }
                CloseGameTcpStream(stream);
            }
            if (error is not SocketException and not IOException and not TimeoutException and not InvalidDataException)
                Abort("The direct TCP stream stopped: " + error.Message);
        }
        finally
        {
            if (streamId == Guid.Empty || !_tcpStreams.ContainsKey((remoteUserId, streamId))) client.Dispose();
        }
    }

    private async Task DispatchGameStreamFramesAsync()
    {
        try
        {
            await foreach (var frame in _incomingStreams.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var bytes = frame.Payload;
                if (bytes.Length < 25 || !bytes.AsSpan(0, 4).SequenceEqual("EDWT"u8)) continue;
                var streamId = new Guid(bytes.AsSpan(4, 16));
                var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(20, 2));
                var targetPort = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(22, 2));
                var type = bytes[24];
                var payload = bytes.AsMemory(25);
                if (streamId == Guid.Empty || sourcePort == 0 || targetPort == 0) continue;
                if (type == StreamOpen)
                {
                    WriteTrafficDiagnostic($"TCP stream open received role={_launchRole} remote={frame.FromUserId:N} source={sourcePort} target={targetPort} stream={streamId:N}");
                    _ = AcceptRemoteTcpStreamAsync(frame.FromUserId, streamId, sourcePort, targetPort);
                    continue;
                }
                if (!_tcpStreams.TryGetValue((frame.FromUserId, streamId), out var stream) ||
                    sourcePort != stream.TargetPort || targetPort != stream.SourcePort) continue;
                if (type == StreamAccepted)
                {
                    WriteTrafficDiagnostic($"TCP stream acceptance received role={_launchRole} remote={frame.FromUserId:N} stream={streamId:N}");
                    stream.Accepted.TrySetResult(true);
                }
                else if (type == StreamRejected)
                {
                    WriteTrafficDiagnostic($"TCP stream rejection received role={_launchRole} remote={frame.FromUserId:N} stream={streamId:N}");
                    stream.Accepted.TrySetResult(false);
                }
                else if (type == StreamData) await stream.Stream.WriteAsync(payload, _lifetime.Token).ConfigureAwait(false);
                else if (type == StreamClosed) CloseGameTcpStream(stream);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Abort("Could not forward TCP gameplay to EDMW: " + error.Message); }
    }

    private async Task AcceptRemoteTcpStreamAsync(Guid remoteUserId, Guid streamId, ushort sourcePort, ushort targetPort)
    {
        TcpClient? localClient = null;
        GameTcpStream? stream = null;
        try
        {
            if (!_boundGameTcpAddresses.TryGetValue(targetPort, out var localAddress))
                throw new SocketException((int)SocketError.ConnectionRefused);
            var remoteMember = _client.CurrentGameRoom?.Members.FirstOrDefault(member => member.Profile.UserId == remoteUserId)
                ?? throw new IOException("The direct TCP peer left the game room.");
            localClient = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            localClient.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var localPort = checked((ushort)((IPEndPoint)localClient.Client.LocalEndPoint!).Port);
            await RegisterTcpPeerMappingAsync(localPort, remoteMember.RelayPort, sourcePort, targetPort).ConfigureAwait(false);
            WriteTrafficDiagnostic($"TCP accepted-peer mapping role={_launchRole} local={localPort} remoteRelay={remoteMember.RelayPort} remoteGame={sourcePort} listener={targetPort}");
            await localClient.ConnectAsync(localAddress, targetPort, _lifetime.Token).ConfigureAwait(false);
            WriteTrafficDiagnostic($"TCP local game listener connected role={_launchRole} endpoint={localAddress}:{targetPort} stream={streamId:N}");
            stream = new GameTcpStream(streamId, remoteUserId, localClient, targetPort, sourcePort);
            if (!_tcpStreams.TryAdd((remoteUserId, streamId), stream)) throw new InvalidDataException("The direct TCP stream identifier is already active.");
            if (!SendGameStreamFrame(stream, StreamAccepted, ReadOnlyMemory<byte>.Empty))
                throw new IOException("The direct TCP data channel is not ready.");
            _ = PumpLocalTcpToPeerAsync(stream);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { localClient?.Dispose(); }
        catch (Exception error)
        {
            WriteTrafficDiagnostic($"TCP incoming stream rejected role={_launchRole} remote={remoteUserId:N} source={sourcePort} target={targetPort} stream={streamId:N}: {error.GetType().Name}: {error.Message}");
            if (stream is not null) CloseGameTcpStream(stream);
            else localClient?.Dispose();
            try { SendGameStreamFrame(remoteUserId, streamId, targetPort, sourcePort, StreamRejected, ReadOnlyMemory<byte>.Empty); }
            catch (Exception) { }
        }
    }

    private async Task RegisterTcpPeerMappingAsync(ushort localPort, int relaySourcePort, int sourceGamePort, ushort listenerPort)
    {
        var key = ((int)localPort, (int)listenerPort);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_tcpPeerMappingAcks.TryAdd(key, completion)) throw new InvalidOperationException("A TCP peer mapping is already pending for this port.");
        try
        {
            var body = new byte[9];
            body[0] = 6;
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1, 2), localPort);
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3, 2), checked((ushort)relaySourcePort));
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(5, 2), checked((ushort)sourceGamePort));
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(7, 2), listenerPort);
            await WritePipeFrameAsync(body, _lifetime.Token).ConfigureAwait(false);
            _ = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token).ConfigureAwait(false);
        }
        finally { _tcpPeerMappingAcks.TryRemove(key, out _); }
    }

    private async Task PumpLocalTcpToPeerAsync(GameTcpStream stream)
    {
        try
        {
            var buffer = new byte[OnlineRelayClient.MaximumGameStreamChunkBytes];
            while (!_lifetime.IsCancellationRequested)
            {
                var count = await stream.Stream.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (!SendGameStreamFrame(stream, StreamData, buffer.AsMemory(0, count)))
                    throw new IOException("The direct TCP data channel closed during gameplay.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { }
        finally
        {
            if (Interlocked.Exchange(ref stream.Closed, 1) == 0)
            {
                try { SendGameStreamFrame(stream, StreamClosed, ReadOnlyMemory<byte>.Empty); } catch (Exception) { }
                _tcpStreams.TryRemove((stream.RemoteUserId, stream.Id), out _);
                try { stream.Client.Dispose(); } catch (Exception) { }
            }
        }
    }

    private bool SendGameStreamFrame(GameTcpStream stream, byte type, ReadOnlyMemory<byte> payload) =>
        SendGameStreamFrame(stream.RemoteUserId, stream.Id, stream.SourcePort, stream.TargetPort, type, payload);

    private bool SendGameStreamFrame(Guid remoteUserId, Guid streamId, int sourcePort, int targetPort,
        byte type, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > OnlineRelayClient.MaximumGameStreamChunkBytes || type is < StreamOpen or > StreamRejected ||
            type == StreamData && payload.IsEmpty || type != StreamData && !payload.IsEmpty) return false;
        var frame = new byte[25 + payload.Length];
        "EDWT"u8.CopyTo(frame);
        streamId.TryWriteBytes(frame.AsSpan(4, 16));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(20, 2), checked((ushort)sourcePort));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(22, 2), checked((ushort)targetPort));
        frame[24] = type;
        payload.CopyTo(frame.AsMemory(25));
        return _client.GameIceSession.SendTcpFrame(remoteUserId, frame);
    }

    private void CloseGameTcpStream(GameTcpStream stream)
    {
        if (Interlocked.Exchange(ref stream.Closed, 1) != 0) return;
        _tcpStreams.TryRemove((stream.RemoteUserId, stream.Id), out _);
        stream.Accepted.TrySetResult(false);
        try { stream.Client.Dispose(); } catch (Exception) { }
    }

    private async Task WriteGamePacketsAsync()
    {
        try
        {
            await foreach (var packet in _incoming.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (packet.Payload.Length is < 1 or > MaximumDatagramBytes) continue;
                await InjectLocalDatagramAsync(packet).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Abort("Could not deliver direct gameplay to EDMW: " + error.Message); }
    }

    private async Task InjectLocalDatagramAsync(OnlineIceDatagram packet)
    {
        var localAddress = _boundGameAddresses.TryGetValue(packet.TargetPort, out var address) ? address : IPAddress.Loopback;
        var remoteMember = _client.CurrentGameRoom?.Members.FirstOrDefault(member => member.Profile.UserId == packet.FromUserId);
        if (remoteMember is null) return;
        var frame = new byte[14 + packet.Payload.Length];
        Encoding.ASCII.GetBytes("EDMWRLY1", frame.AsSpan(0, 8));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8, 2), checked((ushort)remoteMember.RelayPort));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10, 2), checked((ushort)packet.SourcePort));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12, 2), checked((ushort)packet.Payload.Length));
        packet.Payload.CopyTo(frame, 14);
        await _gameSocket.SendToAsync(frame, SocketFlags.None, new IPEndPoint(localAddress, packet.TargetPort), _lifetime.Token)
            .ConfigureAwait(false);
        var trace = Interlocked.Read(ref _packetsInjectedToGame);
        if (trace < 16)
            WriteTrafficDiagnostic($"game UDP injected role={_launchRole} from={packet.FromUserId:N} endpoint={localAddress}:{packet.TargetPort} source={packet.SourcePort} bytes={packet.Payload.Length}");
        Interlocked.Increment(ref _packetsInjectedToGame);
    }

    private async Task WritePipeFrameAsync(byte[] body, CancellationToken cancellationToken)
    {
        if (body.Length is < 1 or > PipeFrameLimit) throw new InvalidDataException("The local game bridge frame is too large.");
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        await _pipeWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await _pipe.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _pipeWrite.Release(); }
    }

    private static void WriteTrafficDiagnostic(string message)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("EDMW_RELAY_DIAGNOSTICS_PATH");
            if (string.IsNullOrWhiteSpace(path)) return;
            var line = $"[{Environment.ProcessId}:{Environment.CurrentManagedThreadId}] [managed] {message}";
            lock (TrafficDiagnosticLock)
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            Console.WriteLine("TRAFFIC: " + message);
        }
        catch (Exception) { }
    }

    private async Task MonitorLauncherAsync()
    {
        try { await _launcher.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception) { }
        var game = _gameProcess;
        if (game is not null && !game.HasExited)
        {
            try { await game.WaitForExitAsync().ConfigureAwait(false); }
            catch (Exception) { }
        }
        if (Volatile.Read(ref _disposed) == 0 && game is { HasExited: true })
            StatusChanged?.Invoke(this, $"EDMW closed with exit code {game.ExitCode}.");
        Exited?.Invoke(this, EventArgs.Empty);
    }

    private async Task MonitorGameProcessAsync(Process game)
    {
        try { await game.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            if (Volatile.Read(ref _disposed) == 0)
                StatusChanged?.Invoke(this, "Could not monitor Empires_DMW.exe: " + error.Message);
            return;
        }
        if (Volatile.Read(ref _disposed) != 0 || _lifetime.IsCancellationRequested) return;
        StatusChanged?.Invoke(this, $"EDMW closed with exit code {game.ExitCode}.");
    }

    private void Abort(string message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Volatile.Write(ref _faulted, 1);
        Volatile.Write(ref _providerStatusMessage, message);
        StatusChanged?.Invoke(this, message);
        _incoming.Writer.TryComplete();
        _incomingStreams.Writer.TryComplete();
        _lifetime.Cancel();
        StopTcpRelay();
        _pipe.Dispose();
        _eventPipe.Dispose();
        _statusPipe.Dispose();
        _gameSocket.Dispose();
        try { if (!_launcher.HasExited) _launcher.Kill(); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.GameIceSession.DatagramReceived -= GameDatagramReceived;
        _client.GameIceSession.TcpFrameReceived -= GameTcpFrameReceived;
        _client.GameIceSession.StatusChanged -= IceStatusChanged;
        _incoming.Writer.TryComplete();
        _incomingStreams.Writer.TryComplete();
        _lifetime.Cancel();
        StopTcpRelay();
        _pipe.Dispose();
        _eventPipe.Dispose();
        _statusPipe.Dispose();
        _gameSocket.Dispose();
        try { if (!_launcher.HasExited) _launcher.Kill(); } catch (Exception) { }
        foreach (var task in new[] { _pipeReader, _statusPipeReader, _pipeWriter, _tcpProxyListenerTask, _streamDispatcherTask, _gameExitMonitor })
            if (task is not null) try { await task.ConfigureAwait(false); } catch (Exception) { }
        try { _launcher.Dispose(); } catch (Exception) { }
        if (_gameProcess is { } game)
        {
            if (game.HasExited)
            {
                game.Dispose();
                await DeleteSessionDirectoryAsync(_sessionDirectory).ConfigureAwait(false);
            }
            else _ = CleanupAfterGameExitAsync(game, _sessionDirectory);
        }
        else await DeleteSessionDirectoryAsync(_sessionDirectory).ConfigureAwait(false);
        _pipeWrite.Dispose(); _lifetime.Dispose();
    }

    private void StopTcpRelay()
    {
        try { _tcpProxyListener.Stop(); } catch (SocketException) { }
        foreach (var completion in _tcpPeerMappingAcks.Values) completion.TrySetResult(false);
        foreach (var stream in _tcpStreams.Values) CloseGameTcpStream(stream);
    }

    private static async Task CleanupAfterGameExitAsync(Process game, string sessionDirectory)
    {
        try { await game.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception) { }
        try { game.Dispose(); } catch (Exception) { }
        await DeleteSessionDirectoryAsync(sessionDirectory).ConfigureAwait(false);
    }

    private static async Task DeleteSessionDirectoryAsync(string sessionDirectory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(sessionDirectory)) Directory.Delete(sessionDirectory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9) { await Task.Delay(500).ConfigureAwait(false); }
            catch (UnauthorizedAccessException) when (attempt < 9) { await Task.Delay(500).ConfigureAwait(false); }
            catch (Exception) { return; }
        }
    }
}

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
    private readonly OnlineRelayClient _client;
    private readonly NamedPipeServerStream _pipe;
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
    private readonly Channel<OnlineIceDatagram> _incoming = Channel.CreateBounded<OnlineIceDatagram>(new BoundedChannelOptions(4096)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<OnlineIceTcpFrame> _incomingStreams = Channel.CreateBounded<OnlineIceTcpFrame>(new BoundedChannelOptions(256)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private Task? _pipeReader;
    private Task? _pipeWriter;
    private Task? _tcpProxyListenerTask;
    private Task? _streamDispatcherTask;
    private Task? _launcherMonitor;
    private int _disposed;
    private int _providerHookState;
    private int _faulted;
    private string? _providerStatusMessage;
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
    private static extern bool AllowSetForegroundWindow(uint processId);

    public event EventHandler<string>? StatusChanged;
    public event EventHandler? Exited;
    public bool IsProviderHooked => Volatile.Read(ref _providerHookState) == 1;
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;
    public bool LauncherHasExited => _launcher.HasExited;
    public string? ProviderStatusMessage => Volatile.Read(ref _providerStatusMessage);
    public string TrafficSummary
    {
        get
        {
            var state = IsFaulted ? "bridge stopped" : IsProviderHooked ? "hook active" :
                Volatile.Read(ref _providerHookState) == 2 ? "provider not loaded" : "waiting for provider";
            var peers = _client.CurrentGameRoom?.Members.Length - 1 ?? 0;
            var ready = _client.GameIceSession.ReadyPeerCount;
            return $"{state} · ICE {ready}/{Math.Max(0, peers)} peers · UDP {Interlocked.Read(ref _registeredGameSockets)} · TX {Interlocked.Read(ref _packetsSentDirect)} (broadcast {Interlocked.Read(ref _broadcastPackets)}) · RX {Interlocked.Read(ref _packetsReceivedDirect)} · game {Interlocked.Read(ref _packetsInjectedToGame)}";
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
                    return SetForegroundWindow(window);
                }
            }
            catch (InvalidOperationException) { return false; }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private OnlineGameTrafficBridge(OnlineRelayClient client, NamedPipeServerStream pipe, Process launcher,
        TcpListener tcpProxyListener, string sessionDirectory)
    {
        _client = client;
        _pipe = pipe;
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
        var executable = Path.Combine(Path.GetFullPath(gameDirectory), "Empires_DMW.exe");
        if (!File.Exists(executable) || !File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "TCP Protocol.dll")))
            throw new FileNotFoundException("The selected game folder needs Empires_DMW.exe and TCP Protocol.dll.", executable);

        var sessionDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EDMW Mod Studio", "Game Relay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDirectory);
        TcpListener? tcpProxyForCleanup = null;
        try
        {
            ExtractResource("EDMWMapDesigner.Native." + NativeLibrary, Path.Combine(sessionDirectory, NativeLibrary));
            ExtractResource("EDMWMapDesigner.Native." + NativeLauncher, Path.Combine(sessionDirectory, NativeLauncher));
            var pipeName = "EDMWRelay-" + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            var tcpProxyListener = tcpProxyForCleanup = new TcpListener(IPAddress.Loopback, 0);
            tcpProxyListener.Start();
            var start = new ProcessStartInfo(Path.Combine(sessionDirectory, NativeLauncher))
            { WorkingDirectory = sessionDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add(Path.Combine(sessionDirectory, NativeLibrary));
            start.ArgumentList.Add(pipeName);
            var launcher = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the relay launcher.");
            var bridge = new OnlineGameTrafficBridge(client, pipe, launcher, tcpProxyListener, sessionDirectory);
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
                await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                var configuration = new byte[5];
                configuration[0] = 3;
                BinaryPrimitives.WriteUInt16BigEndian(configuration.AsSpan(1), checked((ushort)room.RelayPort));
                BinaryPrimitives.WriteUInt16BigEndian(configuration.AsSpan(3), checked((ushort)((IPEndPoint)tcpProxyListener.LocalEndpoint).Port));
                await bridge.WritePipeFrameAsync(configuration, timeout.Token).ConfigureAwait(false);
                bridge._pipeReader = bridge.ReadPipeAsync();
                bridge._pipeWriter = bridge.WriteGamePacketsAsync();
                bridge._tcpProxyListenerTask = bridge.AcceptTcpProxyConnectionsAsync();
                bridge._streamDispatcherTask = bridge.DispatchGameStreamFramesAsync();
                bridge._launcherMonitor = bridge.MonitorLauncherAsync();
                var iceStatus = client.GameIceSession.LatestStatus ?? "Negotiating direct ICE/STUN connections.";
                bridge.StatusChanged?.Invoke(bridge, $"EDMW launched. Select TCP/IP, then host or join a LAN game. {iceStatus}");
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
        try
        {
            var prefix = new byte[sizeof(int)];
            while (!_lifetime.IsCancellationRequested)
            {
                await _pipe.ReadExactlyAsync(prefix, _lifetime.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (length is < 1 or > PipeFrameLimit) throw new InvalidDataException("The game bridge sent an invalid frame length.");
                var body = new byte[length];
                await _pipe.ReadExactlyAsync(body, _lifetime.Token).ConfigureAwait(false);
                if (body[0] == 1) await HandleGameDatagramAsync(body).ConfigureAwait(false);
                else if (body[0] == 4) RegisterGameSocket(body);
                else if (body[0] == 5) HandleProviderHookStatus(body);
                else if (body[0] == 7) HandleTcpPeerMappingAck(body);
                else if (body[0] == 8) RegisterGameTcpListener(body);
                else throw new InvalidDataException("The game bridge sent an unknown frame type.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Abort("Game traffic bridge stopped: " + error.Message); }
    }

    private void HandleProviderHookStatus(byte[] body)
    {
        if (body.Length != 2) throw new InvalidDataException("The game bridge sent an invalid provider status.");
        if (body[1] == 0)
        {
            const string message = "EDMW's TCP/IP provider is connected to direct ICE gameplay. Host or join from the game's LAN screen.";
            Volatile.Write(ref _providerHookState, 1);
            Volatile.Write(ref _providerStatusMessage, message);
            StatusChanged?.Invoke(this, message);
            return;
        }
        if (body[1] == 3)
        {
            const string message = "The game bridge is ready and waiting for EDMW's TCP/IP provider to load.";
            Volatile.Write(ref _providerHookState, 2);
            Volatile.Write(ref _providerStatusMessage, message);
            StatusChanged?.Invoke(this, message);
            return;
        }
        var failure = body[1] == 1
            ? "The supported TCP/IP provider could not be hooked; direct gameplay stopped."
            : "The native gameplay hook could not start; direct gameplay stopped.";
        Abort(failure);
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
        if (!_client.GameIceSession.SendDatagram(targetRelayPort, sourcePort, targetPort, body.AsMemory(9, length)))
            return;
        Interlocked.Increment(ref _packetsSentDirect);
        if (targetRelayPort == 0) Interlocked.Increment(ref _broadcastPackets);
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
        _boundGameTcpAddresses[port] = address.Equals(IPAddress.Any) ? IPAddress.Loopback : address;
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
        Interlocked.Increment(ref _packetsReceivedDirect);
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
            var remote = _client.CurrentGameRoom?.Members.FirstOrDefault(member => member.RelayPort == relayTargetPort &&
                member.Profile.UserId != _client.User?.UserId);
            if (remote is null || sourcePort == 0 || targetPort == 0) throw new InvalidDataException("The game sent invalid direct TCP routing data.");
            remoteUserId = remote.Profile.UserId;
            streamId = Guid.NewGuid();
            var stream = new GameTcpStream(streamId, remoteUserId, client, sourcePort, targetPort);
            if (!_tcpStreams.TryAdd((remoteUserId, streamId), stream)) throw new InvalidOperationException("Could not register the direct TCP stream.");
            if (!SendGameStreamFrame(stream, StreamOpen, ReadOnlyMemory<byte>.Empty))
                throw new IOException("The direct TCP data channel to the selected player is not ready.");
            if (!await stream.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token).ConfigureAwait(false))
                throw new IOException("The remote game rejected the TCP connection.");
            await PumpLocalTcpToPeerAsync(stream).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
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
                    _ = AcceptRemoteTcpStreamAsync(frame.FromUserId, streamId, sourcePort, targetPort);
                    continue;
                }
                if (!_tcpStreams.TryGetValue((frame.FromUserId, streamId), out var stream) ||
                    sourcePort != stream.TargetPort || targetPort != stream.SourcePort) continue;
                if (type == StreamAccepted) stream.Accepted.TrySetResult(true);
                else if (type == StreamRejected) stream.Accepted.TrySetResult(false);
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
            await localClient.ConnectAsync(localAddress, targetPort, _lifetime.Token).ConfigureAwait(false);
            stream = new GameTcpStream(streamId, remoteUserId, localClient, targetPort, sourcePort);
            if (!_tcpStreams.TryAdd((remoteUserId, streamId), stream)) throw new InvalidDataException("The direct TCP stream identifier is already active.");
            if (!SendGameStreamFrame(stream, StreamAccepted, ReadOnlyMemory<byte>.Empty))
                throw new IOException("The direct TCP data channel is not ready.");
            _ = PumpLocalTcpToPeerAsync(stream);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { localClient?.Dispose(); }
        catch (Exception)
        {
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

    private async Task MonitorLauncherAsync()
    {
        try { await _launcher.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception) { }
        Exited?.Invoke(this, EventArgs.Empty);
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
        _gameSocket.Dispose();
        try { if (!_launcher.HasExited) _launcher.Kill(); } catch (Exception) { }
        foreach (var task in new[] { _pipeReader, _pipeWriter, _tcpProxyListenerTask, _streamDispatcherTask })
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

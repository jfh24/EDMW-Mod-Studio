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

/// <summary>Connects the game's UDP provider to the authenticated relay room over a local pipe.</summary>
internal sealed class OnlineGameTrafficBridge : IAsyncDisposable
{
    private const int MaximumDatagramBytes = OnlineRelayClient.MaximumGamePacketBytes;
    private const int PipeFrameLimit = 2048;
    private const int SwRestore = 9;
    private const string NativeLibrary = "EDMWRelayBridge.dll";
    private const string NativeLauncher = "EDMWRelayLauncher.exe";
    private readonly OnlineRelayClient _client;
    private readonly NamedPipeServerStream _pipe;
    private readonly Socket _gameSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Process _launcher;
    private Process? _gameProcess;
    private readonly string _sessionDirectory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _pipeWrite = new(1, 1);
    private readonly ConcurrentDictionary<int, IPAddress> _boundGameAddresses = new();
    private readonly Channel<OnlineGamePacket> _incoming = Channel.CreateBounded<OnlineGamePacket>(new BoundedChannelOptions(4096)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private Task? _pipeReader;
    private Task? _pipeWriter;
    private Task? _launcherMonitor;
    private int _disposed;
    private int _providerHookState;
    private int _faulted;
    private string? _providerStatusMessage;

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

    private OnlineGameTrafficBridge(OnlineRelayClient client, NamedPipeServerStream pipe, Process launcher, string sessionDirectory)
    {
        _client = client;
        _pipe = pipe;
        _launcher = launcher;
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
        try
        {
            ExtractResource("EDMWMapDesigner.Native." + NativeLibrary, Path.Combine(sessionDirectory, NativeLibrary));
            ExtractResource("EDMWMapDesigner.Native." + NativeLauncher, Path.Combine(sessionDirectory, NativeLauncher));
            var pipeName = "EDMWRelay-" + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            var start = new ProcessStartInfo(Path.Combine(sessionDirectory, NativeLauncher))
            { WorkingDirectory = sessionDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add(Path.Combine(sessionDirectory, NativeLibrary));
            start.ArgumentList.Add(pipeName);
            var launcher = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the relay launcher.");
            var bridge = new OnlineGameTrafficBridge(client, pipe, launcher, sessionDirectory);
            client.GamePacketReceived += bridge.GamePacketReceived;
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
                var configuration = new byte[3];
                configuration[0] = 3;
                BinaryPrimitives.WriteUInt16BigEndian(configuration.AsSpan(1), checked((ushort)room.RelayPort));
                await bridge.WritePipeFrameAsync(configuration, timeout.Token).ConfigureAwait(false);
                bridge._pipeReader = bridge.ReadPipeAsync();
                bridge._pipeWriter = bridge.WriteGamePacketsAsync();
                bridge._launcherMonitor = bridge.MonitorLauncherAsync();
                bridge.StatusChanged?.Invoke(bridge, "EDMW launched. Select the TCP/IP provider, then host or join a LAN game; relay hookup will be confirmed when the provider loads.");
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
            const string message = "EDMW's TCP/IP provider is connected to the relay. Host or join from the game's LAN screen.";
            Volatile.Write(ref _providerHookState, 1);
            Volatile.Write(ref _providerStatusMessage, message);
            StatusChanged?.Invoke(this, message);
            return;
        }
        var failure = body[1] == 1
            ? "The supported TCP/IP provider could not be hooked; relay gameplay stopped."
            : "The native relay hook could not start; relay gameplay stopped.";
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
        await _client.SendGamePacketAsync(targetRelayPort, sourcePort, targetPort, body.AsMemory(9, length), _lifetime.Token)
            .ConfigureAwait(false);
    }

    private void RegisterGameSocket(byte[] body)
    {
        if (body.Length != 7 || body[0] != 4) throw new InvalidDataException("The game bridge sent an invalid socket registration.");
        var port = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1, 2));
        if (port == 0) throw new InvalidDataException("The game bridge registered an invalid UDP port.");
        var address = new IPAddress(body.AsSpan(3, 4));
        _boundGameAddresses[port] = address.Equals(IPAddress.Any) ? IPAddress.Loopback : address;
    }

    private void GamePacketReceived(object? sender, OnlineGamePacket packet)
    {
        if (packet.RoomId != _client.CurrentGameRoom?.RoomId || Volatile.Read(ref _disposed) != 0) return;
        if (!_incoming.Writer.TryWrite(packet))
        {
            Abort("The local game bridge queue is full; relay gameplay was stopped to protect state.");
        }
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
        catch (Exception error) { Abort("Could not forward gameplay to EDMW: " + error.Message); }
    }

    private async Task InjectLocalDatagramAsync(OnlineGamePacket packet)
    {
        var localAddress = _boundGameAddresses.TryGetValue(packet.TargetPort, out var address) ? address : IPAddress.Loopback;
        var frame = new byte[14 + packet.Payload.Length];
        Encoding.ASCII.GetBytes("EDMWRLY1", frame.AsSpan(0, 8));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8, 2), checked((ushort)packet.RelaySourcePort));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10, 2), checked((ushort)packet.SourcePort));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12, 2), checked((ushort)packet.Payload.Length));
        packet.Payload.CopyTo(frame, 14);
        await _gameSocket.SendToAsync(frame, SocketFlags.None, new IPEndPoint(localAddress, packet.TargetPort), _lifetime.Token)
            .ConfigureAwait(false);
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
        _lifetime.Cancel();
        _pipe.Dispose();
        _gameSocket.Dispose();
        try { if (!_launcher.HasExited) _launcher.Kill(); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.GamePacketReceived -= GamePacketReceived;
        _incoming.Writer.TryComplete();
        _lifetime.Cancel();
        _pipe.Dispose();
        _gameSocket.Dispose();
        try { if (!_launcher.HasExited) _launcher.Kill(); } catch (Exception) { }
        foreach (var task in new[] { _pipeReader, _pipeWriter })
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

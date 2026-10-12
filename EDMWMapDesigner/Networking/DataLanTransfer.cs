using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.Networking;

/// <summary>Transfers data packages over the local network. Only the review callback can authorize an installation.</summary>
internal sealed class DataLanTransfer<TPackage, TOffer> : IDisposable where TPackage : class
{
    private readonly int ProtocolVersion;
    public const int HeaderSize = 48;
    private readonly int MaxPayloadBytes;
    private readonly string ProtocolMagic;
    private readonly Func<TPackage, byte[]> _encode;
    private readonly Func<byte[], TPackage> _decode;
    private readonly Func<TPackage, string, DateTimeOffset, TOffer> _makeOffer;
    private const int MaxPendingOffers = 2;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReviewTimeout = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8 };
    private readonly Func<TOffer, CancellationToken, Task<CivilizationLanDecision>> _onOffer;
    private readonly string _displayName;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly int _requestedPort;
    private readonly int _requestedDiscoveryPort;
    private readonly IPAddress _bindAddress;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private TcpListener? _listener;
    private UdpClient? _discovery;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _operationLifetime = new();
    private int _clientSequence;
    private int _pendingOffers;
    private long _reservedBytes;
    private bool _disposed;

    public int Port { get; private set; }
    public int DiscoveryPort { get; private set; }
    public bool IsListening { get { lock (_gate) return _listener is not null; } }

    public DataLanTransfer(Func<TOffer, CancellationToken, Task<CivilizationLanDecision>> onOffer,
        Func<TPackage, byte[]> encode, Func<byte[], TPackage> decode, Func<TPackage, string, DateTimeOffset, TOffer> makeOffer,
        string protocolMagic, int protocolVersion, int maxPayloadBytes, string? displayName, int port, int discoveryPort, IPAddress? bindAddress)
    {
        ArgumentNullException.ThrowIfNull(onOffer);
        if (port is < 0 or > 65535 || discoveryPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _bindAddress = bindAddress ?? IPAddress.Any;
        if (_bindAddress.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("LAN sharing uses IPv4.", nameof(bindAddress));
        if (protocolMagic.Length != 8 || protocolMagic.Any(character => character > 127)) throw new ArgumentException("A LAN protocol marker must be eight ASCII bytes.", nameof(protocolMagic));
        _encode = encode; _decode = decode; _makeOffer = makeOffer;
        ProtocolMagic = protocolMagic; ProtocolVersion = protocolVersion; MaxPayloadBytes = maxPayloadBytes;
        _onOffer = onOffer;
        _displayName = CleanText(displayName ?? Environment.MachineName, 80);
        _requestedPort = Port = port;
        _requestedDiscoveryPort = DiscoveryPort = discoveryPort;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) return;
            var listener = new TcpListener(_bindAddress, _requestedPort);
            UdpClient? discovery = null;
            try
            {
                listener.Server.ExclusiveAddressUse = true;
                listener.Start(8);
                discovery = new UdpClient(AddressFamily.InterNetwork);
                discovery.ExclusiveAddressUse = true;
                discovery.Client.Bind(new IPEndPoint(_bindAddress, _requestedDiscoveryPort));
                _listener = listener;
                _discovery = discovery;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                DiscoveryPort = ((IPEndPoint)discovery.Client.LocalEndPoint!).Port;
                _lifetime = new CancellationTokenSource();
                _ = AcceptLoopAsync(listener, _lifetime.Token);
                _ = DiscoveryLoopAsync(discovery, _lifetime.Token);
            }
            catch
            {
                listener.Stop();
                discovery?.Dispose();
                _listener = null;
                _discovery = null;
                _lifetime?.Dispose();
                _lifetime = null;
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _lifetime?.Cancel();
            _operationLifetime?.Cancel();
            _listener?.Stop();
            _discovery?.Dispose();
            foreach (var client in _clients.Values) client.Dispose();
            _listener = null;
            _discovery = null;
            _lifetime?.Dispose();
            _lifetime = null;
            _operationLifetime?.Dispose();
            _operationLifetime = _disposed ? null : new CancellationTokenSource();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }

    public async Task<IReadOnlyList<CivilizationLanPeer>> DiscoverAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        var interval = duration ?? TimeSpan.FromSeconds(2);
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(duration));
        if (DiscoveryPort is < 1 or > 65535) throw new InvalidOperationException("Start the listener before using an automatically assigned discovery port.");
        using var socket = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var nonce = Guid.NewGuid().ToString("N");
        var query = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryMessage(ProtocolMagic, ProtocolVersion, "query", nonce, "", 0, _instanceId), JsonOptions);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
        timeout.CancelAfter(interval);
        var peers = new Dictionary<string, CivilizationLanPeer>(StringComparer.Ordinal);
        var targets = LanInterfaces().Select(item => Broadcast(item.Address, item.Mask)).Append(IPAddress.Loopback).Distinct().ToArray();
        foreach (var target in targets)
        {
            try { await socket.SendAsync(query, new IPEndPoint(target, DiscoveryPort), timeout.Token).ConfigureAwait(false); }
            catch (SocketException) { /* An unavailable adapter must not prevent discovery on other adapters. */ }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { break; }
        }
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var incoming = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (incoming.Buffer.Length is < 1 or > 2048 || !IsLanAddress(incoming.RemoteEndPoint.Address)) continue;
                var reply = JsonSerializer.Deserialize<DiscoveryMessage>(incoming.Buffer, JsonOptions);
                if (reply is null || reply.Magic != ProtocolMagic || reply.Version != ProtocolVersion || reply.Kind != "reply" ||
                    reply.Nonce != nonce || reply.InstanceId == _instanceId || reply.Port is < 1 or > 65535) continue;
                var address = incoming.RemoteEndPoint.Address.ToString();
                // One editor may answer over several active adapters. Send-all should
                // offer to that editor once, while retaining compatibility with older replies.
                var identity = Guid.TryParseExact(reply.InstanceId, "N", out _) ? reply.InstanceId : $"{address}:{reply.Port}";
                peers[identity] = new(CleanText(reply.Name, 80), address, reply.Port);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { break; }
            catch (JsonException) { }
            catch (SocketException) when (!cancellationToken.IsCancellationRequested) { break; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return peers.Values.OrderBy(peer => peer.Name, StringComparer.OrdinalIgnoreCase).ThenBy(peer => peer.Address, StringComparer.Ordinal).ToArray();
    }

    public async Task<CivilizationLanResult> SendAsync(TPackage package, CivilizationLanPeer peer,
        Action<CivilizationLanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(peer);
        if (!IPAddress.TryParse(peer.Address, out var address) || !IsLanAddress(address) || peer.Port is < 1 or > 65535)
            return new(CivilizationLanStatus.Failed, "Enter an IPv4 address on this LAN.");
        try
        {
            var payload = _encode(package);
            operation.Token.ThrowIfCancellationRequested();
            using var socket = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            using (var connect = LinkedTimeout(operation.Token, HandshakeTimeout))
                await socket.ConnectAsync(address, peer.Port, connect.Token).ConfigureAwait(false);
            using var stream = socket.GetStream();
            var header = new byte[HeaderSize];
            Encoding.ASCII.GetBytes(ProtocolMagic).CopyTo(header, 0);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8, 4), ProtocolVersion);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), payload.Length);
            SHA256.HashData(payload).CopyTo(header, 16);
            CivilizationLanResult response;
            using (var handshake = LinkedTimeout(operation.Token, HandshakeTimeout))
            {
                await stream.WriteAsync(header, handshake.Token).ConfigureAwait(false);
                response = await ReadResultAsync(stream, handshake.Token).ConfigureAwait(false);
            }
            Report(progress, response);
            if (response.Status != CivilizationLanStatus.Submitted) return response;
            using (var transfer = LinkedTimeout(operation.Token, TransferTimeout))
            {
                await stream.WriteAsync(payload, transfer.Token).ConfigureAwait(false);
                response = await ReadResultAsync(stream, transfer.Token).ConfigureAwait(false);
            }
            Report(progress, response);
            if (response.Status != CivilizationLanStatus.Awaiting) return response;
            using (var review = LinkedTimeout(operation.Token, ReviewTimeout))
                response = await ReadResultAsync(stream, review.Token).ConfigureAwait(false);
            if (response.Status is not (CivilizationLanStatus.Installed or CivilizationLanStatus.Denied or CivilizationLanStatus.Failed))
                throw new InvalidDataException("The receiver returned an invalid result.");
            Report(progress, response);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or JsonException or OperationCanceledException)
        {
            var failed = new CivilizationLanResult(CivilizationLanStatus.Failed,
                exception is OperationCanceledException ? operation.IsCancellationRequested ? "Transfer canceled." : "The receiver did not respond in time." : CleanText(exception.Message, 300));
            Report(progress, failed);
            return failed;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IsLanAddress(remote.Address))
                { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref _clientSequence);
                _clients[id] = client;
                _ = HandleClientAsync(id, client, remote.Address.ToString(), cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                client?.Dispose();
                if (cancellationToken.IsCancellationRequested) break;
                // Stop/restart is the only recovery action after the listening socket closes.
                break;
            }
        }
    }

    private async Task HandleClientAsync(int id, TcpClient client, string senderAddress, CancellationToken cancellationToken)
    {
        var reserved = 0;
        var pending = false;
        NetworkStream? stream = null;
        try
        {
            stream = client.GetStream();
            lock (_gate)
            {
                if (_pendingOffers < MaxPendingOffers && !cancellationToken.IsCancellationRequested)
                { _pendingOffers++; pending = true; }
            }
            if (!pending)
            { await WriteResultAsync(stream, new(CivilizationLanStatus.Failed, "The receiver is reviewing other exports."), cancellationToken).ConfigureAwait(false); return; }
            var header = new byte[HeaderSize];
            using (var handshake = LinkedTimeout(cancellationToken, HandshakeTimeout))
                await stream.ReadExactlyAsync(header, handshake.Token).ConfigureAwait(false);
            if (!header.AsSpan(0, 8).SequenceEqual(Encoding.ASCII.GetBytes(ProtocolMagic)) ||
                BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4)) != ProtocolVersion)
                throw new InvalidDataException("Unsupported LAN package protocol.");
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12, 4));
            if (length < 1 || length > MaxPayloadBytes) throw new InvalidDataException("The LAN package size is invalid.");
            lock (_gate)
            {
                if (_reservedBytes + length <= MaxPayloadBytes) { _reservedBytes += length; reserved = length; }
            }
            if (reserved == 0) throw new InvalidDataException("The receiver is receiving another large package.");
            await WriteResultAsync(stream, new(CivilizationLanStatus.Submitted, "Sending"), cancellationToken).ConfigureAwait(false);
            var payload = new byte[length];
            using (var transfer = LinkedTimeout(cancellationToken, TransferTimeout))
                await stream.ReadExactlyAsync(payload, transfer.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), header.AsSpan(16, 32)))
                throw new InvalidDataException("The LAN package failed integrity verification.");
            var package = _decode(payload);
            cancellationToken.ThrowIfCancellationRequested();
            await WriteResultAsync(stream, new(CivilizationLanStatus.Awaiting, "Awaiting approval"), cancellationToken).ConfigureAwait(false);
            using var review = LinkedTimeout(cancellationToken, ReviewTimeout);
            using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(review.Token);
            var disconnected = WaitForDisconnectAsync(stream, disconnect.Token);
            var decisionTask = _onOffer(_makeOffer(package, senderAddress, DateTimeOffset.UtcNow), review.Token);
            var completed = await Task.WhenAny(decisionTask, disconnected).ConfigureAwait(false);
            if (completed != decisionTask)
            {
                review.Cancel();
                Observe(decisionTask);
                return;
            }
            var decision = await decisionTask.ConfigureAwait(false);
            disconnect.Cancel();
            if (decision.Status is not (CivilizationLanStatus.Installed or CivilizationLanStatus.Denied or CivilizationLanStatus.Failed))
                throw new InvalidDataException("The review did not return an installation decision.");
            await WriteResultAsync(stream, new(decision.Status, CleanText(decision.Message, 300)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var timeout = LinkedTimeout(cancellationToken, TimeSpan.FromSeconds(2));
                    await WriteResultAsync(stream ?? client.GetStream(), new(CivilizationLanStatus.Failed,
                        exception is OperationCanceledException ? "The request timed out." : CleanText(exception.Message, 300)), timeout.Token).ConfigureAwait(false);
                }
                catch (Exception replyException) when (replyException is not OutOfMemoryException and not StackOverflowException) { }
            }
        }
        finally
        {
            lock (_gate)
            {
                _reservedBytes -= reserved;
                if (pending) _pendingOffers--;
            }
            _clients.TryRemove(id, out _);
            stream?.Dispose();
            client.Dispose();
        }
    }

    private async Task DiscoveryLoopAsync(UdpClient socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var incoming = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (incoming.Buffer.Length is < 1 or > 2048 || !IsLanAddress(incoming.RemoteEndPoint.Address)) continue;
                var message = JsonSerializer.Deserialize<DiscoveryMessage>(incoming.Buffer, JsonOptions);
                if (message is null || message.Magic != ProtocolMagic || message.Version != ProtocolVersion || message.Kind != "query" ||
                    !Guid.TryParseExact(message.Nonce, "N", out _)) continue;
                var reply = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryMessage(ProtocolMagic, ProtocolVersion, "reply", message.Nonce,
                    _displayName, Port, _instanceId), JsonOptions);
                await socket.SendAsync(reply, incoming.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException) { }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (exception is ObjectDisposedException) break;
            }
        }
    }

    internal static bool IsLanAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        var privateAddress = bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
        if (!privateAddress || address.Equals(IPAddress.Any)) return false;
        return LanInterfaces().Any(item => SameSubnet(address, item.Address, item.Mask));
    }

    private static IEnumerable<(IPAddress Address, IPAddress Mask)> LanInterfaces()
    {
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var adapter in interfaces)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            UnicastIPAddressInformationCollection addresses;
            try { addresses = adapter.GetIPProperties().UnicastAddresses; }
            catch (NetworkInformationException) { continue; }
            foreach (var address in addresses)
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address) &&
                    address.IPv4Mask.GetAddressBytes().Any(value => value != 0))
                    yield return (address.Address, address.IPv4Mask);
        }
    }
    private static bool SameSubnet(IPAddress first, IPAddress second, IPAddress mask)
    {
        var left = first.GetAddressBytes(); var right = second.GetAddressBytes(); var bits = mask.GetAddressBytes();
        for (var index = 0; index < 4; index++) if ((left[index] & bits[index]) != (right[index] & bits[index])) return false;
        return true;
    }
    private static IPAddress Broadcast(IPAddress address, IPAddress mask)
    {
        var result = address.GetAddressBytes(); var bits = mask.GetAddressBytes();
        for (var index = 0; index < 4; index++) result[index] |= (byte)~bits[index];
        return new(result);
    }
    private static CancellationTokenSource LinkedTimeout(CancellationToken token, TimeSpan interval)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(interval);
        return source;
    }
    private CancellationTokenSource LinkOperation(CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(token, _operationLifetime!.Token);
        }
    }
    private static async Task WriteResultAsync(NetworkStream stream, CivilizationLanResult result, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
        if (bytes.Length > 4096) throw new InvalidDataException("The LAN response is too large.");
        var size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length);
        await stream.WriteAsync(size, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
    private static async Task<CivilizationLanResult> ReadResultAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var size = new byte[4]; await stream.ReadExactlyAsync(size, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(size);
        if (length is < 1 or > 4096) throw new InvalidDataException("The LAN response size is invalid.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<CivilizationLanResult>(bytes, JsonOptions);
        if (result is null || !Enum.IsDefined(result.Status) || result.Message is null || result.Message.Length > 1000)
            throw new InvalidDataException("The LAN response is invalid.");
        return result;
    }
    private static async Task WaitForDisconnectAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        try { _ = await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or SocketException) { }
    }
    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static void Report(Action<CivilizationLanProgress>? progress, CivilizationLanResult result)
    {
        try { progress?.Invoke(new(result.Status, result.Message)); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }
    private static string CleanText(string? value, int limit) => new((value ?? "").Where(character => !char.IsControl(character)).Take(limit).ToArray());
    private sealed record DiscoveryMessage(string Magic, int Version, string Kind, string Nonce, string Name, int Port, string InstanceId);
}

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using DataChannelDotnet;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;

namespace EDMWMapDesigner.Networking;

internal sealed record OnlineIceDatagram(Guid RoomId, Guid FromUserId, int SourcePort, int TargetPort, byte[] Payload);
internal sealed record OnlineIceTcpFrame(Guid RoomId, Guid FromUserId, byte[] Payload);
internal sealed record OnlineIceControlFrame(Guid RoomId, Guid FromUserId, byte[] Payload);

/// <summary>Establishes direct WebRTC data channels for each online game room member.</summary>
internal sealed class OnlineGameIceSession : IDisposable
{
    private const string UdpChannelLabel = "edmw-game-udp";
    private const string TcpChannelLabel = "edmw-game-tcp";
    private const string ControlChannelLabel = "edmw-session-control";
    private const int UdpHeaderLength = 8;
    private const int TcpHeaderLength = 25;
    private const int RelayOutboundHeaderLength = 23;
    private const int RelayInboundHeaderLength = 37;
    private const byte RelayUdp = 1;
    private const byte RelayTcp = 2;
    private const byte RelayControl = 3;
    private const int MaximumUdpPayload = OnlineRelayClient.MaximumGamePacketBytes;
    private const int MaximumTcpPayload = OnlineRelayClient.MaximumGameStreamChunkBytes;
    internal const int MaximumControlFrameBytes = 16 * 1024;
    private readonly OnlineRelayClient _client;
    private readonly Channel<OnlineGameRoom?> _roomUpdates = Channel.CreateUnbounded<OnlineGameRoom?>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, IcePeer> _peers = new();
    private readonly object _peerLock = new();
    private readonly object _localCandidateLock = new();
    private readonly Dictionary<Guid, string> _selectedLocalIpv4ByPeer = [];
    private readonly Dictionary<Guid, (string Address, uint Priority)> _fallbackLocalIpv4ByPeer = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _roomWorker;
    private OnlineGameRoom? _room;
    private string? _latestStatus;
    private long _relayFramesSent;
    private long _relayFramesReceived;
    private long _directFramesSent;
    private int _relayFallbackReported;
    private int _relayQueueFailureReported;
    private int _noGameplayRouteReported;
    private int _disposed;

    private bool IceGameplayEnabled => _client.Preferences.EnableIceGameplay;
    private bool WssGameplayEnabled => _client.Preferences.EnableWssGameplay;

    public event EventHandler<OnlineIceDatagram>? DatagramReceived;
    public event EventHandler<OnlineIceTcpFrame>? TcpFrameReceived;
    public event EventHandler<OnlineIceControlFrame>? ControlFrameReceived;
    public event EventHandler<string>? StatusChanged;
    public string? LatestStatus => Volatile.Read(ref _latestStatus);
    public long RelayFramesQueued => Interlocked.Read(ref _relayFramesSent);
    public long RelayFramesReceived => Interlocked.Read(ref _relayFramesReceived);
    public long DirectFramesSent => Interlocked.Read(ref _directFramesSent);
    public string? PreferredLocalIpv4
    {
        get
        {
            lock (_localCandidateLock)
            {
                var room = Volatile.Read(ref _room);
                var localId = _client.User?.UserId;
                var hostId = room?.GameHostUserId ?? room?.Host.UserId;
                return SelectPreferredLocalIpv4(localId, hostId, _selectedLocalIpv4ByPeer, _fallbackLocalIpv4ByPeer)
                    ?? FindDefaultRouteLocalIpv4();
            }
        }
    }
    public int ReadyPeerCount => _peers.Values.Count(peer => peer.IsReady);

    public OnlineGameIceSession(OnlineRelayClient client)
    {
        _client = client;
        _client.GameRoomChanged += OnGameRoomChanged;
        _client.GameSignalReceived += OnGameSignalReceived;
        _client.GameRelayFrameReceived += OnGameRelayFrameReceived;
        _roomWorker = RunRoomUpdatesAsync();
        if (client.CurrentGameRoom is { } room) _roomUpdates.Writer.TryWrite(room);
    }

    private void OnGameRoomChanged(object? sender, OnlineGameRoom? room) => _roomUpdates.Writer.TryWrite(room);

    private void OnGameSignalReceived(object? sender, OnlineGameSignal signal)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _ = ApplySignalAsync(signal);
    }

    private void OnGameRelayFrameReceived(object? sender, byte[] frame)
    {
        if (Volatile.Read(ref _disposed) != 0 || !WssGameplayEnabled || frame.Length is < RelayInboundHeaderLength + 1 or > OnlineRelayClient.MaximumGameRelayEnvelopeBytes ||
            !frame.AsSpan(0, 4).SequenceEqual("EDMR"u8)) return;
        var roomId = new Guid(frame.AsSpan(5, 16), bigEndian: true);
        var senderId = new Guid(frame.AsSpan(21, 16), bigEndian: true);
        var room = Volatile.Read(ref _room);
        if (room is null || room.RoomId != roomId || room.RoomId != _client.CurrentGameRoom?.RoomId ||
            senderId == Guid.Empty || senderId == _client.User?.UserId ||
            !room.Members.Any(member => member.Profile.UserId == senderId)) return;

        var payload = frame.AsSpan(RelayInboundHeaderLength);
        LogRelayReceive(room, senderId, frame[4], payload);
        Interlocked.Increment(ref _relayFramesReceived);
        ReportRelayFallback();
        switch (frame[4])
        {
            case RelayUdp:
                if (payload.Length is < 5 or > 4 + MaximumUdpPayload) return;
                var datagram = new byte[UdpHeaderLength + payload.Length - 4];
                "EDGU"u8.CopyTo(datagram);
                payload.CopyTo(datagram.AsSpan(UdpHeaderLength - 4));
                HandleUdpFrameFromRelay(senderId, datagram);
                break;
            case RelayTcp:
                HandleTcpFrameFromRelay(senderId, payload.ToArray());
                break;
            case RelayControl:
                if (payload.Length is > 0 and <= MaximumControlFrameBytes)
                    HandleControlFrameFromRelay(senderId, payload.ToArray());
                break;
        }
    }

    private async Task RunRoomUpdatesAsync()
    {
        try
        {
            await foreach (var room in _roomUpdates.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                var previousRoom = Volatile.Read(ref _room);
                Volatile.Write(ref _room, room);
                if (room is null || room.RoomId != previousRoom?.RoomId)
                {
                    lock (_localCandidateLock)
                    {
                        _selectedLocalIpv4ByPeer.Clear();
                        _fallbackLocalIpv4ByPeer.Clear();
                    }
                }
                var localId = _client.User?.UserId ?? Guid.Empty;
                var members = room?.Members.Where(member => member.Profile.UserId != localId)
                    .ToDictionary(member => member.Profile.UserId) ?? [];
                lock (_localCandidateLock)
                {
                    foreach (var candidateId in _selectedLocalIpv4ByPeer.Keys.Where(id => !members.ContainsKey(id)).ToArray())
                        _selectedLocalIpv4ByPeer.Remove(candidateId);
                    foreach (var candidateId in _fallbackLocalIpv4ByPeer.Keys.Where(id => !members.ContainsKey(id)).ToArray())
                        _fallbackLocalIpv4ByPeer.Remove(candidateId);
                }
                foreach (var entry in _peers.ToArray())
                {
                    if (room?.RoomId != entry.Value.RoomId || !members.ContainsKey(entry.Key))
                    {
                        IcePeer? removed = null;
                        lock (_peerLock)
                            if (_peers.TryRemove(entry.Key, out var found)) removed = found;
                        removed?.Dispose();
                    }
                }
                if (room is null || room.RoomId == Guid.Empty || localId == Guid.Empty) continue;
                foreach (var member in members.Values)
                {
                    try { EnsurePeer(room, member, localId); }
                    catch (Exception error) { ReportStatus($"Could not prepare a direct ICE connection to {member.Profile.DisplayName}: {error.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportStatus("Could not prepare direct ICE connections: " + error.Message); }
    }

    private IcePeer EnsurePeer(OnlineGameRoom room, OnlineGameMember member, Guid localId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_peerLock)
        {
            if (_peers.TryGetValue(member.Profile.UserId, out var current))
                return current.RoomId == room.RoomId ? current : ReplacePeer(room, member, localId);
            return CreatePeer(room, member, localId);
        }
    }

    private IcePeer ReplacePeer(OnlineGameRoom room, OnlineGameMember member, Guid localId)
    {
        if (_peers.TryRemove(member.Profile.UserId, out var previous)) previous.Dispose();
        return CreatePeer(room, member, localId);
    }

    private IcePeer CreatePeer(OnlineGameRoom room, OnlineGameMember member, Guid localId)
    {
        var isOfferer = localId.CompareTo(member.Profile.UserId) < 0;
        var peer = new IcePeer(room.RoomId, member.Profile.UserId, member.Profile.DisplayName, isOfferer,
            SendSignalAsync, HandleUdpFrame, HandleTcpFrame, HandleControlFrame, ReportStatus,
            CaptureLocalCandidate, HasLocalMappingIpv4, ClearSelectedLocalCandidate);
        _peers[member.Profile.UserId] = peer;
        ReportStatus($"Preparing direct ICE connection to {member.Profile.DisplayName} using STUN.");
        if (isOfferer) peer.StartOffer();
        return peer;
    }

    private async Task ApplySignalAsync(OnlineGameSignal signal)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var room = _client.CurrentGameRoom;
            var localId = _client.User?.UserId ?? Guid.Empty;
            if (room is null || room.RoomId != signal.RoomId || signal.From.UserId == localId) return;
            var member = room.Members.FirstOrDefault(entry => entry.Profile.UserId == signal.From.UserId);
            if (member is null) return;
            var peer = EnsurePeer(room, member, localId);
            await peer.ApplySignalAsync(signal.SignalType, signal.Payload, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportStatus("Direct ICE signaling failed: " + error.Message); }
    }

    private Task SendSignalAsync(Guid targetUserId, string type, string payload, CancellationToken cancellationToken) =>
        _client.SendGameSignalAsync(targetUserId, type, payload, cancellationToken);

    public bool SendDatagram(int targetRelayPort, int sourcePort, int targetPort, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > MaximumUdpPayload || sourcePort is < 1 or > 65535 || targetPort is < 1 or > 65535 ||
            targetRelayPort != 0 && (targetRelayPort is < 40000 or > 40007))
            return false;
        var room = Volatile.Read(ref _room);
        if (room is null) return false;
        var members = targetRelayPort == 0
            ? room.Members.Where(member => member.Profile.UserId != _client.User?.UserId)
            : room.Members.Where(member => member.RelayPort == targetRelayPort && member.Profile.UserId != _client.User?.UserId);
        var sent = false;
        foreach (var member in members)
        {
            var frame = new byte[UdpHeaderLength + payload.Length];
            "EDGU"u8.CopyTo(frame);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), checked((ushort)sourcePort));
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6, 2), checked((ushort)targetPort));
            payload.CopyTo(frame.AsMemory(UdpHeaderLength));
            if (IceGameplayEnabled && _peers.TryGetValue(member.Profile.UserId, out var peer) && peer.SendUdp(frame))
            {
                Interlocked.Increment(ref _directFramesSent);
                ResetNoGameplayRouteReport();
                LogIceSend(room, member, "UDP", sourcePort, targetPort, payload.Length, peer);
                sent = true;
            }
            else
            {
                if (WssGameplayEnabled)
                    sent |= SendDatagramViaRelay(room, member, sourcePort, targetPort, payload);
            }
        }
        if (!sent) ReportNoGameplayRoute();
        return sent;
    }

    internal bool SendDatagramViaRelay(int targetRelayPort, int sourcePort, int targetPort, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > MaximumUdpPayload || sourcePort is < 1 or > 65535 || targetPort is < 1 or > 65535 ||
            targetRelayPort != 0 && (targetRelayPort is < 40000 or > 40007)) return false;
        var room = Volatile.Read(ref _room);
        if (room is null) return false;
        var members = targetRelayPort == 0
            ? room.Members.Where(member => member.Profile.UserId != _client.User?.UserId)
            : room.Members.Where(member => member.RelayPort == targetRelayPort && member.Profile.UserId != _client.User?.UserId);
        var sent = false;
        foreach (var member in members) sent |= SendDatagramViaRelay(room, member, sourcePort, targetPort, payload);
        return sent;
    }

    private bool SendDatagramViaRelay(OnlineGameRoom room, OnlineGameMember member, int sourcePort, int targetPort,
        ReadOnlyMemory<byte> payload)
    {
        var relayPayload = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(relayPayload.AsSpan(0, 2), checked((ushort)sourcePort));
        BinaryPrimitives.WriteUInt16BigEndian(relayPayload.AsSpan(2, 2), checked((ushort)targetPort));
        payload.CopyTo(relayPayload.AsMemory(4));
        return SendRelayEnvelope(room, RelayUdp, member.RelayPort, relayPayload);
    }

    public bool SendTcpFrame(Guid peerUserId, ReadOnlySpan<byte> frame)
    {
        if (!IsValidTcpFrame(frame)) return false;
        var room = Volatile.Read(ref _room);
        var member = room?.Members.FirstOrDefault(entry => entry.Profile.UserId == peerUserId);
        if (room is null || member is null || peerUserId == _client.User?.UserId) return false;
        if (IceGameplayEnabled && _peers.TryGetValue(peerUserId, out var peer) && peer.SendTcp(frame))
        {
            Interlocked.Increment(ref _directFramesSent);
            ResetNoGameplayRouteReport();
            LogIceSend(room, member, "TCP", BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(20, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(22, 2)), frame.Length - TcpHeaderLength, peer);
            return true;
        }
        var sent = WssGameplayEnabled && SendTcpFrameViaRelay(room, member, frame);
        if (!sent) ReportNoGameplayRoute();
        return sent;
    }

    internal bool SendTcpFrameViaRelay(Guid peerUserId, ReadOnlySpan<byte> frame)
    {
        if (!IsValidTcpFrame(frame)) return false;
        var room = Volatile.Read(ref _room);
        var member = room?.Members.FirstOrDefault(entry => entry.Profile.UserId == peerUserId);
        return room is not null && member is not null && peerUserId != _client.User?.UserId &&
            SendTcpFrameViaRelay(room, member, frame);
    }

    private bool SendTcpFrameViaRelay(OnlineGameRoom room, OnlineGameMember member, ReadOnlySpan<byte> frame) =>
        SendRelayEnvelope(room, RelayTcp, member.RelayPort, frame);

    public bool SendControlFrame(Guid peerUserId, ReadOnlySpan<byte> frame)
    {
        if (frame.Length is < 1 or > MaximumControlFrameBytes) return false;
        var room = Volatile.Read(ref _room);
        var member = room?.Members.FirstOrDefault(entry => entry.Profile.UserId == peerUserId);
        if (IceGameplayEnabled && room is not null && member is not null &&
            _peers.TryGetValue(peerUserId, out var peer) && peer.SendControl(frame))
        {
            Interlocked.Increment(ref _directFramesSent);
            ResetNoGameplayRouteReport();
            LogIceSend(room, member, "control", null, null, frame.Length, peer);
            return true;
        }
        var sent = WssGameplayEnabled && SendControlFrameViaRelay(peerUserId, frame);
        if (!sent) ReportNoGameplayRoute();
        return sent;
    }

    internal bool SendControlFrameViaRelay(Guid peerUserId, ReadOnlySpan<byte> frame)
    {
        if (frame.Length is < 1 or > MaximumControlFrameBytes) return false;
        var room = Volatile.Read(ref _room);
        var member = room?.Members.FirstOrDefault(entry => entry.Profile.UserId == peerUserId);
        return room is not null && member is not null && peerUserId != _client.User?.UserId &&
            SendRelayEnvelope(room, RelayControl, member.RelayPort, frame);
    }

    private bool SendRelayEnvelope(OnlineGameRoom room, byte type, int targetRelayPort, ReadOnlySpan<byte> payload)
    {
        if (!WssGameplayEnabled) return false;
        if (payload.Length is < 1 || payload.Length + RelayOutboundHeaderLength > OnlineRelayClient.MaximumGameRelayEnvelopeBytes ||
            targetRelayPort is < 40000 or > 40007) return false;
        var frame = new byte[RelayOutboundHeaderLength + payload.Length];
        "EDMR"u8.CopyTo(frame);
        frame[4] = type;
        room.RoomId.TryWriteBytes(frame.AsSpan(5, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(21, 2), checked((ushort)targetRelayPort));
        payload.CopyTo(frame.AsSpan(RelayOutboundHeaderLength));
        if (!_client.TrySendGameRelayFrame(frame))
        {
            if (Interlocked.Exchange(ref _relayQueueFailureReported, 1) == 0)
                ReportStatus($"Could not queue gameplay on the secure WebSocket relay. Check that both players remain connected. WSS local queue pending={_client.GameRelayQueueDepth}, rejected={_client.GameRelayFramesDropped}.");
            return false;
        }
        Interlocked.Exchange(ref _relayQueueFailureReported, 0);
        Interlocked.Increment(ref _relayFramesSent);
        ResetNoGameplayRouteReport();
        var member = room.Members.FirstOrDefault(entry => entry.RelayPort == targetRelayPort && entry.Profile.UserId != _client.User?.UserId);
        if (member is not null) LogRelaySend(room, member, type, payload);
        ReportRelayFallback();
        return true;
    }

    private void ReportRelayFallback()
    {
        if (Interlocked.Exchange(ref _relayFallbackReported, 1) == 0)
            ReportStatus(IceGameplayEnabled
                ? "Direct ICE is unavailable for some traffic; gameplay is using the secure WebSocket relay fallback."
                : "Direct ICE gameplay is disabled; gameplay is using the secure WebSocket relay.");
    }

    private void ReportNoGameplayRoute()
    {
        if (WssGameplayEnabled) return;
        if (Interlocked.Exchange(ref _noGameplayRouteReported, 1) == 0)
            ReportStatus(IceGameplayEnabled
                ? "Gameplay could not be sent because direct ICE is unavailable and the WSS gameplay relay is disabled."
                : "Gameplay could not be sent because both direct ICE and the WSS gameplay relay are disabled.");
    }

    private void ResetNoGameplayRouteReport() => Interlocked.Exchange(ref _noGameplayRouteReported, 0);

    private static bool IsValidTcpFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length is < TcpHeaderLength or > TcpHeaderLength + MaximumTcpPayload ||
            !frame[..4].SequenceEqual("EDWT"u8)) return false;
        var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(20, 2));
        var targetPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(22, 2));
        var type = frame[24];
        var payloadLength = frame.Length - TcpHeaderLength;
        return sourcePort != 0 && targetPort != 0 && type is >= 1 and <= 5 &&
            (type == 3 ? payloadLength is >= 1 and <= MaximumTcpPayload : payloadLength == 0);
    }

    public bool IsPeerReady(Guid peerUserId) => _peers.TryGetValue(peerUserId, out var peer) && peer.IsReady;

    public async Task WaitForGameNetworkReadyAsync(IEnumerable<Guid> peerUserIds, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peerUserIds);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var peers = peerUserIds.Where(id => id != _client.User?.UserId).Distinct().ToArray();
        if (peers.Any(id => id == Guid.Empty)) throw new ArgumentException("A game room contains an invalid peer.", nameof(peerUserIds));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var room = _client.CurrentGameRoom;
        if (room is null || !_client.IsGameRelayAvailable(room.RoomId) ||
            peers.Any(id => !room.Members.Any(member => member.Profile.UserId == id)))
            throw new InvalidOperationException("An authenticated matchmaking WebSocket fallback is required to prepare the game network.");
        try
        {
            while (Volatile.Read(ref _room)?.RoomId != room.RoomId ||
                   IceGameplayEnabled && peers.Any(id => !_peers.ContainsKey(id)) || PreferredLocalIpv4 is null)
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (PreferredLocalIpv4 is null)
                throw new TimeoutException("Direct ICE did not provide a route, and Windows reported no usable IPv4 adapter for the game mapping.");
            var missing = peers.Where(id => !_peers.ContainsKey(id)).Select(id =>
                room.Members.FirstOrDefault(member => member.Profile.UserId == id)?.Profile.DisplayName ?? id.ToString("N"));
            throw new TimeoutException("The game room peer setup was not ready for: " + string.Join(", ", missing));
        }
    }

    private void HandleUdpFrame(Guid peerUserId, byte[] frame) => HandleUdpFrameCore(peerUserId, frame, fromIce: true);
    private void HandleUdpFrameFromRelay(Guid peerUserId, byte[] frame) => HandleUdpFrameCore(peerUserId, frame, fromIce: false);

    private void HandleUdpFrameCore(Guid peerUserId, byte[] frame, bool fromIce)
    {
        if (fromIce && !IceGameplayEnabled) return;
        if (frame.Length is < UdpHeaderLength + 1 or > UdpHeaderLength + MaximumUdpPayload ||
            !frame.AsSpan(0, 4).SequenceEqual("EDGU"u8)) return;
        var room = Volatile.Read(ref _room);
        var member = room?.Members.FirstOrDefault(entry => entry.Profile.UserId == peerUserId);
        var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4, 2));
        var targetPort = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(6, 2));
        if (room is null || member is null || sourcePort == 0 || targetPort == 0) return;
        if (fromIce) LogIceReceive(room, member, "UDP", sourcePort, targetPort, frame.Length - UdpHeaderLength);
        DatagramReceived?.Invoke(this, new(room.RoomId, peerUserId, sourcePort, targetPort,
            frame.AsSpan(UdpHeaderLength).ToArray()));
    }

    private void HandleTcpFrame(Guid peerUserId, byte[] frame) => HandleTcpFrameCore(peerUserId, frame, fromIce: true);
    private void HandleTcpFrameFromRelay(Guid peerUserId, byte[] frame) => HandleTcpFrameCore(peerUserId, frame, fromIce: false);

    private void HandleTcpFrameCore(Guid peerUserId, byte[] frame, bool fromIce)
    {
        if (fromIce && !IceGameplayEnabled) return;
        if (frame.Length is < TcpHeaderLength or > TcpHeaderLength + MaximumTcpPayload ||
            !frame.AsSpan(0, 4).SequenceEqual("EDWT"u8)) return;
        var room = Volatile.Read(ref _room);
        var member = room?.Members.FirstOrDefault(entry => entry.Profile.UserId == peerUserId);
        if (room is null || member is null) return;
        var type = frame[24];
        var payloadLength = frame.Length - TcpHeaderLength;
        if (type is < 1 or > 5 || (type == 3 ? payloadLength is < 1 or > MaximumTcpPayload : payloadLength != 0)) return;
        if (fromIce) LogIceReceive(room, member, "TCP", BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(20, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(22, 2)), payloadLength);
        TcpFrameReceived?.Invoke(this, new(room.RoomId, peerUserId, frame));
    }

    private void HandleControlFrame(Guid peerUserId, byte[] payload) => HandleControlFrameCore(peerUserId, payload, fromIce: true);
    private void HandleControlFrameFromRelay(Guid peerUserId, byte[] payload) => HandleControlFrameCore(peerUserId, payload, fromIce: false);

    private void HandleControlFrameCore(Guid peerUserId, byte[] payload, bool fromIce)
    {
        if (fromIce && !IceGameplayEnabled) return;
        var room = Volatile.Read(ref _room);
        var member = room?.Members.FirstOrDefault(entry => entry.Profile.UserId == peerUserId);
        if (room is null || member is null || payload.Length is < 1 or > MaximumControlFrameBytes || Volatile.Read(ref _disposed) != 0) return;
        if (fromIce) LogIceReceive(room, member, "control", null, null, payload.Length);
        ControlFrameReceived?.Invoke(this, new(room.RoomId, peerUserId, payload));
    }

    private void LogIceSend(OnlineGameRoom room, OnlineGameMember remote, string protocol, int? sourcePort,
        int? targetPort, int bytes, IcePeer peer)
    {
        var local = LocalGameMember(room);
        if (local is null) return;
        OnlineTrafficLogger.LogGameplay("ICE", protocol, "send", GameEndpoint(local, sourcePort),
            GameEndpoint(remote, targetPort), bytes, remote.Profile.UserId.ToString("N"), peer.SelectedRouteForLogging());
    }

    private void LogIceReceive(OnlineGameRoom room, OnlineGameMember remote, string protocol, int? sourcePort,
        int? targetPort, int bytes)
    {
        var local = LocalGameMember(room);
        if (local is null) return;
        var route = _peers.TryGetValue(remote.Profile.UserId, out var peer) ? peer.SelectedRouteForLogging() : "selected_pair=unavailable";
        OnlineTrafficLogger.LogGameplay("ICE", protocol, "receive", GameEndpoint(remote, sourcePort),
            GameEndpoint(local, targetPort), bytes, remote.Profile.UserId.ToString("N"), route);
    }

    private void LogRelaySend(OnlineGameRoom room, OnlineGameMember remote, byte type, ReadOnlySpan<byte> payload)
    {
        var local = LocalGameMember(room);
        if (local is null) return;
        var protocol = type switch { RelayUdp => "UDP", RelayTcp => "TCP", RelayControl => "control", _ => "unknown" };
        int? sourcePort = null;
        int? targetPort = null;
        var bytes = payload.Length;
        if (type == RelayUdp && payload.Length >= 4)
        {
            sourcePort = BinaryPrimitives.ReadUInt16BigEndian(payload[..2]);
            targetPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2));
            bytes -= 4;
        }
        else if (type == RelayTcp && payload.Length >= TcpHeaderLength)
        {
            sourcePort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(20, 2));
            targetPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(22, 2));
            bytes -= TcpHeaderLength;
        }
        OnlineTrafficLogger.LogGameplay("WSS", protocol, "send", GameEndpoint(local, sourcePort),
            GameEndpoint(remote, targetPort), bytes, remote.Profile.UserId.ToString("N"));
    }

    private void LogRelayReceive(OnlineGameRoom room, Guid senderId, byte type, ReadOnlySpan<byte> payload)
    {
        var local = LocalGameMember(room);
        var remote = room.Members.FirstOrDefault(entry => entry.Profile.UserId == senderId);
        if (local is null || remote is null) return;
        var protocol = type switch { RelayUdp => "UDP", RelayTcp => "TCP", RelayControl => "control", _ => "unknown" };
        int? sourcePort = null;
        int? targetPort = null;
        var bytes = payload.Length;
        if (type == RelayUdp && payload.Length >= 4)
        {
            sourcePort = BinaryPrimitives.ReadUInt16BigEndian(payload[..2]);
            targetPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2));
            bytes -= 4;
        }
        else if (type == RelayTcp && payload.Length >= TcpHeaderLength)
        {
            sourcePort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(20, 2));
            targetPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(22, 2));
            bytes -= TcpHeaderLength;
        }
        OnlineTrafficLogger.LogGameplay("WSS", protocol, "receive", GameEndpoint(remote, sourcePort),
            GameEndpoint(local, targetPort), bytes, senderId.ToString("N"));
    }

    private OnlineGameMember? LocalGameMember(OnlineGameRoom room) =>
        room.Members.FirstOrDefault(member => member.Profile.UserId == _client.User?.UserId);

    private static string GameEndpoint(OnlineGameMember member, int? port) => port is { } value
        ? $"{OnlineGameAddress.VirtualIpv4(member.RelayPort)}:{value}" : OnlineGameAddress.VirtualIpv4(member.RelayPort);

    private void ReportStatus(string message)
    {
        Volatile.Write(ref _latestStatus, message);
        try { StatusChanged?.Invoke(this, message); } catch (Exception) { }
    }

    private bool CaptureLocalCandidate(Guid remoteUserId, string candidateText, bool selectedPair)
    {
        if (!TryGetLocalMappingCandidate(candidateText, out var address, out var priority)) return false;
        lock (_localCandidateLock)
        {
            if (selectedPair)
                _selectedLocalIpv4ByPeer[remoteUserId] = address;
            else if (!_fallbackLocalIpv4ByPeer.TryGetValue(remoteUserId, out var current) || priority > current.Priority)
                _fallbackLocalIpv4ByPeer[remoteUserId] = (address, priority);
        }
        return true;
    }

    private bool HasLocalMappingIpv4(Guid remoteUserId)
    {
        lock (_localCandidateLock)
            return _selectedLocalIpv4ByPeer.ContainsKey(remoteUserId) || _fallbackLocalIpv4ByPeer.ContainsKey(remoteUserId);
    }

    private void ClearSelectedLocalCandidate(Guid remoteUserId)
    {
        lock (_localCandidateLock) _selectedLocalIpv4ByPeer.Remove(remoteUserId);
    }

    internal static bool TryGetLocalMappingCandidate(string candidateText, out string addressText, out uint priority)
    {
        addressText = string.Empty;
        priority = 0;
        if (string.IsNullOrWhiteSpace(candidateText)) return false;
        var parts = candidateText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var typeIndex = Array.FindIndex(parts, part => string.Equals(part, "typ", StringComparison.OrdinalIgnoreCase));
        if (typeIndex < 0 || typeIndex + 1 >= parts.Length) return false;

        var type = parts[typeIndex + 1];
        string? localAddress = null;
        if (string.Equals(type, "srflx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "prflx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "relay", StringComparison.OrdinalIgnoreCase))
        {
            var relatedIndex = Array.FindIndex(parts, part => string.Equals(part, "raddr", StringComparison.OrdinalIgnoreCase));
            if (relatedIndex >= 0 && relatedIndex + 1 < parts.Length)
            {
                // Use the candidate's related/base local address, which is the
                // socket-bound interface address; the srflx address itself is
                // the STUN-mapped public address and will not match adapters.
                localAddress = parts[relatedIndex + 1];
            }
        }
        else if (string.Equals(type, "host", StringComparison.OrdinalIgnoreCase) && parts.Length > 4)
        {
            localAddress = parts[4];
        }

        if (localAddress is null || !IPAddress.TryParse(localAddress, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] >= 224 || (bytes[0] == 169 && bytes[1] == 254)) return false;
        addressText = address.ToString();
        if (parts.Length > 3)
            uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out priority);
        return true;
    }

    internal static string? SelectPreferredLocalIpv4(Guid? localUserId, Guid? designatedHostUserId,
        IReadOnlyDictionary<Guid, string> selectedRoutes,
        IReadOnlyDictionary<Guid, (string Address, uint Priority)> gatheredFallbackRoutes)
    {
        if (designatedHostUserId is { } hostId && hostId != localUserId)
        {
            if (selectedRoutes.TryGetValue(hostId, out var selectedHostRoute)) return selectedHostRoute;
            if (gatheredFallbackRoutes.TryGetValue(hostId, out var fallbackHostRoute)) return fallbackHostRoute.Address;
        }

        var routes = selectedRoutes.ToDictionary(entry => entry.Key, entry => entry.Value);
        foreach (var fallback in gatheredFallbackRoutes)
            routes.TryAdd(fallback.Key, fallback.Value.Address);
        return routes.Values
            .GroupBy(address => address, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault();
    }

    internal static string? FindDefaultRouteLocalIpv4()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                     .OrderBy(adapter =>
                     {
                         try { return adapter.GetIPProperties().GetIPv4Properties()?.Index ?? int.MaxValue; }
                         catch (NetworkInformationException) { return int.MaxValue; }
                     }))
        {
            try
            {
                var properties = adapter.GetIPProperties();
                if (!properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !gateway.Address.Equals(IPAddress.Any))) continue;
                var address = properties.UnicastAddresses.Select(info => info.Address)
                    .FirstOrDefault(IsUsableLocalIpv4);
                if (address is not null) return address.ToString();
            }
            catch (NetworkInformationException) { }
        }
        return null;
    }

    private static bool IsUsableLocalIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.GameRoomChanged -= OnGameRoomChanged;
        _client.GameSignalReceived -= OnGameSignalReceived;
        _client.GameRelayFrameReceived -= OnGameRelayFrameReceived;
        _roomUpdates.Writer.TryComplete();
        _lifetime.Cancel();
        lock (_peerLock)
        {
            foreach (var peer in _peers.Values) peer.Dispose();
            _peers.Clear();
        }
        try { _roomWorker.GetAwaiter().GetResult(); } catch (Exception) { }
        _lifetime.Dispose();
    }

    private sealed class IcePeer : IDisposable
    {
        private sealed record Signal(string Type, string Payload);
        private sealed record CandidatePayload(string Candidate, string Mid);

        private readonly Guid _roomId;
        private readonly Guid _remoteUserId;
        private readonly string _remoteName;
        private readonly bool _isOfferer;
        private readonly IRtcPeerConnection _connection;
        private readonly Func<Guid, string, string, CancellationToken, Task> _sendSignal;
        private readonly Action<Guid, byte[]> _udpReceived;
        private readonly Action<Guid, byte[]> _tcpReceived;
        private readonly Action<Guid, byte[]> _controlReceived;
        private readonly Action<string> _status;
        private readonly Func<Guid, string, bool, bool> _localCandidate;
        private readonly Func<Guid, bool> _hasLocalMappingIpv4;
        private readonly Action<Guid> _clearSelectedLocalCandidate;
        private readonly Channel<Signal> _signals = Channel.CreateBounded<Signal>(new BoundedChannelOptions(256)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _remoteSignalLock = new(1, 1);
        private readonly List<RtcCandidate> _pendingCandidates = [];
        private readonly object _channelLock = new();
        private IRtcDataChannel? _udp;
        private IRtcDataChannel? _tcp;
        private IRtcDataChannel? _control;
        private bool _remoteDescriptionSet;
        private int _localMappingIpv4State;
        private int _disposed;

        public Guid RoomId => _roomId;
        public bool IsReady { get { lock (_channelLock) return _udp?.IsOpen == true && _tcp?.IsOpen == true && _control?.IsOpen == true; } }
        public bool HasLocalMappingIpv4 => Volatile.Read(ref _localMappingIpv4State) != 0;

        public IcePeer(Guid roomId, Guid remoteUserId, string remoteName, bool isOfferer,
            Func<Guid, string, string, CancellationToken, Task> sendSignal,
            Action<Guid, byte[]> udpReceived, Action<Guid, byte[]> tcpReceived,
            Action<Guid, byte[]> controlReceived, Action<string> status, Func<Guid, string, bool, bool> localCandidate,
            Func<Guid, bool> hasLocalMappingIpv4, Action<Guid> clearSelectedLocalCandidate)
        {
            _roomId = roomId;
            _remoteUserId = remoteUserId;
            _remoteName = remoteName;
            _isOfferer = isOfferer;
            _sendSignal = sendSignal;
            _udpReceived = udpReceived;
            _tcpReceived = tcpReceived;
            _controlReceived = controlReceived;
            _status = status;
            _localCandidate = localCandidate;
            _hasLocalMappingIpv4 = hasLocalMappingIpv4;
            _clearSelectedLocalCandidate = clearSelectedLocalCandidate;
            _connection = new RtcPeerConnection(new RtcPeerConfiguration
            {
                IceServers = ["stun:stun.l.google.com:19302"],
                EnableIceTcp = true,
                DisableAutoNegotiation = true,
            });
            _connection.OnLocalDescriptionSafe += LocalDescriptionReady;
            _connection.OnCandidateSafe += CandidateReady;
            _connection.OnDataChannel += (_, channel) => AttachChannel(channel);
            _connection.OnIceStateChange += (_, state) =>
            {
                if (state.ToString() is "RTC_ICE_CONNECTED" or "RTC_ICE_COMPLETED")
                {
                    _status($"Direct ICE connection established with {_remoteName}.");
                    if (TryCaptureSelectedLocalCandidate())
                        _status(Volatile.Read(ref _localMappingIpv4State) == 1
                            ? $"ICE selected the local IPv4 interface for {_remoteName}."
                            : $"Using a gathered local IPv4 interface for {_remoteName}; the selected ICE route did not provide an IPv4 base address.");
                }
                else if (state.ToString() is "RTC_ICE_FAILED" or "RTC_ICE_DISCONNECTED")
                {
                    Interlocked.Exchange(ref _localMappingIpv4State, 0);
                    _clearSelectedLocalCandidate(_remoteUserId);
                    _status($"Direct ICE to {_remoteName} failed ({state}); gameplay will use the secure relay path.");
                }
            };
            _connection.OnConnectionStateChange += (_, state) =>
            {
                if (state.ToString().Contains("FAILED", StringComparison.OrdinalIgnoreCase))
                    _status($"Direct ICE to {_remoteName} failed; the authenticated WebSocket relay remains available.");
            };
            _ = SendSignalsAsync();
            if (isOfferer)
            {
                AttachChannel(_connection.CreateDataChannel(new RtcCreateDataChannelArgs
                { Label = UdpChannelLabel, Unordered = true, Unreliable = true, MaxRetransmits = 0 }));
                AttachChannel(_connection.CreateDataChannel(new RtcCreateDataChannelArgs
                { Label = TcpChannelLabel, Unordered = false, Unreliable = false }));
                AttachChannel(_connection.CreateDataChannel(new RtcCreateDataChannelArgs
                { Label = ControlChannelLabel, Unordered = false, Unreliable = false }));
            }
        }

        public void StartOffer()
        {
            if (!_isOfferer || Volatile.Read(ref _disposed) != 0) return;
            try { _connection.SetLocalDescription(RtcDescriptionType.Offer); }
            catch (Exception error) { _status($"Could not offer a direct ICE connection to {_remoteName}: {error.Message}"); }
        }

        private void LocalDescriptionReady(IRtcPeerConnection _, RtcDescription description)
        {
            var type = description.Type switch
            {
                RtcDescriptionType.Offer => "offer",
                RtcDescriptionType.Answer => "answer",
                _ => "",
            };
            if (type.Length == 0 || string.IsNullOrWhiteSpace(description.Sdp)) return;
            var payload = JsonSerializer.Serialize(new { sdp = description.Sdp }, OnlineRelayClient.JsonOptions);
            if (!_signals.Writer.TryWrite(new(type, payload))) _status($"ICE signaling queue is full for {_remoteName}.");
        }

        private void CandidateReady(IRtcPeerConnection _, RtcCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.Content)) return;
            _localCandidate(_remoteUserId, candidate.Content, false);
            var payload = JsonSerializer.Serialize(new CandidatePayload(candidate.Content,
                string.IsNullOrWhiteSpace(candidate.Mid) ? "0" : candidate.Mid), OnlineRelayClient.JsonOptions);
            if (!_signals.Writer.TryWrite(new("candidate", payload))) _status($"ICE signaling queue is full for {_remoteName}.");
        }

        public bool TryCaptureSelectedLocalCandidate()
        {
            try
            {
                if (_connection.TryGetSelectedCandidatePair(out var pair) && !string.IsNullOrWhiteSpace(pair?.LocalCandidate) &&
                    _localCandidate(_remoteUserId, pair.LocalCandidate, true))
                {
                    Interlocked.Exchange(ref _localMappingIpv4State, 1);
                    return true;
                }
                if (!_hasLocalMappingIpv4(_remoteUserId)) return false;
                Interlocked.Exchange(ref _localMappingIpv4State, 2);
                return true;
            }
            catch (Exception error)
            {
                _status($"Could not read the selected ICE route for {_remoteName}: {error.Message}");
                return false;
            }
        }

        public string SelectedRouteForLogging()
        {
            try
            {
                if (_connection.TryGetSelectedCandidatePair(out var pair) && pair is not null)
                    return $"local={FormatCandidateForLogging(pair.LocalCandidate)} remote={FormatCandidateForLogging(pair.RemoteCandidate)}";
            }
            catch (Exception) { }
            return "selected_pair=unavailable";
        }

        private static string FormatCandidateForLogging(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return "unavailable";
            var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 6) return "unavailable";
            var address = parts[4];
            if (address.Contains(':') && !address.StartsWith("[", StringComparison.Ordinal))
                address = $"[{address}]";
            var kind = "unknown";
            var typeIndex = Array.FindIndex(parts, part => string.Equals(part, "typ", StringComparison.OrdinalIgnoreCase));
            if (typeIndex >= 0 && typeIndex + 1 < parts.Length) kind = parts[typeIndex + 1];
            return $"{address}:{parts[5]}/{kind}";
        }

        private async Task SendSignalsAsync()
        {
            try
            {
                await foreach (var signal in _signals.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                    await _sendSignal(_remoteUserId, signal.Type, signal.Payload, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { _status($"Could not exchange ICE signaling with {_remoteName}: {error.Message}"); }
        }

        public async Task ApplySignalAsync(string type, string payload, CancellationToken cancellationToken)
        {
            await _remoteSignalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (type == "candidate")
                {
                    var candidatePayload = JsonSerializer.Deserialize<CandidatePayload>(payload, OnlineRelayClient.JsonOptions)
                        ?? throw new InvalidDataException("The ICE candidate was empty.");
                    if (string.IsNullOrWhiteSpace(candidatePayload.Candidate) || candidatePayload.Candidate.Length > 4096 ||
                        candidatePayload.Mid is null || candidatePayload.Mid.Length > 64)
                        throw new InvalidDataException("The ICE candidate exceeds the supported size.");
                    var candidate = new RtcCandidate { Content = candidatePayload.Candidate, Mid = candidatePayload.Mid };
                    if (_remoteDescriptionSet) _connection.AddRemoteCandidate(candidate);
                    else _pendingCandidates.Add(candidate);
                    return;
                }

                if (type is not ("offer" or "answer")) return;
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
                if (!document.RootElement.TryGetProperty("sdp", out var sdpElement) || sdpElement.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("The ICE description did not include SDP.");
                var sdp = sdpElement.GetString()!;
                if (sdp.Length is < 1 or > OnlineRelayClient.MaximumGameSignalBytes) throw new InvalidDataException("The ICE description exceeds the supported size.");
                if (type == "offer" && _isOfferer || type == "answer" && !_isOfferer)
                    throw new InvalidDataException("The other player sent an unexpected ICE description.");
                _connection.SetRemoteDescription(new RtcDescription
                { Sdp = sdp, Type = type == "offer" ? RtcDescriptionType.Offer : RtcDescriptionType.Answer });
                _remoteDescriptionSet = true;
                foreach (var candidate in _pendingCandidates) _connection.AddRemoteCandidate(candidate);
                _pendingCandidates.Clear();
                if (type == "offer") _connection.SetLocalDescription(RtcDescriptionType.Answer);
            }
            finally { _remoteSignalLock.Release(); }
        }

        private void AttachChannel(IRtcDataChannel channel)
        {
            if (channel.Label is not (UdpChannelLabel or TcpChannelLabel or ControlChannelLabel)) { channel.Dispose(); return; }
            channel.OnOpen += opened => _status($"Direct {opened.Label} data channel to {_remoteName} is ready.");
            channel.OnError += (_, error) => _status($"Direct data channel to {_remoteName} failed: {error}");
            if (channel.Label == UdpChannelLabel)
            {
                channel.OnBinaryReceivedSafe += (_, data) => _udpReceived(_remoteUserId, data.Data.ToArray());
                lock (_channelLock) { _udp?.Dispose(); _udp = channel; }
            }
            else if (channel.Label == TcpChannelLabel)
            {
                channel.OnBinaryReceivedSafe += (_, data) => _tcpReceived(_remoteUserId, data.Data.ToArray());
                lock (_channelLock) { _tcp?.Dispose(); _tcp = channel; }
            }
            else
            {
                channel.OnBinaryReceivedSafe += (_, data) =>
                {
                    if (data.Data.Length is > 0 and <= MaximumControlFrameBytes)
                        _controlReceived(_remoteUserId, data.Data.ToArray());
                    else
                        _status($"Ignored an oversized session-control frame from {_remoteName}.");
                };
                lock (_channelLock) { _control?.Dispose(); _control = channel; }
            }
        }

        public bool SendUdp(ReadOnlySpan<byte> payload)
        {
            lock (_channelLock)
            {
                if (_udp?.IsOpen != true) return false;
                try { _udp.Send(payload); return true; }
                catch (Exception error) { _status($"Could not send a direct game datagram to {_remoteName}: {error.Message}"); return false; }
            }
        }

        public bool SendTcp(ReadOnlySpan<byte> payload)
        {
            lock (_channelLock)
            {
                if (_tcp?.IsOpen != true) return false;
                try { _tcp.Send(payload); return true; }
                catch (Exception error) { _status($"Could not send direct TCP gameplay to {_remoteName}: {error.Message}"); return false; }
            }
        }

        public bool SendControl(ReadOnlySpan<byte> payload)
        {
            lock (_channelLock)
            {
                if (_control?.IsOpen != true || payload.Length is < 1 or > MaximumControlFrameBytes) return false;
                try { _control.Send(payload); return true; }
                catch (Exception error) { _status($"Could not send a session-control message to {_remoteName}: {error.Message}"); return false; }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _signals.Writer.TryComplete();
            _lifetime.Cancel();
            lock (_channelLock) { _udp?.Dispose(); _tcp?.Dispose(); _control?.Dispose(); _udp = null; _tcp = null; _control = null; }
            try { _connection.Dispose(); } catch (Exception) { }
            _lifetime.Dispose();
        }
    }
}

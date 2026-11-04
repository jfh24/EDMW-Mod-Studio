using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using DataChannelDotnet;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;

namespace EDMWMapDesigner.Networking;

internal sealed record OnlineIceDatagram(Guid RoomId, Guid FromUserId, int SourcePort, int TargetPort, byte[] Payload);
internal sealed record OnlineIceTcpFrame(Guid RoomId, Guid FromUserId, byte[] Payload);

/// <summary>Establishes direct WebRTC data channels for each online game room member.</summary>
internal sealed class OnlineGameIceSession : IDisposable
{
    private const string UdpChannelLabel = "edmw-game-udp";
    private const string TcpChannelLabel = "edmw-game-tcp";
    private const int UdpHeaderLength = 8;
    private const int TcpHeaderLength = 25;
    private const int MaximumUdpPayload = OnlineRelayClient.MaximumGamePacketBytes;
    private const int MaximumTcpPayload = OnlineRelayClient.MaximumGameStreamChunkBytes;
    private readonly OnlineRelayClient _client;
    private readonly Channel<OnlineGameRoom?> _roomUpdates = Channel.CreateUnbounded<OnlineGameRoom?>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, IcePeer> _peers = new();
    private readonly object _peerLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _roomWorker;
    private OnlineGameRoom? _room;
    private string? _latestStatus;
    private int _disposed;

    public event EventHandler<OnlineIceDatagram>? DatagramReceived;
    public event EventHandler<OnlineIceTcpFrame>? TcpFrameReceived;
    public event EventHandler<string>? StatusChanged;
    public string? LatestStatus => Volatile.Read(ref _latestStatus);
    public int ReadyPeerCount => _peers.Values.Count(peer => peer.IsReady);

    public OnlineGameIceSession(OnlineRelayClient client)
    {
        _client = client;
        _client.GameRoomChanged += OnGameRoomChanged;
        _client.GameSignalReceived += OnGameSignalReceived;
        _roomWorker = RunRoomUpdatesAsync();
        if (client.CurrentGameRoom is { } room) _roomUpdates.Writer.TryWrite(room);
    }

    private void OnGameRoomChanged(object? sender, OnlineGameRoom? room) => _roomUpdates.Writer.TryWrite(room);

    private void OnGameSignalReceived(object? sender, OnlineGameSignal signal)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _ = ApplySignalAsync(signal);
    }

    private async Task RunRoomUpdatesAsync()
    {
        try
        {
            await foreach (var room in _roomUpdates.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                Volatile.Write(ref _room, room);
                var localId = _client.User?.UserId ?? Guid.Empty;
                var members = room?.Members.Where(member => member.Profile.UserId != localId)
                    .ToDictionary(member => member.Profile.UserId) ?? [];
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
            SendSignalAsync, HandleUdpFrame, HandleTcpFrame, ReportStatus);
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
        if (payload.Length is < 1 or > MaximumUdpPayload || sourcePort is < 1 or > 65535 || targetPort is < 1 or > 65535)
            return false;
        var room = Volatile.Read(ref _room);
        if (room is null) return false;
        var members = targetRelayPort == 0
            ? room.Members.Where(member => member.Profile.UserId != _client.User?.UserId)
            : room.Members.Where(member => member.RelayPort == targetRelayPort && member.Profile.UserId != _client.User?.UserId);
        var sent = false;
        foreach (var member in members)
        {
            if (!_peers.TryGetValue(member.Profile.UserId, out var peer)) continue;
            var frame = new byte[UdpHeaderLength + payload.Length];
            "EDGU"u8.CopyTo(frame);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), checked((ushort)sourcePort));
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6, 2), checked((ushort)targetPort));
            payload.CopyTo(frame.AsMemory(UdpHeaderLength));
            sent |= peer.SendUdp(frame);
        }
        return sent;
    }

    public bool SendTcpFrame(Guid peerUserId, ReadOnlySpan<byte> frame) =>
        _peers.TryGetValue(peerUserId, out var peer) && peer.SendTcp(frame);

    public bool IsPeerReady(Guid peerUserId) => _peers.TryGetValue(peerUserId, out var peer) && peer.IsReady;

    private void HandleUdpFrame(Guid peerUserId, byte[] frame)
    {
        if (frame.Length is < UdpHeaderLength + 1 or > UdpHeaderLength + MaximumUdpPayload ||
            !frame.AsSpan(0, 4).SequenceEqual("EDGU"u8)) return;
        var room = Volatile.Read(ref _room);
        var member = room?.Members.FirstOrDefault(entry => entry.Profile.UserId == peerUserId);
        var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4, 2));
        var targetPort = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(6, 2));
        if (room is null || member is null || sourcePort == 0 || targetPort == 0) return;
        DatagramReceived?.Invoke(this, new(room.RoomId, peerUserId, sourcePort, targetPort,
            frame.AsSpan(UdpHeaderLength).ToArray()));
    }

    private void HandleTcpFrame(Guid peerUserId, byte[] frame)
    {
        if (frame.Length is < TcpHeaderLength or > TcpHeaderLength + MaximumTcpPayload ||
            !frame.AsSpan(0, 4).SequenceEqual("EDWT"u8)) return;
        var room = Volatile.Read(ref _room);
        if (room is null || !room.Members.Any(member => member.Profile.UserId == peerUserId)) return;
        var type = frame[24];
        var payloadLength = frame.Length - TcpHeaderLength;
        if (type is < 1 or > 5 || (type == 3 ? payloadLength is < 1 or > MaximumTcpPayload : payloadLength != 0)) return;
        TcpFrameReceived?.Invoke(this, new(room.RoomId, peerUserId, frame));
    }

    private void ReportStatus(string message)
    {
        Volatile.Write(ref _latestStatus, message);
        try { StatusChanged?.Invoke(this, message); } catch (Exception) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.GameRoomChanged -= OnGameRoomChanged;
        _client.GameSignalReceived -= OnGameSignalReceived;
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
        private readonly Action<string> _status;
        private readonly Channel<Signal> _signals = Channel.CreateBounded<Signal>(new BoundedChannelOptions(256)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _remoteSignalLock = new(1, 1);
        private readonly List<RtcCandidate> _pendingCandidates = [];
        private readonly object _channelLock = new();
        private IRtcDataChannel? _udp;
        private IRtcDataChannel? _tcp;
        private bool _remoteDescriptionSet;
        private int _disposed;

        public Guid RoomId => _roomId;
        public bool IsReady { get { lock (_channelLock) return _udp?.IsOpen == true && _tcp?.IsOpen == true; } }

        public IcePeer(Guid roomId, Guid remoteUserId, string remoteName, bool isOfferer,
            Func<Guid, string, string, CancellationToken, Task> sendSignal,
            Action<Guid, byte[]> udpReceived, Action<Guid, byte[]> tcpReceived, Action<string> status)
        {
            _roomId = roomId;
            _remoteUserId = remoteUserId;
            _remoteName = remoteName;
            _isOfferer = isOfferer;
            _sendSignal = sendSignal;
            _udpReceived = udpReceived;
            _tcpReceived = tcpReceived;
            _status = status;
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
                    _status($"Direct ICE connection established with {_remoteName}.");
                else if (state.ToString() is "RTC_ICE_FAILED" or "RTC_ICE_DISCONNECTED")
                    _status($"Direct ICE connection to {_remoteName} failed ({state}); gameplay cannot continue without a direct path.");
            };
            _connection.OnConnectionStateChange += (_, state) =>
            {
                if (state.ToString().Contains("FAILED", StringComparison.OrdinalIgnoreCase))
                    _status($"Direct ICE connection to {_remoteName} failed; no relay fallback is configured.");
            };
            _ = SendSignalsAsync();
            if (isOfferer)
            {
                AttachChannel(_connection.CreateDataChannel(new RtcCreateDataChannelArgs
                { Label = UdpChannelLabel, Unordered = true, Unreliable = true, MaxRetransmits = 0 }));
                AttachChannel(_connection.CreateDataChannel(new RtcCreateDataChannelArgs
                { Label = TcpChannelLabel, Unordered = false, Unreliable = false }));
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
            var payload = JsonSerializer.Serialize(new CandidatePayload(candidate.Content,
                string.IsNullOrWhiteSpace(candidate.Mid) ? "0" : candidate.Mid), OnlineRelayClient.JsonOptions);
            if (!_signals.Writer.TryWrite(new("candidate", payload))) _status($"ICE signaling queue is full for {_remoteName}.");
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
            if (channel.Label is not (UdpChannelLabel or TcpChannelLabel)) { channel.Dispose(); return; }
            channel.OnOpen += opened => _status($"Direct {opened.Label} data channel to {_remoteName} is ready.");
            channel.OnError += (_, error) => _status($"Direct data channel to {_remoteName} failed: {error}");
            if (channel.Label == UdpChannelLabel)
            {
                channel.OnBinaryReceivedSafe += (_, data) => _udpReceived(_remoteUserId, data.Data.ToArray());
                lock (_channelLock) { _udp?.Dispose(); _udp = channel; }
            }
            else
            {
                channel.OnBinaryReceivedSafe += (_, data) => _tcpReceived(_remoteUserId, data.Data.ToArray());
                lock (_channelLock) { _tcp?.Dispose(); _tcp = channel; }
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

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _signals.Writer.TryComplete();
            _lifetime.Cancel();
            lock (_channelLock) { _udp?.Dispose(); _tcp?.Dispose(); _udp = null; _tcp = null; }
            try { _connection.Dispose(); } catch (Exception) { }
            _lifetime.Dispose();
        }
    }
}

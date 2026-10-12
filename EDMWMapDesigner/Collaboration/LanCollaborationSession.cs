using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Collaboration;

public sealed record CollaboratorCursorUpdate(Guid Id, string Author, Point Cell);
public sealed record CollaboratorInfo(Guid Id, string Author);
public sealed record LanHost(string Address, int Port, string Author);

/// <summary>Hosts or joins a trusted LAN map-editing session with any number of collaborators.</summary>
public sealed class LanCollaborationSession : IDisposable
{
    // Peers must preserve forced cliffs as well as elevation, ramps, and coastline policy.
    private const int ProtocolVersion = 6;
    public const int DefaultPort = 42817;
    public const int DiscoveryPort = 42818;
    private const string DiscoveryQuery = "EDMW_MAP_DISCOVER_V6";
    private const string DiscoveryResponsePrefix = "EDMW_MAP_HOST_V6|";
    private const int MaximumMessageBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private readonly List<PeerConnection> _peers = [];
    private readonly SemaphoreSlim _hostOrderLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<long>> _pendingAcknowledgements = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<MapProject>> _pendingSynchronizations = new();
    private TcpListener? _listener;
    private UdpClient? _discoverySocket;
    private CancellationTokenSource? _cancellation;
    private PeerConnection? _hostPeer;
    private bool _isHost;
    private Guid _localId;
    private string _localAuthor = "Collaborator";
    private MapProject? _authoritativeProject;
    private TaskCompletionSource<MapProject>? _initialProjectReady;
    private bool _clientReady;
    private long _hostRevision;
    private long _lastReceivedRevision = -1;
    private int _resynchronizationPending;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return _isHost ? _listener is not null : _hostPeer is not null && _clientReady;
        }
    }
    public bool IsHosting { get { lock (_gate) return _isHost && _listener is not null; } }
    public event EventHandler<MapProject>? ProjectReceived;
    public event EventHandler<MapDelta>? DeltaReceived;
    public event EventHandler<CollaboratorCursorUpdate>? CursorReceived;
    public event EventHandler<CollaboratorInfo>? CollaboratorConnected;
    public event EventHandler<Guid>? CollaboratorDisconnected;
    public event EventHandler<string>? StatusChanged;

    public Task StartHostingAsync(Func<MapProject> currentProject, string author, int port = DefaultPort,
        bool enableDiscovery = true)
    {
        ArgumentNullException.ThrowIfNull(currentProject);
        Stop();
        var initialProject = currentProject();
        initialProject.Validate();
        try
        {
            _localId = Guid.NewGuid();
            _localAuthor = AuthorOrDefault(author);
            _authoritativeProject = initialProject.Clone();
            _hostRevision = 0;
            _lastReceivedRevision = -1;
            _isHost = true;
            _cancellation = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _discoverySocket = enableDiscovery ? new UdpClient(DiscoveryPort) : null;
            _ = AcceptLoopAsync(_cancellation.Token);
            if (_discoverySocket is not null) _ = DiscoveryLoopAsync(_cancellation.Token);
            StatusChanged?.Invoke(this, "Listening for collaborators.");
            return Task.CompletedTask;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public async Task<MapProject> JoinAsync(string host, string author, int port = DefaultPort)
    {
        Stop();
        _localId = Guid.NewGuid();
        _localAuthor = AuthorOrDefault(author);
        _lastReceivedRevision = -1;
        _clientReady = false;
        _cancellation = new CancellationTokenSource();
        var initialProjectReady = new TaskCompletionSource<MapProject>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _initialProjectReady = initialProjectReady;
        StatusChanged?.Invoke(this, "Connecting to host...");
        try
        {
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, _cancellation.Token);
            if (_cancellation.IsCancellationRequested)
            {
                client.Dispose();
                throw new OperationCanceledException(_cancellation.Token);
            }
            var peer = new PeerConnection(client);
            lock (_gate) _hostPeer = peer;
            _ = ReceiveLoopAsync(peer, _cancellation.Token);
            await SendAsync(peer, CreateHelloMessage(), _cancellation.Token);
            var project = await initialProjectReady.Task.WaitAsync(AcknowledgementTimeout, _cancellation.Token);
            lock (_gate)
            {
                if (ReferenceEquals(_initialProjectReady, initialProjectReady)) _initialProjectReady = null;
                _clientReady = true;
            }
            StatusChanged?.Invoke(this, "Connected.");
            return project.Clone();
        }
        catch
        {
            Stop();
            StatusChanged?.Invoke(this, "Could not connect to the host.");
            throw;
        }
    }

    public async Task SendDeltaAsync(MapDelta delta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (IsHosting)
        {
            await CommitHostDeltaAsync(delta, cancellationToken);
            return;
        }
        await SendWithAcknowledgementAsync(
            new CollaborationMessage { Type = "delta", Delta = delta }, cancellationToken);
    }

    public async Task SendProjectAsync(MapProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();
        if (IsHosting)
        {
            await CommitHostProjectAsync(project, cancellationToken);
            return;
        }
        await SendWithAcknowledgementAsync(
            new CollaborationMessage { Type = "project", Project = project.Clone() }, cancellationToken);
    }

    public Task SendCursorAsync(Point cell, CancellationToken cancellationToken = default) =>
        SendFromLocalAsync(Versioned(new CollaborationMessage
        {
            Type = "cursor",
            Cursor = new CursorMessage { Id = _localId, Author = _localAuthor, X = cell.X, Y = cell.Y }
        }), cancellationToken);

    public async Task<MapProject> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (IsHosting)
        {
            await _hostOrderLock.WaitAsync(cancellationToken);
            try
            {
                lock (_gate)
                    return (_authoritativeProject ??
                            throw new InvalidOperationException("The collaboration host has no map state.")).Clone();
            }
            finally { _hostOrderLock.Release(); }
        }

        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<MapProject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingSynchronizations.TryAdd(requestId, completion))
            throw new InvalidOperationException("Could not create a collaboration synchronization request.");
        try
        {
            await SendFromLocalAsync(Versioned(new CollaborationMessage
            {
                Type = "sync-request",
                RequestId = requestId
            }), cancellationToken);
            return await completion.Task.WaitAsync(AcknowledgementTimeout, cancellationToken);
        }
        finally { _pendingSynchronizations.TryRemove(requestId, out _); }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        TcpListener? listener;
        UdpClient? discoverySocket;
        TaskCompletionSource<MapProject>? initialProjectReady;
        PeerConnection[] peers;
        lock (_gate)
        {
            cancellation = _cancellation;
            listener = _listener;
            discoverySocket = _discoverySocket;
            peers = _peers.Append(_hostPeer).Where(peer => peer is not null).Cast<PeerConnection>().Distinct().ToArray();
            _cancellation = null;
            _listener = null;
            _discoverySocket = null;
            _hostPeer = null;
            _clientReady = false;
            initialProjectReady = _initialProjectReady;
            _initialProjectReady = null;
            _peers.Clear();
            _authoritativeProject = null;
            _hostRevision = 0;
            _lastReceivedRevision = -1;
            _resynchronizationPending = 0;
            _isHost = false;
        }
        cancellation?.Cancel();
        listener?.Stop();
        discoverySocket?.Dispose();
        foreach (var peer in peers) peer.Dispose();
        initialProjectReady?.TrySetCanceled();
        foreach (var pending in _pendingAcknowledgements.Values)
            pending.TrySetCanceled();
        foreach (var pending in _pendingSynchronizations.Values)
            pending.TrySetCanceled();
        _pendingAcknowledgements.Clear();
        _pendingSynchronizations.Clear();
        cancellation?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var listener = _listener ?? throw new OperationCanceledException();
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                var peer = new PeerConnection(client);
                lock (_gate) _peers.Add(peer);
                _ = ReceiveLoopAsync(peer, cancellationToken);
                await SendAsync(peer, CreateHelloMessage(), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested) StatusChanged?.Invoke(this, "Hosting stopped.");
        }
    }

    public static async Task<IReadOnlyList<LanHost>> DiscoverHostsAsync(CancellationToken cancellationToken = default)
    {
        using var client = new UdpClient(0) { EnableBroadcast = true };
        var query = System.Text.Encoding.UTF8.GetBytes(DiscoveryQuery);
        await client.SendAsync(query, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1200));
        var hosts = new Dictionary<string, LanHost>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var response = await client.ReceiveAsync(timeout.Token);
                var message = System.Text.Encoding.UTF8.GetString(response.Buffer);
                if (!message.StartsWith(DiscoveryResponsePrefix, StringComparison.Ordinal)) continue;
                var parts = message.Split('|', 3);
                if (parts.Length != 3 || !int.TryParse(parts[1], out var port) || port is < 1 or > 65535) continue;
                var host = new LanHost(response.RemoteEndPoint.Address.ToString(), port, AuthorOrDefault(parts[2]));
                hosts[$"{host.Address}:{host.Port}"] = host;
            }
        }
        catch (OperationCanceledException) { }
        return hosts.Values.OrderBy(host => host.Author).ThenBy(host => host.Address).ToArray();
    }

    private async Task DiscoveryLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _discoverySocket;
        if (socket is null) return;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await socket.ReceiveAsync(cancellationToken);
                var message = System.Text.Encoding.UTF8.GetString(request.Buffer);
                if (!string.Equals(message, DiscoveryQuery, StringComparison.Ordinal)) continue;
                var response = System.Text.Encoding.UTF8.GetBytes($"{DiscoveryResponsePrefix}{DefaultPort}|{_localAuthor}");
                await socket.SendAsync(response, request.RemoteEndPoint, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SendFromLocalAsync(CollaborationMessage message, CancellationToken cancellationToken)
    {
        PeerConnection[]? recipients = null;
        PeerConnection? hostPeer = null;
        lock (_gate)
        {
            if (_isHost) recipients = _peers.ToArray();
            else hostPeer = _hostPeer;
        }
        if (recipients is not null)
        {
            if (recipients.Length == 0)
                throw new InvalidOperationException("The collaboration session is not connected.");
            await BroadcastAsync(recipients, message, cancellationToken);
            return;
        }
        if (hostPeer is null)
            throw new InvalidOperationException("The collaboration session is not connected.");
        await SendAsync(hostPeer, message, cancellationToken);
    }

    private async Task<long> SendWithAcknowledgementAsync(CollaborationMessage message,
        CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid();
        message.MessageId = messageId;
        var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingAcknowledgements.TryAdd(messageId, completion))
            throw new InvalidOperationException("Could not track a collaboration update.");
        try
        {
            await SendFromLocalAsync(Versioned(message), cancellationToken);
            return await completion.Task.WaitAsync(AcknowledgementTimeout, cancellationToken);
        }
        finally { _pendingAcknowledgements.TryRemove(messageId, out _); }
    }

    private async Task ReceiveLoopAsync(PeerConnection peer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var data = await ReadFrameAsync(peer.Stream, cancellationToken);
                var message = JsonSerializer.Deserialize<CollaborationMessage>(data, JsonOptions)
                    ?? throw new InvalidDataException("Received an empty collaboration message.");
                await HandleMessageAsync(peer, message, data, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                RemovePeer(peer);
                StatusChanged?.Invoke(this, ex.Message);
            }
        }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                RemovePeer(peer);
                StatusChanged?.Invoke(this, "Collaborator disconnected.");
            }
        }
    }

    private async Task HandleMessageAsync(PeerConnection peer, CollaborationMessage message, byte[] raw, CancellationToken cancellationToken)
    {
        if (message.Version != ProtocolVersion)
        {
            StatusChanged?.Invoke(this,
                $"Incompatible collaboration version (requires version {ProtocolVersion}).");
            throw new InvalidDataException("The collaborator is using an incompatible map-designer build.");
        }
        if (!peer.ProtocolValidated && message.Type != "hello")
            throw new InvalidDataException("The collaborator sent map data before protocol negotiation.");

        switch (message.Type)
        {
            case "hello" when message.Hello is not null:
                await HandleHelloAsync(peer, message.Hello, raw, cancellationToken);
                return;
            case "project" when message.Project is not null:
                message.Project.Validate();
                if (_isHost)
                    await CommitRemoteProjectAsync(peer, message, cancellationToken);
                else
                    ApplyReceivedProject(message);
                return;
            case "delta" when message.Delta is not null:
                if (_isHost)
                    await CommitRemoteDeltaAsync(peer, message, cancellationToken);
                else
                    ApplyReceivedDelta(message);
                return;
            case "sync-request" when _isHost && message.RequestId != Guid.Empty:
                await SendSynchronizationAsync(peer, message.RequestId, cancellationToken);
                return;
            case "sync-project" when !_isHost && message.RequestId != Guid.Empty && message.Project is not null:
                message.Project.Validate();
                _lastReceivedRevision = Math.Max(_lastReceivedRevision, message.Revision);
                // Publish every barrier snapshot from this receive loop before
                // completing its waiter or reading a later frame. Consumers therefore
                // cannot apply revision N after already applying N+1.
                ProjectReceived?.Invoke(this, message.Project.Clone());
                if (Interlocked.CompareExchange(ref _resynchronizationPending, 0, 1) == 1)
                    StatusChanged?.Invoke(this, "Collaboration state resynchronized.");
                if (_pendingSynchronizations.TryGetValue(message.RequestId, out var synchronization))
                    synchronization.TrySetResult(message.Project.Clone());
                return;
            case "acknowledgement" when !_isHost && message.AcknowledgedMessageId != Guid.Empty:
                if (_pendingAcknowledgements.TryGetValue(message.AcknowledgedMessageId, out var acknowledgement))
                {
                    if (string.IsNullOrWhiteSpace(message.Error))
                        acknowledgement.TrySetResult(message.Revision);
                    else
                        acknowledgement.TrySetException(new InvalidOperationException(message.Error));
                }
                return;
            case "cursor" when message.Cursor is not null:
                CursorReceived?.Invoke(this, new CollaboratorCursorUpdate(message.Cursor.Id,
                    AuthorOrDefault(message.Cursor.Author), new Point(message.Cursor.X, message.Cursor.Y)));
                if (_isHost)
                {
                    PeerConnection[] recipients;
                    lock (_gate) recipients = _peers
                        .Where(candidate => candidate != peer && candidate.ProtocolValidated).ToArray();
                    await BroadcastRawAsync(recipients, raw, cancellationToken);
                }
                return;
            default:
                throw new InvalidDataException("Received an unknown collaboration message.");
        }
    }

    private async Task HandleHelloAsync(PeerConnection peer, HelloMessage hello, byte[] raw,
        CancellationToken cancellationToken)
    {
        if (hello.Id == Guid.Empty) throw new InvalidDataException("Received an invalid collaborator identity.");
        if (!peer.ProtocolValidated)
        {
            peer.ProtocolValidated = true;
            peer.Id = hello.Id;
            peer.Author = AuthorOrDefault(hello.Author);
        }
        CollaboratorConnected?.Invoke(this,
            new CollaboratorInfo(hello.Id, AuthorOrDefault(hello.Author)));
        if (!_isHost) return;

        PeerConnection[] existing;
        lock (_gate) existing = _peers
            .Where(existingPeer => existingPeer != peer && existingPeer.ProtocolValidated).ToArray();
        foreach (var existingPeer in existing)
            await SendAsync(peer, CreateHelloMessage(existingPeer.Id, existingPeer.Author), cancellationToken);
        await SendSynchronizationAsync(peer, Guid.Empty, cancellationToken);
        await BroadcastRawAsync(existing, raw, cancellationToken);
        StatusChanged?.Invoke(this, "Collaborator joined.");
    }

    private async Task CommitHostDeltaAsync(MapDelta delta, CancellationToken cancellationToken)
    {
        await _hostOrderLock.WaitAsync(cancellationToken);
        try
        {
            MapDelta? canonical;
            PeerConnection[] recipients;
            long revision;
            lock (_gate)
            {
                var before = (_authoritativeProject ??
                              throw new InvalidOperationException("The collaboration host has no map state.")).Clone();
                var after = before.Clone();
                delta.ApplyTo(after);
                canonical = MapDelta.Create(before, after);
                _authoritativeProject = after;
                if (canonical is not null) _hostRevision++;
                revision = _hostRevision;
                recipients = _peers.Where(peer => peer.ProtocolValidated).ToArray();
            }
            if (canonical is not null)
                await BroadcastAsync(recipients, new CollaborationMessage
                    { Type = "delta", Delta = canonical, Revision = revision }, cancellationToken);
        }
        finally { _hostOrderLock.Release(); }
    }

    private async Task CommitRemoteDeltaAsync(PeerConnection sender, CollaborationMessage message,
        CancellationToken cancellationToken)
    {
        await _hostOrderLock.WaitAsync(cancellationToken);
        try
        {
            MapDelta? canonical;
            MapProject? resynchronization = null;
            PeerConnection[] recipients;
            long revision;
            lock (_gate)
            {
                var before = (_authoritativeProject ??
                              throw new InvalidOperationException("The collaboration host has no map state.")).Clone();
                if (message.Delta!.GridSize != before.GridSize)
                {
                    canonical = null;
                    recipients = [];
                    revision = _hostRevision;
                    resynchronization = before;
                }
                else
                {
                    var after = before.Clone();
                    message.Delta.ApplyTo(after);
                    canonical = MapDelta.Create(before, after);
                    _authoritativeProject = after;
                    if (canonical is not null) _hostRevision++;
                    revision = _hostRevision;
                    // Echo the canonical, revisioned edit to the sender too. This makes
                    // simultaneous writes to the same target converge on the host's order.
                    recipients = _peers.Where(peer => peer.ProtocolValidated).ToArray();
                }
            }
            if (resynchronization is not null)
            {
                await SendAsync(sender, new CollaborationMessage
                {
                    Type = "project",
                    Project = resynchronization,
                    Revision = revision
                }, cancellationToken);
                await SendAcknowledgementAsync(sender, message.MessageId, revision, cancellationToken,
                    "The map changed size before this edit arrived. The current shared map was restored; retry the edit.");
                return;
            }
            if (canonical is not null)
            {
                DeltaReceived?.Invoke(this, canonical);
                await BroadcastAsync(recipients, new CollaborationMessage
                    { Type = "delta", Delta = canonical, Revision = revision }, cancellationToken);
            }
            await SendAcknowledgementAsync(sender, message.MessageId, revision, cancellationToken);
        }
        finally { _hostOrderLock.Release(); }
    }

    private async Task CommitHostProjectAsync(MapProject project, CancellationToken cancellationToken)
    {
        await _hostOrderLock.WaitAsync(cancellationToken);
        try
        {
            PeerConnection[] recipients;
            long revision;
            lock (_gate)
            {
                _authoritativeProject = project.Clone();
                revision = ++_hostRevision;
                recipients = _peers.Where(peer => peer.ProtocolValidated).ToArray();
            }
            await BroadcastAsync(recipients, new CollaborationMessage
                { Type = "project", Project = project.Clone(), Revision = revision }, cancellationToken);
        }
        finally { _hostOrderLock.Release(); }
    }

    private async Task CommitRemoteProjectAsync(PeerConnection sender, CollaborationMessage message,
        CancellationToken cancellationToken)
    {
        await _hostOrderLock.WaitAsync(cancellationToken);
        try
        {
            PeerConnection[] recipients;
            MapProject project;
            long revision;
            lock (_gate)
            {
                project = message.Project!.Clone();
                _authoritativeProject = project.Clone();
                revision = ++_hostRevision;
                recipients = _peers.Where(peer => peer.ProtocolValidated).ToArray();
            }
            ProjectReceived?.Invoke(this, project.Clone());
            await BroadcastAsync(recipients, new CollaborationMessage
                { Type = "project", Project = project, Revision = revision }, cancellationToken);
            await SendAcknowledgementAsync(sender, message.MessageId, revision, cancellationToken);
        }
        finally { _hostOrderLock.Release(); }
    }

    private void ApplyReceivedProject(CollaborationMessage message)
    {
        if (message.Revision <= _lastReceivedRevision) return;
        _lastReceivedRevision = message.Revision;
        var project = message.Project!.Clone();
        ProjectReceived?.Invoke(this, project.Clone());
        TaskCompletionSource<MapProject>? initialProjectReady;
        lock (_gate) initialProjectReady = _initialProjectReady;
        initialProjectReady?.TrySetResult(project);
    }

    private void ApplyReceivedDelta(CollaborationMessage message)
    {
        if (message.Revision <= _lastReceivedRevision) return;
        if (message.Revision != _lastReceivedRevision + 1)
        {
            QueueAuthoritativeResynchronization();
            return;
        }
        _lastReceivedRevision = message.Revision;
        DeltaReceived?.Invoke(this, message.Delta!);
    }

    private void QueueAuthoritativeResynchronization()
    {
        if (Interlocked.Exchange(ref _resynchronizationPending, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                _ = await SynchronizeAsync();
            }
            catch (Exception)
            {
                if (IsConnected)
                    StatusChanged?.Invoke(this, "Collaboration state could not be resynchronized.");
            }
            finally { Interlocked.CompareExchange(ref _resynchronizationPending, 0, 1); }
        });
    }

    private async Task SendSynchronizationAsync(PeerConnection peer, Guid requestId,
        CancellationToken cancellationToken)
    {
        await _hostOrderLock.WaitAsync(cancellationToken);
        try
        {
            MapProject project;
            long revision;
            lock (_gate)
            {
                project = (_authoritativeProject ??
                           throw new InvalidOperationException("The collaboration host has no map state.")).Clone();
                revision = _hostRevision;
            }
            await SendAsync(peer, new CollaborationMessage
            {
                Type = requestId == Guid.Empty ? "project" : "sync-project",
                Project = project,
                RequestId = requestId,
                Revision = revision
            }, cancellationToken);
        }
        finally { _hostOrderLock.Release(); }
    }

    private static Task SendAcknowledgementAsync(PeerConnection peer, Guid messageId, long revision,
        CancellationToken cancellationToken, string? error = null) => messageId == Guid.Empty
        ? Task.CompletedTask
        : SendAsync(peer, new CollaborationMessage
        {
            Type = "acknowledgement",
            AcknowledgedMessageId = messageId,
            Revision = revision,
            Error = error
        }, cancellationToken);

    private void RemovePeer(PeerConnection peer)
    {
        Guid id;
        TaskCompletionSource<MapProject>? initialProjectReady = null;
        bool removed;
        lock (_gate)
        {
            id = peer.Id;
            removed = _peers.Remove(peer);
            if (_hostPeer == peer)
            {
                _hostPeer = null;
                initialProjectReady = _initialProjectReady;
                removed = true;
            }
        }
        if (!removed) return;
        peer.Dispose();
        initialProjectReady?.TrySetException(
            new IOException("The collaboration host disconnected before synchronization completed."));
        if (id != Guid.Empty) CollaboratorDisconnected?.Invoke(this, id);
    }

    private CollaborationMessage CreateHelloMessage() => Versioned(new CollaborationMessage
    {
        Type = "hello",
        Hello = new HelloMessage { Id = _localId, Author = _localAuthor }
    });

    private static CollaborationMessage CreateHelloMessage(Guid id, string author) =>
        Versioned(new CollaborationMessage
        {
            Type = "hello",
            Hello = new HelloMessage { Id = id, Author = author }
        });

    private async Task BroadcastAsync(IEnumerable<PeerConnection> peers, CollaborationMessage message,
        CancellationToken cancellationToken)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(Versioned(message), JsonOptions);
        if (data.Length > MaximumMessageBytes) throw new InvalidOperationException("The map project is too large to send over collaboration.");
        await BroadcastRawAsync(peers, data, cancellationToken);
    }

    private async Task BroadcastRawAsync(IEnumerable<PeerConnection> peers, byte[] data,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(peers.ToArray().Select(SendToPeerAsync));

        async Task SendToPeerAsync(PeerConnection peer)
        {
            try { await SendRawAsync(peer, data, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { RemovePeer(peer); }
        }
    }

    private static async Task SendAsync(PeerConnection peer, CollaborationMessage message, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(Versioned(message), JsonOptions);
        await SendRawAsync(peer, data, cancellationToken);
    }

    private static CollaborationMessage Versioned(CollaborationMessage message)
    {
        message.Version = ProtocolVersion;
        return message;
    }

    private static async Task SendRawAsync(PeerConnection peer, byte[] data, CancellationToken cancellationToken)
    {
        if (data.Length > MaximumMessageBytes) throw new InvalidOperationException("The collaboration message is too large.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);
        var lockTaken = false;
        try
        {
            await peer.SendLock.WaitAsync(timeout.Token);
            lockTaken = true;
            var length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(data.Length));
            await peer.Stream.WriteAsync(length, timeout.Token);
            await peer.Stream.WriteAsync(data, timeout.Token);
            await peer.Stream.FlushAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The collaborator did not accept a map update in time.");
        }
        finally
        {
            if (lockTaken) peer.SendLock.Release();
        }
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactlyAsync(stream, sizeof(int), cancellationToken);
        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header));
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("Received an invalid collaboration message.");
        return await ReadExactlyAsync(stream, length, cancellationToken);
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        var data = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(data.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0) throw new EndOfStreamException("The collaborator closed the connection.");
            offset += read;
        }
        return data;
    }

    private static string AuthorOrDefault(string? author) => string.IsNullOrWhiteSpace(author) ? "Collaborator" : author.Trim();

    public void Dispose() => Stop();

    private sealed class PeerConnection(TcpClient client) : IDisposable
    {
        private int _disposed;
        public TcpClient Client { get; } = client;
        public NetworkStream Stream { get; } = client.GetStream();
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public Guid Id { get; set; }
        public string Author { get; set; } = "Collaborator";
        public bool ProtocolValidated { get; set; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stream.Dispose();
            Client.Dispose();
            SendLock.Dispose();
        }
    }

    private sealed class CollaborationMessage
    {
        public int Version { get; set; }
        public string Type { get; set; } = "";
        public Guid MessageId { get; set; }
        public Guid AcknowledgedMessageId { get; set; }
        public Guid RequestId { get; set; }
        public long Revision { get; set; }
        public string? Error { get; set; }
        public HelloMessage? Hello { get; set; }
        public MapProject? Project { get; set; }
        public MapDelta? Delta { get; set; }
        public CursorMessage? Cursor { get; set; }
    }

    private sealed class HelloMessage { public Guid Id { get; set; } public string Author { get; set; } = ""; }
    private sealed class CursorMessage { public Guid Id { get; set; } public string Author { get; set; } = ""; public int X { get; set; } public int Y { get; set; } }
}

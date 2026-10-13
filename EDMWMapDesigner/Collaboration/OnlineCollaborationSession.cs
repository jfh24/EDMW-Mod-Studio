using System.Text.Json;
using EDMWMapDesigner.Models;
using EDMWMapDesigner.Networking;

namespace EDMWMapDesigner.Collaboration;

public sealed record OnlineCanonicalDelta(MapDelta Delta, bool IsLocal);
public sealed record OnlineCanonicalProject(MapProject Project, bool IsLocal);

/// <summary>Applies relay-ordered edits, including local echoes, to one shared authoritative map.</summary>
public sealed class OnlineCollaborationSession : IDisposable
{
    private readonly OnlineRelayClient _client;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private Guid? _sessionId;
    private MapProject? _project;
    private long _revision = -1;
    private bool _hosting;
    private bool _synchronizing;
    private bool _disposed;
    private int _generation;
    private long _lastCursorSentAt = long.MinValue;
    private Dictionary<Guid, OnlineProfile> _members = [];
    private TaskCompletionSource<bool> _revisionChanged = NewRevisionCompletion();

    public OnlineCollaborationSession(OnlineRelayClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _client.RelayEventReceived += OnRelayEvent;
        _client.Disconnected += OnDisconnected;
    }

    public Guid? SessionId { get { lock (_gate) return _sessionId; } }
    public bool IsConnected { get { lock (_gate) return _sessionId is not null && _client.IsConnected; } }
    public bool IsHosting { get { lock (_gate) return _sessionId is not null && _hosting && _client.IsConnected; } }
    public event EventHandler<MapProject>? ProjectReceived;
    public event EventHandler<MapDelta>? DeltaReceived;
    public event EventHandler<OnlineCanonicalProject>? CanonicalProjectReceived;
    public event EventHandler<OnlineCanonicalDelta>? CanonicalDeltaReceived;
    public event EventHandler<CollaboratorCursorUpdate>? CursorReceived;
    public event EventHandler<CollaboratorInfo>? CollaboratorConnected;
    public event EventHandler<Guid>? CollaboratorDisconnected;
    public event EventHandler<string>? StatusChanged;

    public async Task CreateAsync(MapProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();
        _ = await EnterSessionAsync("collab.create", new() { ["project"] = project.Clone() }, true, cancellationToken).ConfigureAwait(false);
    }

    public Task<MapProject> JoinAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Choose a collaboration invitation.", nameof(sessionId));
        return EnterSessionAsync("collab.join", new() { ["sessionId"] = sessionId }, false, cancellationToken);
    }

    private async Task<MapProject> EnterSessionAsync(string command, Dictionary<string, object?> fields,
        bool hosting, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (SessionId is not null)
            {
                ClearSession("Leaving the previous online collaboration.");
                _ = await _client.RequestAsync("collab.leave", cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            int generation;
            lock (_gate) generation = _generation;
            MapProject? initial = null;
            _ = await _client.RequestAsync(command, fields, cancellationToken, data =>
            {
                var snapshot = ReadSnapshot(data);
                lock (_gate)
                {
                    if (generation != _generation) return;
                    _sessionId = snapshot.SessionId;
                    _hosting = hosting;
                    _project = snapshot.Project.Clone();
                    _revision = snapshot.Revision;
                    _lastCursorSentAt = long.MinValue;
                    initial = snapshot.Project.Clone();
                    SignalRevisionChanged();
                }
                if (!hosting)
                {
                    ProjectReceived?.Invoke(this, snapshot.Project.Clone());
                    CanonicalProjectReceived?.Invoke(this, new(snapshot.Project.Clone(), false));
                }
                UpdateMembers(snapshot.Members);
            }).ConfigureAwait(false);
            bool joined;
            lock (_gate) joined = generation == _generation && initial is not null;
            if (joined)
            {
                StatusChanged?.Invoke(this, hosting ? "Hosting online collaboration. Invite an accepted friend." : "Joined online collaboration.");
                return initial!;
            }
            try { _ = await _client.RequestAsync("collab.leave").ConfigureAwait(false); } catch (Exception) { }
            throw new IOException("The online collaboration was stopped before joining completed.");
        }
        finally { _operations.Release(); }
    }

    public async Task InviteAsync(Guid friendId, CancellationToken cancellationToken = default)
    {
        var id = RequireSession();
        if (!IsHosting) throw new InvalidOperationException("Only the collaboration host can invite friends.");
        _ = await _client.RequestAsync("collab.invite", new Dictionary<string, object?>
        { ["sessionId"] = id, ["userId"] = friendId }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendDeltaAsync(MapDelta delta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delta);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = RequireSession();
            lock (_gate)
            {
                var candidate = (_project ?? throw new InvalidOperationException("No online map is available.")).Clone();
                delta.ApplyTo(candidate);
            }
            try
            {
                var result = await _client.RequestAsync("collab.delta", new Dictionary<string, object?>
                { ["sessionId"] = id, ["delta"] = delta }, cancellationToken).ConfigureAwait(false);
                await WaitForRevisionAsync(id, ReadRevision(result), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                await ResynchronizeAfterRejectedEditAsync(id, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally { _operations.Release(); }
    }

    public async Task SendProjectAsync(MapProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = RequireSession();
            long revision;
            lock (_gate) revision = _revision;
            try
            {
                var result = await _client.RequestAsync("collab.project", new Dictionary<string, object?>
                { ["sessionId"] = id, ["project"] = project.Clone(), ["baseRevision"] = revision }, cancellationToken).ConfigureAwait(false);
                await WaitForRevisionAsync(id, ReadRevision(result), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                await ResynchronizeAfterRejectedEditAsync(id, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally { _operations.Release(); }
    }

    public async Task SendCursorAsync(Point cell, CancellationToken cancellationToken = default)
    {
        var id = RequireSession();
        lock (_gate)
        {
            if (_project is null || cell.X < 0 || cell.Y < 0 || cell.X >= _project.GridSize || cell.Y >= _project.GridSize)
                return;
            var now = Environment.TickCount64;
            if (_lastCursorSentAt != long.MinValue && now - _lastCursorSentAt < 50) return;
            _lastCursorSentAt = now;
        }
        _ = await _client.RequestAsync("collab.cursor", new Dictionary<string, object?>
        { ["sessionId"] = id, ["x"] = cell.X, ["y"] = cell.Y }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes the response in the receive loop, before any later map edits can be delivered.</summary>
    public async Task<MapProject> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        var id = RequireSession();
        MapProject? synchronized = null;
        _ = await _client.RequestAsync("collab.sync", new Dictionary<string, object?> { ["sessionId"] = id }, cancellationToken, data =>
        {
            var snapshot = ReadSnapshot(data);
            lock (_gate)
            {
                if (_sessionId != id || snapshot.SessionId != id || snapshot.Revision < _revision)
                    throw new IOException("The online collaboration changed before synchronization completed.");
                _project = snapshot.Project.Clone();
                _revision = snapshot.Revision;
                _synchronizing = false;
                synchronized = snapshot.Project.Clone();
            }
            // UI consumers may synchronously marshal to a thread that reads session state.
            // Publish outside the gate, before releasing revision waiters or reading another event.
            ProjectReceived?.Invoke(this, synchronized.Clone());
            CanonicalProjectReceived?.Invoke(this, new(synchronized.Clone(), false));
            lock (_gate) SignalRevisionChanged();
            UpdateMembers(snapshot.Members);
        }).ConfigureAwait(false);
        return synchronized ?? throw new IOException("The online collaboration stopped during synchronization.");
    }

    private async Task ResynchronizeAfterRejectedEditAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (SessionId != sessionId) return;
        try { _ = await SynchronizeAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        { if (IsConnected) StatusChanged?.Invoke(this, "The rejected edit could not be resynchronized; reconnect to the collaboration."); }
    }

    public void Stop() => _ = StopSafelyAsync();

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ClearSession("Online collaboration stopped.");
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client.IsConnected)
                _ = await _client.RequestAsync("collab.leave", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task StopSafelyAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        catch (Exception) { /* A broken connection has already ended this transient room. */ }
    }

    private void OnDisconnected(object? sender, EventArgs eventArgs) =>
        ClearSession("Online collaboration ended because the relay disconnected. Reconnect and create or join a new session.");

    private void OnRelayEvent(object? sender, OnlineRelayEvent message)
    {
        if (!message.Type.StartsWith("collab.", StringComparison.Ordinal)) return;
        var id = OnlineRelayClient.RequiredGuid(message.Data, "sessionId");
        lock (_gate) if (_sessionId != id) return;
        switch (message.Type)
        {
            case "collab.delta": ApplyDelta(message.Data); break;
            case "collab.project": ApplyProject(message.Data); break;
            case "collab.members":
                UpdateMembers(OnlineRelayClient.Deserialize<OnlineProfile[]>(message.Data.GetProperty("members")));
                break;
            case "collab.cursor":
                var userId = OnlineRelayClient.RequiredGuid(message.Data, "userId");
                var x = message.Data.GetProperty("x").GetInt32();
                var y = message.Data.GetProperty("y").GetInt32();
                lock (_gate)
                {
                    if (!_members.ContainsKey(userId) || _project is null || x < 0 || y < 0 || x >= _project.GridSize || y >= _project.GridSize) return;
                }
                CursorReceived?.Invoke(this, new(userId, message.Data.GetProperty("displayName").GetString() ?? "Friend", new(x, y)));
                break;
            case "collab.ended":
                ClearSession(message.Data.TryGetProperty("message", out var text) ? text.GetString() ?? "Online collaboration ended." : "Online collaboration ended.");
                break;
        }
    }

    private void ApplyDelta(JsonElement data)
    {
        var revision = ReadRevision(data);
        var delta = OnlineRelayClient.Deserialize<MapDelta>(data.GetProperty("delta"));
        lock (_gate)
        {
            if (revision <= _revision) return;
            if (_synchronizing || revision != _revision + 1) { QueueResynchronization(); return; }
            var candidate = (_project ?? throw new InvalidDataException("No online collaboration snapshot is available.")).Clone();
            try { delta.ApplyTo(candidate); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            { QueueResynchronization(); return; }
            _project = candidate;
            _revision = revision;
        }
        DeltaReceived?.Invoke(this, delta);
        CanonicalDeltaReceived?.Invoke(this, new(delta, IsLocalAuthor(data)));
        lock (_gate) SignalRevisionChanged();
    }

    private void ApplyProject(JsonElement data)
    {
        var revision = ReadRevision(data);
        var project = OnlineRelayClient.Deserialize<MapProject>(data.GetProperty("project"));
        project.Validate();
        lock (_gate)
        {
            if (revision <= _revision) return;
            if (_synchronizing || revision != _revision + 1) { QueueResynchronization(); return; }
            _project = project.Clone();
            _revision = revision;
        }
        ProjectReceived?.Invoke(this, project.Clone());
        CanonicalProjectReceived?.Invoke(this, new(project.Clone(), IsLocalAuthor(data)));
        lock (_gate) SignalRevisionChanged();
    }

    private void QueueResynchronization()
    {
        // Called in receive order under the session gate; subsequent deltas wait for the snapshot barrier.
        if (_synchronizing) return;
        _synchronizing = true;
        var generation = _generation;
        _ = Task.Run(async () =>
        {
            try { await ResynchronizeSafelyAsync(generation).ConfigureAwait(false); }
            catch (Exception) { /* Event consumers cannot fault an unobserved recovery task. */ }
        });
    }

    private async Task ResynchronizeSafelyAsync(int generation)
    {
        try
        {
            _ = await SynchronizeAsync().ConfigureAwait(false);
            StatusChanged?.Invoke(this, "Online collaboration state resynchronized.");
        }
        catch (Exception)
        {
            lock (_gate) if (_generation != generation) return;
            ClearSession("Online collaboration could not be resynchronized. Join a new session to continue.");
            try { _ = await _client.RequestAsync("collab.leave").ConfigureAwait(false); } catch (Exception) { }
        }
    }

    private void UpdateMembers(OnlineProfile[] members)
    {
        if (members.Any(member => member.UserId == Guid.Empty) || members.Select(member => member.UserId).Distinct().Count() != members.Length)
            throw new InvalidDataException("The relay returned invalid collaboration members.");
        var localId = _client.User?.UserId;
        var next = members.Where(member => member.UserId != localId).ToDictionary(member => member.UserId);
        Guid[] removed;
        OnlineProfile[] added;
        lock (_gate)
        {
            removed = _members.Keys.Except(next.Keys).ToArray();
            added = next.Values.Where(member => !_members.ContainsKey(member.UserId)).ToArray();
            _members = next;
        }
        foreach (var id in removed) CollaboratorDisconnected?.Invoke(this, id);
        foreach (var member in added) CollaboratorConnected?.Invoke(this, new(member.UserId, member.DisplayName));
    }

    private void ClearSession(string message)
    {
        Guid[] removed;
        bool wasConnected;
        lock (_gate)
        {
            wasConnected = _sessionId is not null;
            removed = _members.Keys.ToArray();
            _sessionId = null;
            _hosting = false;
            _project = null;
            _revision = -1;
            _lastCursorSentAt = long.MinValue;
            _synchronizing = false;
            _generation++;
            _members.Clear();
            SignalRevisionChanged();
        }
        foreach (var id in removed) CollaboratorDisconnected?.Invoke(this, id);
        if (wasConnected) StatusChanged?.Invoke(this, message);
    }

    private async Task WaitForRevisionAsync(Guid sessionId, long revision, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_sessionId != sessionId) throw new IOException("Online collaboration stopped before its edit was confirmed.");
                if (_revision >= revision) return;
                changed = _revisionChanged.Task;
            }
            try { await changed.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { QueueResynchronizationUnderLock(); throw new TimeoutException("The relay did not deliver the confirmed online map revision."); }
        }
    }

    private void QueueResynchronizationUnderLock() { lock (_gate) if (_sessionId is not null) QueueResynchronization(); }
    private void SignalRevisionChanged()
    {
        var previous = _revisionChanged;
        _revisionChanged = NewRevisionCompletion();
        previous.TrySetResult(true);
    }
    private static TaskCompletionSource<bool> NewRevisionCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Guid RequireSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sessionId is Guid id && _client.IsConnected ? id : throw new InvalidOperationException("Join or create an online collaboration first.");
        }
    }
    private static long ReadRevision(JsonElement data)
    {
        var revision = data.GetProperty("revision").GetInt64();
        if (revision < 0) throw new InvalidDataException("The relay returned an invalid map revision.");
        return revision;
    }
    private bool IsLocalAuthor(JsonElement data) => OnlineRelayClient.RequiredGuid(data, "authorId") == _client.User?.UserId;
    private static SessionSnapshot ReadSnapshot(JsonElement data)
    {
        var snapshot = OnlineRelayClient.Deserialize<SessionSnapshot>(data);
        if (snapshot.SessionId == Guid.Empty || snapshot.Revision < 0 || snapshot.Project is null || snapshot.Members is null)
            throw new InvalidDataException("The relay returned an invalid collaboration snapshot.");
        snapshot.Project.Validate();
        return snapshot;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _client.RelayEventReceived -= OnRelayEvent;
        _client.Disconnected -= OnDisconnected;
        Stop();
        _disposed = true;
    }
    private sealed record SessionSnapshot(Guid SessionId, long Revision, MapProject Project, OnlineProfile[] Members);
}

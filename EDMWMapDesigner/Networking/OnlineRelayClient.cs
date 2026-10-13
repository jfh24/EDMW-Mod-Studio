using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.Networking;

/// <summary>Authenticated, bounded client for the single-instance online friends relay.</summary>
public sealed class OnlineRelayClient : IDisposable
{
    public const int MaximumMapBytes = 32 * 1024 * 1024;
    public const int MaximumCivilizationBytes = 128 * 1024 * 1024;
    private const int MaximumMessageBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(3);
    internal static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, MaxDepth = 64 };
    private readonly object _gate = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<OnlineExportResult>> _exportResults = new();
    private readonly ConcurrentDictionary<Guid, IncomingOffer> _incomingOffers = new();
    private Connection? _connection;
    private CancellationTokenSource? _connectingCancellation;
    private OnlineIdentity? _identity;
    private Uri? _endpoint;
    private bool _disposed;

    public OnlineRelayClient(OnlinePreferences? preferences = null) => Preferences = preferences ?? OnlinePreferences.Load();
    public OnlinePreferences Preferences { get; }
    public bool IsConnected { get { lock (_gate) return _connection is { Authenticated: true }; } }
    public OnlineProfile? User { get; private set; }
    public OnlineFriendsSnapshot Friends { get; private set; } = new(new(Guid.Empty, "", ""), [], [], []);
    public event EventHandler<OnlineFriendsSnapshot>? FriendsChanged;
    public event EventHandler<OnlineCollaborationInvite>? CollaborationInviteReceived;
    public event EventHandler<OnlineExportOffer>? ExportOfferReceived;
    public event EventHandler<string>? StatusChanged;
    internal event EventHandler<OnlineRelayEvent>? RelayEventReceived;
    internal event EventHandler? Disconnected;

    public async Task ConnectAsync(string endpoint, string displayName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var address = NormalizeEndpoint(endpoint);
        var name = CleanText(displayName, 64);
        if (name.Length == 0) throw new ArgumentException("Enter your online display name.", nameof(displayName));
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Connection? connection = null;
        CancellationTokenSource? connecting = null;
        try
        {
            Disconnect();
            var timeout = connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate) _connectingCancellation = timeout;
            timeout.CancelAfter(ConnectTimeout);
            StatusChanged?.Invoke(this, "Connecting to the online relay (a sleeping Render service can take a minute)...");
            var key = address.AbsoluteUri;
            var identity = _endpoint == address ? _identity : null;
            identity ??= Preferences.GetIdentity(key);
            if (identity is null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address, "v1/register"))
                { Content = JsonContent(new { displayName = name }) };
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
                var json = await ReadBoundedAsync(response.Content, 16 * 1024, timeout.Token).ConfigureAwait(false);
                identity = JsonSerializer.Deserialize<OnlineIdentity>(json, JsonOptions)
                    ?? throw new InvalidDataException("The online relay returned no identity.");
                if (identity.UserId == Guid.Empty || string.IsNullOrWhiteSpace(identity.Token) || identity.Token.Length > 512)
                    throw new InvalidDataException("The online relay returned an invalid identity.");
                // Save before connecting: a failed cold-start connection must not create another account on retry.
                Preferences.SetIdentity(key, identity);
            }
            _endpoint = address;
            _identity = identity;
            Preferences.Endpoint = key;
            Preferences.DisplayName = name;
            Preferences.Save();
            connection = new Connection();
            connection.Socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            connection.Socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            var socketAddress = new UriBuilder(new Uri(address, "ws"))
            { Scheme = address.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                timeout.Token.ThrowIfCancellationRequested();
                _connection = connection;
            }
            await connection.Socket.ConnectAsync(socketAddress, timeout.Token).ConfigureAwait(false);
            _ = ReceiveLoopAsync(connection);
            await RequestOnConnectionAsync(connection, "auth", new Dictionary<string, object?>
            {
                ["protocol"] = 1, ["userId"] = identity.UserId, ["token"] = identity.Token, ["displayName"] = name
            }, timeout.Token, ApplyFriendsSnapshot).ConfigureAwait(false);
            lock (_gate)
            {
                if (_connection != connection || connection.Lifetime.IsCancellationRequested)
                    throw new IOException("The online relay disconnected during authentication.");
                connection.Authenticated = true;
            }
            StatusChanged?.Invoke(this, "Connected to the online relay.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (connection is not null) EndConnection(connection, "The online relay connection timed out.");
            throw new TimeoutException("The online relay did not respond within 60 seconds. Try connecting again after it wakes up.");
        }
        catch
        {
            if (connection is not null) EndConnection(connection, "Could not connect to the online relay.");
            throw;
        }
        finally
        {
            lock (_gate) if (_connectingCancellation == connecting) _connectingCancellation = null;
            connecting?.Dispose();
            _connectLock.Release();
        }
    }

    public Task RefreshFriendsAsync(CancellationToken cancellationToken = default) =>
        FriendsCommandAsync("friends.list", null, cancellationToken);
    public Task RequestFriendAsync(string friendCode, CancellationToken cancellationToken = default) =>
        FriendsCommandAsync("friends.request", new() { ["friendCode"] = CleanText(friendCode, 80) }, cancellationToken);
    public Task AcceptFriendAsync(Guid userId, CancellationToken cancellationToken = default) =>
        FriendsCommandAsync("friends.accept", new() { ["userId"] = userId }, cancellationToken);
    public Task RemoveFriendAsync(Guid userId, CancellationToken cancellationToken = default) =>
        FriendsCommandAsync("friends.remove", new() { ["userId"] = userId }, cancellationToken);

    private async Task FriendsCommandAsync(string command, Dictionary<string, object?>? fields, CancellationToken cancellationToken) =>
        _ = await RequestAsync(command, fields, cancellationToken, ApplyFriendsSnapshot).ConfigureAwait(false);

    /// <summary>Sends one protocol command. Response callbacks run in receive order before later events.</summary>
    public Task<JsonElement> RequestAsync(string type, IReadOnlyDictionary<string, object?>? fields = null,
        CancellationToken cancellationToken = default) => RequestAsync(type, fields, cancellationToken, null);
    internal Task<JsonElement> RequestAsync(string type, IReadOnlyDictionary<string, object?>? fields,
        CancellationToken cancellationToken, Action<JsonElement>? onResponse)
    {
        var connection = RequireConnection();
        return RequestOnConnectionAsync(connection, type, fields, cancellationToken, onResponse);
    }

    private static async Task<JsonElement> RequestOnConnectionAsync(Connection connection, string type,
        IReadOnlyDictionary<string, object?>? fields, CancellationToken cancellationToken, Action<JsonElement>? onResponse)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Lifetime.Token);
        timeout.CancelAfter(type == "auth" ? ConnectTimeout : CommandTimeout);
        var requestId = Guid.NewGuid();
        var completion = NewCompletion<JsonElement>();
        var pending = new PendingRequest(completion, onResponse);
        if (!connection.Pending.TryAdd(requestId, pending)) throw new InvalidOperationException("Could not track online command.");
        try
        {
            var message = fields?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? new Dictionary<string, object?>();
            message["type"] = type;
            message["requestId"] = requestId;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            if (bytes.Length > MaximumMessageBytes) throw new InvalidOperationException("The online collaboration message is too large.");
            await connection.SendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try { await connection.Socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false); }
            finally { connection.SendLock.Release(); }
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !connection.Lifetime.IsCancellationRequested)
        { throw new TimeoutException("The online relay did not acknowledge the command in time."); }
        finally { connection.Pending.TryRemove(requestId, out _); }
    }

    public async Task<OnlineExportResult> SendExportAsync(Guid friendId, string kind, string name, byte[] payload,
        Action<CivilizationLanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var maximum = PayloadLimit(kind);
        if (payload.Length is < 1 || payload.Length > maximum) throw new InvalidDataException("The export exceeds the online package size limit.");
        var connection = RequireConnection();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(10));
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var completion = NewCompletion<OnlineExportResult>();
        var exportId = Guid.Empty;
        _ = await RequestAsync("export.offer", new Dictionary<string, object?>
        {
            ["userId"] = friendId, ["kind"] = kind, ["name"] = CleanText(name, 128),
            ["size"] = payload.Length, ["sha256"] = digest
        }, operation.Token, response =>
        {
            exportId = RequiredGuid(response, "exportId");
            if (!_exportResults.TryAdd(exportId, completion)) throw new InvalidOperationException("Could not track the online export.");
        }).ConfigureAwait(false);
        var completed = false;
        try
        {
            progress?.Invoke(new(CivilizationLanStatus.Submitted, "Uploading the export to your friend..."));
            using var upload = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            upload.CancelAfter(TransferTimeout);
            using var request = AuthorizedRequest(HttpMethod.Put, $"v1/exports/{exportId:D}");
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var uploaded = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, upload.Token).ConfigureAwait(false);
            await EnsureSuccessAsync(uploaded, upload.Token).ConfigureAwait(false);
            progress?.Invoke(new(CivilizationLanStatus.Awaiting, "Your friend is reviewing the export..."));
            var result = await completion.Task.WaitAsync(operation.Token).ConfigureAwait(false);
            completed = true;
            progress?.Invoke(new(ToLanStatus(result.Status), result.Message));
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("The export expired or the online relay disconnected before your friend finished reviewing it."); }
        finally
        {
            if (!completed && !completion.Task.IsCompleted && IsConnected)
            {
                try
                {
                    using var withdrawal = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    _ = await RequestAsync("export.cancel", new Dictionary<string, object?> { ["exportId"] = exportId }, withdrawal.Token).ConfigureAwait(false);
                }
                catch (Exception) { /* The relay also revokes offers on disconnection and expiry. */ }
            }
            _exportResults.TryRemove(exportId, out _);
        }
    }

    public async Task<byte[]> DownloadExportAsync(OnlineExportOffer offer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ValidateOffer(offer);
        var connection = RequireConnection();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, offer.CancellationToken, connection.Lifetime.Token);
        timeout.CancelAfter(TransferTimeout);
        using var request = AuthorizedRequest(HttpMethod.Get, $"v1/exports/{offer.ExportId:D}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, offer.Size, timeout.Token).ConfigureAwait(false);
        if (bytes.Length != offer.Size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(offer.Sha256)))
            throw new InvalidDataException("The received export does not match its advertised size and SHA-256 digest.");
        return bytes;
    }

    public async Task DecideExportAsync(Guid exportId, string status, string message = "", CancellationToken cancellationToken = default)
    {
        _ = ToLanStatus(status);
        await RequestAsync("export.decide", new Dictionary<string, object?>
        { ["exportId"] = exportId, ["status"] = status, ["message"] = CleanText(message, 500) }, cancellationToken).ConfigureAwait(false);
        RemoveIncomingOffer(exportId);
    }

    public void Disconnect()
    {
        Connection? connection;
        CancellationTokenSource? connecting;
        lock (_gate) { connection = _connection; connecting = _connectingCancellation; }
        try { connecting?.Cancel(); } catch (ObjectDisposedException) { }
        if (connection is not null) EndConnection(connection, "Disconnected from the online relay.");
    }

    private async Task ReceiveLoopAsync(Connection connection)
    {
        var message = "The online relay disconnected. Reconnect to resume online features.";
        try
        {
            var buffer = new byte[16 * 1024];
            while (!connection.Lifetime.IsCancellationRequested)
            {
                using var data = new MemoryStream();
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await connection.Socket.ReceiveAsync(buffer.AsMemory(), connection.Lifetime.Token).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    if (received.MessageType != WebSocketMessageType.Text || data.Length + received.Count > MaximumMessageBytes)
                        throw new InvalidDataException("The online relay sent an invalid or oversized message.");
                    data.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                using var document = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
                HandleMessage(connection, document.RootElement);
            }
        }
        catch (OperationCanceledException) when (connection.Lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (connection.Lifetime.IsCancellationRequested) { }
        catch (Exception error) { message = $"Online connection ended: {CleanText(error.Message, 250)}"; }
        finally { EndConnection(connection, message); }
    }

    private void HandleMessage(Connection connection, JsonElement message)
    {
        var type = message.GetProperty("type").GetString() ?? throw new InvalidDataException("Missing online message type.");
        if (type == "response")
        {
            var id = RequiredGuid(message, "requestId");
            if (!connection.Pending.TryGetValue(id, out var request)) return;
            if (!message.GetProperty("ok").GetBoolean())
            {
                var error = message.TryGetProperty("error", out var value) ? ErrorText(value) : "The online command was rejected.";
                request.Completion.TrySetException(new InvalidOperationException(error));
                return;
            }
            var data = message.GetProperty("data").Clone();
            try { request.OnResponse?.Invoke(data); request.Completion.TrySetResult(data); }
            catch (Exception error) { request.Completion.TrySetException(error); }
            return;
        }
        var payload = message.GetProperty("data").Clone();
        switch (type)
        {
            case "friends.changed": ApplyFriendsSnapshot(payload); break;
            case "collab.invite":
                var invite = Deserialize<OnlineCollaborationInvite>(payload);
                if (invite.SessionId == Guid.Empty || !Friends.Friends.Any(friend => friend.UserId == invite.From.UserId)) return;
                CollaborationInviteReceived?.Invoke(this, invite);
                break;
            case "export.offer":
                var offer = Deserialize<OnlineExportOffer>(payload);
                ValidateOffer(offer);
                if (!Friends.Friends.Any(friend => friend.UserId == offer.From.UserId)) return;
                foreach (var expired in _incomingOffers.Where(entry => entry.Value.Lifetime.IsCancellationRequested))
                    RemoveIncomingOffer(expired.Key);
                if (_incomingOffers.Count >= 2)
                {
                    _ = RejectBusyOfferAsync(offer.ExportId);
                    break;
                }
                var lifetime = CancellationTokenSource.CreateLinkedTokenSource(connection.Lifetime.Token);
                lifetime.CancelAfter(TimeSpan.FromMinutes(10));
                if (!_incomingOffers.TryAdd(offer.ExportId, new(offer.From.UserId, lifetime))) { lifetime.Dispose(); break; }
                ExportOfferReceived?.Invoke(this, offer with { CancellationToken = lifetime.Token });
                break;
            case "export.result":
                var result = Deserialize<OnlineExportResult>(payload);
                _ = ToLanStatus(result.Status);
                if (_exportResults.TryGetValue(result.ExportId, out var completion)) completion.TrySetResult(result);
                break;
            case "export.cancelled":
                RemoveIncomingOffer(RequiredGuid(payload, "exportId"));
                break;
            default: RelayEventReceived?.Invoke(this, new(type, payload)); break;
        }
    }

    private async Task RejectBusyOfferAsync(Guid id)
    {
        try { await DecideExportAsync(id, "denied", "The recipient is already reviewing two exports.").ConfigureAwait(false); }
        catch (Exception) { /* Disconnect or expiry already revokes the offer. */ }
    }

    private void ApplyFriendsSnapshot(JsonElement data)
    {
        var snapshot = Deserialize<OnlineFriendsSnapshot>(data);
        if (snapshot.User.UserId == Guid.Empty || snapshot.Friends is null || snapshot.Incoming is null || snapshot.Outgoing is null)
            throw new InvalidDataException("The relay returned an invalid friends snapshot.");
        Friends = snapshot;
        User = snapshot.User;
        var accepted = snapshot.Friends.Select(friend => friend.UserId).ToHashSet();
        foreach (var entry in _incomingOffers)
            if (!accepted.Contains(entry.Value.SenderId)) RemoveIncomingOffer(entry.Key);
        FriendsChanged?.Invoke(this, snapshot);
    }

    private void EndConnection(Connection connection, string message)
    {
        lock (_gate)
        {
            if (_connection != connection) return;
            _connection = null;
            connection.Authenticated = false;
        }
        connection.Lifetime.Cancel();
        connection.Socket.Abort();
        connection.Socket.Dispose();
        foreach (var pending in connection.Pending.Values) pending.Completion.TrySetException(new IOException(message));
        connection.Pending.Clear();
        foreach (var completion in _exportResults.Values) completion.TrySetException(new IOException(message));
        _exportResults.Clear();
        foreach (var offer in _incomingOffers.Keys) RemoveIncomingOffer(offer);
        var offline = Friends with { Friends = Friends.Friends.Select(friend => friend with { Online = false }).ToArray() };
        Friends = offline;
        // Event consumers marshal onto the UI thread. Their failures cannot create an unobserved receive task.
        try { Disconnected?.Invoke(this, EventArgs.Empty); } catch (Exception) { }
        try { FriendsChanged?.Invoke(this, offline); } catch (Exception) { }
        try { StatusChanged?.Invoke(this, message); } catch (Exception) { }
    }

    private void RemoveIncomingOffer(Guid id)
    {
        if (!_incomingOffers.TryRemove(id, out var offer)) return;
        offer.Lifetime.Cancel();
        offer.Lifetime.Dispose();
    }

    private Connection RequireConnection()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _connection is { Authenticated: true } connection ? connection
                : throw new InvalidOperationException("Connect to the online relay first.");
        }
    }

    private HttpRequestMessage AuthorizedRequest(HttpMethod method, string relativePath)
    {
        var identity = _identity ?? throw new InvalidOperationException("No online identity is connected.");
        var request = new HttpRequestMessage(method, new Uri(_endpoint ?? throw new InvalidOperationException("No relay endpoint is configured."), relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
        request.Headers.Add("X-User-Id", identity.UserId.ToString("D"));
        return request;
    }

    public static Uri NormalizeEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var address) || !string.IsNullOrEmpty(address.UserInfo) ||
            !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment) || address.AbsolutePath != "/")
            throw new ArgumentException("Enter the relay origin, such as https://your-service.onrender.com.", nameof(endpoint));
        var scheme = address.Scheme switch { "wss" => "https", "ws" => "http", var other => other };
        if (scheme != "https" && (scheme != "http" || !address.IsLoopback))
            throw new ArgumentException("Online relays require HTTPS. HTTP is allowed only for loopback development.", nameof(endpoint));
        return new UriBuilder(address) { Scheme = scheme, Path = "/" }.Uri;
    }

    private static ByteArrayContent JsonContent(object value)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string message;
        try
        {
            var bytes = await ReadBoundedAsync(response.Content, 16 * 1024, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            message = document.RootElement.TryGetProperty("error", out var error) ? ErrorText(error) : "The online relay rejected the request.";
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { message = "The online relay rejected the request."; }
        throw new HttpRequestException(CleanText(message, 500), null, response.StatusCode);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("The relay response exceeds its permitted size.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximum) throw new InvalidDataException("The relay response exceeds its permitted size.");
            output.Write(buffer, 0, read);
        }
    }

    internal static T Deserialize<T>(JsonElement element) => element.Deserialize<T>(JsonOptions)
        ?? throw new InvalidDataException("The relay returned an empty response.");
    internal static Guid RequiredGuid(JsonElement element, string property)
    {
        if (!element.GetProperty(property).TryGetGuid(out var id) || id == Guid.Empty)
            throw new InvalidDataException("The relay returned an invalid identifier.");
        return id;
    }
    private static string ErrorText(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "Online request failed."
        : element.TryGetProperty("message", out var message) ? message.GetString() ?? "Online request failed." : "Online request failed.";
    private static string CleanText(string? text, int maximum) => new((text ?? "").Trim().Where(character => !char.IsControl(character)).Take(maximum).ToArray());
    private static int PayloadLimit(string kind) => kind switch
    { "map" => MaximumMapBytes, "civilization" => MaximumCivilizationBytes, _ => throw new ArgumentException("Unknown export package kind.", nameof(kind)) };
    private static CivilizationLanStatus ToLanStatus(string status) => status switch
    { "installed" => CivilizationLanStatus.Installed, "denied" => CivilizationLanStatus.Denied, "failed" => CivilizationLanStatus.Failed,
        _ => throw new InvalidDataException("The online relay returned an invalid export decision.") };
    private static void ValidateOffer(OnlineExportOffer offer)
    {
        if (offer.ExportId == Guid.Empty || offer.From.UserId == Guid.Empty || offer.Size is < 1 || offer.Size > PayloadLimit(offer.Kind) ||
            offer.Sha256.Length != 64 || !offer.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The online relay returned an invalid export offer.");
    }
    private static TaskCompletionSource<T> NewCompletion<T>()
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _http.Dispose();
    }
    private sealed class Connection
    {
        public ClientWebSocket Socket { get; } = new();
        public CancellationTokenSource Lifetime { get; } = new();
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public ConcurrentDictionary<Guid, PendingRequest> Pending { get; } = new();
        public bool Authenticated { get; set; }
    }
    private sealed record PendingRequest(TaskCompletionSource<JsonElement> Completion, Action<JsonElement>? OnResponse);
    private sealed record IncomingOffer(Guid SenderId, CancellationTokenSource Lifetime);
}

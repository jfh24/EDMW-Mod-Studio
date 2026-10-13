using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EDMWMapDesigner.Collaboration;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Networking;

/// <summary>Exercises the desktop protocol against an isolated local Node relay.</summary>
internal static class OnlineVerification
{
    public static async Task RunAsync(string endpoint, string? restartControlDirectory = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            throw new ArgumentException("Online verification requires an isolated loopback relay.");
        await VerifyColdStartAsync();
        using var hostClient = new OnlineRelayClient(new OnlinePreferences { PersistIdentity = false });
        using var friendClient = new OnlineRelayClient(new OnlinePreferences { PersistIdentity = false });
        using var strangerClient = new OnlineRelayClient(new OnlinePreferences { PersistIdentity = false });
        await hostClient.ConnectAsync(endpoint, "Verification host");
        await friendClient.ConnectAsync(endpoint, "Verification friend");
        await strangerClient.ConnectAsync(endpoint, "Verification stranger");
        var hostId = hostClient.User!.UserId;
        var friendId = friendClient.User!.UserId;
        var hostCode = hostClient.User.FriendCode;
        var friendCode = friendClient.User.FriendCode;
        await ExpectRejectedAsync(() => strangerClient.SendExportAsync(hostId, "map", "Unsolicited", [1, 2, 3]));
        await hostClient.RequestFriendAsync(friendClient.User.FriendCode);
        await WaitUntilAsync(() => friendClient.Friends.Incoming.Any(profile => profile.UserId == hostId));
        await friendClient.AcceptFriendAsync(hostId);
        await WaitUntilAsync(() => hostClient.Friends.Friends.Any(profile => profile.UserId == friendId && profile.Online));

        using var host = new OnlineCollaborationSession(hostClient);
        using var friend = new OnlineCollaborationSession(friendClient);
        using var stranger = new OnlineCollaborationSession(strangerClient);
        var baseline = MapProject.CreateDefault(512);
        baseline.Name = "Online verification";
        baseline.SetCell(10, 10, TerrainKind.Grassland);
        baseline.SetCell(20, 20, TerrainKind.Grassland);
        MapProject hostMap = baseline.Clone(), friendMap = baseline.Clone();
        var gate = new object();
        var hostEdits = 0;
        var friendEdits = 0;
        host.ProjectReceived += (_, project) => { lock (gate) hostMap = project.Clone(); };
        friend.ProjectReceived += (_, project) => { lock (gate) friendMap = project.Clone(); };
        host.DeltaReceived += (_, delta) => { lock (gate) { delta.ApplyTo(hostMap); hostEdits++; } };
        friend.DeltaReceived += (_, delta) => { lock (gate) { delta.ApplyTo(friendMap); friendEdits++; } };
        var invites = Channel.CreateUnbounded<OnlineCollaborationInvite>();
        friendClient.CollaborationInviteReceived += (_, invite) => invites.Writer.TryWrite(invite);
        await host.CreateAsync(baseline);
        await ExpectRejectedAsync(() => stranger.JoinAsync(host.SessionId!.Value));
        await host.InviteAsync(friendId);
        var invitation = await invites.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var joined = await friend.JoinAsync(invitation.SessionId);
        Require(joined.GridSize == 512 && joined.Cells.SequenceEqual(baseline.Cells) &&
                joined.ComponentRoles.SequenceEqual(baseline.ComponentRoles) &&
                joined.ElevationMask.SequenceEqual(baseline.ElevationMask),
            "Full-sized desktop map and base64 masks did not survive joining.");
        var hostStroke = baseline.Clone();
        hostStroke.SetCell(10, 10, TerrainKind.Hills);
        hostStroke.ElevationMask[10 * baseline.GridSize + 10] = 25;
        var friendStroke = baseline.Clone();
        friendStroke.SetCell(20, 20, TerrainKind.Forest);
        await Task.WhenAll(host.SendDeltaAsync(MapDelta.Create(baseline, hostStroke)!),
            friend.SendDeltaAsync(MapDelta.Create(baseline, friendStroke)!));
        await WaitUntilAsync(() => { lock (gate) return hostEdits == 2 && friendEdits == 2; });
        lock (gate)
        {
            Require(hostMap[10, 10] == TerrainKind.Hills && hostMap[20, 20] == TerrainKind.Forest &&
                    hostMap.Cells.SequenceEqual(friendMap.Cells) &&
                    hostMap.ElevationMask.SequenceEqual(friendMap.ElevationMask),
                "Concurrent strokes were lost or desktop peers diverged.");
        }
        await ExpectRejectedAsync(() => hostClient.RequestAsync("collab.project", new Dictionary<string, object?>
        { ["sessionId"] = host.SessionId!.Value, ["project"] = baseline, ["baseRevision"] = 0 }));
        var canonical = await host.SynchronizeAsync();
        Require(canonical[10, 10] == TerrainKind.Hills && canonical[20, 20] == TerrainKind.Forest,
            "A stale full replacement erased accepted edits.");
        var cursor = new TaskCompletionSource<CollaboratorCursorUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.CursorReceived += (_, update) => cursor.TrySetResult(update);
        await friend.SendCursorAsync(new Point(20, 20));
        var position = await cursor.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(position.Id == friendId && position.Cell == new Point(20, 20), "Friend cursor identity was not preserved.");

        var resized = MapProject.CreateDefault(64);
        resized.SetCell(5, 5, TerrainKind.Grassland);
        await host.SendProjectAsync(resized);
        await WaitUntilAsync(() => { lock (gate) return friendMap.GridSize == 64; });
        await ExpectRejectedAsync(() => friend.SendDeltaAsync(MapDelta.Create(baseline, friendStroke)!));

        var offers = Channel.CreateUnbounded<OnlineExportOffer>();
        friendClient.ExportOfferReceived += (_, offer) => offers.Writer.TryWrite(offer);
        var bytes = new byte[1024 * 1024 + 37];
        Random.Shared.NextBytes(bytes);
        var sending = hostClient.SendExportAsync(friendId, "map", "Verification payload", bytes);
        var offer = await offers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        var received = await friendClient.DownloadExportAsync(offer);
        Require(received.SequenceEqual(bytes), "The downloaded export differs from the reviewed upload.");
        await friendClient.DecideExportAsync(offer.ExportId, "installed", "Verified test bytes; no game files changed.");
        Require((await sending.WaitAsync(TimeSpan.FromSeconds(10))).Status == "installed", "Installation status did not reach sender.");
        var denying = hostClient.SendExportAsync(friendId, "civilization", "Denied fixture", [3, 2, 1]);
        var deniedOffer = await offers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await friendClient.DecideExportAsync(deniedOffer.ExportId, "denied", "Recipient declined.");
        Require((await denying.WaitAsync(TimeSpan.FromSeconds(10))).Status == "denied", "Denial did not reach sender.");

        using var withdrawal = new CancellationTokenSource();
        var withdrawing = hostClient.SendExportAsync(friendId, "map", "Withdrawn fixture", [9, 8, 7], cancellationToken: withdrawal.Token);
        var withdrawnOffer = await offers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        withdrawal.Cancel();
        try { await withdrawing.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("A withdrawn export reported success."); }
        catch (OperationCanceledException) { }
        await WaitUntilAsync(() => withdrawnOffer.CancellationToken.IsCancellationRequested);

        hostClient.Disconnect();
        await WaitUntilAsync(() => !friend.IsConnected);
        await hostClient.ConnectAsync(endpoint, "Verification host reconnected");
        Require(hostClient.User!.UserId == hostId && hostClient.Friends.Friends.Any(profile => profile.UserId == friendId),
            "Reconnect lost the device identity or accepted friendship.");
        if (restartControlDirectory is not null)
        {
            Require(hostClient.Friends.FriendReceipts.Length == 1 && friendClient.Friends.FriendReceipts.Length == 1,
                "Both desktops must retain their signed friendship acceptance before reset.");
            hostClient.Disconnect();
            friendClient.Disconnect();
            strangerClient.Disconnect();
            await ResetRelayAsync(restartControlDirectory);
            await hostClient.ConnectAsync(endpoint, "Verification host recovered");
            Require(hostClient.User!.UserId == hostId && hostClient.User.FriendCode == hostCode,
                "The free relay failed to recover the original signed device identity and code.");
            Require(hostClient.Friends.Friends.Any(profile => profile.UserId == friendId && !profile.Online),
                "The first recovered desktop lost its offline contact or activated it without peer consent.");
            await ExpectRejectedAsync(() => hostClient.SendExportAsync(friendId, "map", "Unconfirmed restore", [1]));
            await friendClient.ConnectAsync(endpoint, "Verification friend recovered");
            Require(friendClient.User!.UserId == friendId && friendClient.User.FriendCode == friendCode,
                "The second device changed identity after the ephemeral filesystem was replaced.");
            await WaitUntilAsync(() => hostClient.Friends.Friends.Any(profile => profile.UserId == friendId && profile.Online));
            Require(friendClient.Friends.Friends.Any(profile => profile.UserId == hostId && profile.Online),
                "Mutual signed receipts did not restore the accepted friendship.");
        }
        var revoking = hostClient.SendExportAsync(friendId, "map", "Revoked fixture", [1, 2]);
        var revokedOffer = await offers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await hostClient.RemoveFriendAsync(friendId);
        Require((await revoking.WaitAsync(TimeSpan.FromSeconds(10))).Status == "failed", "Revocation did not fail the pending export.");
        await WaitUntilAsync(() => revokedOffer.CancellationToken.IsCancellationRequested);
        await WaitUntilAsync(() => !friendClient.Friends.Friends.Any(profile => profile.UserId == hostId));
        await ExpectRejectedAsync(() => hostClient.SendExportAsync(friendId, "map", "Revoked", [1]));
        if (restartControlDirectory is not null)
        {
            Require(hostClient.Friends.FriendReceipts.Length == 0 && friendClient.Friends.FriendReceipts.Length == 0,
                "Removing a friend did not clear the desktop's durable acceptance receipt.");
            hostClient.Disconnect();
            friendClient.Disconnect();
            await ResetRelayAsync(restartControlDirectory);
            await friendClient.ConnectAsync(endpoint, "Verification former friend");
            await hostClient.ConnectAsync(endpoint, "Verification host after removal");
            Require(hostClient.Friends.Friends.Length == 0 && friendClient.Friends.Friends.Length == 0,
                "A removed friendship reappeared after the free relay lost its files.");
            await ExpectRejectedAsync(() => hostClient.SendExportAsync(friendId, "map", "Removed after reset", [1]));
        }
        Console.WriteLine("ONLINE PASS: cold-start 503/HTML readiness and cancellation, desktop/Node authentication, friendship ACL, invitation-only rooms, 512-cell grid serialization, concurrent strokes, cursors, resize, bounded export bytes and approval status, reconnect and revocation" +
            (restartControlDirectory is null ? "." : "; free-service identity/contact recovery across two complete filesystem replacements."));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Online verification did not receive the expected event.");
            await Task.Delay(25);
        }
    }

    private static async Task ResetRelayAsync(string directory)
    {
        var control = Path.GetFullPath(directory);
        if (!Path.GetFileName(control).StartsWith("online-", StringComparison.Ordinal) ||
            Path.GetFileName(Path.GetDirectoryName(control)) != ".verification" || !Directory.Exists(control))
            throw new ArgumentException("Relay reset requires an isolated .verification/online-* control directory.");
        var request = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(Path.Combine(control, "restart.request"), request);
        var ready = Path.Combine(control, "restart.done");
        await WaitUntilAsync(() => File.Exists(ready) && File.ReadAllText(ready).Trim() == request);
    }

    private static async Task VerifyColdStartAsync()
    {
        using var serverLifetime = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var paths = new List<string>();
        var attempts = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task RespondAsync(bool alwaysSleeping)
        {
            try
            {
                while (!serverLifetime.IsCancellationRequested)
                {
                    using var peer = await listener.AcceptTcpClientAsync(serverLifetime.Token);
                    await using var stream = peer.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    var first = await reader.ReadLineAsync(serverLifetime.Token) ?? throw new IOException("Missing test request.");
                    var requestPath = first.Split(' ')[1];
                    paths.Add(requestPath);
                    while (await reader.ReadLineAsync(serverLifetime.Token) is { Length: > 0 }) { }
                    var step = Interlocked.Increment(ref attempts);
                    var status = alwaysSleeping || step == 1 ? "503 Service Unavailable"
                        : requestPath == "/v1/register" ? "400 Bad Request" : "200 OK";
                    var body = requestPath == "/v1/register" ? "{\"error\":\"Wake verification complete\"}"
                        : alwaysSleeping || step <= 2 ? "<html>Service starting</html>"
                        : "{\"status\":\"ok\",\"protocol\":1}";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {(body.StartsWith('<') ? "text/html" : "application/json")}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, serverLifetime.Token);
                    await stream.WriteAsync(bytes, serverLifetime.Token);
                    started.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (serverLifetime.IsCancellationRequested) { }
            catch (SocketException) when (serverLifetime.IsCancellationRequested) { }
        }
        var responding = RespondAsync(false);
        try
        {
            using (var coldClient = new OnlineRelayClient(new OnlinePreferences { PersistIdentity = false }))
            {
                try
                {
                    await coldClient.ConnectAsync(address, "Cold start verification");
                    throw new InvalidOperationException("The simulated registration should be rejected.");
                }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest) { }
            }
            Require(paths.SequenceEqual(new[] { "/health", "/health", "/health", "/v1/register" }),
                "The desktop did not wait through 503 and HTML wake responses before registering.");
        }
        finally
        {
            serverLifetime.Cancel();
            listener.Stop();
            await responding;
        }

        using var sleepingLifetime = new CancellationTokenSource();
        var sleepingListener = new TcpListener(IPAddress.Loopback, 0);
        sleepingListener.Start();
        var sleepingAddress = $"http://127.0.0.1:{((IPEndPoint)sleepingListener.LocalEndpoint).Port}";
        var sleepObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sleepingServer = Task.Run(async () =>
        {
            try
            {
                while (!sleepingLifetime.IsCancellationRequested)
                {
                    using var peer = await sleepingListener.AcceptTcpClientAsync(sleepingLifetime.Token);
                    await using var stream = peer.GetStream();
                    var buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer, sleepingLifetime.Token);
                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, sleepingLifetime.Token);
                    sleepObserved.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (sleepingLifetime.IsCancellationRequested) { }
            catch (SocketException) when (sleepingLifetime.IsCancellationRequested) { }
        });
        try
        {
            using var cancelledClient = new OnlineRelayClient(new OnlinePreferences { PersistIdentity = false });
            using var cancellation = new CancellationTokenSource();
            var connecting = cancelledClient.ConnectAsync(sleepingAddress, "Cancelled wake", cancellation.Token);
            await sleepObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            try { await connecting.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Cancelling wake did not stop the connection."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Require(!cancelledClient.IsConnected, "A cancelled wake retained an authenticated connection.");
        }
        finally
        {
            sleepingLifetime.Cancel();
            sleepingListener.Stop();
            await sleepingServer;
        }
    }

    private static async Task ExpectRejectedAsync(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or UnauthorizedAccessException)
        { return; }
        throw new InvalidOperationException("The relay accepted an unauthorized or incompatible operation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.Threading.Channels;
using EDMWMapDesigner.Collaboration;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Networking;

/// <summary>Exercises the desktop protocol against an isolated local Node relay.</summary>
internal static class OnlineVerification
{
    public static async Task RunAsync(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            throw new ArgumentException("Online verification requires an isolated loopback relay.");
        using var hostClient = new OnlineRelayClient(new OnlinePreferences { PersistIdentity = false });
        using var friendClient = new OnlineRelayClient(new OnlinePreferences { PersistIdentity = false });
        using var strangerClient = new OnlineRelayClient(new OnlinePreferences { PersistIdentity = false });
        await hostClient.ConnectAsync(endpoint, "Verification host");
        await friendClient.ConnectAsync(endpoint, "Verification friend");
        await strangerClient.ConnectAsync(endpoint, "Verification stranger");
        var hostId = hostClient.User!.UserId;
        var friendId = friendClient.User!.UserId;
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
        var revoking = hostClient.SendExportAsync(friendId, "map", "Revoked fixture", [1, 2]);
        var revokedOffer = await offers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await hostClient.RemoveFriendAsync(friendId);
        Require((await revoking.WaitAsync(TimeSpan.FromSeconds(10))).Status == "failed", "Revocation did not fail the pending export.");
        await WaitUntilAsync(() => revokedOffer.CancellationToken.IsCancellationRequested);
        await WaitUntilAsync(() => !friendClient.Friends.Friends.Any(profile => profile.UserId == hostId));
        await ExpectRejectedAsync(() => hostClient.SendExportAsync(friendId, "map", "Revoked", [1]));
        Console.WriteLine("ONLINE PASS: desktop/Node authentication, friendship ACL, invitation-only rooms, 512-cell grid serialization, concurrent strokes, cursors, resize, bounded export bytes and approval status, reconnect and revocation.");
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

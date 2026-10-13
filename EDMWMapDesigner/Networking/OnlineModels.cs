using System.Text.Json.Serialization;

namespace EDMWMapDesigner.Networking;

public sealed record OnlineProfile(Guid UserId, string DisplayName, string FriendCode, bool Online = false);
public sealed record OnlineFriendsSnapshot(OnlineProfile User, OnlineProfile[] Friends,
    OnlineProfile[] Incoming, OnlineProfile[] Outgoing);
public sealed record OnlineCollaborationInvite(Guid SessionId, OnlineProfile From, string MapName);
public sealed record OnlineExportOffer(Guid ExportId, OnlineProfile From, string Kind, string Name, int Size, string Sha256)
{
    // Installation must remain cancellable when a friendship or connection ends.
    [JsonIgnore] public CancellationToken CancellationToken { get; init; }
}
public sealed record OnlineExportResult(Guid ExportId, string Status, string Message);

internal sealed record OnlineIdentity(Guid UserId, string Token, string FriendCode, string DisplayName);
internal sealed record OnlineRelayEvent(string Type, System.Text.Json.JsonElement Data);

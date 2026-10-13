using System.Text.Json.Serialization;

namespace EDMWMapDesigner.Networking;

public sealed record OnlineProfile(Guid UserId, string DisplayName, string FriendCode, bool Online = false);
public sealed record OnlineFriendsSnapshot(OnlineProfile User, OnlineProfile[] Friends,
    OnlineProfile[] Incoming, OnlineProfile[] Outgoing)
{
    // Relay-signed acceptances let both devices restore their friendship after a free service restart.
    public string[] FriendReceipts { get; init; } = [];
}
public sealed record OnlineCollaborationInvite(Guid SessionId, OnlineProfile From, string MapName);
public sealed record OnlineExportOffer(Guid ExportId, OnlineProfile From, string Kind, string Name, int Size, string Sha256)
{
    // Installation must remain cancellable when a friendship or connection ends.
    [JsonIgnore] public CancellationToken CancellationToken { get; init; }
}
public sealed record OnlineExportResult(Guid ExportId, string Status, string Message);

internal sealed record OnlineIdentity(Guid UserId, string Token, string FriendCode, string DisplayName)
{
    public string[] FriendReceipts { get; init; } = [];
}
internal sealed record OnlineRelayEvent(string Type, System.Text.Json.JsonElement Data);

internal static class OnlineCredentialLimits
{
    internal static string[] ValidateReceipts(string[]? receipts)
    {
        receipts ??= [];
        if (receipts.Length > 256 || receipts.Any(receipt => receipt is null || receipt.Length is < 1 or > 2048))
            throw new InvalidDataException("The saved online friendship receipts exceed the supported limits.");
        return receipts.ToArray();
    }
}

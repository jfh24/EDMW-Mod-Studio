using System.Net;
using EDMWMapDesigner.Civilizations;
using EDMWMapDesigner.Networking;

namespace EDMWMapDesigner.Generation;

public sealed record MapLanOffer(MapLanPackage Package, string SenderAddress, DateTimeOffset ReceivedAtUtc);

/// <summary>Standalone map exports with their own discovery channel, independent of collaboration.</summary>
public sealed class MapLanTransfer : IDisposable
{
    public const int DefaultPort = 42821;
    public const int DefaultDiscoveryPort = 42822;
    public const int ProtocolVersion = 1;
    public const int HeaderSize = 48;
    public const int MaxPayloadBytes = 128 * 1024 * 1024;
    public const string ProtocolMagic = "EDMWMAP1";
    private readonly DataLanTransfer<MapLanPackage, MapLanOffer> _transport;

    public int Port => _transport.Port;
    public int DiscoveryPort => _transport.DiscoveryPort;
    public bool IsListening => _transport.IsListening;
    internal string InstanceId => _transport.InstanceId;
    internal Task<IReadOnlyList<CivilizationLanPeer>> DiscoverForShareAsync(string? excludedInstanceId, CancellationToken cancellationToken) =>
        _transport.DiscoverAsync(null, cancellationToken, excludedInstanceId);

    public MapLanTransfer(Func<MapLanOffer, CancellationToken, Task<CivilizationLanDecision>> onOffer,
        string? displayName = null, int port = DefaultPort, int discoveryPort = DefaultDiscoveryPort, IPAddress? bindAddress = null)
    {
        _transport = new(onOffer, MapLanPackageCodec.Encode, MapLanPackageCodec.Decode,
            (package, sender, received) => new(package, sender, received), ProtocolMagic, ProtocolVersion,
            MaxPayloadBytes, displayName, port, discoveryPort, bindAddress);
    }

    public void Start() => _transport.Start();
    public void Stop() => _transport.Stop();
    public void Dispose() => _transport.Dispose();
    public Task<IReadOnlyList<CivilizationLanPeer>> DiscoverAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default) =>
        _transport.DiscoverAsync(duration, cancellationToken);
    public Task<CivilizationLanResult> SendAsync(MapLanPackage package, CivilizationLanPeer peer,
        Action<CivilizationLanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        _transport.SendAsync(package, peer, progress, cancellationToken);
}

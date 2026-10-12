using System.Net;
using EDMWMapDesigner.Networking;

namespace EDMWMapDesigner.Civilizations;

public enum CivilizationLanStatus { Submitted = 1, Awaiting = 2, Installed = 3, Denied = 4, Failed = 5 }
public sealed record CivilizationLanPeer(string Name, string Address, int Port = CivilizationLanTransfer.DefaultPort);
public sealed record CivilizationLanProgress(CivilizationLanStatus Status, string Message);
public sealed record CivilizationLanResult(CivilizationLanStatus Status, string Message);
public sealed record CivilizationLanDecision(CivilizationLanStatus Status, string Message = "");
public sealed record CivilizationLanOffer(CivilizationInstallerPackage Package, string SenderAddress, DateTimeOffset ReceivedAtUtc);

/// <summary>Standalone civilization exports using the shared bounded LAN transport.</summary>
public sealed class CivilizationLanTransfer : IDisposable
{
    public const int DefaultPort = 42819;
    public const int DefaultDiscoveryPort = 42820;
    public const int ProtocolVersion = 1;
    public const int HeaderSize = 48;
    public const int MaxPayloadBytes = 128 * 1024 * 1024;
    public const string ProtocolMagic = "EDMWLAN1";
    private readonly DataLanTransfer<CivilizationInstallerPackage, CivilizationLanOffer> _transport;

    public int Port => _transport.Port;
    public int DiscoveryPort => _transport.DiscoveryPort;
    public bool IsListening => _transport.IsListening;

    public CivilizationLanTransfer(Func<CivilizationLanOffer, CancellationToken, Task<CivilizationLanDecision>> onOffer,
        string? displayName = null, int port = DefaultPort, int discoveryPort = DefaultDiscoveryPort, IPAddress? bindAddress = null)
    {
        _transport = new(onOffer, CivilizationInstallerPackageCodec.Encode, CivilizationInstallerPackageCodec.Decode,
            (package, sender, received) => new(package, sender, received), ProtocolMagic, ProtocolVersion,
            MaxPayloadBytes, displayName, port, discoveryPort, bindAddress);
    }

    public void Start() => _transport.Start();
    public void Stop() => _transport.Stop();
    public void Dispose() => _transport.Dispose();
    public Task<IReadOnlyList<CivilizationLanPeer>> DiscoverAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default) =>
        _transport.DiscoverAsync(duration, cancellationToken);
    public Task<CivilizationLanResult> SendAsync(CivilizationInstallerPackage package, CivilizationLanPeer peer,
        Action<CivilizationLanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        _transport.SendAsync(package, peer, progress, cancellationToken);
    internal static bool IsLanAddress(IPAddress address) => DataLanTransfer<CivilizationInstallerPackage, CivilizationLanOffer>.IsLanAddress(address);
}

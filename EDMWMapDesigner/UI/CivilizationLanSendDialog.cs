using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

internal sealed class CivilizationLanSendDialog : LanExportStatusDialog
{
    public CivilizationLanSendDialog(CivilizationInstallerPackage package, CivilizationLanTransfer transfer,
        bool verificationMode = false, Func<CancellationToken, Task<CivilizationLanResult>>? installLocal = null)
        : base("Export civilization edits over LAN", PackageName(package), Discover(transfer),
            (peer, progress, cancellationToken) => transfer.SendAsync(package, peer, progress, cancellationToken),
            verificationMode, installLocal)
    {
    }

    private static string PackageName(CivilizationInstallerPackage package) =>
        (package ?? throw new ArgumentNullException(nameof(package))).ProjectName;

    private static Func<CancellationToken, Task<IReadOnlyList<CivilizationLanPeer>>> Discover(CivilizationLanTransfer transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        return cancellationToken => transfer.DiscoverAsync(null, cancellationToken);
    }
}

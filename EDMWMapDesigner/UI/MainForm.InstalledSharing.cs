using EDMWMapDesigner.Civilizations;
using EDMWMapDesigner.Generation;
using EDMWMapDesigner.Networking;

namespace EDMWMapDesigner.UI;

public sealed partial class MainForm
{
    private bool _sharingInstalledContent;

    private sealed record InstalledShareTransport(IDisposable Lifetime,
        Func<CancellationToken, Task<IReadOnlyList<CivilizationLanPeer>>> Discover,
        Func<CivilizationLanPeer, Action<CivilizationLanProgress>, CancellationToken, Task<CivilizationLanResult>> Send) : IDisposable
    {
        public void Dispose() => Lifetime.Dispose();
    }

    private Task ShareInstalledMapAsync(CustomMapInstallation map, string listedGamePath, bool lan, bool friends)
    {
        var hidePreview = _hideMapPreviewToggle.Checked;
        return ShareInstalledContentAsync(map.Name, listedGamePath, lan, friends, "map",
            () => CustomMapInstallationCatalog.CreateSharePackage(map, hidePreview), MapLanPackageCodec.Encode,
            package =>
            {
                var transfer = new MapLanTransfer(ReviewIncomingMapAsync, Environment.MachineName, _mapLanPreferences.Port);
                return new(transfer, token => transfer.DiscoverForShareAsync(_mapLanTransfer?.InstanceId, token),
                    (peer, progress, token) => transfer.SendAsync(package, peer, progress, token));
            });
    }

    private Task ShareInstalledCivilizationAsync(string listedGamePath, CivilizationInstalledPackage installed, bool lan, bool friends) =>
        ShareInstalledContentAsync(installed.ProjectName, listedGamePath, lan, friends, "civilization",
            () => CivilizationPackageManager.CreateSharePackage(listedGamePath, installed.PackageId),
            CivilizationInstallerPackageCodec.Encode, package =>
            {
                var transfer = new CivilizationLanTransfer(ReviewIncomingCivilizationsAsync, Environment.MachineName, _civilizationLanPreferences.Port);
                return new(transfer, token => transfer.DiscoverForShareAsync(_civilizationLanTransfer?.InstanceId, token),
                    (peer, progress, token) => transfer.SendAsync(package, peer, progress, token));
            });

    private async Task ShareInstalledContentAsync<T>(string name, string listedGamePath, bool lan, bool friends,
        string kind, Func<T> prepare, Func<T, byte[]> encode, Func<T, InstalledShareTransport> createTransport)
    {
        if (_closingDesigner || _sharingInstalledContent || _preparingOnlineExport || _preparingMapLanExport || (!lan && !friends)) return;
        _sharingInstalledContent = true;
        _preparingOnlineExport = _preparingMapLanExport = true;
        try
        {
            var gamePath = FindGamePath();
            if (gamePath is null || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(listedGamePath)), StringComparison.OrdinalIgnoreCase))
            {
                _refreshCustomMaps?.Invoke();
                _civilizationInstalledEditsPanel?.RefreshInstalledPackages();
                throw new InvalidOperationException("The selected game folder changed. Refresh Home and choose the installed item again.");
            }

            // Choose friends before any network send so cancelling the picker
            // cancels this entire action, including LAN when both were selected.
            OnlineProfile[] selectedFriends = [];
            if (friends)
            {
                var selection = ChooseOnlineRecipients(name);
                if (selection is null || selection.Length == 0) return;
                selectedFriends = selection;
            }
            _status.Text = "Preparing " + name + " for sharing…";
            var export = await Task.Run(() =>
            {
                var package = prepare();
                return (Package: package, Bytes: encode(package));
            }, _onlineLifetime.Token);
            if (_closingDesigner || IsDisposed) return;

            using var transport = lan ? createTransport(export.Package) : null;
            string? discoveryNotice = null;
            async Task<IReadOnlyList<CivilizationLanPeer>> Discover(CancellationToken token)
            {
                var peers = selectedFriends.Select(friend => new CivilizationLanPeer(
                    friend.DisplayName + " (Friends)", friend.UserId.ToString(), 0)).ToList();
                if (transport is not null)
                {
                    try
                    {
                        var computers = await transport.Discover(token);
                        peers.AddRange(computers.Select(peer => peer with { Name = peer.Name + " (LAN)" }));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        // A LAN search failure must not discard chosen friends.
                        discoveryNotice = "LAN search failed: " + error.Message;
                    }
                }
                return peers;
            }
            async Task<CivilizationLanResult> Send(CivilizationLanPeer peer, Action<CivilizationLanProgress> progress, CancellationToken token)
            {
                if (peer.Port != 0) return await transport!.Send(peer, progress, token);
                var result = await _online.SendExportAsync(Guid.Parse(peer.Address), kind, name, export.Bytes, progress, token);
                return new(ParseOnlineExportStatus(result.Status), result.Message);
            }

            using var dialog = new LanExportStatusDialog("Share installed " + (kind == "map" ? "map" : "civilization edits"),
                name, Discover, Send, onlineFriends: !lan, recipientLabel: "Recipient", discoveryNotice: () => discoveryNotice);
            // Already-installed content is offered without installing it again locally.
            dialog.ShowDialog(this);
            _status.Text = "Sharing " + name + " finished.";
        }
        catch (OperationCanceledException) when (_onlineLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closingDesigner && !IsDisposed)
                MedievalDialog.Show(this, error.Message, "Share installed content", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _sharingInstalledContent = false;
            _preparingOnlineExport = _preparingMapLanExport = false;
        }
    }
}

using EDMWMapDesigner.Civilizations;
using EDMWMapDesigner.Collaboration;
using EDMWMapDesigner.Generation;
using EDMWMapDesigner.Networking;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.UI;

public sealed partial class MainForm
{
    private readonly OnlinePreferences _onlinePreferences = OnlinePreferences.Load();
    private readonly OnlineRelayClient _online;
    private readonly OnlineCollaborationSession _onlineCollaboration;
    private readonly CancellationTokenSource _onlineLifetime = new();
    private readonly Dictionary<Guid, OnlineCollaborationInvite> _onlineInvitations = [];
    private ToolStripButton? _friendsButton;
    private bool _preparingOnlineExport;
    private bool _joiningOnlineMap;
    private MapDelta? _onlineSubmittedDelta;
    private MapProject? _onlineSubmittedProject;
    private bool _discardPendingOnlineReplacement;
    private sealed class OnlineReplacementRejectedException(string message) : Exception(message);
    private bool MapSessionConnected => _collaboration.IsConnected || _onlineCollaboration.IsConnected;

    private void InitializeOnlineSharing()
    {
        _onlineCollaboration.CanonicalProjectReceived += (_, update) => ApplyOnlineDocument(() => ApplyCanonicalOnlineProject(update));
        _onlineCollaboration.CanonicalDeltaReceived += (_, update) => ApplyOnlineDocument(() => ApplyCanonicalOnlineDelta(update));
        _onlineCollaboration.CursorReceived += (_, cursor) => PostOnlineUi(() => _canvas.SetRemoteCursor(cursor));
        _onlineCollaboration.CollaboratorConnected += (_, collaborator) => PostOnlineUi(() =>
        {
            _connectedCollaborators[collaborator.Id] = collaborator.Author;
            UpdateCollaborationPanel();
        });
        _onlineCollaboration.CollaboratorDisconnected += (_, id) => PostOnlineUi(() =>
        {
            _canvas.RemoveRemoteCursor(id);
            _connectedCollaborators.Remove(id);
            UpdateCollaborationPanel();
        });
        _onlineCollaboration.StatusChanged += (_, message) => PostOnlineUi(() =>
        {
            if (!MapSessionConnected) ClearOnlineMapState();
            _status.Text = message;
            UpdateCollaborationPanel();
        });
        _online.FriendsChanged += (_, snapshot) => PostOnlineUi(() =>
        {
            var friends = snapshot.Friends.Select(friend => friend.UserId).ToHashSet();
            foreach (var invitation in _onlineInvitations.Values.Where(invite => !friends.Contains(invite.From.UserId)).ToArray())
                _onlineInvitations.Remove(invitation.SessionId);
            UpdateFriendsButton();
        });
        _online.StatusChanged += (_, message) => PostOnlineUi(() =>
        {
            if (!_online.IsConnected)
            {
                _onlineInvitations.Clear();
                if (!_collaboration.IsConnected) ClearOnlineMapState();
            }
            _status.Text = message;
            UpdateFriendsButton();
            UpdateCollaborationPanel();
        });
        _online.CollaborationInviteReceived += (_, invitation) => PostOnlineUi(() =>
        {
            _onlineInvitations[invitation.SessionId] = invitation;
            UpdateFriendsButton();
            _status.Text = $"{invitation.From.DisplayName} invited you to {invitation.MapName}. Open Friends to join.";
        });
        _online.ExportOfferReceived += (_, offer) => _ = ReviewOnlineExportAsync(offer);
    }

    private void ApplyOnlineDocument(Action action)
    {
        if (_closingDesigner || IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) Invoke(() => { if (!_closingDesigner && !IsDisposed) action(); });
            else action();
        }
        catch (InvalidOperationException) when (_closingDesigner || IsDisposed || !IsHandleCreated) { }
    }

    private void ApplyCanonicalOnlineDelta(OnlineCanonicalDelta update)
    {
        var result = OnlineMapRebase.Delta(_lastSharedProject ?? _project, _project,
            update.Delta, _onlineSubmittedDelta, update.IsLocal);
        if (!update.IsLocal && _strokeSnapshot is { } stroke && stroke.GridSize == update.Delta.GridSize)
        {
            // A stroke's undo should restore remote edits that arrived during
            // painting instead of undoing the collaborator's independent work.
            try { update.Delta.ApplyTo(stroke); }
            catch (InvalidDataException) { _strokeSnapshot = result.Canonical.Clone(); }
        }
        if (update.IsLocal) _onlineSubmittedDelta = null;
        ShowRebasedOnlineProject(result, fullProject: false, isLocal: update.IsLocal);
    }

    private void ApplyCanonicalOnlineProject(OnlineCanonicalProject update)
    {
        if (_joiningOnlineMap || _discardPendingOnlineReplacement)
        {
            if (_joiningOnlineMap) _projectPath = null;
            _strokeSnapshot = null;
            ApplyRemoteProject(update.Project);
            return;
        }
        var result = OnlineMapRebase.Project(_lastSharedProject ?? _project, _project,
            update.Project, _onlineSubmittedProject, update.IsLocal);
        if (!update.IsLocal && _strokeSnapshot is not null)
            _strokeSnapshot = result.Visible.GridSize == result.Canonical.GridSize ? result.Canonical.Clone() : null;
        if (update.IsLocal) _onlineSubmittedProject = null;
        ShowRebasedOnlineProject(result, fullProject: true, isLocal: update.IsLocal);
    }

    private async Task CancelRejectedOnlineReplacementAsync(string message = "The shared map changed while replacing it. Replacement cancelled; try again.")
    {
        _onlineSubmittedProject = null;
        _onlineSubmittedDelta = null;
        _collaborationFullProjectPending = false;
        _collaborationUpdatePending = false;
        _discardPendingOnlineReplacement = true;
        try
        {
            _ = await _onlineCollaboration.SynchronizeAsync();
        }
        catch (Exception)
        {
            _onlineCollaboration.Stop();
            throw new OnlineReplacementRejectedException("Shared map replacement cancelled. The map could not be refreshed; reconnect before editing.");
        }
        finally { _discardPendingOnlineReplacement = false; }
        throw new OnlineReplacementRejectedException(message);
    }

    private void ShowRebasedOnlineProject(OnlineMapRebaseResult result, bool fullProject, bool isLocal)
    {
        _applyingRemoteProject = true;
        try
        {
            var resized = _project.GridSize != result.Visible.GridSize;
            _project = result.Visible;
            _lastSharedProject = result.Canonical;
            _canvas.Project = _project;
            _nameBox.Text = _project.Name;
            _authorBox.Text = _project.Author;
            _gridSize.Value = _project.GridSize;
            _variance.Value = _project.Variance;
            _varianceValue.Text = $"{_project.Variance}%";
            RefreshGenerationSeed();
            if (resized)
            {
                _canvas.ResetView();
                ResetManualDrawingTools();
                _strokeSnapshot = null;
            }
            if (fullProject && !isLocal || resized || result.DroppedPending)
            {
                _undo.Clear();
                _redo.Clear();
            }
            if (isLocal || result.DroppedPending) _collaborationFullProjectPending = false;
            if (result.Visible.GridSize != result.Canonical.GridSize) _collaborationFullProjectPending = true;
            _collaborationUpdatePending = result.HasPending;
            if (result.HasPending) QueueCollaborationUpdate();
            MarkDirty();
            UpdateAnalysis();
            _status.Text = result.DroppedPending
                ? "The shared map replacement invalidated unshared edits; those edits were cleared."
                : result.HasPending ? "Shared map updated. Your latest edits are awaiting confirmation." : "Shared map updated.";
        }
        finally { _applyingRemoteProject = false; }
    }

    private void PostOnlineUi(Action action)
    {
        if (_closingDesigner || IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() => { if (!_closingDesigner && !IsDisposed) action(); });
        }
        catch (InvalidOperationException) when (_closingDesigner || IsDisposed || !IsHandleCreated) { }
    }

    private void UpdateFriendsButton()
    {
        if (_friendsButton is null) return;
        var pending = _onlineInvitations.Count + (_online.IsConnected ? _online.Friends.Incoming.Length : 0);
        _friendsButton.Text = pending > 0 ? $"Friends ({pending})" : "Friends";
        _friendsButton.ToolTipText = _online.IsConnected
            ? $"Online as {_online.User?.DisplayName}. {pending} pending friend requests or map invitations. Manage friends and exports."
            : "Connect to your online friends, share a map, and manage requests";
        _centerToolbarActions?.Invoke();
    }

    private void ShowFriends()
    {
        if (_closingDesigner) return;
        using var dialog = new FriendsDialog(_online, _onlinePreferences,
            () => _onlineInvitations.Values.ToArray(),
            () => _activeView == AppView.MapGenerator,
            () => _onlineCollaboration.IsConnected,
            InviteFriendToMapAsync, JoinOnlineMapAsync, LeaveOnlineMapAsync,
            () => _collaboration.IsConnected ? "Leave the LAN session before inviting friends."
                : _onlineCollaboration.IsConnected && !_onlineCollaboration.IsHosting ? "Only the map host can invite friends." : null);
        dialog.ShowDialog(this);
    }

    private async Task InviteFriendToMapAsync(Guid friendId)
    {
        if (_activeView != AppView.MapGenerator) throw new InvalidOperationException("Open the Map Generator before inviting a friend.");
        if (_collaboration.IsConnected) throw new InvalidOperationException("Leave the LAN session before starting online collaboration.");
        if (!_onlineCollaboration.IsConnected)
        {
            await _onlineCollaboration.CreateAsync(_project.Clone());
            _lastSharedProject = _project.Clone();
            _collaborationFullProjectPending = false;
            _connectedCollaborators.Clear();
            _hostLanAddress = null;
        }
        if (!_onlineCollaboration.IsHosting) throw new InvalidOperationException("Only the map host can invite friends.");
        await _onlineCollaboration.InviteAsync(friendId);
        UpdateCollaborationPanel();
        _status.Text = "Map invitation sent. Your friend can join from Friends.";
    }

    private async Task JoinOnlineMapAsync(Guid sessionId)
    {
        if (MapSessionConnected) throw new InvalidOperationException("Leave the current map session before joining another.");
        if (!ConfirmDiscard()) return;
        _joiningOnlineMap = true;
        try { _ = await _onlineCollaboration.JoinAsync(sessionId); }
        finally { _joiningOnlineMap = false; }
        _onlineInvitations.Remove(sessionId);
        _hostLanAddress = null;
        UpdateFriendsButton();
        ShowView(AppView.MapGenerator, confirm: false);
        UpdateCollaborationPanel();
        _status.Text = "Joined online map collaboration.";
    }

    private async Task LeaveOnlineMapAsync()
    {
        await _onlineCollaboration.StopAsync();
        ClearOnlineMapState();
        UpdateCollaborationPanel();
    }

    private void ClearOnlineMapState()
    {
        if (_collaboration.IsConnected) return;
        _collaborationSyncTimer.Stop();
        _collaborationUpdatePending = false;
        _collaborationFullProjectPending = false;
        foreach (var id in _connectedCollaborators.Keys.ToArray()) _canvas.RemoveRemoteCursor(id);
        _connectedCollaborators.Clear();
        _lastSharedProject = null;
        _onlineSubmittedDelta = null;
        _onlineSubmittedProject = null;
        _hostLanAddress = null;
    }

    private OnlineProfile[]? ChooseOnlineRecipients(string packageName)
    {
        if (!_online.IsConnected) ShowFriends();
        if (!_online.IsConnected || _closingDesigner) return null;
        using var dialog = new OnlineFriendPickerDialog(packageName, _online.Friends.Friends.Where(friend => friend.Online).ToArray());
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedFriends : null;
    }

    private async Task ExportMapToFriendsAsync()
    {
        if (_closingDesigner || _preparingOnlineExport) return;
        var gamePath = FindGamePath();
        if (gamePath is null) { _status.Text = "Choose the EDMW game folder in Settings first."; return; }
        var friends = ChooseOnlineRecipients(_project.Name);
        if (friends is null) return;
        var project = _project.Clone();
        var hidePreview = _hideMapPreviewToggle.Checked;
        _preparingOnlineExport = true;
        try
        {
            _status.Text = "Preparing export to friends…";
            var package = await Task.Run(() => PrepareMapLanPackage(project, gamePath, hidePreview), _onlineLifetime.Token);
            if (_closingDesigner || IsDisposed) return;
            ShowOnlineExportStatus(package.MapName, "map", MapLanPackageCodec.Encode(package), friends,
                async token =>
                {
                    var result = await InstallLocalMapForLanAsync(package, gamePath, token);
                    if (result.Status == CivilizationLanStatus.Installed) RefreshLocalLanInstall(civilizations: false);
                    return result;
                });
        }
        catch (OperationCanceledException) when (_onlineLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closingDesigner && !IsDisposed)
                MedievalDialog.Show(this, error.Message, "Export to friends", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _preparingOnlineExport = false; }
    }

    private void SendCivilizationsToFriends(CivilizationInstallerPackage package)
    {
        if (_closingDesigner || _preparingOnlineExport) return;
        var gamePath = FindGamePath();
        if (gamePath is null) { _status.Text = "Choose the EDMW game folder in Settings first."; return; }
        var friends = ChooseOnlineRecipients(package.ProjectName);
        if (friends is null) return;
        _preparingOnlineExport = true;
        try
        {
            ShowOnlineExportStatus(package.ProjectName, "civilization", CivilizationInstallerPackageCodec.Encode(package), friends,
                async token =>
                {
                    var result = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        return CivilizationLanLocalExport.Install(package, gamePath);
                    }, token);
                    if (result.Status == CivilizationLanStatus.Installed) RefreshLocalLanInstall(civilizations: true);
                    return result;
                });
        }
        catch (Exception error)
        {
            if (!_closingDesigner && !IsDisposed)
                MedievalDialog.Show(this, error.Message, "Export to friends", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _preparingOnlineExport = false; }
    }

    private void ShowOnlineExportStatus(string name, string kind, byte[] payload, OnlineProfile[] friends,
        Func<CancellationToken, Task<CivilizationLanResult>> installLocal)
    {
        using var dialog = new LanExportStatusDialog("Export to friends", name,
            _ => Task.FromResult<IReadOnlyList<CivilizationLanPeer>>(friends.Select(friend =>
                new CivilizationLanPeer(friend.DisplayName, friend.UserId.ToString(), 0)).ToArray()),
            async (peer, progress, token) =>
            {
                var result = await _online.SendExportAsync(Guid.Parse(peer.Address), kind, name, payload, progress, token);
                return new CivilizationLanResult(ParseOnlineExportStatus(result.Status), result.Message);
            }, installLocal: installLocal, onlineFriends: true);
        dialog.ShowDialog(this);
        _status.Text = "Export to friends finished.";
    }

    private static CivilizationLanStatus ParseOnlineExportStatus(string status) => status switch
    {
        "installed" => CivilizationLanStatus.Installed,
        "denied" => CivilizationLanStatus.Denied,
        _ => CivilizationLanStatus.Failed
    };

    private async Task ReviewOnlineExportAsync(OnlineExportOffer offer)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_onlineLifetime.Token, offer.CancellationToken);
        var token = lifetime.Token;
        CivilizationLanDecision decision;
        try
        {
            token.ThrowIfCancellationRequested();
            var bytes = await _online.DownloadExportAsync(offer, token);
            token.ThrowIfCancellationRequested();
            var sender = $"{offer.From.DisplayName} · {offer.From.FriendCode}";
            decision = offer.Kind switch
            {
                "map" => await ReviewIncomingMapAsync(new(MapLanPackageCodec.Decode(bytes), sender, DateTimeOffset.UtcNow), token),
                "civilization" => await ReviewIncomingCivilizationsAsync(new(CivilizationInstallerPackageCodec.Decode(bytes), sender, DateTimeOffset.UtcNow), token),
                _ => new(CivilizationLanStatus.Failed, "Unsupported export type.")
            };
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { decision = new(CivilizationLanStatus.Failed, error.Message); }
        if (_closingDesigner || !_online.IsConnected || token.IsCancellationRequested) return;
        try
        {
            await _online.DecideExportAsync(offer.ExportId, decision.Status switch
            {
                CivilizationLanStatus.Installed => "installed",
                CivilizationLanStatus.Denied => "denied",
                _ => "failed"
            }, decision.Message, token);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
        { PostOnlineUi(() => _status.Text = "The export decision could not be returned to the sender."); }
    }
}

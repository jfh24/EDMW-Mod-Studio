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
    private OnlineGameTrafficBridge? _onlineGameTrafficBridge;
    private OnlineGameStartState? _onlineGameStartState;
    private event EventHandler<string>? OnlineRoomStartStatusChanged;
    private readonly CancellationTokenSource _onlineLifetime = new();
    private readonly Dictionary<Guid, OnlineCollaborationInvite> _onlineInvitations = [];
    private ToolStripButton? _friendsButton;
    private bool _preparingOnlineExport;
    private bool _joiningOnlineMap;
    private MapDelta? _onlineSubmittedDelta;
    private MapProject? _onlineSubmittedProject;
    private bool _discardPendingOnlineReplacement;
    private sealed class OnlineReplacementRejectedException(string message) : Exception(message);
    private sealed class OnlineGameStartState(Guid sessionId, Guid[] peerIds)
    {
        public Guid SessionId { get; } = sessionId;
        public Dictionary<Guid, OnlineGamePeerStartState> Peers { get; } = peerIds.ToDictionary(
            id => id, _ => new OnlineGamePeerStartState());
    }
    private sealed class OnlineGamePeerStartState
    {
        public TaskCompletionSource<OnlineGameSessionStatus> GameReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
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
            UpdateCollaborationPanel();
        });
        _online.GameInvitationsChanged += (_, _) => PostOnlineUi(UpdateFriendsButton);
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
            UpdateCollaborationPanel();
            _status.Text = $"{invitation.From.DisplayName} invited you to {invitation.MapName}. Open the map script page to join.";
        });
        _online.GameInviteReceived += (_, invitation) => PostOnlineUi(() =>
        {
            UpdateFriendsButton();
            _status.Text = $"{invitation.From.DisplayName} invited you to {invitation.Name}. Open Friends > Matchmaking to join.";
        });
        _online.GameRoomEnded += (_, message) => PostOnlineUi(() =>
        {
            UpdateFriendsButton();
            _status.Text = message;
        });
        _online.GameRoomChanged += (_, room) => PostOnlineUi(() =>
        {
            UpdateFriendsButton();
            if (room is null)
            {
                StopOnlineGameTrafficBridge();
                if (_onlineGameStartState is { } start)
                {
                    foreach (var peer in start.Peers.Values)
                        peer.GameReady.TrySetException(new OperationCanceledException("The matchmaking room ended during network setup."));
                    _onlineGameStartState = null;
                }
            }
        });
        _online.GameIceSession.ControlFrameReceived += OnlineGameControlFrameReceived;
        _online.ExportOfferReceived += (_, offer) => _ = ReviewOnlineExportAsync(offer);
    }

    private async Task AutoConnectOnlineRelayAsync()
    {
        if (_verificationMode || _closingDesigner || _online.IsConnected) return;
        if (_onlinePreferences.LoadError is { } loadError)
        {
            _status.Text = loadError;
            return;
        }
        try
        {
            _status.Text = "Connecting to the online relay…";
            await _online.ConnectAsync(_onlinePreferences.Endpoint, _onlinePreferences.DisplayName, _onlineLifetime.Token);
        }
        catch (OperationCanceledException) when (_onlineLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            PostOnlineUi(() =>
            {
                _status.Text = "Online relay connection failed: " + error.Message;
                UpdateFriendsButton();
                UpdateCollaborationPanel();
            });
        }
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
        var pending = _onlineInvitations.Count + _online.GameInvitations.Length + (_online.IsConnected ? _online.Friends.Incoming.Length : 0);
        _friendsButton.Text = pending > 0 ? $"Friends ({pending})" : "Friends";
        _friendsButton.ToolTipText = _online.IsConnected
            ? $"Online as {_online.User?.DisplayName}. {pending} pending requests or invitations. Manage friends, matchmaking, and exports."
            : "Manage friends, connection settings, and matchmaking";
        _centerToolbarActions?.Invoke();
    }

    private void ShowFriends()
    {
        if (_closingDesigner) return;
        using var dialog = new FriendsDialog(_online, _onlinePreferences,
            () => !_onlineCollaboration.IsConnected,
            () => _onlineGameTrafficBridge?.TrafficSummary,
            PrepareOnlineGameNetworkAsync);
        void HandleRoomStatus(object? sender, string message) => dialog.UpdateMatchmakingStatus(message);
        OnlineRoomStartStatusChanged += HandleRoomStatus;
        try { dialog.ShowDialog(this); }
        finally { OnlineRoomStartStatusChanged -= HandleRoomStatus; }
    }

    private async Task PrepareOnlineGameNetworkAsync(Action<string> reportStatus)
    {
        var room = _online.CurrentGameRoom ?? throw new InvalidOperationException("Join a matchmaking room first.");
        var user = _online.User ?? throw new InvalidOperationException("Connect to the online relay first.");
        var gameHostId = room.GameHostUserId ?? room.Host.UserId;
        if (gameHostId != user.UserId)
            throw new InvalidOperationException("Only the designated EDMW host can prepare the room game network.");
        if (_onlineGameTrafficBridge is not null || _onlineGameStartState is not null)
            throw new InvalidOperationException("A room game network is already starting or running.");
        var gamePath = FindGamePath();
        if (gamePath is null && !SelectGameInstallPath())
            throw new InvalidOperationException("Select the EDMW game folder before preparing the room game network.");
        gamePath = FindGamePath() ?? throw new InvalidOperationException("The selected EDMW folder is unavailable.");
        if (!_verificationMode && IsEdmwRunning())
            throw new InvalidOperationException("Close EDMW on this computer before preparing the room game network.");

        var memberIds = room.Members.Select(member => member.Profile.UserId).Distinct().ToArray();
        if (!memberIds.Contains(user.UserId)) throw new InvalidDataException("The relay room does not list this device as a member.");
        var peerIds = memberIds.Where(id => id != user.UserId).ToArray();
        if (peerIds.Length == 0) throw new InvalidOperationException("Invite or join at least one other player before preparing the game network.");

        var sessionId = Guid.NewGuid();
        var startState = new OnlineGameStartState(sessionId, peerIds);
        _onlineGameStartState = startState;
        try
        {
            reportStatus("Preparing the game network (direct ICE preferred; secure relay fallback ready)…");
            await _online.GameIceSession.WaitForGameNetworkReadyAsync(peerIds, TimeSpan.FromSeconds(60), _onlineLifetime.Token);
            if (string.IsNullOrWhiteSpace(_online.GameIceSession.PreferredLocalIpv4))
                throw new InvalidOperationException("No usable local IPv4 adapter was found for the game network mapping.");
            reportStatus("Launching EDMW with the room network bridge…");
            var bridge = await OnlineGameTrafficBridge.LaunchAsync(_online, gamePath, _onlineLifetime.Token);
            if (_closingDesigner || IsDisposed || _online.CurrentGameRoom?.RoomId != room.RoomId)
            {
                await bridge.DisposeAsync();
                throw new InvalidOperationException("The matchmaking room ended before EDMW finished launching.");
            }
            AttachOnlineGameBridge(bridge);
            if (!_verificationMode && !await bridge.BringGameWindowToFrontAsync(_onlineLifetime.Token))
                throw new InvalidOperationException("EDMW started, but Windows could not show its native game window.");

            var hostMember = room.Members.FirstOrDefault(member => member.Profile.UserId == user.UserId)
                ?? throw new InvalidDataException("The designated EDMW host is no longer in this room.");
            var launch = new OnlineGameClientLaunch(sessionId, OnlineGameAddress.VirtualIpv4(hostMember.RelayPort));
            var launchFrame = OnlineGameSessionProtocol.EncodeJson(OnlineGameSessionFrameType.LaunchClient, launch);
            foreach (var peerId in peerIds)
                if (!_online.GameIceSession.SendControlFrame(peerId, launchFrame))
                    throw new IOException("Could not send the native game launch request to every room member.");
            reportStatus("Launching each room member with the room network bridge…");
            var launchStatuses = await Task.WhenAll(startState.Peers.Select(async pair =>
            {
                var memberName = room.Members.FirstOrDefault(member => member.Profile.UserId == pair.Key)?.Profile.DisplayName ?? pair.Key.ToString("N");
                reportStatus($"Waiting for {memberName}'s game network bridge…");
                return (PeerId: pair.Key, Status: await pair.Value.GameReady.Task.WaitAsync(TimeSpan.FromSeconds(120), _onlineLifetime.Token));
            }));
            RequireSuccessfulRoomStatuses(launchStatuses, "start EDMW with the room network bridge");
            reportStatus($"EDMW is open on all {room.Members.Length} players. Host IP: {launch.HostVirtualIpv4}. Create a game in EDMW, then have other players join by IP using this address.");
        }
        catch
        {
            StopOnlineGameTrafficBridge();
            throw;
        }
        finally
        {
            if (ReferenceEquals(_onlineGameStartState, startState)) _onlineGameStartState = null;
        }
    }

    private void RequireSuccessfulRoomStatuses(IEnumerable<(Guid PeerId, OnlineGameSessionStatus Status)> statuses, string action)
    {
        foreach (var (peerId, status) in statuses)
        {
            var player = _online.CurrentGameRoom?.Members.FirstOrDefault(member => member.Profile.UserId == peerId)?.Profile.DisplayName
                ?? peerId.ToString("N");
            if (string.IsNullOrEmpty(status.Message) || status.Message.StartsWith("FAILED:", StringComparison.Ordinal))
            {
                var details = status.Message is { Length: > 7 } failure && failure.StartsWith("FAILED:", StringComparison.Ordinal)
                    ? failure[7..] : "No stage details were returned.";
                throw new InvalidOperationException($"{player} could not {action}: {details}");
            }
        }
    }

    private void AttachOnlineGameBridge(OnlineGameTrafficBridge bridge)
    {
        _onlineGameTrafficBridge = bridge;
        bridge.StatusChanged += OnlineGameBridgeStatusChanged;
        bridge.Exited += OnlineGameBridgeExited;
        if (_verificationMode && bridge.LatestAutomationStatus is { } latestStatus)
            Console.WriteLine("BRIDGE: " + latestStatus);
        if (bridge.IsFaulted)
        {
            var message = bridge.ProviderStatusMessage ?? "The game network bridge stopped.";
            StopOnlineGameTrafficBridge();
            throw new InvalidOperationException(message);
        }
    }

    private void OnlineGameControlFrameReceived(object? sender, OnlineIceControlFrame frame) =>
        PostOnlineUi(() => _ = HandleOnlineGameControlFrameAsync(frame));

    private void PublishOnlineRoomStartStatus(string message)
    {
        _status.Text = message;
        OnlineRoomStartStatusChanged?.Invoke(this, message);
    }

    private async Task HandleOnlineGameControlFrameAsync(OnlineIceControlFrame frame)
    {
        var room = _online.CurrentGameRoom;
        if (room is null || room.RoomId != frame.RoomId ||
            !room.Members.Any(member => member.Profile.UserId == frame.FromUserId)) return;
        try
        {
            if (OnlineGameSessionProtocol.TryDecodeJson(frame.Payload, OnlineGameSessionFrameType.LaunchClient, out OnlineGameClientLaunch? launch))
            {
                await LaunchOnlineRoomClientAsync(frame.FromUserId, room, launch!);
                return;
            }
            if (OnlineGameSessionProtocol.TryDecodeJson(frame.Payload, OnlineGameSessionFrameType.GameReady, out OnlineGameSessionStatus? gameReady))
            {
                CompleteOnlineGamePeerStatus(frame.FromUserId, gameReady!);
                return;
            }
            if (OnlineGameSessionProtocol.TryDecodeJson(frame.Payload, OnlineGameSessionFrameType.LaunchFailed, out OnlineGameSessionStatus? failed))
            {
                failed = failed! with { Message = "FAILED:" + failed!.Message };
                CompleteOnlineGamePeerStatus(frame.FromUserId, failed!);
                return;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            var session = TryReadSessionId(frame.Payload);
            if (session != Guid.Empty) SendOnlineSessionStatus(frame.FromUserId, OnlineGameSessionFrameType.LaunchFailed, session, error.Message);
            PublishOnlineRoomStartStatus("Room game setup failed: " + error.Message);
        }
    }

    private async Task LaunchOnlineRoomClientAsync(Guid fromUserId, OnlineGameRoom room, OnlineGameClientLaunch launch)
    {
        try
        {
            var hostId = room.GameHostUserId ?? room.Host.UserId;
            var hostMember = room.Members.FirstOrDefault(member => member.Profile.UserId == hostId)
                ?? throw new InvalidDataException("The designated EDMW host is no longer in this room.");
            var expectedAddress = OnlineGameAddress.VirtualIpv4(hostMember.RelayPort);
            if (fromUserId != hostId || launch.SessionId == Guid.Empty ||
                !string.Equals(launch.HostVirtualIpv4, expectedAddress, StringComparison.Ordinal))
                throw new InvalidDataException("The room game network request does not match the designated EDMW host.");
            if (_onlineGameTrafficBridge is not null || (!_verificationMode && IsEdmwRunning()))
                throw new InvalidOperationException("Close any existing EDMW process before preparing the room game network.");
            var gamePath = FindGamePath() ?? throw new InvalidOperationException("Select the EDMW game folder before preparing the room game network.");
            PublishOnlineRoomStartStatus($"Starting EDMW's native UI with the room network. The host's Join by IP address is {expectedAddress}.");
            var bridge = await OnlineGameTrafficBridge.LaunchAsync(_online, gamePath, _onlineLifetime.Token);
            if (_online.CurrentGameRoom?.RoomId != room.RoomId)
            {
                await bridge.DisposeAsync();
                throw new InvalidOperationException("The matchmaking room ended before EDMW finished launching.");
            }
            AttachOnlineGameBridge(bridge);
            if (!_verificationMode && !await bridge.BringGameWindowToFrontAsync(_onlineLifetime.Token))
                throw new InvalidOperationException("EDMW started, but Windows could not show its native game window.");
            var status = bridge.ProviderStatusMessage ?? "EDMW's native UI is open with the room network bridge configured.";
            PublishOnlineRoomStartStatus(status + $" Host Join by IP address: {expectedAddress}.");
            SendOnlineSessionStatus(fromUserId, OnlineGameSessionFrameType.GameReady, launch.SessionId,
                "EDMW started with the matchmaking network bridge configured.");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (_verificationMode) Console.WriteLine("CLIENT FAIL: " + error);
            SendOnlineSessionStatus(fromUserId, OnlineGameSessionFrameType.LaunchFailed, launch.SessionId, error.Message);
            PublishOnlineRoomStartStatus("Could not prepare the room game: " + error.Message);
        }
    }

    private static Guid TryReadSessionId(byte[] frame)
    {
        if (OnlineGameSessionProtocol.TryDecodeJson(frame, OnlineGameSessionFrameType.LaunchClient, out OnlineGameClientLaunch? launch))
            return launch!.SessionId;
        return Guid.Empty;
    }

    private void CompleteOnlineGamePeerStatus(Guid peerId, OnlineGameSessionStatus status)
    {
        var state = _onlineGameStartState;
        if (state is null || status.SessionId != state.SessionId ||
            !state.Peers.TryGetValue(peerId, out var peer)) return;
        peer.GameReady.TrySetResult(status);
    }

    private void SendOnlineSessionStatus(Guid peerId, OnlineGameSessionFrameType type, Guid sessionId, string message)
    {
        if (message.Length > 1024) message = message[..1024];
        _online.GameIceSession.SendControlFrame(peerId,
            OnlineGameSessionProtocol.EncodeJson(type, new OnlineGameSessionStatus(sessionId, message)));
    }

    private void OnlineGameBridgeStatusChanged(object? sender, string message)
    {
        if (_verificationMode) Console.WriteLine("BRIDGE: " + message);
        PostOnlineUi(() => PublishOnlineRoomStartStatus(message));
    }

    private void OnlineGameBridgeExited(object? sender, EventArgs e)
    {
        if (sender is not OnlineGameTrafficBridge bridge) return;
        PostOnlineUi(() =>
        {
            if (_onlineGameTrafficBridge != bridge) return;
            var faulted = bridge.IsFaulted;
            StopOnlineGameTrafficBridge();
            _status.Text = faulted
                ? bridge.ProviderStatusMessage ?? "The EDMW network bridge stopped."
                : "EDMW closed. The matchmaking room is still available.";
        });
    }

    private void StopOnlineGameTrafficBridge()
    {
        var bridge = _onlineGameTrafficBridge;
        if (bridge is null) return;
        _onlineGameTrafficBridge = null;
        bridge.StatusChanged -= OnlineGameBridgeStatusChanged;
        bridge.Exited -= OnlineGameBridgeExited;
        _ = bridge.DisposeAsync();
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
        _status.Text = "Map invitation sent.";
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

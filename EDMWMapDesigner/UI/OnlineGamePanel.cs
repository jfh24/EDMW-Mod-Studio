using EDMWMapDesigner.Networking;

namespace EDMWMapDesigner.UI;

/// <summary>Public and friends-only matchmaking rooms embedded in the Matchmaking tab.</summary>
internal sealed class OnlineGamePanel : UserControl
{
    private sealed record FriendChoice(OnlineProfile Profile)
    {
        public override string ToString() => Profile.DisplayName;
    }
    private sealed record GameHostChoice(OnlineGameMember Member)
    {
        public override string ToString() => $"{Member.Profile.DisplayName} · {OnlineGameAddress.VirtualIpv4(Member.RelayPort)}";
    }

    private readonly OnlineRelayClient _client;
    private readonly Func<string?> _gameTrafficStatus;
    private readonly Func<Action<string>, Task>? _prepareGameNetwork;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _trafficTimer = new() { Interval = 1000 };
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly TextBox _name = new() { MaxLength = 64, PlaceholderText = "Game room name", AccessibleName = "Game room name" };
    private readonly ComboBox _visibility = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Game room visibility" };
    private readonly NumericUpDown _capacity = new() { Minimum = 2, Maximum = 8, Value = 8, AccessibleName = "Maximum game room players" };
    private readonly ComboBox _friendPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Height = 32, Width = 160, AccessibleName = "Invite an online friend" };
    private readonly ComboBox _gameHostPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Height = 32, AccessibleName = "EDMW game host" };
    private readonly ListView _rooms = MakeList("Available relay game rooms");
    private readonly ListView _invitations = MakeList("Incoming game invitations");
    private readonly Label _roomsEmpty = EmptyListLabel("No open game rooms. Refresh the list or create a room.");
    private readonly Label _invitationsEmpty = EmptyListLabel("No game invitations yet.");
    private readonly Label _roomState = new() { Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextSecondary, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Current game room" };
    private readonly Label _hostAddress = new() { Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextSecondary, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "EDMW host virtual IP address" };
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextSecondary, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _create;
    private readonly Button _refresh;
    private readonly Button _invite;
    private readonly Button _leave;
    private readonly Button _prepareNetwork;
    private readonly Button _setGameHost;
    private readonly Button _copyHostAddress;
    private readonly Button _transportSettings;
    private bool _busy;

    public OnlineGamePanel(OnlineRelayClient client, Func<string?>? gameTrafficStatus = null,
        Func<Action<string>, Task>? prepareGameNetwork = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _gameTrafficStatus = gameTrafficStatus ?? (() => null);
        _prepareGameNetwork = prepareGameNetwork;
        _name.Text = $"{_client.User?.DisplayName ?? "Player"}'s game";
        Dock = DockStyle.Fill;
        Font = MedievalTheme.UiFont();
        ForeColor = MedievalTheme.Text;
        BackColor = MedievalTheme.Sidebar;

        _create = Button("Create room", () => RunAsync(async () =>
        {
            var roomName = _name.Text.Trim();
            if (roomName.Length == 0) throw new InvalidOperationException("Enter a name for the game room.");
            var visibility = _visibility.SelectedIndex == 1 ? "public" : "friends";
            SetStatus("Creating game room…");
            var room = await _client.CreateGameRoomAsync(roomName, visibility, (int)_capacity.Value, _lifetime.Token);
            SetStatus($"Created {room.Name}. Invite an online friend to join.");
            await _client.RefreshGameRoomsAsync(_lifetime.Token);
        }), primary: true);
        _refresh = Button("Refresh rooms", () => RunAsync(async () =>
        {
            SetStatus("Refreshing game rooms…");
            await _client.RefreshGameRoomsAsync(_lifetime.Token);
            SetStatus("Game rooms refreshed.");
        }));
        _invite = Button("Invite friend", () => RunAsync(async () =>
        {
            if (_friendPicker.SelectedItem is not FriendChoice friend) return;
            SetStatus($"Inviting {friend.Profile.DisplayName}…");
            await _client.InviteToGameAsync(friend.Profile.UserId, _lifetime.Token);
            SetStatus($"Invitation sent to {friend.Profile.DisplayName}.");
        }));
        _leave = Button("Leave room", () => RunAsync(async () =>
        {
            SetStatus("Leaving game room…");
            await _client.LeaveGameRoomAsync(_lifetime.Token);
            SetStatus("Left the game room.");
        }));
        _prepareNetwork = Button("Prepare game network", () => RunAsync(async () =>
        {
            if (_prepareGameNetwork is null) throw new InvalidOperationException("Room game network setup is unavailable from this Matchmaking panel.");
            SetStatus("Preparing the room network…");
            await _prepareGameNetwork(message => SetStatus(message));
            SetStatus(_gameTrafficStatus() ?? "EDMW is open for every player. The host creates a game in EDMW; other players join with the displayed host IP.");
            _trafficTimer.Start();
        }), primary: true);
        _setGameHost = Button("Set EDMW host", () => RunAsync(async () =>
        {
            if (_gameHostPicker.SelectedItem is not GameHostChoice choice) return;
            await _client.DesignateGameHostAsync(choice.Member.Profile.UserId, _lifetime.Token);
            SetStatus($"{choice.Member.Profile.DisplayName} is the designated EDMW host.");
        }));
        _copyHostAddress = Button("Copy host IP", () => RunAsync(() =>
        {
            var room = _client.CurrentGameRoom;
            var member = room is null ? null : DesignatedGameHost(room);
            if (member is null) throw new InvalidOperationException("Choose an EDMW host in the current room first.");
            var address = OnlineGameAddress.VirtualIpv4(member.RelayPort);
            Clipboard.SetText(address);
            SetStatus($"Copied EDMW host IP {address}.");
            return Task.CompletedTask;
        }));
        _transportSettings = Button("⚙", ShowTransportSettingsAsync);
        _transportSettings.Width = 34;
        _transportSettings.AccessibleName = "Matchmaking network and logging settings";
        _tips.SetToolTip(_transportSettings, "Configure ICE, WSS gameplay relay, and network traffic logging.");

        MedievalTheme.StyleTextInput(_name);
        MedievalTheme.StyleTextInput(_visibility);
        MedievalTheme.StyleTextInput(_capacity);
        MedievalTheme.StyleTextInput(_friendPicker);
        MedievalTheme.StyleTextInput(_gameHostPicker);
        _visibility.Items.AddRange(["Friends only", "Public"]);
        _visibility.SelectedIndex = 0;
        _tips.SetToolTip(_visibility, "Public rooms are listed for all connected players. Friends-only rooms are listed for accepted friends and invitees.");

        _rooms.Columns.Add("Game", 260);
        _rooms.Columns.Add("Host", 170);
        _rooms.Columns.Add("Players", 90);
        _rooms.Columns.Add("Access", 90);
        _rooms.SizeChanged += (_, _) => FriendsDialog.FitColumns(_rooms, 0.37f, 0.25f, 0.14f);
        _rooms.SelectedIndexChanged += (_, _) => RefreshActions();
        _invitations.Columns.Add("Game", 330);
        _invitations.Columns.Add("From", 250);
        _invitations.SizeChanged += (_, _) => FriendsDialog.FitColumns(_invitations, 0.58f);
        _invitations.SelectedIndexChanged += (_, _) => RefreshActions();
        ConfigureJoinContextMenus();
        _friendPicker.SelectedIndexChanged += (_, _) => RefreshActions();
        _gameHostPicker.SelectedIndexChanged += (_, _) => RefreshActions();

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 2, BackColor = Color.Transparent };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var lists = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 8, 0), BackColor = Color.Transparent };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 64));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
        lists.Controls.Add(RoomListCard(), 0, 0);
        lists.Controls.Add(InvitationCard(), 0, 1);
        layout.Controls.Add(lists, 0, 0);
        layout.Controls.Add(CreateSidebar(), 1, 0);
        layout.Controls.Add(_status, 0, 1);
        layout.SetColumnSpan(_status, 2);
        Controls.Add(layout);

        _client.GameRoomsChanged += GameRoomsChanged;
        _client.FriendsChanged += FriendsChanged;
        _client.GameInviteReceived += GameInviteReceived;
        _client.GameInvitationsChanged += GameInvitationsChanged;
        _client.GameRoomChanged += GameRoomChanged;
        _client.GameRoomEnded += GameRoomEnded;
        _client.StatusChanged += StatusChanged;
        _trafficTimer.Tick += (_, _) =>
        {
            var status = _gameTrafficStatus();
            if (status is null) _trafficTimer.Stop();
            else SetStatus(status);
        };
        Disposed += (_, _) =>
        {
            _lifetime.Cancel();
            _trafficTimer.Stop();
            _trafficTimer.Dispose();
            _tips.Dispose();
            _client.GameRoomsChanged -= GameRoomsChanged;
            _client.FriendsChanged -= FriendsChanged;
            _client.GameInviteReceived -= GameInviteReceived;
            _client.GameInvitationsChanged -= GameInvitationsChanged;
            _client.GameRoomChanged -= GameRoomChanged;
            _client.GameRoomEnded -= GameRoomEnded;
            _client.StatusChanged -= StatusChanged;
            _lifetime.Dispose();
        };
        RefreshActions();
    }

    public void Activate()
    {
        if (IsDisposed || Disposing) return;
        RefreshFriendPicker();
        RefreshLists();
        if (!_client.IsConnected)
        {
            SetStatus("The relay connection is needed to use matchmaking. Check Friends for connection settings.");
            return;
        }
        if (_busy) return;
        SetStatus("Checking relay matchmaking support…");
        _ = RunAsync(async () =>
        {
            await _client.RefreshGameRoomsAsync(_lifetime.Token);
            SetStatus(_client.TrafficLogSetupError ?? "Game rooms refreshed.", error: _client.TrafficLogSetupError is not null);
        });
    }

    public void SetExternalStatus(string message)
    {
        SetStatus(message);
        if (_gameTrafficStatus() is not null) _trafficTimer.Start();
    }

    private OnlineGameRoom? SelectedRoom => _rooms.SelectedItems.Count > 0 ? _rooms.SelectedItems[0].Tag as OnlineGameRoom : null;
    private OnlineGameInvite? SelectedInvite => _invitations.SelectedItems.Count > 0 ? _invitations.SelectedItems[0].Tag as OnlineGameInvite : null;

    private Control CreateRoomCard()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        var heading = new Label { Text = "Create a game room", Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright, Font = MedievalTheme.DisplayFont(12), TextAlign = ContentAlignment.MiddleLeft };
        content.Controls.Add(heading, 0, 0);
        _name.Dock = DockStyle.Fill;
        _name.Margin = Padding.Empty;
        content.Controls.Add(MedievalTheme.FrameInput(_name), 0, 1);
        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        options.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        options.Controls.Add(new Label { Text = "Room access", Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        options.Controls.Add(new Label { Text = "Players", Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
        options.Controls.Add(MedievalTheme.FrameInput(_visibility), 0, 1);
        options.Controls.Add(MedievalTheme.FrameInput(_capacity), 1, 1);
        content.Controls.Add(options, 0, 2);
        _create.Dock = DockStyle.Fill;
        _create.Margin = Padding.Empty;
        content.Controls.Add(_create, 0, 3);
        content.Controls.Add(new Label
        {
            Text = "Friends-only rooms are visible to friends and invitees.", Dock = DockStyle.Fill,
            ForeColor = MedievalTheme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
        }, 0, 4);
        return Card(content);
    }

    private Control RoomListCard()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        header.Controls.Add(new Label
        {
            Text = "Available game rooms", Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.DisplayFont(12), TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        _refresh.Dock = DockStyle.Fill;
        _refresh.Margin = new Padding(4, 0, 0, 2);
        header.Controls.Add(_refresh, 1, 0);
        content.Controls.Add(header, 0, 0);
        content.Controls.Add(ListWithEmptyState(_rooms, _roomsEmpty), 0, 1);
        return Card(content);
    }

    private Control InvitationCard()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(new Label { Text = "Game invitations", Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright, Font = MedievalTheme.DisplayFont(12), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        content.Controls.Add(ListWithEmptyState(_invitations, _invitationsEmpty), 0, 1);
        return Card(content);
    }

    private Control CurrentRoomCard()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        var titleRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        titleRow.Controls.Add(new Label
        {
            Text = "Current room", Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.DisplayFont(12), TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        _transportSettings.Dock = DockStyle.Fill;
        _transportSettings.Margin = new Padding(0, 0, 0, 2);
        titleRow.Controls.Add(_transportSettings, 1, 0);
        content.Controls.Add(titleRow, 0, 0);
        content.Controls.Add(_roomState, 0, 1);

        var inviteRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        inviteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inviteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        _friendPicker.Dock = DockStyle.Fill;
        _friendPicker.Margin = new Padding(0, 0, 5, 0);
        _invite.Dock = DockStyle.Fill;
        _invite.Margin = Padding.Empty;
        inviteRow.Controls.Add(MedievalTheme.FrameInput(_friendPicker), 0, 0);
        inviteRow.Controls.Add(_invite, 1, 0);
        content.Controls.Add(inviteRow, 0, 2);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        _leave.Dock = DockStyle.Fill;
        _leave.Margin = new Padding(0, 0, 5, 0);
        _prepareNetwork.Dock = DockStyle.Fill;
        _prepareNetwork.Margin = Padding.Empty;
        actions.Controls.Add(_leave, 0, 0);
        actions.Controls.Add(_prepareNetwork, 1, 0);
        content.Controls.Add(actions, 0, 3);
        return Card(content);
    }

    private Task ShowTransportSettingsAsync()
    {
        using var dialog = new Form
        {
            Text = "Matchmaking network settings",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowIcon = false,
            ShowInTaskbar = false,
            ClientSize = new Size(500, 438),
            BackColor = MedievalTheme.Sidebar,
            ForeColor = MedievalTheme.Text,
            Font = MedievalTheme.UiFont()
        };

        var directIce = new CheckBox
        {
            Text = "Direct ICE / STUN gameplay",
            Checked = _client.Preferences.EnableIceGameplay,
            AutoSize = true,
            ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.UiFont(10, FontStyle.Bold),
            Margin = Padding.Empty,
            AccessibleName = "Enable direct ICE gameplay"
        };
        var wssRelay = new CheckBox
        {
            Text = "WSS gameplay relay",
            Checked = _client.Preferences.EnableWssGameplay,
            AutoSize = true,
            ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.UiFont(10, FontStyle.Bold),
            Margin = Padding.Empty,
            AccessibleName = "Enable WSS gameplay relay"
        };
        var debugLog = new CheckBox
        {
            Text = "Log network traffic to a text file",
            Checked = _client.Preferences.EnableNetworkTrafficLogging,
            AutoSize = true,
            ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.UiFont(10, FontStyle.Bold),
            Margin = Padding.Empty,
            AccessibleName = "Enable network traffic logging"
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 14), ColumnCount = 1, RowCount = 6,
            BackColor = MedievalTheme.Sidebar, Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        content.Controls.Add(new Label
        {
            Text = "Gameplay transports", Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.DisplayFont(15), TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        content.Controls.Add(TransportOption(directIce,
            "Send and accept gameplay directly over negotiated ICE data channels."), 0, 1);
        content.Controls.Add(TransportOption(wssRelay,
            "Send and accept gameplay through the authenticated WebSocket relay when enabled."), 0, 2);
        content.Controls.Add(TransportOption(debugLog,
            "Record each gameplay frame's direction, size, virtual endpoint IPs, ICE candidate IPs, and WSS relay IPs. Packet contents are excluded."), 0, 3);
        content.Controls.Add(new Label
        {
            Text = "Matchmaking, login, and ICE signaling stay on WSS. ICE may still negotiate for network setup, but with direct ICE gameplay off, game frames use WSS. " +
                "For a definitive WSS test, turn off direct ICE on both players, leave WSS gameplay enabled, and confirm the WSS queued and received counters rise. " +
                "WSS server IPs in the log are DNS-resolved candidates.\r\n\r\nLog file: " + OnlineTrafficLogger.LogPath,
            Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft, Font = MedievalTheme.UiFont(8.5f), Padding = new Padding(2, 6, 2, 6)
        }, 0, 4);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent
        };
        var save = FriendsDialog.MakeButton("Save settings", () =>
        {
            var previousIce = _client.Preferences.EnableIceGameplay;
            var previousWss = _client.Preferences.EnableWssGameplay;
            var previousLog = _client.Preferences.EnableNetworkTrafficLogging;
            _client.Preferences.EnableIceGameplay = directIce.Checked;
            _client.Preferences.EnableWssGameplay = wssRelay.Checked;
            _client.Preferences.EnableNetworkTrafficLogging = debugLog.Checked;
            try { _client.Preferences.Save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _client.Preferences.EnableIceGameplay = previousIce;
                _client.Preferences.EnableWssGameplay = previousWss;
                _client.Preferences.EnableNetworkTrafficLogging = previousLog;
                MedievalDialog.Show(dialog, "Could not save the matchmaking settings: " + error.Message,
                    "Matchmaking settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return Task.CompletedTask;
            }

            var logError = _client.ConfigureNetworkTrafficLogging(debugLog.Checked);
            if (logError is not null)
            {
                _client.Preferences.EnableIceGameplay = previousIce;
                _client.Preferences.EnableWssGameplay = previousWss;
                _client.Preferences.EnableNetworkTrafficLogging = previousLog;
                try { _client.Preferences.Save(); } catch (Exception) { }
                MedievalDialog.Show(dialog, logError, "Network traffic log", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return Task.CompletedTask;
            }

            RefreshActions();
            var mode = !directIce.Checked && wssRelay.Checked ? "WSS gameplay is forced." :
                !directIce.Checked && !wssRelay.Checked ? "Gameplay networking is disabled." :
                "Gameplay transport settings saved.";
            SetStatus(debugLog.Checked
                ? $"{mode} Network traffic log: {OnlineTrafficLogger.LogPath}"
                : mode);
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
            return Task.CompletedTask;
        }, primary: true);
        var cancel = FriendsDialog.MakeButton("Cancel", () =>
        {
            dialog.DialogResult = DialogResult.Cancel;
            dialog.Close();
            return Task.CompletedTask;
        });
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        content.Controls.Add(buttons, 0, 5);
        dialog.Controls.Add(content);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        _ = dialog.ShowDialog(this);
        return Task.CompletedTask;
    }

    private static Control TransportOption(CheckBox toggle, string description)
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 6, 8, 5),
            Margin = Padding.Empty, BackColor = MedievalTheme.Surface
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(toggle, 0, 0);
        content.Controls.Add(new Label
        {
            Text = description, Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextSecondary,
            Font = MedievalTheme.UiFont(8.5f), TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(25, 0, 2, 0), AutoEllipsis = true
        }, 0, 1);
        return content;
    }

    private Control GameHostControls()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        content.Controls.Add(new Label { Text = "Host settings", Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright, Font = MedievalTheme.DisplayFont(12), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _gameHostPicker.Dock = DockStyle.Fill;
        _gameHostPicker.Margin = Padding.Empty;
        content.Controls.Add(MedievalTheme.FrameInput(_gameHostPicker), 0, 1);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        _setGameHost.Dock = DockStyle.Fill;
        _setGameHost.Margin = new Padding(0, 0, 4, 0);
        _copyHostAddress.Dock = DockStyle.Fill;
        _copyHostAddress.Margin = new Padding(4, 0, 0, 0);
        buttons.Controls.Add(_setGameHost, 0, 0);
        buttons.Controls.Add(_copyHostAddress, 1, 0);
        content.Controls.Add(buttons, 0, 2);
        content.Controls.Add(_hostAddress, 0, 3);
        _tips.SetToolTip(_gameHostPicker, "The matchmaking room host selects which player is hosting EDMW.");
        _tips.SetToolTip(_setGameHost, "Designate which room member will host the EDMW multiplayer game.");
        _tips.SetToolTip(_copyHostAddress, "Copy the designated host's virtual IPv4 address for the game's Join by IP field.");
        return Card(content);
    }

    private static Control ListWithEmptyState(ListView list, Label empty)
    {
        var host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = MedievalTheme.Input };
        list.Dock = DockStyle.Fill;
        void ResizeEmptyState()
        {
            var headerHeight = Math.Min(host.ClientSize.Height, Math.Max(22, list.Font.Height + 8));
            empty.SetBounds(0, headerHeight, host.ClientSize.Width, Math.Max(0, host.ClientSize.Height - headerHeight));
        }
        host.Controls.Add(list);
        host.Controls.Add(empty);
        empty.BringToFront();
        host.SizeChanged += (_, _) => ResizeEmptyState();
        ResizeEmptyState();
        return host;
    }

    private static Label EmptyListLabel(string message) => new()
    {
        Text = message, Dock = DockStyle.Fill, Visible = true, AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleCenter, BackColor = MedievalTheme.Input,
        ForeColor = MedievalTheme.TextSecondary, Padding = new Padding(16),
        AccessibleRole = AccessibleRole.StaticText
    };

    private Control CreateSidebar()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, BackColor = Color.Transparent };
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, BackColor = Color.Transparent };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 152));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 186));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
        stack.Controls.Add(CurrentRoomCard(), 0, 0);
        stack.Controls.Add(CreateRoomCard(), 0, 1);
        stack.Controls.Add(GameHostControls(), 0, 2);
        scroll.Controls.Add(stack);
        scroll.SizeChanged += (_, _) => stack.Width = Math.Max(0, scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        stack.Width = scroll.ClientSize.Width;
        return scroll;
    }

    private void ConfigureJoinContextMenus()
    {
        var joinRoom = new ToolStripMenuItem("Join room");
        joinRoom.Click += (_, _) =>
        {
            if (SelectedRoom is not { } room) return;
            _ = RunAsync(async () =>
            {
                SetStatus($"Joining {room.Name}…");
                await _client.JoinGameRoomAsync(room.RoomId, _lifetime.Token);
                SetStatus($"Joined {room.Name}.");
            });
        };
        var roomsMenu = new ContextMenuStrip();
        roomsMenu.Items.Add(joinRoom);
        roomsMenu.Opening += (_, e) =>
        {
            joinRoom.Enabled = _client.IsConnected && !_busy && _client.CurrentGameRoom is null && SelectedRoom is not null;
            e.Cancel = SelectedRoom is null;
        };
        _rooms.ContextMenuStrip = roomsMenu;
        _rooms.MouseDown += (_, e) => SelectContextRow(_rooms, e);
        _rooms.DoubleClick += (_, _) =>
        {
            if (_client.IsConnected && !_busy && _client.CurrentGameRoom is null && SelectedRoom is not null) joinRoom.PerformClick();
        };

        var acceptInvite = new ToolStripMenuItem("Accept invitation");
        acceptInvite.Click += (_, _) =>
        {
            if (SelectedInvite is not { } invitation) return;
            _ = RunAsync(async () =>
            {
                SetStatus($"Joining {invitation.Name}…");
                await _client.JoinGameRoomAsync(invitation.RoomId, _lifetime.Token);
                SetStatus($"Joined {invitation.Name}.");
            });
        };
        var invitationsMenu = new ContextMenuStrip();
        invitationsMenu.Items.Add(acceptInvite);
        invitationsMenu.Opening += (_, e) =>
        {
            acceptInvite.Enabled = _client.IsConnected && !_busy && _client.CurrentGameRoom is null && SelectedInvite is not null;
            e.Cancel = SelectedInvite is null;
        };
        _invitations.ContextMenuStrip = invitationsMenu;
        _invitations.MouseDown += (_, e) => SelectContextRow(_invitations, e);
        _invitations.DoubleClick += (_, _) =>
        {
            if (_client.IsConnected && !_busy && _client.CurrentGameRoom is null && SelectedInvite is not null) acceptInvite.PerformClick();
        };
    }

    private static void SelectContextRow(ListView list, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var item = list.GetItemAt(e.X, e.Y);
        if (item is null)
        {
            list.SelectedItems.Clear();
            return;
        }
        item.Selected = true;
    }

    private void RefreshFriendPicker()
    {
        var selected = (_friendPicker.SelectedItem as FriendChoice)?.Profile.UserId;
        _friendPicker.Items.Clear();
        foreach (var friend in _client.Friends.Friends.Where(friend => friend.Online).OrderBy(friend => friend.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var choice = new FriendChoice(friend);
            _friendPicker.Items.Add(choice);
            if (friend.UserId == selected) _friendPicker.SelectedItem = choice;
        }
        if (_friendPicker.Items.Count == 0)
        {
            _friendPicker.Items.Add("No online friends");
            _friendPicker.SelectedIndex = 0;
        }
        if (_friendPicker.SelectedIndex < 0 && _friendPicker.Items.Count > 0) _friendPicker.SelectedIndex = 0;
    }

    private void RefreshLists()
    {
        if (IsDisposed || Disposing) return;
        var selectedRoom = SelectedRoom?.RoomId;
        var selectedInvite = SelectedInvite?.RoomId;
        _rooms.BeginUpdate();
        _rooms.Items.Clear();
        foreach (var room in _client.GameRooms.OrderBy(room => room.Name, StringComparer.OrdinalIgnoreCase))
        {
            var row = new ListViewItem(room.Name) { Tag = room };
            row.SubItems.Add(room.Host.DisplayName);
            row.SubItems.Add($"{room.Members.Length}/{room.MaxPlayers}");
            row.SubItems.Add(room.Visibility == "public" ? "Public" : "Friends");
            _rooms.Items.Add(row);
            if (selectedRoom == room.RoomId) row.Selected = true;
        }
        _rooms.EndUpdate();
        _invitations.BeginUpdate();
        _invitations.Items.Clear();
        foreach (var invitation in _client.GameInvitations.OrderBy(invite => invite.Name, StringComparer.OrdinalIgnoreCase))
        {
            var row = new ListViewItem(invitation.Name) { Tag = invitation };
            row.SubItems.Add(invitation.From.DisplayName);
            _invitations.Items.Add(row);
            if (selectedInvite == invitation.RoomId) row.Selected = true;
        }
        _invitations.EndUpdate();
        _roomsEmpty.Visible = _rooms.Items.Count == 0;
        _invitationsEmpty.Visible = _invitations.Items.Count == 0;
        var active = _client.CurrentGameRoom;
        _roomState.Text = active is null
            ? "No active room · create one or right-click a room to join."
            : $"{active.Name} · {active.Members.Length}/{active.MaxPlayers} players · {(active.Visibility == "public" ? "Public" : "Friends only")}";
        RefreshGameHostPicker();
        RefreshFriendPicker();
        RefreshActions();
    }

    private void RefreshGameHostPicker()
    {
        var room = _client.CurrentGameRoom;
        _gameHostPicker.BeginUpdate();
        try
        {
            _gameHostPicker.Items.Clear();
            if (room is not null)
            {
                foreach (var member in room.Members.OrderBy(member => member.Profile.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    var choice = new GameHostChoice(member);
                    _gameHostPicker.Items.Add(choice);
                    if (member.Profile.UserId == (room.GameHostUserId ?? room.Host.UserId)) _gameHostPicker.SelectedItem = choice;
                }
            }
            else
            {
                _gameHostPicker.Items.Add("Join a room to choose its host");
                _gameHostPicker.SelectedIndex = 0;
            }
        }
        finally { _gameHostPicker.EndUpdate(); }

        var gameHost = room is null ? null : DesignatedGameHost(room);
        _hostAddress.Text = gameHost is null
            ? "EDMW host IP: not set"
            : $"EDMW host IP: {OnlineGameAddress.VirtualIpv4(gameHost.RelayPort)} · {gameHost.Profile.DisplayName}";
    }

    private static OnlineGameMember? DesignatedGameHost(OnlineGameRoom room) => room.GameHostUserId is { } userId
        ? room.Members.FirstOrDefault(member => member.Profile.UserId == userId)
        : room.Members.FirstOrDefault(member => member.Profile.UserId == room.Host.UserId);

    private void RefreshActions()
    {
        var connected = _client.IsConnected;
        var active = _client.CurrentGameRoom;
        var host = active is not null && _client.User?.UserId == active.Host.UserId;
        var designatedGameHost = active is not null && _client.User?.UserId ==
            (active.GameHostUserId ?? active.Host.UserId);
        var gameplayTransportsEnabled = _client.Preferences.EnableIceGameplay || _client.Preferences.EnableWssGameplay;
        var friend = _friendPicker.SelectedItem as FriendChoice;
        _create.Enabled = connected && !_busy && active is null;
        _name.Enabled = _visibility.Enabled = _capacity.Enabled = connected && !_busy && active is null;
        _tips.SetToolTip(_create, !connected ? "Connect to the relay first." : _busy ? "A matchmaking request is in progress."
            : active is not null ? "Leave your active game room before creating another."
            : "Set the room name, access, and player limit.");
        _refresh.Enabled = connected && !_busy;
        _friendPicker.Enabled = connected && !_busy && host;
        _invite.Enabled = connected && !_busy && host && friend is not null;
        _leave.Enabled = connected && !_busy && active is not null;
        _gameHostPicker.Enabled = connected && !_busy && host;
        _copyHostAddress.Enabled = active is not null && DesignatedGameHost(active) is not null;
        _prepareNetwork.Enabled = connected && !_busy && active is not null && designatedGameHost &&
            gameplayTransportsEnabled && _prepareGameNetwork is not null && _gameTrafficStatus() is null;
        _setGameHost.Enabled = connected && !_busy && host && _gameHostPicker.SelectedItem is GameHostChoice selectedHost &&
            active?.GameHostUserId != selectedHost.Member.Profile.UserId;
        _tips.SetToolTip(_prepareNetwork, !connected ? "Connect to the relay first." : _busy ? "A matchmaking action is in progress."
            : active is null ? "Join a matchmaking room first."
            : !designatedGameHost ? "Only the designated EDMW host can prepare the room game network."
            : !gameplayTransportsEnabled ? "Enable ICE or WSS gameplay in the matchmaking settings first."
            : _gameTrafficStatus() is not null ? "EDMW already has an active game bridge. Close the game to prepare another room."
            : "Open EDMW's native UI on each room member and configure the room's virtual IP network.");
        _tips.SetToolTip(_setGameHost, !host ? "The matchmaking room host chooses which member is hosting EDMW." :
            _gameHostPicker.SelectedItem is not GameHostChoice ? "Choose the player who will host the EDMW game." :
            "Set the selected member as the EDMW game host.");
        _roomState.ForeColor = active is null ? MedievalTheme.TextSecondary : MedievalTheme.Success;
        _hostAddress.ForeColor = active?.GameHostUserId is null ? MedievalTheme.TextSecondary : MedievalTheme.Success;
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy || IsDisposed) return;
        _busy = true;
        RefreshActions();
        try { await operation(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            var message = string.Equals(error.Message, "Unknown command.", StringComparison.OrdinalIgnoreCase)
                ? "This relay does not support the current matchmaking command. Deploy the updated relay server, then reconnect."
                : error.Message;
            SetStatus(message, error: true);
            MedievalDialog.Show(this, message, "Online games", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            if (!IsDisposed && !Disposing) RefreshLists();
        }
    }

    private void SetStatus(string message, bool error = false)
    {
        if (IsDisposed || Disposing) return;
        _status.Text = message;
        _status.ForeColor = error ? MedievalTheme.Warning : MedievalTheme.TextSecondary;
        _tips.SetToolTip(_status, message);
    }

    private void GameRoomsChanged(object? sender, OnlineGameRoom[] rooms) => OnUi(RefreshLists);
    private void FriendsChanged(object? sender, OnlineFriendsSnapshot snapshot) => OnUi(() =>
    {
        RefreshFriendPicker();
        RefreshActions();
    });
    private void GameInviteReceived(object? sender, OnlineGameInvite invitation) => OnUi(() => { RefreshLists(); SetStatus($"{invitation.From.DisplayName} invited you to {invitation.Name}."); });
    private void GameInvitationsChanged(object? sender, OnlineGameInvite[] invitations) => OnUi(RefreshLists);
    private void GameRoomChanged(object? sender, OnlineGameRoom? room) => OnUi(RefreshLists);
    private void GameRoomEnded(object? sender, string message) => OnUi(() => { RefreshLists(); SetStatus(message); });
    private void StatusChanged(object? sender, string message) => OnUi(() =>
    {
        RefreshFriendPicker();
        RefreshActions();
    });

    private void OnUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed && !Disposing) action(); }); }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }

    private static Control Card(Control content)
    {
        var card = new MedievalCard { Dock = DockStyle.Fill, Padding = new Padding(10, 7, 10, 7), Margin = new Padding(0, 0, 0, 8) };
        card.Controls.Add(content);
        return card;
    }

    private static ListView MakeList(string accessibleName)
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
            MultiSelect = false, AccessibleName = accessibleName, Margin = Padding.Empty,
            BackColor = MedievalTheme.Input, ForeColor = MedievalTheme.Text, BorderStyle = BorderStyle.FixedSingle
        };
        MedievalDialogFrame.StyleListView(list);
        return list;
    }

    private static Button Button(string text, Func<Task> action, bool primary = false)
        => FriendsDialog.MakeButton(text, action, primary);
}

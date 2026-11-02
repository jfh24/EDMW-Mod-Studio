using EDMWMapDesigner.Networking;

namespace EDMWMapDesigner.UI;

/// <summary>Relay-backed public and friends-only game rooms embedded in the Matchmaking tab.</summary>
internal sealed class OnlineGamePanel : UserControl
{
    private sealed record FriendChoice(OnlineProfile Profile)
    {
        public override string ToString() => Profile.DisplayName;
    }

    private readonly OnlineRelayClient _client;
    private readonly Func<Task>? _launchGameplay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly TextBox _name = new() { MaxLength = 80, PlaceholderText = "Game room name", AccessibleName = "Game room name" };
    private readonly ComboBox _visibility = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Game room visibility" };
    private readonly NumericUpDown _capacity = new() { Minimum = 2, Maximum = 8, Value = 8, AccessibleName = "Maximum game room players" };
    private readonly ComboBox _friendPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Height = 32, Width = 160, AccessibleName = "Invite an online friend" };
    private readonly ListView _rooms = MakeList("Available relay game rooms");
    private readonly ListView _invitations = MakeList("Incoming game invitations");
    private readonly Label _roomState = new() { Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextSecondary, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextSecondary, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _create;
    private readonly Button _refresh;
    private readonly Button _invite;
    private readonly Button _joinRoom;
    private readonly Button _joinInvite;
    private readonly Button _leave;
    private readonly Button _launch;
    private bool _busy;

    public OnlineGamePanel(OnlineRelayClient client, Func<Task>? launchGameplay = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _launchGameplay = launchGameplay;
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
        _joinRoom = Button("Join selected room", () => RunAsync(async () =>
        {
            if (SelectedRoom is not { } room) return;
            SetStatus($"Joining {room.Name}…");
            await _client.JoinGameRoomAsync(room.RoomId, _lifetime.Token);
            SetStatus($"Joined {room.Name}.");
        }));
        _joinInvite = Button("Accept invitation", () => RunAsync(async () =>
        {
            if (SelectedInvite is not { } invitation) return;
            SetStatus($"Joining {invitation.Name}…");
            await _client.JoinGameRoomAsync(invitation.RoomId, _lifetime.Token);
            SetStatus($"Joined {invitation.Name}.");
        }));
        _leave = Button("Leave room", () => RunAsync(async () =>
        {
            SetStatus("Leaving game room…");
            await _client.LeaveGameRoomAsync(_lifetime.Token);
            SetStatus("Left the game room.");
        }));
        _launch = Button("Launch EDMW", () => RunAsync(async () =>
        {
            if (_launchGameplay is null) throw new InvalidOperationException("Launch EDMW from the Matchmaking tab.");
            SetStatus("Starting EDMW with the relay bridge…");
            await _launchGameplay();
            SetStatus("EDMW launched. Choose TCP/IP, then host or join a LAN game. Friends status confirms when relay hookup is active.");
        }), primary: true);

        MedievalTheme.StyleTextInput(_name);
        MedievalTheme.StyleTextInput(_visibility);
        MedievalTheme.StyleTextInput(_capacity);
        MedievalTheme.StyleTextInput(_friendPicker);
        _visibility.Items.AddRange(["Friends only", "Public"]);
        _visibility.SelectedIndex = 0;

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
        _friendPicker.SelectedIndexChanged += (_, _) => RefreshActions();
        _name.TextChanged += (_, _) => RefreshActions();

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6, BackColor = Color.Transparent };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.Controls.Add(CreateRoomCard(), 0, 0);
        layout.Controls.Add(RoomListCard(), 0, 1);
        layout.Controls.Add(InvitationCard(), 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        foreach (Control control in new Control[] { _refresh, _friendPicker, _invite, _joinRoom, _joinInvite, _leave, _launch }) actions.Controls.Add(control);
        layout.Controls.Add(_roomState, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        layout.Controls.Add(_status, 0, 5);
        Controls.Add(layout);

        _client.GameRoomsChanged += GameRoomsChanged;
        _client.FriendsChanged += FriendsChanged;
        _client.GameInviteReceived += GameInviteReceived;
        _client.GameInvitationsChanged += GameInvitationsChanged;
        _client.GameRoomChanged += GameRoomChanged;
        _client.GameRoomEnded += GameRoomEnded;
        _client.StatusChanged += StatusChanged;
        Disposed += (_, _) =>
        {
            _lifetime.Cancel();
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
            SetStatus("Connect to the relay from the Friends tab to use matchmaking.");
            return;
        }
        if (_busy) return;
        SetStatus("Checking relay matchmaking support…");
        _ = RunAsync(async () =>
        {
            await _client.RefreshGameRoomsAsync(_lifetime.Token);
            SetStatus("Game rooms refreshed.");
        });
    }

    private OnlineGameRoom? SelectedRoom => _rooms.SelectedItems.Count > 0 ? _rooms.SelectedItems[0].Tag as OnlineGameRoom : null;
    private OnlineGameInvite? SelectedInvite => _invitations.SelectedItems.Count > 0 ? _invitations.SelectedItems[0].Tag as OnlineGameInvite : null;

    private Control CreateRoomCard()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new Label { Text = "Create a game room", Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright, Font = MedievalTheme.DisplayFont(12), TextAlign = ContentAlignment.MiddleLeft };
        content.Controls.Add(heading, 0, 0);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        fields.Controls.Add(MedievalTheme.FrameInput(_name), 0, 0);
        fields.Controls.Add(MedievalTheme.FrameInput(_visibility), 1, 0);
        fields.Controls.Add(MedievalTheme.FrameInput(_capacity), 2, 0);
        fields.Controls.Add(_create, 3, 0);
        content.Controls.Add(fields, 0, 1);
        var hint = new Label { Text = "Invite accepted online friends or publish a public room. After joining, launch EDMW and choose its TCP/IP provider; the relay bridge carries gameplay traffic while you host or join a LAN game.", Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        content.Controls.Add(hint, 0, 2);
        return Card(content);
    }

    private Control RoomListCard()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(new Label { Text = "Available game rooms", Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright, Font = MedievalTheme.DisplayFont(12), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        content.Controls.Add(_rooms, 0, 1);
        return Card(content);
    }

    private Control InvitationCard()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(new Label { Text = "Game invitations", Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright, Font = MedievalTheme.DisplayFont(12), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        content.Controls.Add(_invitations, 0, 1);
        return Card(content);
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
        var active = _client.CurrentGameRoom;
        _roomState.Text = active is null ? "No active room" : $"In {active.Name} · {active.Members.Length}/{active.MaxPlayers} players";
        RefreshFriendPicker();
        RefreshActions();
    }

    private void RefreshActions()
    {
        var connected = _client.IsConnected;
        var active = _client.CurrentGameRoom;
        var host = active is not null && _client.User?.UserId == active.Host.UserId;
        var friend = _friendPicker.SelectedItem as FriendChoice;
        _create.Enabled = connected && !_busy && active is null && !string.IsNullOrWhiteSpace(_name.Text);
        _tips.SetToolTip(_create, !connected ? "Connect to the relay first." : _busy ? "A matchmaking request is in progress."
            : active is not null ? "Leave your active game room before creating another."
            : string.IsNullOrWhiteSpace(_name.Text) ? "Enter a game room name." : "Create a relay game room.");
        _refresh.Enabled = connected && !_busy;
        _friendPicker.Enabled = connected && !_busy && host;
        _invite.Enabled = connected && !_busy && host && friend is not null;
        _joinRoom.Enabled = connected && !_busy && active is null && SelectedRoom is not null;
        _joinInvite.Enabled = connected && !_busy && active is null && SelectedInvite is not null;
        _leave.Enabled = connected && !_busy && active is not null;
        _launch.Enabled = connected && !_busy && active is not null && _launchGameplay is not null;
        _roomState.ForeColor = active is null ? MedievalTheme.TextSecondary : MedievalTheme.Success;
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
                ? "This relay does not support matchmaking yet. Deploy the 1.4.0 relay update, then reconnect."
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

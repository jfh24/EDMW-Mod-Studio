using EDMWMapDesigner.Networking;

namespace EDMWMapDesigner.UI;

/// <summary>Explicit friendship requests and invitations through the configured relay.</summary>
internal sealed class FriendsDialog : Form
{
    private sealed record FriendRow(OnlineProfile Profile, string Relationship);
    private readonly OnlineRelayClient _client;
    private readonly Func<OnlineCollaborationInvite[]> _invitations;
    private readonly Func<bool> _mapWorkspace;
    private readonly Func<bool> _mapSession;
    private readonly Func<Guid, Task> _invite;
    private readonly Func<Guid, Task> _join;
    private readonly Func<Task> _leave;
    private readonly Func<string?> _inviteUnavailableReason;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly TextBox _endpoint;
    private readonly TextBox _displayName;
    private readonly TextBox _requestCode = new() { MaxLength = 64, PlaceholderText = "Paste a friend's code", AccessibleName = "Friend code to request" };
    private readonly Label _friendCode = CopyLabel("Available after connecting", "Your friend code");
    private readonly Label _connectionState = CopyLabel("●  Offline", "Connection state");
    private readonly Label _friendCount = CopyLabel("0 friends", "Friend and request count");
    private readonly Label _friendsEmptyTitle = CopyLabel("Your friends will appear here", "Friends empty state");
    private readonly ListView _friends = MakeList("Friends and friend requests");
    private readonly ListView _inviteList = MakeList("Incoming map invitations");
    private readonly Control _friendsEmpty;
    private readonly Control _invitationsEmpty;
    private readonly Control _relaySettings;
    private readonly Button _relayToggle;
    private readonly TableLayoutPanel _layout;
    private readonly TableLayoutPanel _connectionLayout;
    private readonly Panel _scrollContent;
    private readonly Button _connect;
    private readonly Button _request;
    private readonly Button _accept;
    private readonly Button _remove;
    private readonly Button _inviteButton;
    private readonly Button _leaveButton;
    private readonly Button _joinButton;
    private readonly Button _copy;
    private bool _busy;
    private bool _connecting;

    public FriendsDialog(OnlineRelayClient client, OnlinePreferences preferences,
        Func<OnlineCollaborationInvite[]> invitations, Func<bool> mapWorkspace, Func<bool> mapSession,
        Func<Guid, Task> invite, Func<Guid, Task> join, Func<Task> leave,
        Func<string?>? inviteUnavailableReason = null)
    {
        _client = client;
        _invitations = invitations;
        _mapWorkspace = mapWorkspace;
        _mapSession = mapSession;
        _invite = invite;
        _join = join;
        _leave = leave;
        _inviteUnavailableReason = inviteUnavailableReason ?? (() => null);
        Text = "Online friends";
        ClientSize = new Size(800, 560);
        MinimumSize = new Size(680, 500);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = MedievalTheme.UiFont();
        ForeColor = MedievalTheme.Text;
        BackColor = MedievalTheme.Sidebar;

        _layout = Stack(110, 100, -1, -1);
        _layout.Dock = DockStyle.Top;
        _layout.MinimumSize = new Size(0, 512);
        _layout.RowStyles[2].Height = 55;
        _layout.RowStyles[3].Height = 45;
        _scrollContent = new MedievalBufferedPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty,
            AccessibleName = "Friends content", AutoScrollMinSize = new Size(0, 512)
        };
        _scrollContent.Controls.Add(_layout);
        _scrollContent.SizeChanged += (_, _) => UpdateScrollLayout();
        _endpoint = new TextBox { MaxLength = 2048, Text = preferences.Endpoint, PlaceholderText = OnlinePreferences.DefaultEndpoint, AccessibleName = "Relay HTTPS URL" };
        _displayName = new TextBox { MaxLength = 64, Text = preferences.DisplayName, AccessibleName = "Online display name" };

        _connect = MakeButton("Connect", () => RunAsync(async () =>
        {
            if (_client.IsConnected)
            {
                try { if (_mapSession()) await _leave(); }
                finally { _client.Disconnect(); }
                SetStatus("Disconnected. Connect again whenever you're ready.");
            }
            else
            {
                _connecting = true;
                RefreshActions();
                SetStatus("Connecting… The relay may take a moment to wake up.");
                try
                {
                    await _client.ConnectAsync(_endpoint.Text.Trim(), _displayName.Text.Trim(), _lifetime.Token);
                    if (!IsDisposed) SetStatus("Connected. Share your code or add a friend.");
                }
                finally { _connecting = false; }
            }
        }), primary: true);
        _connectionLayout = Stack(20, 34, 26, 0);
        var nameHeading = Row(132);
        nameHeading.Controls.Add(CopyLabel("Display name"), 0, 0);
        _connectionState.TextAlign = ContentAlignment.MiddleRight;
        nameHeading.Controls.Add(_connectionState, 1, 0);
        _connectionLayout.Controls.Add(nameHeading, 0, 0);
        var nameRow = Row(126);
        MedievalTheme.StyleTextInput(_displayName);
        nameRow.Controls.Add(MedievalTheme.FrameInput(_displayName), 0, 0);
        _connect.Dock = DockStyle.Fill;
        _connect.Margin = new Padding(10, 0, 0, 0);
        nameRow.Controls.Add(_connect, 1, 0);
        _connectionLayout.Controls.Add(nameRow, 0, 1);
        _relaySettings = LabeledInput("Relay URL", _endpoint);
        _relayToggle = MakeButton("▸  Relay settings", () =>
        {
            SetRelaySettingsVisible(!_relaySettings.Visible);
            return Task.CompletedTask;
        });
        _relayToggle.AccessibleName = "Relay settings";
        _relayToggle.Width = 150;
        _relayToggle.Height = 26;
        _connectionLayout.Controls.Add(_relayToggle, 0, 2);
        _relaySettings.Visible = false;
        _relaySettings.Margin = new Padding(0, 8, 0, 0);
        _connectionLayout.Controls.Add(_relaySettings, 0, 3);
        _layout.Controls.Add(Card(_connectionLayout), 0, 0);

        _copy = MakeButton("Copy code", () =>
        {
            if (_client.User is not { } user) return Task.CompletedTask;
            try { Clipboard.SetText(user.FriendCode); SetStatus("Friend code copied. Share it with your friend."); }
            catch (System.Runtime.InteropServices.ExternalException) { SetStatus("The clipboard is busy. Copy the code shown above.", error: true); }
            return Task.CompletedTask;
        });
        var share = Stack(27, 34, -1);
        share.Controls.Add(Heading("Your friend code"), 0, 0);
        var codeRow = Row(104);
        _friendCode.BackColor = MedievalTheme.Input;
        _friendCode.Padding = new Padding(8, 0, 4, 0);
        _friendCode.Font = MedievalTheme.UiFont(9.5f, FontStyle.Bold);
        codeRow.Controls.Add(_friendCode, 0, 0);
        _copy.Dock = DockStyle.Fill;
        _copy.Margin = new Padding(8, 0, 0, 0);
        codeRow.Controls.Add(_copy, 1, 0);
        share.Controls.Add(codeRow, 0, 1);

        _request = MakeButton("Send request", () => RunAsync(async () =>
        {
            await _client.RequestFriendAsync(_requestCode.Text.Trim());
            _requestCode.Clear();
            SetStatus("Friend request sent. Your friend must accept it.");
        }));
        var add = Stack(27, 34, -1);
        add.Controls.Add(Heading("Add a friend"), 0, 0);
        var requestRow = Row(118);
        MedievalTheme.StyleTextInput(_requestCode);
        requestRow.Controls.Add(MedievalTheme.FrameInput(_requestCode), 0, 0);
        _request.Dock = DockStyle.Fill;
        _request.Margin = new Padding(8, 0, 0, 0);
        requestRow.Controls.Add(_request, 1, 0);
        add.Controls.Add(requestRow, 0, 1);
        var codes = new MedievalBufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        codes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        codes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        codes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var shareCard = Card(share);
        shareCard.Margin = new Padding(0, 0, 6, 12);
        var addCard = Card(add);
        addCard.Margin = new Padding(6, 0, 0, 12);
        codes.Controls.Add(shareCard, 0, 0);
        codes.Controls.Add(addCard, 1, 0);
        _layout.Controls.Add(codes, 0, 1);

        _friends.Columns.Add("Name", 250);
        _friends.Columns.Add("Relationship", 180);
        _friends.Columns.Add("Presence", 170);
        _friends.SizeChanged += (_, _) => FitColumns(_friends, 0.42f, 0.34f);
        _friends.SelectedIndexChanged += (_, _) => RefreshActions();
        _accept = MakeButton("Accept request", () => RunAsync(async () =>
        {
            if (SelectedFriend is not { Relationship: "Incoming request" } row) return;
            await _client.AcceptFriendAsync(row.Profile.UserId);
            SetStatus("Friend request accepted.");
        }));
        _remove = MakeButton("Remove friend", () => RunAsync(async () =>
        {
            if (SelectedFriend is not { } row) return;
            await _client.RemoveFriendAsync(row.Profile.UserId);
            SetStatus(row.Relationship == "Friend" ? "Friend removed." : "Friend request removed.");
        }));
        _remove.Width = 136;
        _inviteButton = MakeButton("Invite to map", () => RunAsync(async () =>
        {
            if (SelectedFriend is not { } row) return;
            await _invite(row.Profile.UserId);
            SetStatus("Map invitation sent. Your friend can join from Friends.");
        }));
        var friendsCard = Stack(28, -1, 34);
        friendsCard.Controls.Add(Heading("Friends", _friendCount, 200), 0, 0);
        _friendsEmpty = EmptyState(_friendsEmptyTitle);
        friendsCard.Controls.Add(ListHost(_friends, _friendsEmpty), 0, 1);
        friendsCard.Controls.Add(Actions(_accept, _remove, _inviteButton), 0, 2);
        _layout.Controls.Add(Card(friendsCard), 0, 2);

        _inviteList.Columns.Add("Map", 250);
        _inviteList.Columns.Add("From", 350);
        _inviteList.SizeChanged += (_, _) => FitColumns(_inviteList, 0.55f);
        _inviteList.SelectedIndexChanged += (_, _) => RefreshActions();
        _joinButton = MakeButton("Join map", () => RunAsync(async () =>
        {
            if (_inviteList.SelectedItems.Count == 0 || _inviteList.SelectedItems[0].Tag is not OnlineCollaborationInvite invitation) return;
            await _join(invitation.SessionId);
            SetStatus(_mapSession() ? "Joined the online map session." : "Map invitation kept.");
        }));
        _leaveButton = MakeButton("Leave session", () => RunAsync(async () => { await _leave(); SetStatus("Left online map session."); }));
        var invitationCard = Stack(28, -1, 34);
        invitationCard.Controls.Add(Heading("Map invitations"), 0, 0);
        _invitationsEmpty = EmptyState(CopyLabel("No map invitations yet", "Map invitations empty state"));
        invitationCard.Controls.Add(ListHost(_inviteList, _invitationsEmpty), 0, 1);
        invitationCard.Controls.Add(Actions(_joinButton, _leaveButton), 0, 2);
        _layout.Controls.Add(Card(invitationCard), 0, 3);

        var shell = Stack(-1);
        shell.Padding = new Padding(18);
        shell.Controls.Add(_scrollContent, 0, 0);
        AcceptButton = _connect;
        MedievalDialogFrame.Apply(this, shell);

        _displayName.TextChanged += (_, _) => RefreshActions();
        _endpoint.TextChanged += (_, _) => RefreshActions();
        _requestCode.TextChanged += (_, _) => RefreshActions();
        _requestCode.Enter += (_, _) => UpdateDefaultAction();
        _requestCode.Leave += (_, _) => AcceptButton = _client.IsConnected ? null : _connect;
        _client.FriendsChanged += FriendsChanged;
        _client.CollaborationInviteReceived += InvitationReceived;
        _client.StatusChanged += StatusChanged;
        Shown += (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(MinimumSize.Width, Math.Min(MinimumSize.Height, area.Height));
            Height = Math.Min(Height, area.Height);
            if (StartPosition != FormStartPosition.Manual)
                Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
                    Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
            UpdateScrollLayout();
            RefreshLists();
            if (!_client.IsConnected) _displayName.Focus();
        };
        FormClosed += (_, _) =>
        {
            _lifetime.Cancel();
            _client.FriendsChanged -= FriendsChanged;
            _client.CollaborationInviteReceived -= InvitationReceived;
            _client.StatusChanged -= StatusChanged;
            _tips.Dispose();
        };
    }

    private FriendRow? SelectedFriend => _friends.SelectedItems.Count > 0 ? _friends.SelectedItems[0].Tag as FriendRow : null;

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }
    private void FriendsChanged(object? sender, OnlineFriendsSnapshot snapshot) => OnUi(RefreshLists);
    private void InvitationReceived(object? sender, OnlineCollaborationInvite invitation) => OnUi(RefreshLists);
    private void StatusChanged(object? sender, string message) => OnUi(() => { SetStatus(message); RefreshLists(); });

    private void SetRelaySettingsVisible(bool visible)
    {
        // Use the already-scaled row height so this also works at higher DPI.
        var unit = _connectionLayout.RowStyles[2].Height / 26f;
        _layout.SuspendLayout();
        _connectionLayout.SuspendLayout();
        _relaySettings.Visible = visible;
        _relayToggle.Text = (visible ? "▾" : "▸") + "  Relay settings";
        _connectionLayout.RowStyles[3].Height = visible ? 60 * unit : 0;
        _layout.RowStyles[0].Height = (visible ? 170 : 110) * unit;
        _layout.MinimumSize = new Size(0, (int)Math.Ceiling((visible ? 572 : 512) * unit));
        UpdateScrollLayout();
        _connectionLayout.ResumeLayout(true);
        _layout.ResumeLayout(true);
        if (visible && _endpoint.Enabled) _endpoint.Focus();
    }

    private void UpdateScrollLayout()
    {
        _scrollContent.AutoScrollMinSize = _layout.MinimumSize;
        _layout.Height = Math.Max(_layout.MinimumSize.Height, _scrollContent.ClientSize.Height);
    }

    private void RefreshLists()
    {
        if (IsDisposed || Disposing) return;
        var selected = SelectedFriend?.Profile.UserId;
        _friends.BeginUpdate();
        _friends.Items.Clear();
        if (_client.IsConnected)
        {
            var snapshot = _client.Friends;
            void Add(IEnumerable<OnlineProfile> profiles, string relationship)
            {
                foreach (var profile in profiles.OrderBy(friend => friend.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    var row = new ListViewItem(profile.DisplayName) { Tag = new FriendRow(profile, relationship), ToolTipText = profile.FriendCode };
                    row.SubItems.Add(relationship);
                    row.SubItems.Add(profile.Online ? "Online" : "Offline");
                    _friends.Items.Add(row);
                    if (selected == profile.UserId) row.Selected = true;
                }
            }
            Add(snapshot.Friends, "Friend");
            Add(snapshot.Incoming, "Incoming request");
            Add(snapshot.Outgoing, "Sent request");
            var count = snapshot.Friends.Length;
            var requests = snapshot.Incoming.Length + snapshot.Outgoing.Length;
            _friendCount.Text = count + (count == 1 ? " friend" : " friends") +
                (requests > 0 ? " · " + requests + (requests == 1 ? " request" : " requests") : "");
        }
        else _friendCount.Text = "0 friends";
        _friends.EndUpdate();
        _friends.Visible = _friends.Items.Count > 0;
        _friendsEmpty.Visible = !_friends.Visible;
        _friendsEmptyTitle.Text = "Your friends will appear here";
        _inviteList.BeginUpdate();
        var selectedInvite = _inviteList.SelectedItems.Count > 0 ? (_inviteList.SelectedItems[0].Tag as OnlineCollaborationInvite)?.SessionId : null;
        _inviteList.Items.Clear();
        foreach (var invitation in _invitations())
        {
            var row = new ListViewItem(invitation.MapName) { Tag = invitation };
            row.SubItems.Add(invitation.From.DisplayName);
            _inviteList.Items.Add(row);
            if (selectedInvite == invitation.SessionId) row.Selected = true;
        }
        _inviteList.EndUpdate();
        _inviteList.Visible = _inviteList.Items.Count > 0;
        _invitationsEmpty.Visible = !_inviteList.Visible;
        _friendCode.Text = _client.IsConnected && _client.User is { } user ? user.FriendCode : "Available after connecting";
        _friendCode.ForeColor = _client.IsConnected ? MedievalTheme.GoldBright : MedievalTheme.TextSecondary;
        _tips.SetToolTip(_friendCode, _friendCode.Text);
        RefreshActions();
    }

    private void RefreshActions()
    {
        var connected = _client.IsConnected;
        var ready = !_busy && connected;
        var selected = SelectedFriend;
        var inviteUnavailable = _inviteUnavailableReason();
        _connectionState.Text = _connecting ? "●  Connecting…" : connected ? "●  Online" : "●  Offline";
        _connectionState.ForeColor = connected ? MedievalTheme.Success : MedievalTheme.TextSecondary;
        _connect.Text = _connecting ? "Connecting…" : connected ? "Disconnect" : "Connect";
        _connect.AccessibleName = _connect.Text;
        ((FriendActionButton)_connect).Primary = !connected;
        _connect.Enabled = !_busy && (connected || (!string.IsNullOrWhiteSpace(_displayName.Text) && !string.IsNullOrWhiteSpace(_endpoint.Text)));
        _endpoint.Enabled = _displayName.Enabled = !_busy && !connected;
        _requestCode.Enabled = ready;
        _request.Enabled = ready && !string.IsNullOrWhiteSpace(_requestCode.Text);
        _copy.Enabled = ready;
        _accept.Enabled = ready && selected?.Relationship == "Incoming request";
        _remove.Text = selected?.Relationship switch { "Incoming request" => "Decline request", "Sent request" => "Cancel request", _ => "Remove friend" };
        _remove.AccessibleName = _remove.Text;
        _remove.Enabled = ready && selected is not null;
        _inviteButton.Enabled = ready && _mapWorkspace() && inviteUnavailable is null && selected is { Relationship: "Friend", Profile.Online: true };
        _leaveButton.Visible = _mapSession();
        _leaveButton.Enabled = ready && _mapSession();
        _joinButton.Enabled = ready && !_mapSession() && _inviteList.SelectedItems.Count > 0;
        var selectionHint = !connected ? "Connect to manage friends and requests." : selected switch
        {
            { Relationship: "Incoming request" } => "Accept this request to become friends, or decline it.",
            { Relationship: "Sent request" } => "Waiting for your friend to accept. You can cancel this request.",
            { Profile.Online: false } => "This friend is offline. Invite them when they're online.",
            not null when !_mapWorkspace() => "Open the Map Generator to invite this friend.",
            not null when inviteUnavailable is not null => inviteUnavailable,
            not null => "Invite this friend to edit your map together.",
            _ => "Select a friend or request to see available actions."
        };
        _tips.SetToolTip(_inviteButton, selectionHint);
        _tips.SetToolTip(_joinButton, _mapSession() ? "Leave the current map session before joining another." : "Select an invitation to join.");
        UpdateDefaultAction();
    }

    private void UpdateDefaultAction() => AcceptButton = _requestCode.ContainsFocus ? _request : _client.IsConnected ? null : _connect;

    private void SetStatus(string message, bool error = false)
    {
        if (IsDisposed || Disposing) return;
        _tips.SetToolTip(_connectionState, message);
        if (error)
            MedievalDialog.Show(this, message, "Online friends", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy || IsDisposed) return;
        _busy = true;
        RefreshActions();
        try { await operation(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { SetStatus(error.Message, error: true); }
        finally
        {
            _busy = false;
            if (!IsDisposed && !Disposing) RefreshLists();
        }
    }

    private void OnUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed && !Disposing) action(); }); }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }

    private static ListView MakeList(string name)
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
            MultiSelect = false, ShowItemToolTips = true, AccessibleName = name, Margin = new Padding(0, 4, 0, 4),
            BackColor = MedievalTheme.Input, ForeColor = MedievalTheme.Text, BorderStyle = BorderStyle.FixedSingle
        };
        MedievalDialogFrame.StyleListView(list);
        return list;
    }

    internal static void FitColumns(ListView list, params float[] portions)
    {
        if (list.Columns.Count == 0) return;
        var remaining = Math.Max(0, list.ClientSize.Width);
        for (var index = 0; index < list.Columns.Count - 1; index++)
        {
            var width = Math.Min(remaining, (int)(list.ClientSize.Width * portions[index]));
            list.Columns[index].Width = width;
            remaining -= width;
        }
        list.Columns[^1].Width = remaining;
    }

    private static Label CopyLabel(string text, string? accessibleName = null) => new()
    {
        Text = text, AccessibleName = accessibleName ?? text, Dock = DockStyle.Fill,
        Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        ForeColor = MedievalTheme.TextSecondary, UseMnemonic = false
    };

    private static Control Heading(string text, Label? detail = null, int detailWidth = 100)
    {
        var title = CopyLabel(text);
        title.Font = MedievalTheme.DisplayFont(12);
        title.ForeColor = MedievalTheme.GoldBright;
        if (detail is null) return title;
        var row = Row(detailWidth);
        detail.TextAlign = ContentAlignment.MiddleRight;
        row.Controls.Add(title, 0, 0);
        row.Controls.Add(detail, 1, 0);
        return row;
    }

    private static TableLayoutPanel Stack(params int[] heights)
    {
        var stack = new MedievalBufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length, Margin = Padding.Empty
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in heights) stack.RowStyles.Add(new RowStyle(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
        return stack;
    }

    private static Control Card(Control content)
    {
        var card = new MedievalCard { Dock = DockStyle.Fill, Padding = new Padding(12, 9, 12, 9), Margin = new Padding(0, 0, 0, 12) };
        card.Controls.Add(content);
        return card;
    }

    private static Control EmptyState(Label title)
    {
        title.TextAlign = ContentAlignment.MiddleCenter;
        title.ForeColor = MedievalTheme.Text;
        title.Font = MedievalTheme.UiFont(10, FontStyle.Bold);
        return title;
    }

    private static Control ListHost(ListView list, Control empty)
    {
        var host = new MedievalBufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        host.Controls.Add(list);
        host.Controls.Add(empty);
        return host;
    }

    private static Control LabeledInput(string label, TextBox input)
    {
        MedievalTheme.StyleTextInput(input);
        var panel = Stack(20, -1);
        panel.Controls.Add(CopyLabel(label), 0, 0);
        panel.Controls.Add(MedievalTheme.FrameInput(input), 0, 1);
        return panel;
    }

    private static TableLayoutPanel Row(int lastWidth)
    {
        var row = new MedievalBufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, lastWidth));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return row;
    }

    private static Control Actions(params Button[] buttons)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        foreach (var button in buttons) panel.Controls.Add(button);
        return panel;
    }

    private static Button MakeButton(string text, Func<Task> action, bool primary = false)
    {
        var button = new FriendActionButton
        {
            Text = text, Height = 32, Width = Math.Max(92, TextRenderer.MeasureText(text, MedievalTheme.UiFont(9.25f, FontStyle.Bold)).Width + 24),
            Margin = new Padding(0, 0, 8, 0), AccessibleName = text, Primary = primary
        };
        MedievalTheme.StyleSecondaryButton(button);
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>Native disabled flat buttons paint black text; draw legible states and keyboard focus.</summary>
    private sealed class FriendActionButton : Button
    {
        private bool _hovered;
        private bool _pressed;
        private bool _primary;
        [System.ComponentModel.DefaultValue(false)]
        public bool Primary { get => _primary; set { _primary = value; Invalidate(); } }

        public FriendActionButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UseVisualStyleBackColor = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var fill = !Enabled ? MedievalTheme.Surface : _pressed ? MedievalTheme.Border :
                Primary ? Color.FromArgb(94, 65, 23) : _hovered ? MedievalTheme.SurfaceRaised : MedievalTheme.Surface;
            using var background = new SolidBrush(fill);
            using var border = new Pen(Enabled && (Primary || _hovered) ? MedievalTheme.Gold : MedievalTheme.Border);
            e.Graphics.FillRectangle(background, ClientRectangle);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Inflate(ClientRectangle, -5, -2),
                Enabled ? Primary ? MedievalTheme.GoldBright : MedievalTheme.Text : MedievalTheme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), MedievalTheme.GoldBright, fill);
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); _pressed = _hovered = false; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = _pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = e.Button == MouseButtons.Left; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) { _pressed = true; Invalidate(); } }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); _pressed = false; Invalidate(); }
    }
}


internal sealed class OnlineFriendPickerDialog : Form
{
    private readonly ListView _friends;
    public OnlineProfile[] SelectedFriends => _friends.CheckedItems.Cast<ListViewItem>().Select(row => (OnlineProfile)row.Tag!).ToArray();

    public OnlineFriendPickerDialog(string packageName, OnlineProfile[] friends)
    {
        Text = "Export to friends";
        ClientSize = new Size(520, 345);
        MinimumSize = new Size(460, 320);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = MedievalTheme.UiFont();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(new Label { Text = packageName, Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright, AutoEllipsis = true, Font = MedievalTheme.DisplayFont(15), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _friends = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true,
            BackColor = MedievalTheme.Input, ForeColor = MedievalTheme.Text, AccessibleName = "Select online friends to receive this export"
        };
        _friends.Columns.Add("Online friend", 290);
        _friends.Columns.Add("Friend code", 155);
        _friends.SizeChanged += (_, _) => FriendsDialog.FitColumns(_friends, 0.65f);
        MedievalDialogFrame.StyleListView(_friends);
        foreach (var friend in friends.OrderBy(friend => friend.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var row = new ListViewItem(friend.DisplayName) { Tag = friend };
            row.SubItems.Add(friend.FriendCode);
            _friends.Items.Add(row);
        }
        layout.Controls.Add(_friends, 0, 1);
        layout.Controls.Add(new Label
        {
            Text = friends.Length == 0 ? "No accepted friends are online. Connect and add friends from Friends."
                : "Select friends to receive this export. Sending also installs it on this computer. Each friend reviews and accepts their installation.",
            Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextMuted
        }, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, MinimumSize = new Size(84, 30) };
        var send = new Button { Text = "Send", AutoSize = true, DialogResult = DialogResult.OK, Enabled = false, MinimumSize = new Size(84, 30), AccessibleName = "Send export to selected friends" };
        MedievalTheme.StyleSecondaryButton(cancel);
        MedievalTheme.StyleSecondaryButton(send);
        _friends.ItemChecked += (_, _) => send.Enabled = _friends.CheckedItems.Count > 0;
        actions.Controls.Add(cancel);
        actions.Controls.Add(send);
        layout.Controls.Add(actions, 0, 3);
        AcceptButton = send;
        CancelButton = cancel;
        MedievalDialogFrame.Apply(this, layout);
    }
}

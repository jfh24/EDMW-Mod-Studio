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
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBox _endpoint;
    private readonly TextBox _displayName;
    private readonly TextBox _requestCode = new() { Dock = DockStyle.Fill, MaxLength = 64, PlaceholderText = "Friend code", AccessibleName = "Friend code to request" };
    private readonly Label _friendCode = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = MedievalTheme.GoldBright, AccessibleName = "Your friend code" };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = MedievalTheme.TextMuted, AccessibleName = "Online friends status", Text = "Connect to your Render relay to manage friends." };
    private readonly ListView _friends = MakeList("Friends and friend requests");
    private readonly ListView _inviteList = MakeList("Incoming map invitations");
    private readonly Button _connect;
    private readonly Button _request;
    private readonly Button _accept;
    private readonly Button _remove;
    private readonly Button _inviteButton;
    private readonly Button _leaveButton;
    private readonly Button _joinButton;
    private readonly Button _copy;
    private bool _busy;

    public FriendsDialog(OnlineRelayClient client, OnlinePreferences preferences,
        Func<OnlineCollaborationInvite[]> invitations, Func<bool> mapWorkspace, Func<bool> mapSession,
        Func<Guid, Task> invite, Func<Guid, Task> join, Func<Task> leave)
    {
        _client = client;
        _invitations = invitations;
        _mapWorkspace = mapWorkspace;
        _mapSession = mapSession;
        _invite = invite;
        _join = join;
        _leave = leave;
        Text = "Online friends";
        ClientSize = new Size(700, 650);
        MinimumSize = new Size(600, 570);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = MedievalTheme.UiFont();
        ForeColor = MedievalTheme.Text;
        BackColor = MedievalTheme.Sidebar;
        var layout = new MedievalBufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 11,
            BackColor = MedievalTheme.Sidebar
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 54, 54, 36, 38 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        _endpoint = new TextBox { Dock = DockStyle.Fill, MaxLength = 2048, Text = preferences.Endpoint, PlaceholderText = "https://your-relay.onrender.com", AccessibleName = "Relay HTTPS URL" };
        _displayName = new TextBox { Dock = DockStyle.Fill, MaxLength = 64, Text = preferences.DisplayName, AccessibleName = "Online display name" };
        layout.Controls.Add(LabeledInput("Relay URL", _endpoint), 0, 0);
        _connect = MakeButton("Connect", () => RunAsync(async () =>
        {
            if (_client.IsConnected)
            {
                try { if (_mapSession()) await _leave(); }
                finally { _client.Disconnect(); }
                _status.Text = "Disconnected.";
            }
            else
            {
                _status.Text = "Connecting…";
                await _client.ConnectAsync(_endpoint.Text.Trim(), _displayName.Text.Trim(), _lifetime.Token);
                if (!IsDisposed) _status.Text = "Connected. Share your code to add friends.";
            }
        }));
        var nameRow = Row(2, 120);
        nameRow.Controls.Add(LabeledInput("Display name", _displayName), 0, 0);
        nameRow.Controls.Add(_connect, 1, 0);
        layout.Controls.Add(nameRow, 0, 1);
        var codeRow = Row(2, 100);
        codeRow.Controls.Add(_friendCode, 0, 0);
        _copy = MakeButton("Copy code", () =>
        {
            if (_client.User is not { } user) return Task.CompletedTask;
            try { Clipboard.SetText(user.FriendCode); _status.Text = "Friend code copied."; }
            catch (System.Runtime.InteropServices.ExternalException) { _status.Text = "The clipboard is busy. Copy the code shown above."; }
            return Task.CompletedTask;
        });
        codeRow.Controls.Add(_copy, 1, 0);
        layout.Controls.Add(codeRow, 0, 2);
        MedievalTheme.StyleTextInput(_requestCode);
        _request = MakeButton("Send request", () => RunAsync(async () =>
        {
            await _client.RequestFriendAsync(_requestCode.Text.Trim());
            _requestCode.Clear();
            _status.Text = "Friend request sent. Your friend must accept it.";
        }));
        var requestRow = Row(2, 120);
        requestRow.Controls.Add(MedievalTheme.FrameInput(_requestCode), 0, 0);
        requestRow.Controls.Add(_request, 1, 0);
        layout.Controls.Add(requestRow, 0, 3);
        _friends.Columns.Add("Name", 250);
        _friends.Columns.Add("Relationship", 180);
        _friends.Columns.Add("Presence", 170);
        _friends.SizeChanged += (_, _) => FitColumns(_friends, 0.42f, 0.32f);
        _friends.SelectedIndexChanged += (_, _) => RefreshActions();
        layout.Controls.Add(_friends, 0, 4);
        _accept = MakeButton("Accept", () => RunAsync(async () =>
        {
            if (SelectedFriend is { Relationship: "Incoming request" } row) await _client.AcceptFriendAsync(row.Profile.UserId);
            _status.Text = "Friend request accepted.";
        }));
        _remove = MakeButton("Remove/cancel", () => RunAsync(async () =>
        {
            if (SelectedFriend is { } row) await _client.RemoveFriendAsync(row.Profile.UserId);
            _status.Text = "Friendship or request removed.";
        }));
        _inviteButton = MakeButton("Invite to map", () => RunAsync(async () =>
        {
            if (SelectedFriend is { } row) await _invite(row.Profile.UserId);
            _status.Text = "Map invitation sent. Your friend can join from Friends.";
        }));
        _leaveButton = MakeButton("Leave map session", () => RunAsync(async () => { await _leave(); _status.Text = "Left online map session."; }));
        layout.Controls.Add(Actions(_accept, _remove, _inviteButton, _leaveButton), 0, 5);
        layout.Controls.Add(new Label
        {
            Text = "Incoming map invitations", Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright,
            TextAlign = ContentAlignment.MiddleLeft, Font = MedievalTheme.UiFont(9, FontStyle.Bold)
        }, 0, 6);
        _inviteList.Columns.Add("Map", 250);
        _inviteList.Columns.Add("From", 350);
        _inviteList.SizeChanged += (_, _) => FitColumns(_inviteList, 0.55f);
        _inviteList.SelectedIndexChanged += (_, _) => RefreshActions();
        layout.Controls.Add(_inviteList, 0, 7);
        _joinButton = MakeButton("Join selected", () => RunAsync(async () =>
        {
            if (_inviteList.SelectedItems.Count == 0 || _inviteList.SelectedItems[0].Tag is not OnlineCollaborationInvite invitation) return;
            await _join(invitation.SessionId);
            _status.Text = _mapSession() ? "Joined the online map session." : "Map invitation kept.";
        }));
        layout.Controls.Add(Actions(_joinButton), 0, 8);
        layout.Controls.Add(_status, 0, 9);
        var done = MakeButton("Done", () => { Close(); return Task.CompletedTask; });
        layout.Controls.Add(Actions(done), 0, 10);
        CancelButton = done;
        MedievalDialogFrame.Apply(this, layout);
        _client.FriendsChanged += FriendsChanged;
        _client.CollaborationInviteReceived += InvitationReceived;
        _client.StatusChanged += StatusChanged;
        Shown += (_, _) => RefreshLists();
        FormClosed += (_, _) =>
        {
            _lifetime.Cancel();
            _client.FriendsChanged -= FriendsChanged;
            _client.CollaborationInviteReceived -= InvitationReceived;
            _client.StatusChanged -= StatusChanged;
        };
    }

    private FriendRow? SelectedFriend => _friends.SelectedItems.Count > 0 ? _friends.SelectedItems[0].Tag as FriendRow : null;
    private void FriendsChanged(object? sender, OnlineFriendsSnapshot snapshot) => OnUi(RefreshLists);
    private void InvitationReceived(object? sender, OnlineCollaborationInvite invitation) => OnUi(RefreshLists);
    private void StatusChanged(object? sender, string message) => OnUi(() => { _status.Text = message; RefreshLists(); });

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
        }
        _friends.EndUpdate();
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
        _friendCode.Text = _client.IsConnected && _client.User is { } user ? "Your friend code: " + user.FriendCode : "Your friend code appears after connecting.";
        RefreshActions();
    }

    private void RefreshActions()
    {
        var ready = !_busy && _client.IsConnected;
        var selected = SelectedFriend;
        _connect.Text = _client.IsConnected ? "Disconnect" : "Connect";
        _connect.Enabled = !_busy;
        _endpoint.Enabled = _displayName.Enabled = !_busy && !_client.IsConnected;
        _requestCode.Enabled = _request.Enabled = ready;
        _copy.Enabled = ready;
        _accept.Enabled = ready && selected?.Relationship == "Incoming request";
        _remove.Enabled = ready && selected is not null;
        _inviteButton.Enabled = ready && _mapWorkspace() && selected is { Relationship: "Friend", Profile.Online: true };
        _leaveButton.Enabled = ready && _mapSession();
        _joinButton.Enabled = ready && !_mapSession() && _inviteList.SelectedItems.Count > 0;
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy || IsDisposed) return;
        _busy = true;
        RefreshActions();
        try { await operation(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!IsDisposed && !Disposing) _status.Text = error.Message; }
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
            MultiSelect = false, ShowItemToolTips = true, AccessibleName = name,
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

    private static Control LabeledInput(string label, TextBox input)
    {
        MedievalTheme.StyleTextInput(input);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextMuted }, 0, 0);
        panel.Controls.Add(MedievalTheme.FrameInput(input), 0, 1);
        return panel;
    }

    private static TableLayoutPanel Row(int columns, int lastWidth)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = 1, Margin = Padding.Empty };
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

    private static Button MakeButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30, MinimumSize = new Size(84, 30), Margin = new Padding(0, 5, 7, 3), AccessibleName = text };
        MedievalTheme.StyleSecondaryButton(button);
        button.Click += async (_, _) => await action();
        return button;
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

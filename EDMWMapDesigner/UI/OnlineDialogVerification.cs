using EDMWMapDesigner.Networking;

namespace EDMWMapDesigner.UI;

/// <summary>Inspects online entry points and dialog bounds without connecting or saving credentials.</summary>
internal static class OnlineDialogVerification
{
    public static IReadOnlyList<string> Verify(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var checks = new List<string>();
        checks.AddRange(OnlineMapRebase.Verify());
        using (var main = new MainForm(verificationMode: true)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false
        })
        {
            main.Show();
            main.AutoScaleMode = AutoScaleMode.None;
            foreach (var mapWorkspace in new[] { false, true })
            {
                main.ShowWorkspaceForVerification(mapWorkspace);
                foreach (var size in new[] { new Size(1200, 760), new Size(1920, 1080) })
                {
                    main.ClientSize = size;
                    LayoutAll(main);
                    Application.DoEvents();
                    LayoutAll(main);
                    var toolbar = Descendants(main).OfType<ToolStrip>().Single(strip => strip.Items.Cast<ToolStripItem>().Any(item => item.Text == "Friends"));
                    var friends = toolbar.Items.Cast<ToolStripItem>().Single(item => item.Text == "Friends");
                    Require(friends.Available && friends.Enabled && friends.Placement == ToolStripItemPlacement.Main && toolbar.ClientRectangle.Contains(friends.Bounds), "Friends is directly accessible on Home and map workspaces.");
                    if (mapWorkspace)
                    {
                        foreach (var label in new[] { "Export over LAN", "Export to friends" })
                        {
                            var export = toolbar.Items.Cast<ToolStripItem>().Single(item => item.Text == label);
                            Require(export.Available && export.Enabled && export.Placement == ToolStripItemPlacement.Main && toolbar.ClientRectangle.Contains(export.Bounds), label + " remains directly accessible at " + size.Width + " pixels.");
                        }
                    }
                    Snapshot(toolbar, Path.Combine(outputDirectory, (mapWorkspace ? "online-map-toolbar-" : "online-home-toolbar-") + size.Width + ".png"));
                }
            }
            var editor = main.OpenCivilizationEditorForVerification();
            LayoutAll(main);
            Application.DoEvents();
            var gameMenu = Descendants(editor).OfType<Button>().Single(button => button.Text == "Game ▾");
            Require(gameMenu.ContextMenuStrip!.Items.Cast<ToolStripItem>().Any(item => item.Text == "Export to friends"), "Civilizations offers Export to friends in Game.");
            var civToolbar = Descendants(main).OfType<ToolStrip>().Single(strip => strip.Items.Cast<ToolStripItem>().Any(item => item.Text == "Friends"));
            Require(civToolbar.Items.Cast<ToolStripItem>().Single(item => item.Text == "Friends").Available, "Friends remains available in Civilizations.");
            main.Hide();
            checks.Add("Friends is available in every workspace; maps retain direct LAN and friends export at 1200 and 1920 pixels; civilization Game offers both exports.");
        }

        var preferences = new OnlinePreferences { PersistIdentity = false, Endpoint = "https://edmw-relay.onrender.com", DisplayName = "Offline layout check" };
        using var client = new OnlineRelayClient(preferences);
        using (var friends = new FriendsDialog(client, preferences, () => [], () => true, () => false,
            _ => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask))
        {
            Prepare(friends);
            foreach (var size in new[] { friends.ClientSize, new Size(602, 604) })
            {
                friends.ClientSize = size;
                VerifyBounds(friends);
                Snapshot(friends, Path.Combine(outputDirectory, "online-friends-" + friends.ClientSize.Width + ".png"));
            }
            var connect = Descendants(friends).OfType<Button>().Single(button => button.Text == "Connect");
            Require(connect.Enabled && !Descendants(friends).OfType<Button>().Single(button => button.Text == "Send request").Enabled,
                "Disconnected state enables Connect and requires a connection before requesting friends.");
            friends.Hide();
            checks.Add("Themed Friends dialog keeps connection, requests, friend actions, invitations and status inside its standard and minimum bounds while disconnected.");
        }
        var profiles = new[] { new OnlineProfile(Guid.NewGuid(), "Accepted online friend", "ABCD-EFGH", true), new OnlineProfile(Guid.NewGuid(), "Another friend", "JKLM-NPQR", true) };
        using (var picker = new OnlineFriendPickerDialog("Verification export", profiles))
        {
            Prepare(picker);
            var send = Descendants(picker).OfType<Button>().Single(button => button.Text == "Send");
            var list = Descendants(picker).OfType<ListView>().Single();
            Require(list.CheckBoxes && !send.Enabled && picker.SelectedFriends.Length == 0, "Friend exports require an explicit recipient selection.");
            list.Items[0].Checked = true;
            Application.DoEvents();
            Require(send.Enabled && picker.SelectedFriends.Select(friend => friend.UserId).SequenceEqual(new[] { profiles[0].UserId }), "Only checked friends become export recipients.");
            foreach (var size in new[] { picker.ClientSize, new Size(462, 354) })
            {
                picker.ClientSize = size;
                VerifyBounds(picker);
                Snapshot(picker, Path.Combine(outputDirectory, "online-friend-picker-" + picker.ClientSize.Width + ".png"));
            }
            picker.Hide();
            checks.Add("Friend picker stays within standard and minimum bounds and enables Send only after explicit selection of an online accepted friend.");
        }
        return checks;
    }

    private static void Prepare(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.None;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.ShowInTaskbar = false;
        form.Show();
        LayoutAll(form);
        Application.DoEvents();
    }

    private static void VerifyBounds(Form form)
    {
        LayoutAll(form);
        Application.DoEvents();
        LayoutAll(form);
        var controls = Descendants(form).Where(control => control.Visible && control is Button or TextBox or ListView).ToArray();
        foreach (var control in controls)
        {
            var bounds = new Rectangle(form.PointToClient(control.Parent!.PointToScreen(control.Location)), control.Size);
            Require(control.Width > 0 && control.Height > 0 && form.ClientRectangle.Contains(bounds) && control.Parent.ClientRectangle.Contains(control.Bounds),
                (control.AccessibleName ?? control.Text) + " stays inside its parent and dialog at " + form.ClientSize + ".");
        }
        var inputs = controls.Where(control => control.Name != MedievalDialogFrame.CloseButtonName).ToArray();
        for (var index = 0; index < inputs.Length; index++)
            for (var next = index + 1; next < inputs.Length; next++)
            {
                var a = new Rectangle(form.PointToClient(inputs[index].Parent!.PointToScreen(inputs[index].Location)), inputs[index].Size);
                var b = new Rectangle(form.PointToClient(inputs[next].Parent!.PointToScreen(inputs[next].Location)), inputs[next].Size);
                Require(!a.IntersectsWith(b), "Dialog buttons, inputs, and lists do not overlap.");
            }
        Require(form.FormBorderStyle == FormBorderStyle.None && Descendants(form).OfType<ListView>().All(list => list.OwnerDraw), "Online dialogs use shared themed chrome and list headings.");
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void LayoutAll(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) LayoutAll(child);
    }

    private static void Snapshot(Control control, string path)
    {
        using var image = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(image, new Rectangle(Point.Empty, control.Size));
        image.Save(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Online UI verification failed: " + message);
    }
}

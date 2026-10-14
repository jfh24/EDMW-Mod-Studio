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

        var preferences = new OnlinePreferences { PersistIdentity = false, DisplayName = "Offline layout check" };
        using var client = new OnlineRelayClient(preferences);
        using (var friends = new FriendsDialog(client, preferences, () => [], () => true, () => false,
            _ => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask))
        {
            Prepare(friends);
            var relaySettings = Descendants(friends).OfType<Button>().Single(button => button.AccessibleName == "Relay settings");
            var endpoint = Descendants(friends).OfType<TextBox>().Single(input => input.AccessibleName == "Relay HTTPS URL");
            Require(!endpoint.Visible, "Relay settings do not compete with the initial connection step.");
            foreach (var size in new[] { friends.ClientSize, friends.MinimumSize })
            {
                friends.ClientSize = size;
                VerifyBounds(friends);
                VerifyFriendsEmptyStates(friends);
                Snapshot(friends, Path.Combine(outputDirectory, "online-friends-" + friends.ClientSize.Width + ".png"));
            }
            var connect = Descendants(friends).OfType<Button>().Single(button => button.Text == "Connect");
            var request = Descendants(friends).OfType<Button>().Single(button => button.Text == "Send request");
            var requestCode = Descendants(friends).OfType<TextBox>().Single(input => input.AccessibleName == "Friend code to request");
            Require(connect.Enabled && !request.Enabled && !requestCode.Enabled,
                "Disconnected state enables Connect and requires a connection before requesting friends.");
            Require(!Descendants(friends).OfType<Button>().Any(button => button.Text == "Leave session" && button.Visible),
                "An inactive map session does not expose a Leave session action.");
            relaySettings.PerformClick();
            VerifyBounds(friends);
            Require(endpoint.Visible && endpoint.Enabled, "Relay settings can be opened and edited before connecting.");
            VerifyFriendsEmptyStates(friends);
            Snapshot(friends, Path.Combine(outputDirectory, "online-friends-relay-settings-minimum.png"));
            relaySettings.PerformClick();
            VerifyBounds(friends);
            Require(!endpoint.Visible && requestCode.Text.Length == 0 && !request.Enabled,
                "Relay settings close cleanly while an empty friend code remains unavailable offline.");
            friends.Hide();
            checks.Add("Friends has readable empty states, hides relay settings until requested, and keeps disconnected controls reachable inside standard and minimum bounds with settings expanded or collapsed.");
        }
        using (var scaledFriends = new FriendsDialog(client, preferences, () => [], () => true, () => false,
            _ => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask))
        {
            Prepare(scaledFriends);
            var scaledMinimum = new Size((int)Math.Ceiling(scaledFriends.MinimumSize.Width * 1.5),
                (int)Math.Ceiling(scaledFriends.MinimumSize.Height * 1.5));
            scaledFriends.Scale(new SizeF(1.5f, 1.5f));
            scaledFriends.ClientSize = scaledMinimum;
            Descendants(scaledFriends).OfType<Button>().Single(button => button.AccessibleName == "Relay settings").PerformClick();
            VerifyBounds(scaledFriends);
            Snapshot(scaledFriends, Path.Combine(outputDirectory, "online-friends-layout-scale-150.png"));
            VerifyFriendsEmptyStates(scaledFriends);
            scaledFriends.Hide();
            checks.Add("Friends keeps expanded relay settings, lists, and empty-state text reachable in its minimum layout after 150 percent control scaling.");
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
        try
        {
            foreach (var control in controls)
                Require(control.Width > 0 && control.Height > 0 && control.Parent!.ClientRectangle.Contains(control.Bounds) &&
                    ReachableInViewport(form, control),
                    (control.AccessibleName ?? control.Text) + " stays inside its parent and can be fully shown inside the dialog at " + form.ClientSize + ".");
        }
        finally { ResetScrollPositions(form); }
        var inputs = controls.Where(control => control.Name != MedievalDialogFrame.CloseButtonName).ToArray();
        for (var index = 0; index < inputs.Length; index++)
            for (var next = index + 1; next < inputs.Length; next++)
            {
                var sameViewport = ReferenceEquals(ScrollAncestors(inputs[index]).FirstOrDefault(), ScrollAncestors(inputs[next]).FirstOrDefault());
                var a = sameViewport ? BoundsIn(form, inputs[index]) : VisibleBoundsIn(form, inputs[index]);
                var b = sameViewport ? BoundsIn(form, inputs[next]) : VisibleBoundsIn(form, inputs[next]);
                Require(!a.IntersectsWith(b), "Dialog buttons, inputs, and lists do not overlap.");
            }
        Require(form.FormBorderStyle == FormBorderStyle.None && Descendants(form).OfType<ListView>().All(list => list.OwnerDraw), "Online dialogs use shared themed chrome and list headings.");
    }

    private static void VerifyFriendsEmptyStates(Form form)
    {
        try
        {
            foreach (var text in new[] { "Your friends will appear here", "No map invitations yet" })
            {
                var label = Descendants(form).OfType<Label>().Single(label => label.Text == text);
                var measured = TextRenderer.MeasureText(label.Text, label.Font,
                    new Size(Math.Max(1, label.ClientSize.Width), int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                Require(label.Visible && label.Width > 0 && label.Height >= measured.Height &&
                    label.Width >= measured.Width && label.Parent!.ClientRectangle.Contains(label.Bounds) && ReachableInViewport(form, label),
                    text + " is readable, inside its empty state, and can be fully shown at " + form.ClientSize + ".");
            }
            foreach (var list in Descendants(form).OfType<ListView>().Where(list => list.Visible))
                Require(list.Width > 0 && list.Height >= list.Font.Height * 2 && ReachableInViewport(form, list),
                    list.AccessibleName + " reserves enough height for its heading and a row and can be fully shown at " + form.ClientSize + ".");
        }
        finally { ResetScrollPositions(form); }
    }

    private static bool ReachableInViewport(Form form, Control control)
    {
        var viewports = ScrollAncestors(control).ToArray();
        foreach (var viewport in viewports)
        {
            viewport.ScrollControlIntoView(control);
            LayoutAll(form);
        }
        return form.ClientRectangle.Contains(BoundsIn(form, control)) &&
            viewports.All(viewport => viewport.ClientRectangle.Contains(BoundsIn(viewport, control)));
    }

    private static Rectangle BoundsIn(Control container, Control control) =>
        new(container.PointToClient(control.PointToScreen(Point.Empty)), control.Size);

    private static Rectangle VisibleBoundsIn(Form form, Control control)
    {
        var bounds = Rectangle.Intersect(form.ClientRectangle, BoundsIn(form, control));
        foreach (var viewport in ScrollAncestors(control))
            bounds = Rectangle.Intersect(bounds, new Rectangle(form.PointToClient(viewport.PointToScreen(Point.Empty)), viewport.ClientSize));
        return bounds;
    }

    private static IEnumerable<ScrollableControl> ScrollAncestors(Control control)
    {
        for (Control? parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent is ScrollableControl { AutoScroll: true } viewport) yield return viewport;
    }

    private static void ResetScrollPositions(Form form)
    {
        foreach (var viewport in Descendants(form).OfType<ScrollableControl>().Where(control => control.AutoScroll))
            viewport.AutoScrollPosition = Point.Empty;
        LayoutAll(form);
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

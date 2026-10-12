using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using EDMWMapDesigner.Civilizations;
using EDMWMapDesigner.Generation;

namespace EDMWMapDesigner.UI;

/// <summary>Runs recipient approval checks only against newly created game fixtures.</summary>
internal static class MapLanDialogVerification
{
    public static IReadOnlyList<string> Verify(MapLanPackage package, string gamePath)
    {
        var checks = new List<string>();
        foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            using var review = NewReview(package, gamePath);
            PrepareOffscreen(review);
            Require(review.FormBorderStyle == FormBorderStyle.None &&
                review.Controls.Find(MedievalDialogFrame.CaptionName, true).Length == 1,
                "Map review uses the main application's custom dialog chrome.");
            Require(review.Decision.Status == CivilizationLanStatus.Denied && review.InstalledGamePath is null,
                "Incoming maps default to Denied and uninstalled.");
            Require(review.AcceptButton is null && !review.ActionButtonsForVerification[1].Enabled,
                "Unverified destinations cannot be accepted.");
            Require(review.PreviewForVerification.Image is not null, "The exported preview is shown.");
            review.StatusForVerification.Text = new string('W', 500);
            VerifySizes(review, scale, checks);
            review.Hide();
        }
        using (var withdrawn = new CancellationTokenSource())
        using (var review = NewReview(package, gamePath, withdrawn.Token))
        {
            PrepareOffscreen(review);
            withdrawn.Cancel();
            Application.DoEvents();
            Require(!review.Visible && review.Decision.Status == CivilizationLanStatus.Denied && review.InstalledGamePath is null,
                "Withdrawing an unaccepted map offer closes review without installing.");
            checks.Add("Unaccepted offer cancellation closes review and stays Denied.");
        }
        using (var review = NewReview(package, gamePath))
        {
            PrepareOffscreen(review);
            review.Close();
            Require(review.Decision.Status == CivilizationLanStatus.Denied && review.InstalledGamePath is null,
                "Closing review denies an unaccepted map offer.");
            checks.Add("Closing review stays Denied.");
        }
        return checks.AsReadOnly();
    }

    public static IReadOnlyList<string> VerifySender(MapLanPackage package, string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        var fixture = Path.Combine(output, "map-lan-sender-fixtures", Guid.NewGuid().ToString("N"));
        if (Directory.Exists(fixture)) throw new InvalidOperationException("LAN sender verification requires a new fixture.");
        Directory.CreateDirectory(Path.Combine(fixture, "Data", "Random Map Scripts"));
        File.WriteAllText(Path.Combine(fixture, "Empires_DMW.exe"), "Isolated sender verification fixture. Never executed.\n");
        var before = Snapshot(fixture);
        var checks = new List<string>();
        var networkSends = 0;
        var discoveries = 0;
        var localInstalls = 0;
        Task<IReadOnlyList<CivilizationLanPeer>> EmptyDiscovery(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            discoveries++;
            return Task.FromResult<IReadOnlyList<CivilizationLanPeer>>([]);
        }
        Task<CivilizationLanResult> UnexpectedSend(CivilizationLanPeer _, Action<CivilizationLanProgress> __,
            CancellationToken ___)
        {
            networkSends++;
            throw new InvalidOperationException("Empty LAN sender verification attempted a network send.");
        }
        Dictionary<string, string>? installed = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var popup = new LanExportStatusDialog("Map LAN export", package.MapName, EmptyDiscovery,
                UnexpectedSend, verificationMode: true, installLocal: token =>
                {
                    localInstalls++;
                    return MainForm.InstallLocalMapForLanAsync(package, fixture, token);
                });
            PrepareOffscreen(popup);
            Require(popup.FormBorderStyle == FormBorderStyle.None && popup.PeerListForVerification.OwnerDraw &&
                popup.Controls.Find(MedievalDialogFrame.CaptionName, true).Length == 1,
                "The sender installation popup uses custom medieval chrome and list headings.");
            Complete(popup.StartForVerificationAsync(), "Automatic sender map installation");
            Require(popup.Results.Count == 1 && popup.Results.TryGetValue("local:0", out var result) &&
                result.Status == CivilizationLanStatus.Installed && networkSends == 0,
                "The production local installation succeeds when no remote computers are discovered.");
            Require(popup.PeerListForVerification.Items.Count == 1 &&
                popup.PeerListForVerification.Items[0].Text == "You (this computer)" &&
                popup.PeerListForVerification.Items[0].SubItems[1].Text == "Installed" &&
                popup.StatusForVerification.Text.Contains("Installed locally", StringComparison.Ordinal),
                "The sender popup displays this computer's successful installation.");
            var maps = CustomMapInstallationCatalog.Discover(fixture);
            Require(maps.Count == 1 && maps[0].IsComplete && maps[0].Name == package.MapName &&
                maps[0].MapId == package.MapId.ToString("X6"),
                "Home discovers the sender's installed map as one complete package.");
            var actual = Snapshot(fixture);
            var installedFiles = actual.Where(pair => pair.Value != "directory" && !before.ContainsKey(pair.Key)).ToArray();
            Require(installedFiles.Length == 3 && installedFiles.Select(pair => pair.Key.Replace('\\', '/'))
                .Order(StringComparer.Ordinal).SequenceEqual(package.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal)),
                "The sender installs exactly the three exported map files, without an installer or staging files.");
            using (var memory = new MemoryStream(MapLanPackageCodec.Encode(package), writable: false))
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Read))
                foreach (var file in package.Files)
                {
                    using var entry = archive.GetEntry(file.RelativePath)!.Open();
                    using var bytes = new MemoryStream();
                    entry.CopyTo(bytes);
                    var target = Path.Combine(fixture, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    Require(File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes.ToArray()),
                        "The sender installs the exact exported bytes for " + file.RelativePath);
                }
            foreach (var pair in before)
                Require(actual.TryGetValue(pair.Key, out var value) && value == pair.Value,
                    "Sender installation preserves unrelated fixture content: " + pair.Key);
            if (attempt == 0)
            {
                installed = actual;
                using var image = new Bitmap(popup.Width, popup.Height);
                popup.DrawToBitmap(image, new Rectangle(Point.Empty, popup.Size));
                image.Save(Path.Combine(output, "local-sender.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            else RequireSame(installed!, actual, "Repeated local export keeps installed bytes and directory inventory unchanged.");
            popup.Close();
        }
        Require(localInstalls == 2 && discoveries == 2 && networkSends == 0,
            "Each one-click sender workflow installs locally once and discovers recipients once.");
        checks.Add("The production sender popup installs the exact three map files on this computer without any remote peers; Home discovers a complete map, and repeating the export preserves all bytes and leaves no extra files.");

        var installedMap = CustomMapInstallationCatalog.Discover(fixture).Single();
        CustomMapInstallationCatalog.Uninstall(installedMap);
        Require(CustomMapInstallationCatalog.Discover(fixture).Count == 0,
            "Home no longer lists the sender's map after uninstall.");
        RequireSame(before, Snapshot(fixture), "Home uninstall removes every sender-installed map file and the empty payload folder.");
        checks.Add("Home uninstall removes the sender-installed map and restores the isolated fixture inventory.");

        var invalid = Path.Combine(output, "map-lan-sender-invalid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(invalid);
        var invalidBefore = Snapshot(invalid);
        using (var popup = new LanExportStatusDialog("Map LAN export", package.MapName, EmptyDiscovery,
            UnexpectedSend, verificationMode: true,
            installLocal: token => MainForm.InstallLocalMapForLanAsync(package, invalid, token)))
        {
            PrepareOffscreen(popup);
            Complete(popup.StartForVerificationAsync(), "Invalid sender game folder");
            Require(popup.Results.TryGetValue("local:0", out var result) && result.Status == CivilizationLanStatus.Failed &&
                popup.PeerListForVerification.Items[0].SubItems[1].Text == "Failed" &&
                popup.StatusForVerification.Text.Contains("Local install failed", StringComparison.Ordinal),
                "The production sender popup reports a local installation failure.");
            popup.Close();
        }
        RequireSame(invalidBefore, Snapshot(invalid), "An invalid sender game folder creates no files or directories.");
        Require(networkSends == 0, "Invalid local installation does not introduce a network send to an empty LAN.");
        checks.Add("The production sender popup displays a failed local install for an invalid game folder and makes no writes.");
        File.WriteAllLines(Path.Combine(output, "map-lan-sender-verification.txt"), checks.Append("Fixture: " + fixture));
        return checks.AsReadOnly();
    }

    public static IReadOnlyList<string> VerifyApproval(MapLanPackage package, string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        var fixture = Path.Combine(output, "map-lan-dialog-fixtures", Guid.NewGuid().ToString("N"));
        if (Directory.Exists(fixture)) throw new InvalidOperationException("LAN approval verification requires a new fixture.");
        Directory.CreateDirectory(Path.Combine(fixture, "Data", "Random Map Scripts"));
        File.WriteAllText(Path.Combine(fixture, "Empires_DMW.exe"), "Isolated verification fixture. Never executed.\n");
        var before = Snapshot(fixture);
        var checks = new List<string>();
        VerifyGameFolderChooser(fixture, output, checks);
        RequireSame(before, Snapshot(fixture), "The themed folder chooser creates no files or directories.");

        using (var denied = NewReview(package, fixture))
        {
            PrepareOffscreen(denied);
            Complete(denied.RefreshForVerificationAsync(), "Read-only map recipient review");
            RequireReady(denied);
            using (var image = new Bitmap(denied.Width, denied.Height))
            {
                denied.DrawToBitmap(image, new Rectangle(Point.Empty, denied.Size));
                image.Save(Path.Combine(output, "map-recipient.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            RequireSame(before, Snapshot(fixture), "Validated review makes no game-folder writes.");
            denied.ActionButtonsForVerification[1].Focus();
            Require(denied.EnterForVerification() && denied.Decision.Status == CivilizationLanStatus.Denied &&
                denied.InstalledGamePath is null, "Enter cannot install even when Accept has focus.");
            denied.ActionButtonsForVerification[0].PerformClick();
            Require(denied.Decision.Status == CivilizationLanStatus.Denied && denied.InstalledGamePath is null,
                "The actual Deny action leaves the offer uninstalled.");
        }
        RequireSame(before, Snapshot(fixture), "Deny and Enter make no game-folder writes.");
        checks.Add("Production review is read-only; Enter and Deny leave every file and directory unchanged.");

        using (var withdrawn = new CancellationTokenSource())
        using (var cancelled = NewReview(package, fixture, withdrawn.Token))
        {
            PrepareOffscreen(cancelled);
            Complete(cancelled.RefreshForVerificationAsync(), "Review before withdrawal");
            RequireReady(cancelled);
            withdrawn.Cancel();
            // Exercise the Accept handler before pumping the queued close callback.
            Complete(cancelled.AcceptForVerificationAsync(), "Withdrawn map offer acceptance guard");
            Require(cancelled.Decision.Status == CivilizationLanStatus.Denied && cancelled.InstalledGamePath is null,
                "Withdrawal after destination validation prevents installation.");
        }
        RequireSame(before, Snapshot(fixture), "A withdrawn ready offer makes no game-folder writes.");
        checks.Add("Withdrawal after validation prevents writes through the production Accept handler.");

        var invalidFixture = Path.Combine(output, "map-lan-dialog-invalid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(invalidFixture);
        var invalidBefore = Snapshot(invalidFixture);
        using (var invalid = NewReview(package, invalidFixture))
        {
            PrepareOffscreen(invalid);
            Complete(invalid.RefreshForVerificationAsync(), "Invalid game destination review");
            Require(!invalid.ActionButtonsForVerification[1].Enabled && invalid.Decision.Status == CivilizationLanStatus.Denied,
                "An invalid game folder cannot be accepted.");
            Complete(invalid.AcceptForVerificationAsync(), "Invalid destination acceptance guard");
        }
        RequireSame(invalidBefore, Snapshot(invalidFixture), "An invalid game destination makes no writes.");
        checks.Add("Invalid game folders keep Accept disabled and make no writes.");

        using (var accepted = NewReview(package, fixture))
        {
            PrepareOffscreen(accepted);
            Complete(accepted.RefreshForVerificationAsync(), "Review before explicit map acceptance");
            RequireReady(accepted);
            Complete(accepted.AcceptForVerificationAsync(), "Explicit map recipient acceptance");
            Require(accepted.Decision.Status == CivilizationLanStatus.Installed && accepted.InstalledGamePath == fixture,
                "The production Accept handler installs and reports the chosen game folder.");
        }
        Require(File.ReadAllText(Path.Combine(fixture, "Empires_DMW.exe")) == "Isolated verification fixture. Never executed.\n",
            "Acceptance preserves unrelated game files.");
        foreach (var file in package.Files)
        {
            var installed = Path.Combine(fixture, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Require(File.Exists(installed) && new FileInfo(installed).Length == file.Length,
                "Acceptance installs the exported file: " + file.RelativePath);
        }
        checks.Add("Explicit production acceptance installs the exported map payload and preserves unrelated files.");
        File.WriteAllLines(Path.Combine(output, "map-lan-dialog-approval-verification.txt"), checks.Append("Fixture: " + fixture));
        return checks.AsReadOnly();
    }

    private static MapLanReviewDialog NewReview(MapLanPackage package, string gamePath,
        CancellationToken cancellationToken = default) =>
        new(new(package, "192.168.1.20", DateTimeOffset.UtcNow), gamePath, verificationMode: true,
            offerCancellation: cancellationToken);

    private static void RequireReady(MapLanReviewDialog dialog)
    {
        Require(dialog.ActionButtonsForVerification[1].Enabled,
            "Destination validation enables explicit acceptance: " + dialog.StatusForVerification.Text);
        Require(dialog.Decision.Status == CivilizationLanStatus.Denied && dialog.InstalledGamePath is null,
            "Destination validation alone does not approve installation.");
    }

    private static void VerifyGameFolderChooser(string fixture, string output, List<string> checks)
    {
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        {
            using var picker = new MedievalGameFolderDialog(fixture, verificationMode: true, verificationRoot: fixture);
            PrepareOffscreen(picker);
            Require(picker.FormBorderStyle == FormBorderStyle.None &&
                picker.Controls.Find(MedievalDialogFrame.CaptionName, true).Length == 1 &&
                picker.TreeForVerification.DrawMode == TreeViewDrawMode.OwnerDrawText,
                "The folder chooser uses the application's caption, colors and folder rendering.");
            picker.PathForVerification.Text = Path.Combine(fixture, "Missing folder");
            Require(!picker.ActionButtonsForVerification[1].Enabled, "A missing folder disables Select Folder.");
            picker.SelectForVerification();
            Require(picker.SelectedPath.Length == 0 && picker.Visible, "An invalid typed folder cannot be chosen.");
            picker.PathForVerification.Text = fixture;
            Require(picker.ActionButtonsForVerification[1].Enabled, "An existing typed folder enables explicit selection.");
            if (scale == 1f)
            {
                using var image = new Bitmap(picker.Width, picker.Height);
                picker.DrawToBitmap(image, new Rectangle(Point.Empty, picker.Size));
                image.Save(Path.Combine(output, "game-folder-picker.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            picker.StatusForVerification.Text = new string('W', 500);
            VerifyPickerSizes(picker, scale, checks);
            var close = picker.Controls.Find(MedievalDialogFrame.CloseButtonName, true).OfType<Button>().Single();
            close.PerformClick();
            Require(!picker.Visible && picker.SelectedPath.Length == 0,
                "The themed caption Close action cancels folder selection.");
        }
        using (var picker = new MedievalGameFolderDialog(fixture, verificationMode: true, verificationRoot: fixture))
        {
            PrepareOffscreen(picker);
            picker.PathForVerification.Text = fixture + Path.DirectorySeparatorChar;
            picker.SelectForVerification();
            Require(!picker.Visible && picker.SelectedPath == fixture && picker.DialogResult == DialogResult.OK,
                "Explicit folder selection returns the normalized existing path.");
        }
        checks.Add("The themed folder chooser rejects missing paths, returns explicit valid selections and cancels from its caption without filesystem writes.");
    }

    private static void VerifyPickerSizes(MedievalGameFolderDialog form, float scale, List<string> checks)
    {
        var defaultSize = form.ClientSize;
        form.Size = form.MinimumSize;
        var minimumSize = form.ClientSize;
        form.ClientSize = defaultSize;
        if (scale != 1f) form.Scale(new SizeF(scale, scale));
        foreach (var size in new[]
        {
            (Name: "default", Client: Scaled(defaultSize, scale)),
            (Name: "minimum", Client: Scaled(minimumSize, scale))
        })
        {
            form.ClientSize = size.Client;
            PerformLayouts(form);
            Application.DoEvents();
            PerformLayouts(form);
            var context = $"folder chooser {size.Name} at {scale:0.##} layout scale";
            var panel = form.ActionPanelForVerification;
            var buttons = form.ActionButtonsForVerification;
            foreach (var button in buttons)
                Require(button.Visible && button.Height >= 28 && panel.ClientRectangle.Contains(button.Bounds) &&
                    form.ClientRectangle.Contains(BoundsInForm(form, button)), context + ": actions remain visible.");
            Require(!buttons[0].Bounds.IntersectsWith(buttons[1].Bounds) &&
                BoundsInForm(form, panel).Top >= BoundsInForm(form, form.StatusForVerification).Bottom,
                context + ": long errors remain above distinct actions.");
            Require(form.TreeForVerification.Height > 0 &&
                form.ClientRectangle.Contains(BoundsInForm(form, form.TreeForVerification)),
                context + ": folder tree remains inside the dialog.");
            checks.Add(context + ": custom caption, folder tree and action geometry passed.");
        }
    }

    private static void Complete(Task task, string operation)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("Map LAN dialog verification timed out: " + operation);
            Application.DoEvents();
            Thread.Sleep(2);
        }
        task.GetAwaiter().GetResult();
        Application.DoEvents();
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(root, directory) + "\\"] = "directory";
        return files;
    }

    private static void RequireSame(IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual, string message) =>
        Require(expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var hash) && hash == pair.Value), message);

    private static void PrepareOffscreen(Form form)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.Show();
        Application.DoEvents();
        form.AutoScaleMode = AutoScaleMode.None;
    }

    private static void VerifySizes(MapLanReviewDialog form, float scale, List<string> checks)
    {
        var defaultSize = form.ClientSize;
        form.Size = form.MinimumSize;
        var minimumSize = form.ClientSize;
        form.ClientSize = defaultSize;
        if (scale != 1f) form.Scale(new SizeF(scale, scale));
        foreach (var size in new[]
        {
            (Name: "default", Client: Scaled(defaultSize, scale)),
            (Name: "minimum", Client: Scaled(minimumSize, scale)),
            (Name: "large", Client: Scaled(new Size(1920, 1080), scale))
        })
        {
            form.ClientSize = size.Client;
            PerformLayouts(form);
            Application.DoEvents();
            PerformLayouts(form);
            var context = $"map review {size.Name} at {scale:0.##} layout scale";
            var panel = form.ActionPanelForVerification;
            var buttons = form.ActionButtonsForVerification;
            Require(panel.Visible && panel.Height > 0, context + ": action panel is visible.");
            foreach (var button in buttons)
            {
                Require(button.Visible && button.Width > 0 && button.Height >= 28,
                    context + ": " + button.Text + " has visible bounds.");
                Require(panel.ClientRectangle.Contains(button.Bounds) && form.ClientRectangle.Contains(BoundsInForm(form, button)),
                    context + ": " + button.Text + " remains inside its action row.");
            }
            Require(!buttons[0].Bounds.IntersectsWith(buttons[1].Bounds), context + ": actions do not overlap.");
            Require(BoundsInForm(form, panel).Top >= BoundsInForm(form, form.StatusForVerification).Bottom,
                context + ": long errors stay above the actions.");
            var preview = form.PreviewForVerification;
            Require(preview.Width > 0 && preview.Height > 0 && form.ClientRectangle.Contains(BoundsInForm(form, preview)),
                context + ": map preview has visible bounds.");
            checks.Add(context + ": preview, action geometry and long error layout passed.");
        }
    }

    private static Size Scaled(Size size, float scale) => new(
        (int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));

    private static void PerformLayouts(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) PerformLayouts(child);
    }

    private static Rectangle BoundsInForm(Form form, Control control)
    {
        var parent = control.Parent ?? throw new InvalidOperationException("A verified control has no parent.");
        return new Rectangle(form.PointToClient(parent.PointToScreen(control.Location)), control.Size);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Map LAN dialog verification failed: " + message);
    }
}

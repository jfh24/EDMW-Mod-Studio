using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using EDMWMapDesigner.Civilizations;
using EDMWMapDesigner.Generation;

namespace EDMWMapDesigner.UI;

internal static class InstalledContentUiVerification
{
    internal static void Verify(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        using var owner = new Form { ClientSize = new Size(560, 230), BackColor = MedievalTheme.Sidebar };
        Prepare(owner);
        var details = 0;
        var uninstall = 0;
        var destinations = new List<InstalledShareDestination>();
        var send = new TaskCompletionSource();
        using var row = new DashboardInstalledItemRow("Islands & Empires", "Updated 10/14/2026 9:30 AM", null,
            true, () => details++, () => uninstall++, destination => { destinations.Add(destination); return send.Task; })
        { Width = 520, Location = new Point(16, 16) };
        owner.Controls.Add(row);
        Application.DoEvents();
        Require(row.Controls.Cast<Control>().All(control => control is Label && control.ContextMenuStrip == row.ContextMenuStrip),
            "Every part of a text-only row must open the same context menu.");
        Require(row.Controls.OfType<Label>().All(label => !label.UseMnemonic), "Mod names must retain ampersands.");
        MenuItem(row, "Details").PerformClick();
        MenuItem(row, "Uninstall").PerformClick();
        Require(details == 1 && uninstall == 1, "Context actions must address the selected entry.");
        foreach (var width in new[] { 285, 520 })
        {
            row.Width = width;
            row.PerformLayout();
            Require(row.Controls.Cast<Control>().All(control => row.ClientRectangle.Contains(control.Bounds) && control.Height >= control.Font.Height),
                "Text must remain within narrow and wide rows.");
            Snapshot(row, Path.Combine(output, $"installed-row-{width}.png"));
        }
        ChooseShare("Cancel");
        ChooseShare("×");
        Require(destinations.Count == 0, "Canceling the destination dialog must not send anything.");
        foreach (var choice in new[] { ("LAN", InstalledShareDestination.Lan), ("Friends", InstalledShareDestination.Friends) })
        {
            send = new TaskCompletionSource();
            ChooseShare(choice.Item1);
            Require(destinations.Last() == choice.Item2 && destinations.Count == (choice.Item2 == InstalledShareDestination.Lan ? 1 : 2),
                "Share must send once, to only the selected destination.");
            Require(row.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().All(item => !item.Enabled),
                "Actions must remain disabled during an active share.");
            send.SetResult();
            PumpUntil(() => MenuItem(row, "Share…").Enabled);
        }
        using (var unavailable = new DashboardInstalledItemRow("Incomplete map", "Files missing", "Incomplete installation",
                   false, () => { }, () => { }, _ => throw new InvalidOperationException("Unavailable share was invoked.")))
        {
            Require(!MenuItem(unavailable, "Share…").Enabled && MenuItem(unavailable, "Details").Enabled && MenuItem(unavailable, "Uninstall").Enabled,
                "Incomplete entries must retain Details and Uninstall while disabling Share.");
        }
        using (var scaled = new InstalledShareDialog("Islands & Empires"))
        {
            Prepare(scaled);
            scaled.Scale(new SizeF(1.5f, 1.5f));
            scaled.PerformLayout();
            Require(Descendants(scaled).OfType<Button>().All(button => button.Parent!.ClientRectangle.Contains(button.Bounds) && button.Height >= button.Font.Height),
                "Share choices must fit at 150 percent scaling.");
            Snapshot(scaled, Path.Combine(output, "share-dialog-scaled.png"));
        }
        VerifyCivilizationDetails(owner);
        VerifyMapPreview(output);
        using var home = new MainForm(verificationMode: true);
        Prepare(home);
        home.ShowWorkspaceForVerification(mapDesigner: false);
        home.ClientSize = new Size(1600, 900);
        Application.DoEvents();
        Require(Descendants(home).OfType<DashboardMapRow>().All(entry => entry is DashboardInstalledItemRow &&
                entry.Controls.Cast<Control>().All(control => control is Label && control.ContextMenuStrip == entry.ContextMenuStrip)),
            "Home must use text-only context-menu entries for both installed lists.");
        Snapshot(home, Path.Combine(output, "home.png"));

        void ChooseShare(string choice)
        {
            using var timer = new System.Windows.Forms.Timer { Interval = 20 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.OfType<InstalledShareDialog>().SingleOrDefault();
                if (dialog is null) return;
                timer.Stop();
                Snapshot(dialog, Path.Combine(output, "share-dialog.png"));
                Descendants(dialog).OfType<Button>().Single(button => button.Text == choice).PerformClick();
            };
            timer.Start();
            MenuItem(row, "Share…").PerformClick();
        }
    }

    private static void VerifyCivilizationDetails(Form owner)
    {
        using var panel = new CivilizationInstalledEditsPanel(() => null, () => { });
        owner.Controls.Add(panel);
        var package = new CivilizationInstalledPackage("legacy", "Civilization fixture", DateTimeOffset.Now, []);
        using var row = (Control)typeof(CivilizationInstalledEditsPanel)
            .GetMethod("CreatePackageRow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [package])!;
        Require(!MenuItem(row, "Share…").Enabled, "Legacy civilization edits cannot be shared.");
        Require(row.Controls.Cast<Control>().All(control => control is Label), "Civilization entries must contain text only.");
        var opened = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.Text == "Installed civilization edits — Civilization fixture");
            if (dialog is null) return;
            timer.Stop();
            opened = Descendants(dialog).OfType<TextBox>().Any(text => text.ReadOnly && text.Text == CivilizationInstalledEditsPanel.BuildDetails(package));
            dialog.Close();
        };
        timer.Start();
        MenuItem(row, "Details").PerformClick();
        Require(opened, "Civilization Details must open the existing change-details popup.");
    }

    private static void VerifyMapPreview(string output)
    {
        var fixture = Path.Combine(output, "map-preview", Guid.NewGuid().ToString("N"));
        var game = Path.Combine(fixture, "game");
        Directory.CreateDirectory(Path.Combine(game, "Data", "Random Map Scripts"));
        File.WriteAllBytes(Path.Combine(game, GameInstallationLocator.GameExecutableName), []);
        var package = MapLanVerification.CreatePackage(fixture);
        package.Install(game);
        var map = CustomMapInstallationCatalog.Discover(game).Single();
        var before = SnapshotFiles(game);
        Require(CustomMapInstallationCatalog.ReadPreview(map).AsSpan().SequenceEqual(package.PreviewBytes),
            "Details must reconstruct the installed terrain's exported preview without a project.");
        using (var dialog = new InstalledMapDetailsDialog(map))
        {
            Prepare(dialog);
            var picture = Descendants(dialog).OfType<PictureBox>().Single();
            PumpUntil(() => picture.Image is not null);
            Require(picture.Visible && picture.SizeMode == PictureBoxSizeMode.Zoom, "Map preview must be visible and preserve aspect ratio.");
            Snapshot(dialog, Path.Combine(output, "map-details.png"));
        }
        Require(before.OrderBy(pair => pair.Key).SequenceEqual(SnapshotFiles(game).OrderBy(pair => pair.Key)),
            "Opening Details must leave every game file unchanged.");
        File.Delete(map.GeneratorPath);
        File.Delete(map.ScriptPath!);
        Require(CustomMapInstallationCatalog.ReadPreview(map).Length > 0, "Remaining terrain must still be previewable for an incomplete installation.");
        var bytes = File.ReadAllBytes(map.PayloadPath);
        bytes[^1] ^= 1;
        File.WriteAllBytes(map.PayloadPath, bytes);
        using var damaged = new InstalledMapDetailsDialog(map);
        Prepare(damaged);
        var status = Descendants(damaged).OfType<Label>().Single(label => label.AccessibleName == "Map preview status");
        PumpUntil(() => status.Text.StartsWith("Preview unavailable.", StringComparison.Ordinal));
        Require(status.Visible && !Descendants(damaged).OfType<PictureBox>().Single().Visible,
            "Damaged terrain must show the unavailable message instead of an empty image.");
        Snapshot(damaged, Path.Combine(output, "map-details-unavailable.png"));
    }

    private static Dictionary<string, string> SnapshotFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static ToolStripMenuItem MenuItem(Control row, string text) => row.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == text);

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Prepare(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.None;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(10); }
        Require(ready(), "The UI operation did not complete in time.");
    }

    private static void Snapshot(Control control, string path)
    {
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, control.ClientRectangle);
        bitmap.Save(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

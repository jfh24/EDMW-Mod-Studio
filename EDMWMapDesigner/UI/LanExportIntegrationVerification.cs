using System.Diagnostics;
using System.Reflection;

namespace EDMWMapDesigner.UI;

/// <summary>Checks LAN export entry points and receiver settings without changing preferences or sending an export.</summary>
internal static class LanExportIntegrationVerification
{
    public static IReadOnlyList<string> Verify(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var checks = new List<string>();
        using var main = new MainForm(verificationMode: true)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false
        };
        main.Show();
        main.AutoScaleMode = AutoScaleMode.None;
        main.ShowWorkspaceForVerification(mapDesigner: true);
        var toolbar = Descendants(main).OfType<ToolStrip>().Single(strip =>
            strip.Items.Cast<ToolStripItem>().Any(item => item.Text == "Export over LAN"));
        var export = toolbar.Items.Cast<ToolStripItem>().Single(item => item.Text == "Export over LAN");
        Require(!toolbar.Items.Cast<ToolStripItem>().Any(item => item.Text?.Contains("Installer", StringComparison.OrdinalIgnoreCase) == true) &&
            !Descendants(main).OfType<CheckBox>().Any(control => control.Text.Contains("Run installer", StringComparison.OrdinalIgnoreCase)),
            "Map export exposes LAN and friends delivery and has no installer action or run-installer toggle.");
        foreach (var size in new[] { new Size(1200, 760), new Size(1920, 1080) })
        {
            main.ClientSize = size;
            PerformLayouts(main);
            Application.DoEvents();
            PerformLayouts(main);
            Require(toolbar.Visible && export.Available && export.Visible && export.Enabled &&
                export.Placement == ToolStripItemPlacement.Main && toolbar.ClientRectangle.Contains(export.Bounds),
                $"The map Export over LAN button is visible and directly reachable at {size.Width} pixels.");
            SaveSnapshot(toolbar, Path.Combine(outputDirectory, size.Width == 1920 ? "map-toolbar.png" : "map-toolbar-1200.png"));
            checks.Add($"Map Export over LAN is directly reachable at {size.Width} pixels without activating export or collaboration.");
        }

        var civilizations = main.OpenCivilizationEditorForVerification();
        PerformLayouts(main);
        Application.DoEvents();
        var gameMenu = Descendants(civilizations).OfType<Button>().Single(control => control.Text == "Game ▾");
        var exportActions = gameMenu.ContextMenuStrip!.Items.Cast<ToolStripItem>().Where(item =>
            item.Text?.StartsWith("Export", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        Require(gameMenu.Visible && gameMenu.Enabled && exportActions.Select(item => item.Text).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "Export over LAN", "Export to friends" }),
            "The civilization Game menu exposes LAN and friends export.");
        checks.Add("Maps and civilizations expose LAN and friends export; installer export actions and the run-installer option are absent.");

        VerifySettings(main, outputDirectory, checks);
        main.Hide();
        return checks;
    }

    private static void VerifySettings(MainForm main, string outputDirectory, List<string> checks)
    {
        var method = typeof(MainForm).GetMethod("ShowGameSettings", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The production Settings entry point is missing.");
        Exception? failure = null;
        var verified = false;
        var elapsed = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.Text == "EDMW Mod Studio Settings");
            if (dialog is null)
            {
                if (elapsed.Elapsed < TimeSpan.FromSeconds(5)) return;
                timer.Stop();
                failure = new TimeoutException("The production Settings dialog did not open.");
                foreach (var owned in main.OwnedForms) owned.Close();
                return;
            }
            timer.Stop();
            try
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(-30000, -30000);
                PerformLayouts(dialog);
                Require(dialog.ClientSize == new Size(602, 404), "LAN receiver settings preserve the 600 × 370 content area inside the shared frame.");
                var controls = Descendants(dialog).ToArray();
                var caption = controls.Single(control => control.Name == MedievalDialogFrame.CaptionName);
                Require(dialog.FormBorderStyle == FormBorderStyle.None && dialog.BackColor == MedievalTheme.BorderStrong &&
                    caption.BackColor == MedievalTheme.SurfaceRaised && caption.Controls.OfType<Label>().Single().ForeColor == MedievalTheme.GoldBright,
                    "Settings uses the shared border, dark caption and gold title styling.");
                var civ = controls.OfType<CheckBox>().Single(control => control.Text == "Receive civ edits over LAN");
                var maps = controls.OfType<CheckBox>().Single(control => control.Text == "Receive maps over LAN");
                var civPort = controls.OfType<NumericUpDown>().Single(control => control.AccessibleName == "LAN civilization receiver port");
                var mapPort = controls.OfType<NumericUpDown>().Single(control => control.AccessibleName == "LAN map receiver port");
                Require(civPort.Minimum == 1 && civPort.Maximum == 65535 && mapPort.Minimum == 1 && mapPort.Maximum == 65535,
                    "Both independent LAN receiver port controls cover valid TCP ports.");
                var statuses = controls.OfType<Label>().Where(control => control.Text == "Receiving off").ToArray();
                Require(statuses.Length == 2, "Both receiver statuses are shown with listeners disabled in verification mode.");
                var actions = controls.Where(control => control is Button or CheckBox or NumericUpDown).ToArray();
                Require(actions.OfType<Button>().Where(control => control.Name != MedievalDialogFrame.CloseButtonName)
                    .Select(control => control.Text).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "Change...", "Done", "Geography credits" }), "All Settings actions remain present.");
                foreach (var control in actions.Concat(statuses))
                {
                    var bounds = BoundsInForm(dialog, control);
                    Require(control.Visible && control.Enabled && control.Width > 0 && control.Height > 0 &&
                        dialog.ClientRectangle.Contains(bounds) && control.Parent!.ClientRectangle.Contains(control.Bounds),
                        "Settings keeps " + (control.AccessibleName ?? control.Text) + " fully visible inside its row and dialog.");
                }
                for (var index = 0; index < actions.Length; index++)
                    for (var next = index + 1; next < actions.Length; next++)
                        Require(!BoundsInForm(dialog, actions[index]).IntersectsWith(BoundsInForm(dialog, actions[next])),
                            "Settings receiver controls and actions do not overlap.");
                Require(BoundsInForm(dialog, civ).Top < BoundsInForm(dialog, maps).Top,
                    "Civilization and map receiver controls occupy separate rows.");
                SaveSnapshot(dialog, Path.Combine(outputDirectory, "lan-settings.png"));
                verified = true;
                checks.Add("Themed Settings shows separate civilization/map receiver toggles, ports and statuses with all actions inside the preserved 600 × 370 content area.");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
            }
        };
        timer.Start();
        try { method.Invoke(main, null); }
        finally { timer.Stop(); }
        if (failure is not null) throw new InvalidOperationException("LAN integration Settings verification failed.", failure);
        Require(verified, "Settings was inspected and automatically closed without user interaction.");
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void PerformLayouts(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) PerformLayouts(child);
    }

    private static Rectangle BoundsInForm(Form form, Control control) =>
        new(form.PointToClient(control.Parent!.PointToScreen(control.Location)), control.Size);

    private static void SaveSnapshot(Control control, string path)
    {
        using var image = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(image, new Rectangle(Point.Empty, control.Size));
        image.Save(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("LAN integration verification failed: " + message);
    }
}

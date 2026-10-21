using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Offscreen checks for merge selection, naming, and row context actions.</summary>
internal static class CivilizationMergeUiVerification
{
    internal static void Verify(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var selected = new CivilizationInstalledPackage("selected", "Recon & support", DateTimeOffset.UnixEpoch, []);
        var grouped = new CivilizationInstalledPackage("grouped", "Frontier collection", DateTimeOffset.UnixEpoch, [], ComponentCount: 3);
        var legacy = new CivilizationInstalledPackage("legacy", "Earlier edit", DateTimeOffset.UnixEpoch, []);
        var interrupted = new CivilizationInstalledPackage("interrupted", "Interrupted edit", DateTimeOffset.UnixEpoch, [], "Recovery required");
        foreach (var scale in new[] { 1f, 1.5f })
        {
            using var dialog = new CivilizationMergeDialog([legacy, grouped, interrupted, selected], selected.PackageId);
            dialog.AutoScaleMode = AutoScaleMode.None;
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-30000, -30000);
            dialog.Show();
            Application.DoEvents();
            if (scale != 1f) dialog.Scale(new SizeF(scale, scale));
            dialog.PerformLayout();
            var list = Descendants(dialog).OfType<CheckedListBox>().Single();
            var name = Descendants(dialog).OfType<TextBox>().Single();
            var merge = Descendants(dialog).OfType<Button>().Single(button => button.Text == "Merge");
            Require(list.Items.Count == 2 && dialog.SelectedPackageIds.SequenceEqual([selected.PackageId]),
                "The current edit is preselected and only complete managed entries can be merged.");
            Require(name.MaxLength == 120 && dialog.MergedName == "Recon & support collection" && !merge.Enabled,
                "The merge dialog starts with an editable name and requires another entry.");
            list.SetItemChecked(1, true);
            Require(merge.Enabled && dialog.SelectedPackageIds.ToHashSet().SetEquals([selected.PackageId, grouped.PackageId]),
                "The chooser can combine an existing group with another installed entry.");
            name.Text = "   ";
            Require(!merge.Enabled, "An empty merged name cannot be submitted.");
            name.Text = "  Combined & named edits  ";
            Require(merge.Enabled && dialog.MergedName == "Combined & named edits",
                "A custom merged name preserves literal ampersands and trims surrounding whitespace.");
            Require(Descendants(dialog).OfType<Button>().All(button => button.Parent!.ClientRectangle.Contains(button.Bounds) && button.Height >= button.Font.Height),
                "The merge actions fit their containers at ordinary and enlarged scaling.");
            Require(list.Height > list.ItemHeight * 3 && name.Height >= name.Font.Height,
                "The merge name and multi-selection list remain usable at enlarged scaling.");
            using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
            {
                dialog.DrawToBitmap(bitmap, dialog.ClientRectangle);
                bitmap.Save(Path.Combine(outputDirectory, $"civilization-merge-{(int)(scale * 100)}.png"));
            }
            merge.PerformClick();
            Require(dialog.DialogResult == DialogResult.OK,
                "A valid named selection can be returned to the installed-edit merge action.");
        }
        var merges = 0;
        var unmerges = 0;
        using var row = new DashboardInstalledItemRow("Named collection", "Three edits merged", null,
            true, () => { }, () => { }, _ => Task.CompletedTask, merge: () => merges++, unmerge: () => unmerges++);
        var menu = row.ContextMenuStrip!;
        menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Merge with…").PerformClick();
        menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Unmerge").PerformClick();
        Require(merges == 1 && unmerges == 1 && row.TabStop && row.AccessibleDescription!.Contains("Shift+F10", StringComparison.Ordinal) &&
            row.Controls.Cast<Control>().All(control => control.ContextMenuStrip == menu),
            "Merge and unmerge act on the selected row through its shared keyboard-accessible context menu.");
        Require(CivilizationInstalledEditsPanel.BuildDetails(grouped).Contains("3 constituent edits", StringComparison.Ordinal),
            "Merged-entry details explain how many constituent edits are included.");
    }

    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

internal sealed class CivilizationMergeDialog : Form
{
    private readonly TextBox _name = new()
    {
        Dock = DockStyle.Fill, MaxLength = 120, AccessibleName = "Name for merged civilization edit", TabIndex = 0
    };
    private readonly CheckedListBox _edits = new()
    {
        Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false,
        HorizontalScrollbar = true, BorderStyle = BorderStyle.None,
        AccessibleName = "Installed civilization edits to merge", TabIndex = 1
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextMuted,
        TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty
    };
    private readonly Button _merge = new()
    {
        Text = "Merge", Width = 100, Height = 32, Enabled = false,
        AccessibleName = "Merge selected civilization edits", TabIndex = 2
    };

    internal string MergedName => _name.Text.Trim();
    internal IReadOnlyList<string> SelectedPackageIds => _edits.CheckedItems.Cast<Choice>().Select(choice => choice.Package.PackageId).ToArray();

    internal CivilizationMergeDialog(IReadOnlyList<CivilizationInstalledPackage> installed, string selectedPackageId)
    {
        Text = "Merge installed civilization edits";
        ClientSize = new Size(600, 468);
        MinimumSize = new Size(500, 420);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = MedievalTheme.UiFont();
        AccessibleName = Text;
        var layout = new MedievalBufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 7
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label
        {
            Text = "Choose edits to combine into one installed entry. You can share them together or unmerge them later.",
            Dock = DockStyle.Fill, ForeColor = MedievalTheme.Text, Margin = Padding.Empty, UseMnemonic = false
        }, 0, 0);
        layout.Controls.Add(Caption("Merged edit name"), 0, 1);
        MedievalTheme.StyleTextInput(_name);
        layout.Controls.Add(MedievalTheme.FrameInput(_name), 0, 2);
        layout.Controls.Add(Caption("Installed edits — check at least two"), 0, 3);
        MedievalTheme.StyleTextInput(_edits);
        layout.Controls.Add(MedievalTheme.FrameInput(_edits), 0, 4);
        layout.Controls.Add(_status, 0, 5);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            BackColor = Color.Transparent, Margin = Padding.Empty, Padding = new Padding(0, 6, 0, 0)
        };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 32, DialogResult = DialogResult.Cancel, TabIndex = 3 };
        MedievalTheme.StyleSecondaryButton(cancel);
        MedievalTheme.StyleSecondaryButton(_merge);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_merge);
        layout.Controls.Add(buttons, 0, 6);
        AcceptButton = _merge;
        CancelButton = cancel;
        _merge.Click += (_, _) =>
        {
            UpdateValidation();
            if (_merge.Enabled) DialogResult = DialogResult.OK;
        };
        foreach (var package in installed.Where(package => package.PackageId != "legacy" &&
                     string.Equals(package.Status, "Installed", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(package => package.PackageId == selectedPackageId ? 0 : 1)
                 .ThenBy(package => package.ProjectName, StringComparer.CurrentCultureIgnoreCase))
            _edits.Items.Add(new Choice(package), package.PackageId == selectedPackageId);
        var selected = installed.FirstOrDefault(package => package.PackageId == selectedPackageId);
        var suggestedName = (selected?.ProjectName ?? "Civilization edits") + " collection";
        _name.Text = suggestedName.Length > 120 ? suggestedName[..120] : suggestedName;
        _name.TextChanged += (_, _) => UpdateValidation();
        _edits.ItemCheck += (_, args) => UpdateValidation(_edits.CheckedItems.Count +
            (args.NewValue == CheckState.Checked ? 1 : 0) - (args.CurrentValue == CheckState.Checked ? 1 : 0));
        _edits.HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(_edits);
        Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
        UpdateValidation();
        MedievalDialogFrame.Apply(this, layout);
    }

    private void UpdateValidation(int? checkedCount = null)
    {
        var count = checkedCount ?? _edits.CheckedItems.Count;
        var validName = MergedName.Length is > 0 and <= 120 &&
            !MergedName.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029');
        _merge.Enabled = count >= 2 && validName;
        _status.Text = count < 2 ? "Select at least two installed entries." :
            !validName ? "Enter a name from 1 to 120 characters." : $"{count} installed entries selected.";
    }

    private static Label Caption(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright,
        TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, UseMnemonic = false
    };

    private sealed record Choice(CivilizationInstalledPackage Package)
    {
        public override string ToString() => Package.ProjectName +
            (Package.ComponentCount > 1 ? $" ({Package.ComponentCount} merged edits)" : "");
    }
}

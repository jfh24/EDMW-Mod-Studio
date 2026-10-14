namespace EDMWMapDesigner.UI;

internal enum InstalledShareDestination { Lan, Friends }

internal sealed class InstalledShareDialog : Form
{
    internal InstalledShareDestination? Destination { get; private set; }

    internal InstalledShareDialog(string itemName)
    {
        Text = "Share " + itemName;
        ClientSize = new Size(420, 168);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = MedievalTheme.UiFont();
        var layout = new MedievalBufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var prompt = new Label
        {
            Text = "Where would you like to share this mod?", Dock = DockStyle.Fill,
            ForeColor = MedievalTheme.Text, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(prompt, 0, 0);
        layout.SetColumnSpan(prompt, 2);
        AddDestination("LAN", InstalledShareDestination.Lan, 0);
        AddDestination("Friends", InstalledShareDestination.Friends, 1);
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right,
            Width = 90, Margin = new Padding(0, 8, 0, 0)
        };
        MedievalTheme.StyleSecondaryButton(cancel);
        layout.Controls.Add(cancel, 1, 2);
        CancelButton = cancel;
        MedievalDialogFrame.Apply(this, layout);

        void AddDestination(string label, InstalledShareDestination destination, int column)
        {
            var button = new Button { Text = label, Dock = DockStyle.Fill, Margin = new Padding(column == 0 ? 0 : 5, 4, column == 0 ? 5 : 0, 4) };
            MedievalTheme.StyleSecondaryButton(button);
            button.Click += (_, _) => { Destination = destination; DialogResult = DialogResult.OK; };
            layout.Controls.Add(button, column, 1);
        }
    }
}

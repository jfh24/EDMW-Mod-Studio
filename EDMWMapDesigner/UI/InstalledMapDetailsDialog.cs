namespace EDMWMapDesigner.UI;

internal sealed class InstalledMapDetailsDialog : Form
{
    private readonly PictureBox _preview;
    private readonly Label _status;

    internal InstalledMapDetailsDialog(CustomMapInstallation map)
    {
        Text = "Map details — " + map.Name;
        ClientSize = new Size(620, 590);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = MedievalTheme.UiFont();
        var layout = new MedievalBufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label
        {
            Text = map.Name, UseMnemonic = false, Dock = DockStyle.Fill,
            Font = MedievalTheme.DisplayFont(18), ForeColor = MedievalTheme.GoldBright,
            AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = map.UpdatedDescription + (map.IsComplete ? "" : " · Incomplete installation"),
            Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextMuted,
            AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
        }, 0, 1);
        var previewPanel = new Panel { Dock = DockStyle.Fill, BackColor = MedievalTheme.Input, Margin = new Padding(0, 8, 0, 0) };
        _preview = new PictureBox
        {
            Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Visible = false, AccessibleName = "Installed map preview"
        };
        _status = new Label
        {
            Text = "Loading map preview…", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = MedievalTheme.TextMuted, Padding = new Padding(20), AccessibleName = "Map preview status"
        };
        previewPanel.Controls.Add(_preview);
        previewPanel.Controls.Add(_status);
        _status.BringToFront();
        layout.Controls.Add(previewPanel, 0, 2);
        var close = new Button { Text = "Close", Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.OK, Margin = new Padding(0, 8, 0, 0) };
        MedievalTheme.StyleSecondaryButton(close);
        layout.Controls.Add(close, 0, 3);
        AcceptButton = CancelButton = close;
        MedievalDialogFrame.Apply(this, layout);
        Shown += async (_, _) =>
        {
            try
            {
                var bytes = await Task.Run(() => CustomMapInstallationCatalog.ReadPreview(map));
                if (IsDisposed || Disposing) return;
                using var stream = new MemoryStream(bytes, writable: false);
                using var image = Image.FromStream(stream);
                _preview.Image = new Bitmap(image);
                _status.Visible = false;
                _preview.Visible = true;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                if (IsDisposed || Disposing) return;
                _status.Text = "Preview unavailable.\n\n" + error.Message;
            }
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _preview.Image?.Dispose();
        base.Dispose(disposing);
    }
}

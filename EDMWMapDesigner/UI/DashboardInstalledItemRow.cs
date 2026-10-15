namespace EDMWMapDesigner.UI;

internal sealed class DashboardInstalledItemRow : DashboardMapRow
{
    private readonly ContextMenuStrip _menu;
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private bool _busy;

    internal DashboardInstalledItemRow(string itemName, string metadata, string? warning,
        bool canShare, Action details, Action uninstall, Func<InstalledShareDestination, Task> share,
        string? shareUnavailableReason = null, string? toggleLabel = null, Action? toggle = null)
    {
        Height = warning is null ? 64 : 84;
        ColumnCount = 1;
        RowCount = warning is null ? 2 : 3;
        Padding = new Padding(12, 7, 12, 7);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        if (warning is not null) RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        AccessibleName = itemName;
        AccessibleDescription = "Right-click or press Shift+F10 for Details, Share, and Uninstall.";
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);

        AddLabel(itemName, MedievalTheme.Text, 10, FontStyle.Bold, 0);
        AddLabel(metadata, MedievalTheme.TextMuted, 8.5f, FontStyle.Regular, 1);
        if (warning is not null) AddLabel(warning, MedievalTheme.Warning, 8.5f, FontStyle.Regular, 2);

        _menu = new ContextMenuStrip
        {
            Renderer = new MedievalToolStripRenderer(), ShowImageMargin = false,
            BackColor = MedievalTheme.Sidebar, ForeColor = MedievalTheme.Text,
            Font = MedievalTheme.UiFont()
        };
        _menu.Items.Add("Details", null, (_, _) => details());
        if (toggle is not null) _menu.Items.Add(toggleLabel, null, (_, _) => toggle());
        var shareItem = _menu.Items.Add("Share…");
        shareItem.Enabled = canShare;
        shareItem.ToolTipText = canShare ? "Choose LAN or Friends" : shareUnavailableReason ?? "Reinstall this item to enable sharing.";
        shareItem.Click += async (_, _) =>
        {
            if (_busy || !canShare) return;
            _busy = true;
            foreach (ToolStripItem item in _menu.Items) item.Enabled = false;
            try
            {
                using var dialog = new InstalledShareDialog(itemName);
                if (dialog.ShowDialog((IWin32Window?)FindForm() ?? this) == DialogResult.OK &&
                    dialog.Destination is { } destination)
                    await share(destination);
            }
            finally
            {
                if (!IsDisposed && !Disposing)
                {
                    _busy = false;
                    foreach (ToolStripItem item in _menu.Items) item.Enabled = true;
                    shareItem.Enabled = canShare;
                }
            }
        };
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Uninstall", null, (_, _) => uninstall());
        ContextMenuStrip = _menu;
        foreach (Control child in Controls)
        {
            child.ContextMenuStrip = _menu;
            child.MouseDown += (_, _) => Focus();
        }
        MouseDown += (_, _) => Focus();
    }

    private void AddLabel(string text, Color color, float size, FontStyle style, int row)
    {
        var label = new Label
        {
            Text = text, UseMnemonic = false, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            Margin = Padding.Empty, ForeColor = color, BackColor = Color.Transparent,
            Font = MedievalTheme.UiFont(size, style)
        };
        _tips.SetToolTip(label, text);
        Controls.Add(label, 0, row);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is (Keys.Shift | Keys.F10) or Keys.Apps)
        {
            _menu.Show(this, new Point(Padding.Left, Height / 2));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5),
                MedievalTheme.GoldBright, BackColor);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _menu.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}

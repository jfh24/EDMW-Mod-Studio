namespace EDMWMapDesigner.UI;

/// <summary>Destination choices and one sharing action for an installed item.</summary>
internal sealed class DashboardShareControls : UserControl
{
    private readonly CheckBox _lan;
    private readonly CheckBox _friends;
    private readonly Button _share;
    private readonly bool _available;
    private bool _busy;

    public DashboardShareControls(string itemName, bool available, Func<bool, bool, Task> share)
    {
        _available = available;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        BackColor = Color.Transparent;
        DoubleBuffered = true;
        AccessibleName = "Share " + itemName;
        var layout = new MedievalBufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var destinations = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false,
            Margin = Padding.Empty, BackColor = Color.Transparent
        };
        _lan = Destination("LAN", itemName);
        _friends = Destination("Friends", itemName);
        destinations.Controls.Add(_lan);
        destinations.Controls.Add(_friends);
        _share = new DashboardPlaqueButton
        {
            Text = "Share", Dock = DockStyle.Fill, Margin = new Padding(2),
            AccessibleName = "Share " + itemName + " to selected destinations"
        };
        _share.Click += async (_, _) =>
        {
            if (_busy || !_available || (!_lan.Checked && !_friends.Checked)) return;
            _busy = true;
            UpdateActions();
            try { await share(_lan.Checked, _friends.Checked); }
            finally
            {
                if (!IsDisposed && !Disposing)
                {
                    _busy = false;
                    UpdateActions();
                }
            }
        };
        _lan.CheckedChanged += (_, _) => UpdateActions();
        _friends.CheckedChanged += (_, _) => UpdateActions();
        layout.Controls.Add(destinations, 0, 0);
        layout.Controls.Add(_share, 1, 0);
        Controls.Add(layout);
        UpdateActions();
    }

    private static CheckBox Destination(string name, string itemName) => new DestinationCheckBox
    {
        Text = name, AutoSize = true,
        ForeColor = MedievalTheme.Text, BackColor = Color.Transparent,
        Font = MedievalTheme.UiFont(9), Margin = new Padding(4, 7, 8, 0),
        AccessibleName = "Share " + itemName + " over " + name
    };

    private void UpdateActions()
    {
        _lan.Enabled = _friends.Enabled = _available && !_busy;
        _share.Enabled = _available && !_busy && (_lan.Checked || _friends.Checked);
    }

    private sealed class DestinationCheckBox : CheckBox
    {
        private bool _hovered;

        public DestinationCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text, Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            return new Size(CheckboxSize + Scale(7) + text.Width + Padding.Horizontal,
                Math.Max(CheckboxSize, text.Height) + Padding.Vertical + Scale(2));
        }

        private int CheckboxSize => Math.Max(Scale(14), Font.Height - 2);
        private int Scale(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

        protected override void OnPaint(PaintEventArgs e)
        {
            var side = CheckboxSize;
            var box = new Rectangle(Padding.Left, (Height - side) / 2, side - 1, side - 1);
            using var fill = new SolidBrush(Enabled && _hovered ? MedievalTheme.SurfaceRaised : MedievalTheme.Input);
            using var border = new Pen(Enabled && (_hovered || Focused) ? MedievalTheme.GoldBright : MedievalTheme.BorderStrong);
            e.Graphics.FillRectangle(fill, box);
            e.Graphics.DrawRectangle(border, box);
            var inset = Rectangle.Inflate(box, -2, -2);
            using var inner = new Pen(MedievalTheme.Border);
            e.Graphics.DrawRectangle(inner, inset);
            if (Checked)
            {
                using var tick = new Pen(Enabled ? MedievalTheme.GoldBright : MedievalTheme.TextSecondary, Math.Max(1.5f, DeviceDpi / 48f));
                e.Graphics.DrawLines(tick,
                [new(box.Left + Scale(3), box.Top + box.Height / 2),
                 new(box.Left + box.Width / 2 - 1, box.Bottom - Scale(3)),
                 new(box.Right - Scale(2), box.Top + Scale(3))]);
            }
            var textBounds = Rectangle.FromLTRB(box.Right + Scale(7), Padding.Top, Width - Padding.Right, Height - Padding.Bottom);
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Enabled ? ForeColor : MedievalTheme.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, textBounds, MedievalTheme.GoldBright, BackColor);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; Invalidate(); }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); _hovered = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }
}

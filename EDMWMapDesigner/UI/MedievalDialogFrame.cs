namespace EDMWMapDesigner.UI;

/// <summary>Keeps modal window chrome and list headings in the editor's visual theme.</summary>
internal static class MedievalDialogFrame
{
    internal const string CaptionName = "MedievalDialogCaption";
    internal const string CloseButtonName = "MedievalDialogClose";
    private const int CaptionHeight = 32;

    public static void Apply(Form form, Control content)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(content);
        var clientSize = form.ClientSize;
        var minimumSize = form.MinimumSize;
        form.FormBorderStyle = FormBorderStyle.None;
        form.BackColor = MedievalTheme.BorderStrong;
        form.ForeColor = MedievalTheme.Text;
        form.Padding = new Padding(1);
        form.MaximizeBox = false;
        form.MinimizeBox = false;

        var shell = new MedievalBufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty,
            Padding = Padding.Empty, BackColor = MedievalTheme.Sidebar,
            AutoSize = form.AutoSize, AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, CaptionHeight));
        shell.RowStyles.Add(new RowStyle(form.AutoSize ? SizeType.AutoSize : SizeType.Percent, 100));
        var caption = new CaptionPanel
        {
            Name = CaptionName, Dock = DockStyle.Fill, Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, 1), BackColor = MedievalTheme.SurfaceRaised,
            AccessibleName = form.Text + " title bar"
        };
        var title = new Label
        {
            Text = form.Text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 8, 0), ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.DisplayFont(9.5f), BackColor = Color.Transparent,
            AutoEllipsis = true, Margin = Padding.Empty, UseMnemonic = false
        };
        var close = new Button
        {
            Name = CloseButtonName, Text = "×", Dock = DockStyle.Right, Width = 38,
            FlatStyle = FlatStyle.Flat, BackColor = MedievalTheme.SurfaceRaised,
            ForeColor = MedievalTheme.TextMuted, Font = MedievalTheme.UiFont(14f),
            Margin = Padding.Empty, Padding = Padding.Empty, TabStop = false,
            Cursor = Cursors.Hand, AccessibleName = "Close dialog", Tag = "dialog-chrome-close"
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = MedievalTheme.Crimson;
        close.FlatAppearance.MouseDownBackColor = MedievalTheme.CrimsonBright;
        close.Click += (_, _) => form.Close();
        void Drag(object? _, MouseEventArgs args)
        {
            if (args.Button == MouseButtons.Left) MedievalWindowChrome.BeginDrag(form);
        }
        title.MouseDown += Drag;
        caption.MouseDown += Drag;
        form.TextChanged += (_, _) =>
        {
            title.Text = form.Text;
            caption.AccessibleName = form.Text + " title bar";
        };
        caption.Controls.Add(title);
        caption.Controls.Add(close);
        content.Dock = DockStyle.Fill;
        content.Margin = Padding.Empty;
        content.BackColor = MedievalTheme.Sidebar;
        shell.Controls.Add(caption, 0, 0);
        shell.Controls.Add(content, 0, 1);
        form.Controls.Add(shell);
        // Preserve the previous content area while supplying our own caption.
        if (!form.AutoSize) form.ClientSize = new Size(clientSize.Width + 2, clientSize.Height + CaptionHeight + 2);
        form.MinimumSize = new Size(minimumSize.Width > 0 ? minimumSize.Width + 2 : 0,
            minimumSize.Height > 0 ? minimumSize.Height + CaptionHeight + 2 : 0);
    }

    public static void StyleListView(ListView list)
    {
        ArgumentNullException.ThrowIfNull(list);
        list.BackColor = MedievalTheme.Input;
        list.ForeColor = MedievalTheme.Text;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.OwnerDraw = true;
        list.DrawColumnHeader += (_, args) =>
        {
            using var background = new SolidBrush(MedievalTheme.SurfaceRaised);
            using var border = new Pen(MedievalTheme.BorderStrong);
            args.Graphics.FillRectangle(background, args.Bounds);
            if (args.ColumnIndex == list.Columns.Count - 1 && args.Bounds.Right < list.ClientSize.Width)
                args.Graphics.FillRectangle(background, args.Bounds.Right, args.Bounds.Top,
                    list.ClientSize.Width - args.Bounds.Right, args.Bounds.Height);
            args.Graphics.DrawLine(border, args.Bounds.Left, args.Bounds.Bottom - 1,
                args.Bounds.Right, args.Bounds.Bottom - 1);
            args.Graphics.DrawLine(border, args.Bounds.Right - 1, args.Bounds.Top,
                args.Bounds.Right - 1, args.Bounds.Bottom);
            using var font = MedievalTheme.DisplayFont(8.5f);
            var textBounds = Rectangle.Inflate(args.Bounds, -7, 0);
            TextRenderer.DrawText(args.Graphics, args.Header?.Text ?? "", font, textBounds, MedievalTheme.GoldBright,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        list.DrawItem += (_, args) => args.DrawDefault = list.View != View.Details;
        list.DrawSubItem += (_, args) =>
        {
            if (args.Item is not { } item || args.SubItem is not { } subItem) return;
            var selected = item.Selected && (!list.HideSelection || list.ContainsFocus);
            using var background = new SolidBrush(selected ? MedievalTheme.SurfaceRaised : list.BackColor);
            args.Graphics.FillRectangle(background, args.Bounds);
            var textBounds = Rectangle.Inflate(args.Bounds, -7, 0);
            if (list.CheckBoxes && args.ColumnIndex == 0)
            {
                // Owner drawing covers the native state image; paint its visible
                // equivalent in the same hit area and leave room for the label.
                var side = Math.Max(12, Math.Min(18, args.Bounds.Height - 4));
                var box = new Rectangle(args.Bounds.Left + 4,
                    args.Bounds.Top + (args.Bounds.Height - side) / 2, side, side);
                using var border = new Pen(item.Checked ? MedievalTheme.GoldBright : MedievalTheme.BorderStrong);
                args.Graphics.DrawRectangle(border, box);
                if (item.Checked)
                {
                    using var tick = new Pen(MedievalTheme.GoldBright, 2);
                    args.Graphics.DrawLines(tick,
                    [new Point(box.Left + 3, box.Top + side / 2),
                     new Point(box.Left + side / 2 - 1, box.Bottom - 4),
                     new Point(box.Right - 3, box.Top + 3)]);
                }
                var indent = box.Right + 5 - textBounds.Left;
                textBounds.X += indent;
                textBounds.Width -= indent;
            }
            if (textBounds.Width <= 0) return;
            var color = selected ? MedievalTheme.GoldBright : item.ForeColor;
            if (color.IsEmpty) color = list.ForeColor;
            TextRenderer.DrawText(args.Graphics, subItem.Text, subItem.Font ?? list.Font,
                textBounds, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        list.HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(list);
    }

    private sealed class CaptionPanel : Panel
    {
        public CaptionPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs args)
        {
            base.OnPaintBackground(args);
            var texture = MedievalAssets.OrnateFrame;
            var source = new Rectangle(texture.Width / 3, texture.Height / 3,
                texture.Width / 3, texture.Height / 3);
            args.Graphics.DrawImage(texture, ClientRectangle, source, GraphicsUnit.Pixel);
            using var wash = new SolidBrush(Color.FromArgb(155, MedievalTheme.Surface));
            args.Graphics.FillRectangle(wash, ClientRectangle);
            using var border = new Pen(MedievalTheme.BorderStrong);
            args.Graphics.DrawLine(border, 0, Height - 1, Width, Height - 1);
        }
    }
}

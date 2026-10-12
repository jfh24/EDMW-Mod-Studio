using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EDMWMapDesigner.UI;

internal static class MedievalTheme
{
    public static readonly Color Shell = Color.FromArgb(10, 9, 8);
    public static readonly Color Canvas = Color.FromArgb(13, 17, 19);
    public static readonly Color Sidebar = Color.FromArgb(20, 17, 14);
    public static readonly Color Surface = Color.FromArgb(28, 23, 19);
    public static readonly Color SurfaceRaised = Color.FromArgb(45, 34, 27);
    public static readonly Color Input = Color.FromArgb(24, 20, 16);
    public static readonly Color Border = Color.FromArgb(91, 69, 41);
    public static readonly Color BorderStrong = Color.FromArgb(151, 103, 43);
    public static readonly Color Gold = Color.FromArgb(211, 152, 58);
    public static readonly Color GoldBright = Color.FromArgb(247, 197, 96);
    public static readonly Color Crimson = Color.FromArgb(102, 30, 24);
    public static readonly Color CrimsonBright = Color.FromArgb(160, 48, 34);
    public static readonly Color Text = Color.FromArgb(245, 224, 184);
    public static readonly Color TextMuted = Color.FromArgb(194, 164, 116);
    public static readonly Color TextSecondary = Color.FromArgb(169, 157, 139);
    public static readonly Color Warning = Color.FromArgb(231, 177, 91);
    public static readonly Color Success = Color.FromArgb(144, 181, 116);

    public static Font DisplayFont(float size, FontStyle style = FontStyle.Bold) =>
        new("Georgia", size, style, GraphicsUnit.Point);

    public static Font UiFont(float size = 9.25f, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style, GraphicsUnit.Point);

    public static void StyleTextInput(Control control)
    {
        control.BackColor = Input;
        control.ForeColor = Text;
        control.Font = UiFont();
        control.HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(control);
        switch (control)
        {
            case TextBox textBox:
                textBox.BorderStyle = BorderStyle.None;
                textBox.AutoSize = false;
                break;
            case NumericUpDown numeric:
                numeric.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat;
                break;
        }
    }

    public static Control FrameInput(Control control) => new MedievalInputFrame(control);

    public static void StyleSecondaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = BorderStrong;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = SurfaceRaised;
        button.ForeColor = Text;
        button.Font = UiFont(9.25f, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }

    public static Label CreateSectionHeading(string text, int topMargin = 18) => new()
    {
        Text = text.ToUpperInvariant(),
        AutoSize = true,
        UseMnemonic = false,
        Font = DisplayFont(8.5f, FontStyle.Bold),
        ForeColor = Gold,
        Margin = new Padding(0, topMargin, 0, 8)
    };

    public static Label CreateHint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(335, 0),
        Font = UiFont(8.75f),
        ForeColor = TextMuted,
        Margin = new Padding(0, 5, 0, 8)
    };
}

internal sealed class ToolChoiceCard : UserControl
{
    public ToolChoiceCard(string title, string description, Image actionImage, string actionName, Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        ArgumentNullException.ThrowIfNull(actionImage);
        Height = 286;
        Dock = DockStyle.Fill;
        Padding = Padding.Empty;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = actionName;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);

        var surface = new Panel { Dock = DockStyle.Fill, Padding = new Padding(32, 16, 32, 12), BackColor = Color.Transparent, Margin = Padding.Empty };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = Color.Transparent, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var titleLabel = new DashboardDisplayLabel { Text = title, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = MedievalTheme.DisplayFont(17.5f), ForeColor = MedievalTheme.Text, Margin = Padding.Empty };
        var descriptionLabel = new Label { Text = description, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopCenter, Font = MedievalTheme.UiFont(9.5f), ForeColor = Color.FromArgb(221, 182, 120), Margin = new Padding(12, 0, 12, 0) };
        layout.Controls.Add(titleLabel, 0, 1);
        layout.Controls.Add(descriptionLabel, 0, 2);
        var actionHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent, Margin = Padding.Empty };
        actionHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        actionHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        actionHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        var action = new DashboardActionIcon
        {
            Image = actionImage,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            AccessibleName = actionName
        };
        action.Click += (_, _) => activate();
        action.KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space) { activate(); e.SuppressKeyPress = true; } };
        action.Disposed += (_, _) => actionImage.Dispose();
        actionHost.Controls.Add(action, 1, 0);
        layout.Controls.Add(actionHost, 0, 4);
        surface.Controls.Add(layout);
        Controls.Add(surface);

        foreach (Control control in new Control[] { this, surface, layout, titleLabel, descriptionLabel })
            control.Click += (_, _) => activate();
        KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space) { activate(); e.SuppressKeyPress = true; } };

        void ScaleActionArea()
        {
            // Reserve space for readable copy, then fit artwork into the remaining
            // well so all three actions stay inside their frames at smaller sizes.
            var scale = Math.Clamp(Math.Min(ClientSize.Width / 324f, ClientSize.Height / 360f), 0.6f, 2f);
            surface.Padding = new Padding(
                (int)Math.Clamp(ClientSize.Width * 0.09f, 14, 32),
                (int)(14 * scale),
                (int)Math.Clamp(ClientSize.Width * 0.09f, 14, 32),
                (int)(10 * scale));
            layout.RowStyles[0].Height = 10 * scale;
            layout.RowStyles[1].Height = 40 * scale;
            layout.RowStyles[2].Height = 58 * scale;
            layout.RowStyles[3].Height = 8 * scale;
            layout.RowStyles[4].SizeType = SizeType.Percent;
            layout.RowStyles[4].Height = 100;
            layout.RowStyles[5].SizeType = SizeType.Absolute;
            layout.RowStyles[5].Height = 12 * scale;
            var titleSize = Math.Clamp(17.5f * scale, 13.5f, 19.5f);
            if (Math.Abs(titleLabel.Font.Size - titleSize) > 0.05f)
            {
                var previousFont = titleLabel.Font;
                titleLabel.Font = MedievalTheme.DisplayFont(titleSize);
                previousFont.Dispose();
            }
            var descriptionSize = Math.Clamp(9.5f * scale, 8.25f, 10.5f);
            if (Math.Abs(descriptionLabel.Font.Size - descriptionSize) > 0.05f)
            {
                var previousFont = descriptionLabel.Font;
                descriptionLabel.Font = MedievalTheme.UiFont(descriptionSize);
                previousFont.Dispose();
            }
            descriptionLabel.Margin = new Padding((int)(6 * scale), 0, (int)(6 * scale), 0);
        }

        Resize += (_, _) => ScaleActionArea();
        ScaleActionArea();
    }
}

internal sealed class DashboardActionIcon : PictureBox
{
    private bool _hovered;

    public DashboardActionIcon()
    {
        BackColor = Color.Transparent;
        SizeMode = PictureBoxSizeMode.Zoom;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_hovered || Width < 8 || Height < 8) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var glowBounds = Rectangle.Inflate(ClientRectangle, -3, -3);
        using var glow = new Pen(Color.FromArgb(70, MedievalTheme.GoldBright), 1.5f);
        e.Graphics.DrawEllipse(glow, glowBounds);
    }
}

internal sealed class DashboardDisplayLabel : Label
{
    protected override void OnPaint(PaintEventArgs e)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;
        flags |= TextAlign switch
        {
            ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.Left
        };
        flags |= TextAlign switch
        {
            ContentAlignment.TopLeft or ContentAlignment.TopCenter or ContentAlignment.TopRight => TextFormatFlags.Top,
            ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => TextFormatFlags.Bottom,
            _ => TextFormatFlags.VerticalCenter
        };

        var bounds = Rectangle.FromLTRB(Padding.Left, Padding.Top, Width - Padding.Right, Height - Padding.Bottom);
        var shadowBounds = new Rectangle(bounds.X + 1, bounds.Y + 2, bounds.Width, bounds.Height);
        var highlightBounds = new Rectangle(bounds.X, bounds.Y - 1, bounds.Width, bounds.Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, shadowBounds, Color.FromArgb(210, 0, 0, 0), flags);
        TextRenderer.DrawText(e.Graphics, Text, Font, highlightBounds, Color.FromArgb(72, MedievalTheme.GoldBright), flags);
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ForeColor, flags);
    }
}

internal sealed class DashboardPlaqueButton : Button
{
    private bool _hovered;
    private bool _pressed;

    public DashboardPlaqueButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
        ForeColor = MedievalTheme.Text;
        Font = MedievalTheme.UiFont(9.75f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        Padding = new Padding(8, 0, 8, 0);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 8 || Height < 8) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var shift = _pressed ? 1 : 0;
        var outer = Rectangle.FromLTRB(1, 1 + shift, Width - 2, Height - 2 + shift);
        var inner = Rectangle.Inflate(outer, -4, -4);
        if (inner.Width < 4 || inner.Height < 4) return;

        var shadowBounds = new Rectangle(outer.X, outer.Y + 2, outer.Width, outer.Height);
        using (var shadow = CreatePlaquePath(shadowBounds, 7))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
            e.Graphics.FillPath(shadowBrush, shadow);
        using (var outerPath = CreatePlaquePath(outer, 7))
        using (var outerBrush = new LinearGradientBrush(outer, Color.FromArgb(57, 37, 19), Color.FromArgb(16, 11, 8), LinearGradientMode.Vertical))
        using (var outerBorder = new Pen(Color.FromArgb(220, MedievalTheme.Gold), 1.25f))
        {
            e.Graphics.FillPath(outerBrush, outerPath);
            e.Graphics.DrawPath(outerBorder, outerPath);
        }
        using (var innerPath = CreatePlaquePath(inner, 4))
        using (var innerBrush = DashboardTextures.CreateButtonBrush())
        using (var innerBorder = new Pen(_hovered ? MedievalTheme.GoldBright : MedievalTheme.BorderStrong, 1f))
        {
            e.Graphics.FillPath(innerBrush, innerPath);
            e.Graphics.DrawPath(innerBorder, innerPath);
        }

        var textBounds = Rectangle.Inflate(inner, -5, -1);
        var textShadowBounds = new Rectangle(textBounds.X + 1, textBounds.Y + 1, textBounds.Width, textBounds.Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, textShadowBounds, Color.FromArgb(220, 0, 0, 0),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }

    private static GraphicsPath CreatePlaquePath(Rectangle bounds, int corner)
    {
        var cut = Math.Min(corner, Math.Min(bounds.Width, bounds.Height) / 3);
        var path = new GraphicsPath();
        path.AddPolygon(
        [
            new Point(bounds.Left + cut, bounds.Top), new Point(bounds.Right - cut, bounds.Top),
            new Point(bounds.Right, bounds.Top + cut), new Point(bounds.Right, bounds.Bottom - cut),
            new Point(bounds.Right - cut, bounds.Bottom), new Point(bounds.Left + cut, bounds.Bottom),
            new Point(bounds.Left, bounds.Bottom - cut), new Point(bounds.Left, bounds.Top + cut)
        ]);
        path.CloseFigure();
        return path;
    }
}

internal sealed class DashboardRecessedWell : Panel
{
    public DashboardRecessedWell()
    {
        BackColor = Color.Transparent;
        Padding = new Padding(10);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        if (bounds.Width < 4 || bounds.Height < 4) return;
        using var background = DashboardTextures.CreateMapWellBrush();
        using var outer = new Pen(Color.FromArgb(210, 0, 0, 0), 2f);
        using var inner = new Pen(Color.FromArgb(150, MedievalTheme.Border), 1f);
        e.Graphics.FillRectangle(background, bounds);
        e.Graphics.DrawRectangle(outer, bounds);
        var inset = Rectangle.Inflate(bounds, -3, -3);
        if (inset.Width > 1 && inset.Height > 1) e.Graphics.DrawRectangle(inner, inset);
    }
}

internal sealed class DashboardMapRow : TableLayoutPanel
{
    public DashboardMapRow()
    {
        BackColor = Color.Transparent;
        ColumnCount = 2;
        Margin = new Padding(0, 0, 0, 9);
        Padding = new Padding(8, 3, 6, 3);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        if (bounds.Width < 4 || bounds.Height < 4) return;
        using var fill = DashboardTextures.CreateMapRowBrush();
        using var shadow = new Pen(Color.FromArgb(210, 0, 0, 0), 2f);
        using var edge = new Pen(Color.FromArgb(165, MedievalTheme.BorderStrong), 1f);
        e.Graphics.FillRectangle(fill, bounds);
        e.Graphics.DrawRectangle(shadow, bounds);
        var inset = Rectangle.Inflate(bounds, -3, -3);
        if (inset.Width > 1 && inset.Height > 1) e.Graphics.DrawRectangle(edge, inset);
    }
}

internal static class DashboardTextures
{
    private const string ButtonStoneResource = "EDMWMapDesigner.Assets.dashboard-button-stone-v2.png";
    private const string MapRowStoneResource = "EDMWMapDesigner.Assets.dashboard-map-row-stone-v2.png";
    private static readonly Lazy<Bitmap> ButtonStone = new(() => Load(ButtonStoneResource));
    private static readonly Lazy<Bitmap> MapRowStone = new(() => Load(MapRowStoneResource));

    public static TextureBrush CreateButtonBrush() => CreateBrush(ButtonStone.Value, 0.22f);
    public static TextureBrush CreateMapRowBrush() => CreateBrush(MapRowStone.Value, 0.24f);
    public static TextureBrush CreateMapWellBrush() => CreateBrush(MapRowStone.Value, 0.16f);

    private static TextureBrush CreateBrush(Bitmap bitmap, float scale)
    {
        var brush = new TextureBrush(bitmap, WrapMode.Tile);
        brush.ScaleTransform(scale, scale);
        return brush;
    }

    private static Bitmap Load(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Dashboard texture '{resourceName}' is missing.");
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }
}

internal sealed class MedievalBufferedPanel : Panel
{
    public MedievalBufferedPanel() => DoubleBuffered = true;
}

internal sealed class MedievalBufferedTableLayoutPanel : TableLayoutPanel
{
    public MedievalBufferedTableLayoutPanel() => DoubleBuffered = true;
}

internal sealed class MedievalInputFrame : Panel
{
    private readonly Control _input;

    public MedievalInputFrame(Control input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        Height = Math.Max(25, input.PreferredSize.Height + 4);
        Margin = Padding.Empty;
        Padding = new Padding(1);
        BackColor = MedievalTheme.Input;

        input.Dock = DockStyle.None;
        input.Margin = Padding.Empty;
        if (input is NumericUpDown numeric) numeric.BorderStyle = BorderStyle.None;
        input.Enter += (_, _) => Invalidate();
        input.Leave += (_, _) => Invalidate();
        Controls.Add(input);
    }

    protected override void OnLayout(LayoutEventArgs args)
    {
        base.OnLayout(args);
        if (_input is null) return;
        var inset = Math.Max(4, DeviceDpi / 24);
        var width = Math.Max(1, ClientSize.Width - inset * 2);
        var flexible = _input is TextBox { Multiline: true } || _input is not (TextBox or ComboBox or NumericUpDown);
        var height = flexible ? Math.Max(1, ClientSize.Height - 2) :
            Math.Min(Math.Max(1, ClientSize.Height - 2), _input is TextBox ? _input.Font.Height + 4 : _input.PreferredSize.Height);
        _input.Bounds = new Rectangle(inset, Math.Max(1, (ClientSize.Height - height) / 2), width, height);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        using var border = new Pen(_input.ContainsFocus ? MedievalTheme.GoldBright : MedievalTheme.BorderStrong);
        args.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
    }
}

internal sealed class MedievalNumericInput : UserControl
{
    private readonly TextBox _editor;
    private readonly TableLayoutPanel _layout;
    private readonly MedievalSpinButton _increase;
    private readonly MedievalSpinButton _decrease;
    private decimal _minimum;
    private decimal _maximum = 100;
    private decimal _increment = 1;
    private decimal _value;
    private bool _synchronizing;

    public MedievalNumericInput()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Height = 25;
        MinimumSize = new Size(52, 25);
        BackColor = MedievalTheme.BorderStrong;
        Padding = new Padding(1);
        AccessibleRole = AccessibleRole.SpinButton;
        TabStop = false;

        _editor = new TextBox
        {
            BorderStyle = BorderStyle.None,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = MedievalTheme.Input,
            ForeColor = MedievalTheme.Text,
            Text = "0",
            TextAlign = HorizontalAlignment.Left
        };
        _editor.Enter += (_, _) => BackColor = MedievalTheme.GoldBright;
        _editor.Leave += (_, _) =>
        {
            CommitEditor();
            BackColor = MedievalTheme.BorderStrong;
        };
        _editor.KeyDown += EditorKeyDown;
        _editor.MouseWheel += EditorMouseWheel;

        _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = MedievalTheme.Input
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        _increase = new MedievalSpinButton(increase: true) { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _decrease = new MedievalSpinButton(increase: false) { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _increase.Click += (_, _) => Step(1);
        _decrease.Click += (_, _) => Step(-1);

        _layout.Controls.Add(_editor, 0, 0);
        _layout.SetRowSpan(_editor, 2);
        _layout.Controls.Add(_increase, 1, 0);
        _layout.Controls.Add(_decrease, 1, 1);
        Controls.Add(_layout);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSpinButtons
    {
        get => _increase.Visible;
        set
        {
            _increase.Visible = value;
            _decrease.Visible = value;
            _layout.ColumnStyles[1].Width = value ? 18 : 0;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HorizontalAlignment TextAlignment
    {
        get => _editor.TextAlign;
        set => _editor.TextAlign = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            if (_maximum < value) _maximum = value;
            Value = _value;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            if (_minimum > value) _minimum = value;
            Value = _value;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal Increment
    {
        get => _increment;
        set => _increment = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal Value
    {
        get => _value;
        set
        {
            var normalized = Math.Clamp(value, _minimum, _maximum);
            if (_value == normalized && _editor.Text == normalized.ToString()) return;
            var changed = _value != normalized;
            _value = normalized;
            SynchronizeEditor();
            if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    protected override void OnFontChanged(EventArgs eventArgs)
    {
        base.OnFontChanged(eventArgs);
        if (_editor is not null) _editor.Font = Font;
    }

    private void EditorKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Keys.Up)
        {
            Step(1);
            eventArgs.SuppressKeyPress = true;
        }
        else if (eventArgs.KeyCode == Keys.Down)
        {
            Step(-1);
            eventArgs.SuppressKeyPress = true;
        }
        else if (eventArgs.KeyCode == Keys.Enter)
        {
            CommitEditor();
            eventArgs.SuppressKeyPress = true;
        }
    }

    private void EditorMouseWheel(object? sender, MouseEventArgs eventArgs)
    {
        Step(Math.Sign(eventArgs.Delta));
        if (eventArgs is HandledMouseEventArgs handled) handled.Handled = true;
    }

    private void Step(int direction)
    {
        CommitEditor();
        Value += direction * _increment;
        _editor.Focus();
        _editor.SelectAll();
    }

    private void CommitEditor()
    {
        if (_synchronizing) return;
        if (decimal.TryParse(_editor.Text, out var parsed)) Value = parsed;
        else SynchronizeEditor();
    }

    private void SynchronizeEditor()
    {
        _synchronizing = true;
        try
        {
            _editor.Text = _value.ToString();
        }
        finally
        {
            _synchronizing = false;
        }
    }
}

internal sealed class MedievalSpinButton : Button
{
    private readonly bool _increase;
    private bool _hovered;

    public MedievalSpinButton(bool increase)
    {
        _increase = increase;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TabStop = false;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var background = new SolidBrush(_hovered ? MedievalTheme.SurfaceRaised : MedievalTheme.Input);
        e.Graphics.FillRectangle(background, ClientRectangle);
        using var separator = new Pen(MedievalTheme.Border);
        e.Graphics.DrawLine(separator, 0, 0, 0, Height);
        e.Graphics.DrawLine(separator, 0, Height - 1, Width, Height - 1);

        var centerX = Width / 2;
        var centerY = Height / 2;
        Point[] triangle = _increase
            ? [new(centerX - 3, centerY + 2), new(centerX + 3, centerY + 2), new(centerX, centerY - 2)]
            : [new(centerX - 3, centerY - 2), new(centerX + 3, centerY - 2), new(centerX, centerY + 2)];
        using var arrow = new SolidBrush(_hovered ? MedievalTheme.GoldBright : MedievalTheme.Gold);
        e.Graphics.FillPolygon(arrow, triangle);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        base.OnMouseEnter(eventArgs);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        _hovered = false;
        Invalidate();
    }
}

internal sealed class MedievalComboBox : ComboBox
{
    private const int PaintMessage = 0x000F;
    private const int PrintMessage = 0x0317;
    private const int PrintClientMessage = 0x0318;
    public MedievalComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = 22;
        BackColor = MedievalTheme.Input;
        ForeColor = MedievalTheme.Text;
        HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(this);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var selected = (e.State & DrawItemState.Selected) != 0;
        var currentField = (e.State & DrawItemState.ComboBoxEdit) != 0 || e.Index < 0;
        using var background = new SolidBrush(
            selected && !currentField ? MedievalTheme.Crimson : MedievalTheme.Input);
        e.Graphics.FillRectangle(background, e.Bounds);
        var item = currentField ? SelectedItem : e.Index < Items.Count ? Items[e.Index] : null;
        var text = item is null ? string.Empty : GetItemText(item);
        TextRenderer.DrawText(e.Graphics, text, Font, Rectangle.Inflate(e.Bounds, -5, 0),
            selected && !currentField ? MedievalTheme.GoldBright : MedievalTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (currentField)
        {
            using var border = new Pen(Focused ? MedievalTheme.GoldBright : MedievalTheme.BorderStrong);
            e.Graphics.DrawRectangle(border, e.Bounds.X, e.Bounds.Y,
                Math.Max(0, e.Bounds.Width - 1), Math.Max(0, e.Bounds.Height - 1));
        }
        if (!currentField && (e.State & DrawItemState.Focus) != 0)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -2, -2));
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == PaintMessage && IsHandleCreated && DropDownStyle == ComboBoxStyle.DropDownList)
        {
            // Keep the native value and custom chrome in one paint transaction.
            // Drawing through a new window DC after native EndPaint conflicts
            // with composition and can expose successive incomplete frames.
            var handle = Handle;
            var deviceContext = BeginPaint(handle, out var paint);
            if (deviceContext != IntPtr.Zero)
            {
                try
                {
                    var nativePaint = Message.Create(handle, PrintClientMessage, deviceContext, (IntPtr)0x000C);
                    base.WndProc(ref nativePaint);
                    using var graphics = Graphics.FromHdc(deviceContext);
                    DrawChrome(graphics);
                    message.Result = IntPtr.Zero;
                }
                finally { EndPaint(handle, ref paint); }
                return;
            }
        }
        base.WndProc(ref message);
        if (message.Msg is not (PrintMessage or PrintClientMessage) || message.WParam == IntPtr.Zero ||
            !IsHandleCreated || DropDownStyle != ComboBoxStyle.DropDownList)
            return;
        using var printGraphics = Graphics.FromHdc(message.WParam);
        DrawChrome(printGraphics);
    }

    private void DrawChrome(Graphics graphics)
    {
        var buttonWidth = Math.Max(20, SystemInformation.VerticalScrollBarWidth);
        var button = new Rectangle(Math.Max(0, ClientSize.Width - buttonWidth), 0,
            buttonWidth, ClientSize.Height);
        using var buttonFill = new SolidBrush(MedievalTheme.SurfaceRaised);
        using var border = new Pen(Focused ? MedievalTheme.GoldBright : MedievalTheme.BorderStrong);
        using var arrow = new SolidBrush(MedievalTheme.GoldBright);
        graphics.FillRectangle(buttonFill, button);
        graphics.DrawRectangle(border, 0, 0, Math.Max(0, ClientSize.Width - 1),
            Math.Max(0, ClientSize.Height - 1));
        var centerX = button.Left + button.Width / 2;
        var centerY = button.Top + button.Height / 2 + 1;
        graphics.FillPolygon(arrow,
        [
            new Point(centerX - 4, centerY - 2),
            new Point(centerX + 4, centerY - 2),
            new Point(centerX, centerY + 3)
        ]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePaint
    {
        public IntPtr DeviceContext;
        public int Erase;
        public int Left, Top, Right, Bottom;
        public int Restore, IncrementalUpdate;
        public uint Reserved0, Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr BeginPaint(IntPtr window, out NativePaint paint);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr window, ref NativePaint paint);
}

internal sealed class MedievalSlider : Control
{
    private int _minimum;
    private int _maximum = 10;
    private int _value;
    private bool _dragging;
    private bool _hovered;

    public MedievalSlider()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable |
                 ControlStyles.SupportsTransparentBackColor, true);
        Height = 30;
        MinimumSize = new Size(80, 30);
        BackColor = Color.Transparent;
        ForeColor = MedievalTheme.Gold;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            if (_maximum < value) _maximum = value;
            Value = _value;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            if (_minimum > value) _minimum = value;
            Value = _value;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int TickFrequency { get; set; } = 1;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            var normalized = Math.Clamp(value, _minimum, _maximum);
            if (_value == normalized) return;
            _value = normalized;
            ValueChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    public event EventHandler? ValueChanged;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(7, Height / 2 - 3, Math.Max(1, Width - 14), 6);
        using var trackFill = new SolidBrush(MedievalTheme.Input);
        using var trackBorder = new Pen(MedievalTheme.BorderStrong);
        e.Graphics.FillRectangle(trackFill, track);
        e.Graphics.DrawRectangle(trackBorder, track);

        var thumbCenter = ValueToX(_value);
        var filled = Rectangle.FromLTRB(track.Left + 1, track.Top + 1, Math.Max(track.Left + 1, thumbCenter), track.Bottom);
        using var activeFill = new SolidBrush(MedievalTheme.Crimson);
        e.Graphics.FillRectangle(activeFill, filled);

        using var thumbFill = new SolidBrush(_hovered || _dragging ? MedievalTheme.GoldBright : MedievalTheme.Gold);
        using var thumbBorder = new Pen(MedievalTheme.BorderStrong);
        var thumb = new Rectangle(thumbCenter - 6, Height / 2 - 8, 12, 16);
        e.Graphics.FillEllipse(thumbFill, thumb);
        e.Graphics.DrawEllipse(thumbBorder, thumb);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != MouseButtons.Left) return;
        Focus();
        _dragging = true;
        Capture = true;
        SetFromX(eventArgs.X);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        _hovered = true;
        if (_dragging) SetFromX(eventArgs.X);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        _hovered = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs eventArgs)
    {
        base.OnMouseWheel(eventArgs);
        Value += Math.Sign(eventArgs.Delta) * Math.Max(1, TickFrequency);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if (eventArgs.KeyCode is Keys.Left or Keys.Down)
        {
            Value -= Math.Max(1, TickFrequency);
            eventArgs.Handled = true;
        }
        else if (eventArgs.KeyCode is Keys.Right or Keys.Up)
        {
            Value += Math.Max(1, TickFrequency);
            eventArgs.Handled = true;
        }
    }

    private int ValueToX(int value)
    {
        var range = Math.Max(1, _maximum - _minimum);
        return 7 + (int)Math.Round((value - _minimum) / (double)range * Math.Max(1, Width - 14));
    }

    private void SetFromX(int x)
    {
        var width = Math.Max(1, Width - 14);
        var ratio = Math.Clamp((x - 7) / (double)width, 0, 1);
        Value = _minimum + (int)Math.Round(ratio * (_maximum - _minimum));
    }
}

internal static class MedievalAssets
{
    private const string BannerResource = "EDMWMapDesigner.Assets.medieval-command-banner.png";
    private const string FrameResource = "EDMWMapDesigner.Assets.ornate-panel-frame.png";
    private const string RailResource = "EDMWMapDesigner.Assets.medieval-command-rail.png";
    private const string CartographyResource = "EDMWMapDesigner.Assets.dark-cartography-texture.png";

    public static Image CommandBanner { get; } = LoadBitmap(BannerResource);
    public static Image OrnateFrame { get; } = LoadBitmap(FrameResource);
    public static Image CommandRail { get; } = LoadBitmap(RailResource);
    public static Image CartographyTexture { get; } = LoadBitmap(CartographyResource);

    private static Bitmap LoadBitmap(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null) return CreateFallback(resourceName);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            return new Bitmap(image);
        }
        catch (Exception) when (resourceName is BannerResource or FrameResource or RailResource or CartographyResource)
        {
            return CreateFallback(resourceName);
        }
    }

    private static Bitmap CreateFallback(string resourceName)
    {
        var size = resourceName == BannerResource ? new Size(1200, 180) : new Size(768, 768);
        var bitmap = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(MedievalTheme.Shell);
        using var background = new LinearGradientBrush(new Rectangle(Point.Empty, size),
            MedievalTheme.SurfaceRaised, MedievalTheme.Shell, 35f);
        graphics.FillRectangle(background, new Rectangle(Point.Empty, size));
        using var border = new Pen(Color.FromArgb(180, MedievalTheme.BorderStrong), Math.Max(2, size.Width / 260f));
        graphics.DrawRectangle(border, 4, 4, size.Width - 9, size.Height - 9);
        using var fine = new Pen(Color.FromArgb(90, MedievalTheme.Gold), 1);
        var spacing = resourceName == BannerResource ? 32 : 48;
        for (var x = -size.Height; x < size.Width; x += spacing)
            graphics.DrawLine(fine, x, size.Height, x + size.Height, 0);
        if (resourceName == FrameResource)
        {
            using var inner = new Pen(MedievalTheme.Gold, 8);
            graphics.DrawRectangle(inner, 22, 22, size.Width - 45, size.Height - 45);
        }
        return bitmap;
    }
}

internal static class MedievalArt
{
    public static void DrawCover(Graphics graphics, Image image, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var scale = Math.Max(bounds.Width / (float)image.Width, bounds.Height / (float)image.Height);
        var width = image.Width * scale;
        var height = image.Height * scale;
        var destination = new RectangleF(
            bounds.Left + (bounds.Width - width) / 2f,
            bounds.Top + (bounds.Height - height) / 2f,
            width,
            height);
        graphics.DrawImage(image, destination);
    }

    public static void DrawOrnateFrame(Graphics graphics, Rectangle bounds, bool drawCenter = true)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var image = MedievalAssets.OrnateFrame;
        const int sourceCorner = 145;
        var destinationCorner = Math.Clamp(Math.Min(bounds.Width, bounds.Height) / 10, 18, 34);

        if (drawCenter)
        {
            var centerSource = new Rectangle(sourceCorner, sourceCorner,
                image.Width - sourceCorner * 2, image.Height - sourceCorner * 2);
            var centerDestination = Rectangle.FromLTRB(bounds.Left + destinationCorner, bounds.Top + destinationCorner,
                bounds.Right - destinationCorner, bounds.Bottom - destinationCorner);
            graphics.DrawImage(image, centerDestination, centerSource, GraphicsUnit.Pixel);
        }

        DrawSlice(graphics, image, new Rectangle(0, 0, sourceCorner, sourceCorner),
            new Rectangle(bounds.Left, bounds.Top, destinationCorner, destinationCorner));
        DrawSlice(graphics, image, new Rectangle(image.Width - sourceCorner, 0, sourceCorner, sourceCorner),
            new Rectangle(bounds.Right - destinationCorner, bounds.Top, destinationCorner, destinationCorner));
        DrawSlice(graphics, image, new Rectangle(0, image.Height - sourceCorner, sourceCorner, sourceCorner),
            new Rectangle(bounds.Left, bounds.Bottom - destinationCorner, destinationCorner, destinationCorner));
        DrawSlice(graphics, image, new Rectangle(image.Width - sourceCorner, image.Height - sourceCorner, sourceCorner, sourceCorner),
            new Rectangle(bounds.Right - destinationCorner, bounds.Bottom - destinationCorner, destinationCorner, destinationCorner));

        DrawSlice(graphics, image, new Rectangle(220, 0, 220, sourceCorner),
            new Rectangle(bounds.Left + destinationCorner, bounds.Top, Math.Max(0, bounds.Width - destinationCorner * 2), destinationCorner));
        DrawSlice(graphics, image, new Rectangle(220, image.Height - sourceCorner, 220, sourceCorner),
            new Rectangle(bounds.Left + destinationCorner, bounds.Bottom - destinationCorner, Math.Max(0, bounds.Width - destinationCorner * 2), destinationCorner));
        DrawSlice(graphics, image, new Rectangle(0, 220, sourceCorner, 220),
            new Rectangle(bounds.Left, bounds.Top + destinationCorner, destinationCorner, Math.Max(0, bounds.Height - destinationCorner * 2)));
        DrawSlice(graphics, image, new Rectangle(image.Width - sourceCorner, 220, sourceCorner, 220),
            new Rectangle(bounds.Right - destinationCorner, bounds.Top + destinationCorner, destinationCorner, Math.Max(0, bounds.Height - destinationCorner * 2)));

        var ornamentWidth = Math.Min(180, Math.Max(84, bounds.Width / 5));
        var ornamentHeight = Math.Min(62, Math.Max(38, destinationCorner * 2));
        var ornamentSourceWidth = 420;
        var ornamentSourceLeft = (image.Width - ornamentSourceWidth) / 2;
        DrawSlice(graphics, image, new Rectangle(ornamentSourceLeft, 0, ornamentSourceWidth, 190),
            new Rectangle(bounds.Left + (bounds.Width - ornamentWidth) / 2, bounds.Top, ornamentWidth, ornamentHeight));
        DrawSlice(graphics, image, new Rectangle(ornamentSourceLeft, image.Height - 190, ornamentSourceWidth, 190),
            new Rectangle(bounds.Left + (bounds.Width - ornamentWidth) / 2, bounds.Bottom - ornamentHeight, ornamentWidth, ornamentHeight));
    }

    private static void DrawSlice(Graphics graphics, Image image, Rectangle source, Rectangle destination)
    {
        if (destination.Width <= 0 || destination.Height <= 0) return;
        graphics.DrawImage(image, destination, source, GraphicsUnit.Pixel);
    }
}

internal sealed class MedievalBanner : Control
{
    public MedievalBanner()
    {
        DoubleBuffered = true;
        Dock = DockStyle.Top;
        Height = 124;
        MinimumSize = new Size(0, 108);
        AccessibleName = "EDMW Mod Studio banner";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        MedievalArt.DrawCover(e.Graphics, MedievalAssets.CommandBanner, ClientRectangle);

        using var sideShade = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(205, Color.Black), Color.FromArgb(48, Color.Black), LinearGradientMode.Horizontal);
        e.Graphics.FillRectangle(sideShade, ClientRectangle);

        var titleBounds = new Rectangle(24, Height - 70, Math.Max(0, Width - 150), 30);
        var subtitleBounds = new Rectangle(26, Height - 40, Math.Max(0, Width - 155), 18);
        using var titleFont = MedievalTheme.DisplayFont(15.5f);
        using var subtitleFont = MedievalTheme.UiFont(7.75f, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, "EDMW MOD STUDIO", titleFont, titleBounds,
            MedievalTheme.GoldBright, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, "EDMW  /  BATTLEFIELD DESIGN COMMAND", subtitleFont, subtitleBounds,
            MedievalTheme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        MedievalArt.DrawOrnateFrame(e.Graphics, ClientRectangle, drawCenter: false);
    }
}

internal sealed class OrnateFramePanel : Panel
{
    public OrnateFramePanel(int framePadding = 24)
    {
        DoubleBuffered = true;
        BackColor = MedievalTheme.Shell;
        Padding = new Padding(framePadding);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        MedievalArt.DrawOrnateFrame(e.Graphics, ClientRectangle);
    }
}

internal sealed class MedievalSceneRail : Control
{
    public MedievalSceneRail()
    {
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        AccessibleName = "Medieval command scene";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        MedievalArt.DrawCover(e.Graphics, MedievalAssets.CommandRail, ClientRectangle);
        MedievalArt.DrawOrnateFrame(e.Graphics, ClientRectangle, drawCenter: false);
    }
}

internal sealed class MedievalLeatherPanel : Panel
{
    public MedievalLeatherPanel()
    {
        DoubleBuffered = true;
        BackColor = MedievalTheme.Sidebar;
        HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(this);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var texture = MedievalAssets.OrnateFrame;
        var source = new Rectangle(texture.Width / 3, texture.Height / 3,
            texture.Width / 3, texture.Height / 3);
        e.Graphics.DrawImage(texture, ClientRectangle, source, GraphicsUnit.Pixel);

        using var wash = new SolidBrush(Color.FromArgb(82, MedievalTheme.Sidebar));
        e.Graphics.FillRectangle(wash, ClientRectangle);
    }
}

internal sealed class MedievalScrollPanel : UserControl
{
    private readonly MedievalLeatherPanel _viewport;
    private readonly MedievalVerticalScrollBar _scrollBar;
    private readonly ColumnStyle _scrollBarColumn;
    private Control? _content;
    private bool _updatingLayout;

    public MedievalScrollPanel()
    {
        DoubleBuffered = true;
        BackColor = MedievalTheme.Sidebar;

        _viewport = new MedievalLeatherPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            TabStop = true
        };
        _viewport.MouseWheel += ScrollFromMouseWheel;
        _viewport.Resize += (_, _) => UpdateScrollMetrics();

        _scrollBar = new MedievalVerticalScrollBar
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Visible = false
        };
        _scrollBar.ValueChanged += (_, _) => PositionContent();

        _scrollBarColumn = new ColumnStyle(SizeType.Absolute, 0);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = MedievalTheme.BorderStrong
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(_scrollBarColumn);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_viewport, 0, 0);
        layout.Controls.Add(_scrollBar, 1, 0);
        Controls.Add(layout);
    }

    public void SetContent(Control content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_content is not null) throw new InvalidOperationException("Scroll content has already been assigned.");

        _content = content;
        content.Dock = DockStyle.None;
        content.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        content.Location = Point.Empty;
        content.SizeChanged += (_, _) => UpdateScrollMetrics();
        AttachMouseWheel(content);
        _viewport.Controls.Add(content);
        UpdateScrollMetrics();
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        UpdateScrollMetrics();
    }

    private void UpdateScrollMetrics()
    {
        if (_updatingLayout || _content is null || _viewport.ClientSize.Width <= 0) return;
        _updatingLayout = true;
        try
        {
            _content.Width = _viewport.ClientSize.Width;
            _content.PerformLayout();

            var needsScrollBar = _content.Height > _viewport.ClientSize.Height;
            var expectedColumnWidth = needsScrollBar ? 16 : 0;
            if ((int)_scrollBarColumn.Width != expectedColumnWidth)
            {
                _scrollBarColumn.Width = expectedColumnWidth;
                _scrollBar.Visible = needsScrollBar;
                PerformLayout();
                _content.Width = Math.Max(0, _viewport.ClientSize.Width);
                _content.PerformLayout();
            }

            var maximum = Math.Max(0, _content.Height - _viewport.ClientSize.Height);
            _scrollBar.SetRange(maximum, _viewport.ClientSize.Height);
            PositionContent();
        }
        finally
        {
            _updatingLayout = false;
        }
    }

    private void PositionContent()
    {
        if (_content is null) return;
        _content.Location = new Point(0, -_scrollBar.Value);
    }

    private void AttachMouseWheel(Control control)
    {
        if (control is MedievalNumericInput or ComboBox or TrackBar) return;
        control.MouseWheel += ScrollFromMouseWheel;
        control.ControlAdded += (_, eventArgs) =>
        {
            if (eventArgs.Control is not null) AttachMouseWheel(eventArgs.Control);
        };
        foreach (Control child in control.Controls) AttachMouseWheel(child);
    }

    private void ScrollFromMouseWheel(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs is HandledMouseEventArgs { Handled: true }) return;
        _scrollBar.ScrollBy(-Math.Sign(eventArgs.Delta) * 48);
        if (eventArgs is HandledMouseEventArgs handled) handled.Handled = true;
    }
}

internal sealed class MedievalVerticalScrollBar : Control
{
    private const int ArrowHeight = 16;
    private const int MinimumThumbHeight = 28;
    private int _maximum;
    private int _viewportSize = 1;
    private int _value;
    private bool _draggingThumb;
    private int _dragOffset;
    private Point _pointer = new(-1, -1);

    public MedievalVerticalScrollBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = MedievalTheme.Input;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.ScrollBar;
        TabStop = false;
    }

    public int Value => _value;

    public event EventHandler? ValueChanged;

    public void SetRange(int maximum, int viewportSize)
    {
        _maximum = Math.Max(0, maximum);
        _viewportSize = Math.Max(1, viewportSize);
        SetValue(_value);
        Invalidate();
    }

    public void ScrollBy(int delta) => SetValue(_value + delta);

    protected override void OnPaint(PaintEventArgs e)
    {
        using var track = new SolidBrush(MedievalTheme.Input);
        e.Graphics.FillRectangle(track, ClientRectangle);

        using var edge = new Pen(MedievalTheme.BorderStrong);
        e.Graphics.DrawLine(edge, 0, 0, 0, Height);
        e.Graphics.DrawLine(edge, Width - 1, 0, Width - 1, Height);
        e.Graphics.DrawLine(edge, 0, ArrowHeight, Width, ArrowHeight);
        e.Graphics.DrawLine(edge, 0, Height - ArrowHeight - 1, Width, Height - ArrowHeight - 1);

        DrawArrow(e.Graphics, new Rectangle(0, 0, Width, ArrowHeight), increase: false,
            new Rectangle(0, 0, Width, ArrowHeight).Contains(_pointer));
        DrawArrow(e.Graphics, new Rectangle(0, Height - ArrowHeight, Width, ArrowHeight), increase: true,
            new Rectangle(0, Height - ArrowHeight, Width, ArrowHeight).Contains(_pointer));

        var thumb = ThumbRectangle();
        if (thumb.Height > 0)
        {
            var hovered = thumb.Contains(_pointer) || _draggingThumb;
            using var thumbFill = new LinearGradientBrush(thumb,
                hovered ? MedievalTheme.SurfaceRaised : MedievalTheme.Surface,
                MedievalTheme.Crimson, LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(thumbFill, thumb);
            using var thumbBorder = new Pen(hovered ? MedievalTheme.GoldBright : MedievalTheme.Gold);
            e.Graphics.DrawRectangle(thumbBorder, thumb.X, thumb.Y,
                Math.Max(0, thumb.Width - 1), Math.Max(0, thumb.Height - 1));
            using var grip = new Pen(Color.FromArgb(155, MedievalTheme.GoldBright));
            var middle = thumb.Top + thumb.Height / 2;
            e.Graphics.DrawLine(grip, thumb.Left + 4, middle - 2, thumb.Right - 4, middle - 2);
            e.Graphics.DrawLine(grip, thumb.Left + 4, middle + 2, thumb.Right - 4, middle + 2);
        }
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != MouseButtons.Left) return;
        var thumb = ThumbRectangle();
        if (eventArgs.Y < ArrowHeight)
        {
            ScrollBy(-48);
        }
        else if (eventArgs.Y >= Height - ArrowHeight)
        {
            ScrollBy(48);
        }
        else if (thumb.Contains(eventArgs.Location))
        {
            _draggingThumb = true;
            _dragOffset = eventArgs.Y - thumb.Top;
            Capture = true;
        }
        else
        {
            ScrollBy(eventArgs.Y < thumb.Top ? -_viewportSize : _viewportSize);
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        _pointer = eventArgs.Location;
        if (_draggingThumb)
        {
            var trackHeight = Math.Max(0, Height - ArrowHeight * 2);
            var thumbHeight = ThumbRectangle().Height;
            var travel = Math.Max(1, trackHeight - thumbHeight);
            var position = Math.Clamp(eventArgs.Y - ArrowHeight - _dragOffset, 0, travel);
            SetValue((int)Math.Round(position / (double)travel * _maximum));
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        _draggingThumb = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        if (!_draggingThumb) _pointer = new Point(-1, -1);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs eventArgs)
    {
        base.OnMouseWheel(eventArgs);
        ScrollBy(-Math.Sign(eventArgs.Delta) * 48);
    }

    private void SetValue(int value)
    {
        var normalized = Math.Clamp(value, 0, _maximum);
        if (_value == normalized) return;
        _value = normalized;
        ValueChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private Rectangle ThumbRectangle()
    {
        var trackHeight = Math.Max(0, Height - ArrowHeight * 2);
        if (trackHeight <= 0) return Rectangle.Empty;
        var contentSize = _maximum + _viewportSize;
        var thumbHeight = _maximum == 0
            ? trackHeight
            : Math.Clamp(trackHeight * _viewportSize / Math.Max(1, contentSize), MinimumThumbHeight, trackHeight);
        var travel = trackHeight - thumbHeight;
        var offset = _maximum == 0 ? 0 : (int)Math.Round(_value / (double)_maximum * travel);
        return new Rectangle(2, ArrowHeight + offset, Math.Max(0, Width - 4), thumbHeight);
    }

    private static void DrawArrow(Graphics graphics, Rectangle bounds, bool increase, bool hovered)
    {
        using var background = new SolidBrush(hovered ? MedievalTheme.SurfaceRaised : MedievalTheme.Surface);
        graphics.FillRectangle(background, bounds);
        var centerX = bounds.Left + bounds.Width / 2;
        var centerY = bounds.Top + bounds.Height / 2;
        Point[] triangle = increase
            ? [new(centerX - 4, centerY - 2), new(centerX + 4, centerY - 2), new(centerX, centerY + 3)]
            : [new(centerX - 4, centerY + 2), new(centerX + 4, centerY + 2), new(centerX, centerY - 3)];
        using var arrow = new SolidBrush(hovered ? MedievalTheme.GoldBright : MedievalTheme.Gold);
        graphics.FillPolygon(arrow, triangle);
    }
}

internal sealed class MedievalTabControl : UserControl
{
    private readonly TableLayoutPanel _tabBar;
    private readonly Panel _pageHost;
    private readonly List<(MedievalTabButton Button, Control Page)> _pages = [];
    private int _selectedIndex = -1;

    public MedievalTabControl()
    {
        DoubleBuffered = true;
        BackColor = MedievalTheme.BorderStrong;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = MedievalTheme.BorderStrong
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _tabBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = MedievalTheme.BorderStrong
        };
        _tabBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _pageHost = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(1),
            BackColor = MedievalTheme.BorderStrong
        };

        layout.Controls.Add(_tabBar, 0, 0);
        layout.Controls.Add(_pageHost, 0, 1);
        Controls.Add(layout);
    }

    public void AddPage(string title, Control page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(page);

        var index = _pages.Count;
        var button = new MedievalTabButton { Text = title, Dock = DockStyle.Fill, Margin = Padding.Empty };
        button.Click += (_, _) => SelectPage(index);

        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _pages.Add((button, page));
        _pageHost.Controls.Add(page);

        _tabBar.ColumnCount = _pages.Count;
        _tabBar.ColumnStyles.Clear();
        for (var column = 0; column < _pages.Count; column++)
            _tabBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _tabBar.Controls.Add(button, index, 0);

        if (_selectedIndex < 0) SelectPage(0);
    }

    private void SelectPage(int index)
    {
        if ((uint)index >= (uint)_pages.Count || index == _selectedIndex) return;
        SuspendLayout();
        try
        {
            for (var pageIndex = 0; pageIndex < _pages.Count; pageIndex++)
            {
                var selected = pageIndex == index;
                _pages[pageIndex].Button.SetSelected(selected);
                _pages[pageIndex].Page.Visible = selected;
                if (selected) _pages[pageIndex].Page.BringToFront();
            }
            _selectedIndex = index;
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }
}

internal sealed class MedievalTabButton : Button
{
    private bool _selected;

    public void SetSelected(bool selected)
    {
        if (_selected == selected) return;
        _selected = selected;
        Invalidate();
    }

    public MedievalTabButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        AccessibleRole = AccessibleRole.PageTab;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var backgroundColor = _selected
            ? MedievalTheme.Crimson
            : ClientRectangle.Contains(PointToClient(Cursor.Position))
                ? MedievalTheme.SurfaceRaised
                : MedievalTheme.Surface;
        using var background = new SolidBrush(backgroundColor);
        e.Graphics.FillRectangle(background, ClientRectangle);

        using var border = new Pen(_selected ? MedievalTheme.GoldBright : MedievalTheme.BorderStrong);
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

        if (_selected)
        {
            using var marker = new SolidBrush(MedievalTheme.Gold);
            Point[] points =
            [
                new(Width / 2 - 5, Height - 6),
                new(Width / 2 + 5, Height - 6),
                new(Width / 2, Height - 1)
            ];
            e.Graphics.FillPolygon(marker, points);
        }

        using var font = MedievalTheme.UiFont(9f, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, Text, font, ClientRectangle,
            _selected ? MedievalTheme.Text : MedievalTheme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}

internal sealed class MedievalToggleSwitch : CheckBox
{
    private bool _hovered;

    public MedievalToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        AutoSize = true;
        Cursor = Cursors.Hand;
        Appearance = Appearance.Normal;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var textSize = TextRenderer.MeasureText(Text, Font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size(textSize.Width + ScaleLogical(94) + Padding.Horizontal,
            Math.Max(textSize.Height, ScaleLogical(27)) + ScaleLogical(4) + Padding.Vertical);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var trackWidth = ScaleLogical(82);
        var trackHeight = ScaleLogical(25);
        var trackLeft = string.IsNullOrWhiteSpace(Text)
            ? (Width - trackWidth) / 2
            : Width - Padding.Right - trackWidth;
        var track = new Rectangle(trackLeft,
            (Height - trackHeight) / 2, trackWidth, trackHeight);
        var labelBounds = Rectangle.FromLTRB(Padding.Left, 0,
            track.Left - ScaleLogical(10), Height);

        TextRenderer.DrawText(e.Graphics, Text, Font, labelBounds,
            Enabled ? ForeColor : MedievalTheme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        using var background = new SolidBrush(MedievalTheme.Input);
        using var active = new SolidBrush(Checked ? MedievalTheme.Gold : MedievalTheme.SurfaceRaised);
        using var border = new Pen(_hovered || Focused ? MedievalTheme.GoldBright : MedievalTheme.BorderStrong);
        e.Graphics.FillRectangle(background, track);
        var half = track.Width / 2;
        var selected = Checked
            ? new Rectangle(track.Left + half, track.Top, track.Width - half, track.Height)
            : new Rectangle(track.Left, track.Top, half, track.Height);
        e.Graphics.FillRectangle(active, selected);
        e.Graphics.DrawRectangle(border, track.X, track.Y, track.Width - 1, track.Height - 1);
        e.Graphics.DrawLine(border, track.Left + half, track.Top, track.Left + half, track.Bottom - 1);

        var offBounds = new Rectangle(track.Left, track.Top, half, track.Height);
        var onBounds = new Rectangle(track.Left + half, track.Top, track.Width - half, track.Height);
        using var statusFont = MedievalTheme.UiFont(7.5f, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, "OFF", statusFont, offBounds,
            !Checked ? MedievalTheme.Text : MedievalTheme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, "ON", statusFont, onBounds,
            Checked ? Color.FromArgb(28, 20, 10) : MedievalTheme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        if (Focused)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(track, -2, -2));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    private int ScaleLogical(int value) => Math.Max(1, value * DeviceDpi / 96);
}

internal sealed class MedievalCard : Panel
{
    public MedievalCard()
    {
        BackColor = MedievalTheme.Surface;
        Padding = new Padding(14);
        Margin = new Padding(0, 4, 0, 10);
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(MedievalTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
    }
}

internal sealed class MedievalToolStripRenderer : ToolStripProfessionalRenderer
{
    public bool IsHomeScreen { get; set; }

    public MedievalToolStripRenderer() : base(new MedievalColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            using var dropDownBackground = new SolidBrush(MedievalTheme.Sidebar);
            e.Graphics.FillRectangle(dropDownBackground, e.AffectedBounds);
            return;
        }
        var image = MedievalAssets.OrnateFrame;
        var source = new Rectangle(image.Width / 3, image.Height / 3, image.Width / 3, image.Height / 3);
        e.Graphics.DrawImage(image, e.AffectedBounds, source, GraphicsUnit.Pixel);
        if (!IsHomeScreen)
        {
            using var upperLine = new Pen(Color.FromArgb(85, MedievalTheme.Gold));
            using var lowerLine = new Pen(MedievalTheme.BorderStrong);
            e.Graphics.DrawLine(upperLine, 0, e.ToolStrip.Height - 4, e.ToolStrip.Width, e.ToolStrip.Height - 4);
            e.Graphics.DrawLine(lowerLine, 0, e.ToolStrip.Height - 2, e.ToolStrip.Width, e.ToolStrip.Height - 2);
        }
        else
        {
            using var divider = new Pen(MedievalTheme.BorderStrong);
            e.Graphics.DrawLine(divider, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
        }
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(MedievalTheme.Gold);
        if (e.ToolStrip is ToolStripDropDown)
            e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, e.ToolStrip.Width - 1),
                Math.Max(0, e.ToolStrip.Height - 1));
        else if (!IsHomeScreen)
            e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        var selected = e.Item.Selected || (e.Item is ToolStripDropDownButton { Pressed: true });
        using var fill = new SolidBrush(selected ? MedievalTheme.Crimson : Color.Transparent);
        if (selected) e.Graphics.FillRectangle(fill, bounds);
        if (selected)
        {
            using var accent = new Pen(MedievalTheme.GoldBright);
            e.Graphics.DrawRectangle(accent, 0, 0, Math.Max(0, bounds.Width - 1),
                Math.Max(0, bounds.Height - 1));
        }
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var margin = new SolidBrush(MedievalTheme.Surface);
        e.Graphics.FillRectangle(margin, e.AffectedBounds);
        using var edge = new Pen(MedievalTheme.Border);
        e.Graphics.DrawLine(edge, e.AffectedBounds.Right - 1, e.AffectedBounds.Top,
            e.AffectedBounds.Right - 1, e.AffectedBounds.Bottom);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item is { Selected: true } ? MedievalTheme.GoldBright : MedievalTheme.Gold;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripButton button) return;
        var bounds = new Rectangle(Point.Empty, button.Size);
        var primary = string.Equals(button.Tag as string, "primary", StringComparison.Ordinal);
        var color = button.Pressed
            ? MedievalTheme.Crimson
            : button.Selected
                ? (primary ? MedievalTheme.CrimsonBright : MedievalTheme.SurfaceRaised)
                : (primary ? MedievalTheme.Crimson : Color.Transparent);
        if (color == Color.Transparent) return;
        using var brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, bounds);
        if (primary)
        {
            using var pen = new Pen(MedievalTheme.Gold);
            e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? MedievalTheme.Text : MedievalTheme.TextMuted;
        base.OnRenderItemText(e);
    }
}

internal static class MedievalDialog
{
    public static DialogResult Show(
        IWin32Window owner,
        string message,
        string title,
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None)
    {
        using var dialog = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(500, 0),
            MaximumSize = new Size(720, 700),
            BackColor = MedievalTheme.BorderStrong,
            ForeColor = MedievalTheme.Text,
            Font = MedievalTheme.UiFont(9.5f),
            Padding = new Padding(1)
        };

        var content = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = MedievalTheme.Sidebar,
            Margin = Padding.Empty
        };
        var titleBar = new TableLayoutPanel
        {
            Height = 38,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = MedievalTheme.SurfaceRaised,
            Margin = Padding.Empty
        };
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        titleBar.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
            ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.DisplayFont(9.5f)
        }, 0, 0);
        var close = new Button
        {
            Text = "×",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = MedievalTheme.TextMuted,
            Font = MedievalTheme.UiFont(14f),
            Margin = Padding.Empty,
            TabStop = false
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); };
        titleBar.Controls.Add(close, 1, 0);
        content.Controls.Add(titleBar);

        var messageRow = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = icon == MessageBoxIcon.None ? 1 : 2,
            Padding = new Padding(18, 18, 18, 14),
            BackColor = MedievalTheme.Sidebar,
            Margin = Padding.Empty
        };
        if (icon != MessageBoxIcon.None)
        {
            messageRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            messageRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            messageRow.Controls.Add(new Label
            {
                Text = icon switch
                {
                    MessageBoxIcon.Error => "×",
                    MessageBoxIcon.Warning => "!",
                    MessageBoxIcon.Question => "?",
                    _ => "i"
                },
                AutoSize = false,
                Size = new Size(34, 34),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = icon == MessageBoxIcon.Error ? MedievalTheme.Crimson : MedievalTheme.SurfaceRaised,
                ForeColor = MedievalTheme.GoldBright,
                Font = MedievalTheme.DisplayFont(14f),
                Margin = new Padding(0, 0, 12, 0)
            }, 0, 0);
        }
        var messageLabel = new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(590, 500),
            ForeColor = MedievalTheme.Text,
            Font = MedievalTheme.UiFont(9.5f),
            Margin = Padding.Empty
        };
        messageRow.Controls.Add(messageLabel, icon == MessageBoxIcon.None ? 0 : 1, 0);
        content.Controls.Add(messageRow);

        var buttonBar = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(12, 8, 12, 10),
            BackColor = MedievalTheme.Surface,
            Margin = Padding.Empty
        };
        void AddButton(string text, DialogResult result, bool primary = false)
        {
            var button = new Button
            {
                Text = text,
                DialogResult = result,
                AutoSize = false,
                Size = new Size(92, 30),
                Margin = new Padding(6, 0, 0, 0)
            };
            MedievalTheme.StyleSecondaryButton(button);
            if (primary)
            {
                button.BackColor = MedievalTheme.Crimson;
                button.FlatAppearance.BorderColor = MedievalTheme.Gold;
                dialog.AcceptButton = button;
            }
            if (result == DialogResult.Cancel) dialog.CancelButton = button;
            buttonBar.Controls.Add(button);
        }

        switch (buttons)
        {
            case MessageBoxButtons.YesNoCancel:
                AddButton("Cancel", DialogResult.Cancel);
                AddButton("No", DialogResult.No);
                AddButton("Yes", DialogResult.Yes, true);
                break;
            case MessageBoxButtons.YesNo:
                AddButton("No", DialogResult.No);
                AddButton("Yes", DialogResult.Yes, true);
                break;
            case MessageBoxButtons.OKCancel:
                AddButton("Cancel", DialogResult.Cancel);
                AddButton("OK", DialogResult.OK, true);
                break;
            default:
                AddButton("OK", DialogResult.OK, true);
                break;
        }
        content.Controls.Add(buttonBar);
        dialog.Controls.Add(content);
        return dialog.ShowDialog(owner);
    }
}

internal sealed class MedievalStatusStripRenderer : ToolStripProfessionalRenderer
{
    public MedievalStatusStripRenderer() : base(new MedievalColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var texture = MedievalAssets.OrnateFrame;
        var source = new Rectangle(texture.Width / 3, texture.Height / 3, texture.Width / 3, texture.Height / 3);
        e.Graphics.DrawImage(texture, e.AffectedBounds, source, GraphicsUnit.Pixel);
        using var wash = new SolidBrush(Color.FromArgb(126, MedievalTheme.Shell));
        e.Graphics.FillRectangle(wash, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var upper = new Pen(MedievalTheme.BorderStrong);
        using var lower = new Pen(Color.FromArgb(170, MedievalTheme.Gold));
        e.Graphics.DrawLine(upper, 0, 0, e.ToolStrip.Width, 0);
        e.Graphics.DrawLine(lower, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = MedievalTheme.TextMuted;
        base.OnRenderItemText(e);
    }
}

internal sealed class MedievalColorTable : ProfessionalColorTable
{
    public override Color ToolStripGradientBegin => MedievalTheme.Shell;
    public override Color ToolStripGradientMiddle => MedievalTheme.Shell;
    public override Color ToolStripGradientEnd => MedievalTheme.Shell;
    public override Color ButtonSelectedHighlight => MedievalTheme.SurfaceRaised;
    public override Color ButtonSelectedGradientBegin => MedievalTheme.SurfaceRaised;
    public override Color ButtonSelectedGradientEnd => MedievalTheme.SurfaceRaised;
    public override Color ButtonPressedGradientBegin => MedievalTheme.Crimson;
    public override Color ButtonPressedGradientEnd => MedievalTheme.Crimson;
    public override Color SeparatorDark => MedievalTheme.Border;
    public override Color SeparatorLight => MedievalTheme.Border;
    public override Color MenuItemSelected => MedievalTheme.Crimson;
    public override Color MenuItemSelectedGradientBegin => MedievalTheme.Crimson;
    public override Color MenuItemSelectedGradientEnd => MedievalTheme.Crimson;
    public override Color MenuItemPressedGradientBegin => MedievalTheme.SurfaceRaised;
    public override Color MenuItemPressedGradientMiddle => MedievalTheme.SurfaceRaised;
    public override Color MenuItemPressedGradientEnd => MedievalTheme.SurfaceRaised;
    public override Color MenuBorder => MedievalTheme.Gold;
    public override Color ImageMarginGradientBegin => MedievalTheme.Surface;
    public override Color ImageMarginGradientMiddle => MedievalTheme.Surface;
    public override Color ImageMarginGradientEnd => MedievalTheme.Surface;
}

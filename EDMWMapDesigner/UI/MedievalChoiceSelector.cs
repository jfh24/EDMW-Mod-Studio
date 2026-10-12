using System.Collections.ObjectModel;
using System.ComponentModel;

namespace EDMWMapDesigner.UI;

/// <summary>Keyboard-accessible, wrapping choice buttons in the application's palette.</summary>
internal sealed class MedievalChoiceSelector : UserControl
{
    private readonly List<ChoiceButton> _buttons = [];
    private int _selectedIndex = -1;
    private int _minimumButtonWidth = 60;
    private int _maximumButtonWidth = 200;
    private Func<object, Image?>? _iconProvider;
    private Func<object, string>? _displayTextProvider;
    private bool _showItemText = true;
    private readonly ToolTip _tips = new();
    private int _arrangedHeight;
    private bool _notifyingParent;

    public MedievalChoiceSelector()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.ResizeRedraw, true);
        Items = new ChoiceItemCollection(this);
        BackColor = MedievalTheme.Surface;
        ForeColor = MedievalTheme.Text;
        Font = MedievalTheme.UiFont(9);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        TabStop = false;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        Size = new Size(280, 30);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public ChoiceItemCollection Items { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < -1 || value >= Items.Count) throw new ArgumentOutOfRangeException(nameof(value));
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            UpdateButtons();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? SelectedItem => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int MinimumButtonWidth
    {
        get => _minimumButtonWidth;
        set { _minimumButtonWidth = Math.Max(30, value); PerformLayout(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int MaximumButtonWidth
    {
        get => _maximumButtonWidth;
        set { _maximumButtonWidth = Math.Max(30, value); PerformLayout(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<object, Image?>? IconProvider
    {
        get => _iconProvider;
        set { _iconProvider = value; RebuildButtons(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<object, string>? DisplayTextProvider
    {
        get => _displayTextProvider;
        set { _displayTextProvider = value; RebuildButtons(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowItemText
    {
        get => _showItemText;
        set { _showItemText = value; RebuildButtons(); }
    }

    public event EventHandler? SelectedIndexChanged;

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (_buttons is null) return base.GetPreferredSize(proposedSize);
        // TableLayoutPanel probes auto-sized rows with a one-pixel width.
        // Use the arranged width for that probe rather than reserving one row per item.
        var width = proposedSize.Width > 1 ? proposedSize.Width : Width > 1 ? Width : 280;
        return Arrange(width, false);
    }

    protected override void OnLayout(LayoutEventArgs args)
    {
        base.OnLayout(args);
        if (_buttons is null) return;
        var arranged = Arrange(ClientSize.Width, true);
        if (_arrangedHeight == arranged.Height) return;
        _arrangedHeight = arranged.Height;
        if (Parent is null || _notifyingParent) return;
        _notifyingParent = true;
        try { Parent.PerformLayout(this, nameof(PreferredSize)); }
        finally { _notifyingParent = false; }
    }

    protected override void OnFontChanged(EventArgs args)
    {
        base.OnFontChanged(args);
        if (_buttons is null) return;
        foreach (var button in _buttons) button.Font = Font;
        PerformLayout();
        Parent?.PerformLayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs args)
    {
        base.OnDpiChangedAfterParent(args);
        PerformLayout();
        Parent?.PerformLayout();
    }

    private Size Arrange(int availableWidth, bool apply)
    {
        availableWidth = Math.Max(30, availableWidth);
        var scale = DeviceDpi / 96f;
        var gap = Math.Max((int)Math.Round(4 * scale), Font.Height / 3);
        var height = Math.Max((int)Math.Round((ShowItemText ? 28 : 42) * scale),
            Font.Height + (int)Math.Round(12 * scale));
        var minimumWidth = (int)Math.Round(MinimumButtonWidth * scale);
        var maximumWidth = Math.Max(minimumWidth, (int)Math.Round(MaximumButtonWidth * scale));
        var x = 0;
        var y = 0;
        var usedWidth = 0;
        foreach (var button in _buttons)
        {
            var imageOnly = !ShowItemText && button.Image is not null;
            var imageWidth = button.Image is null ? 0 : height - 4;
            var measured = imageOnly ? height : TextRenderer.MeasureText(button.DisplayText, Font,
                Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + (int)Math.Round(20 * scale) + imageWidth;
            var width = Math.Min(availableWidth, Math.Clamp(measured, minimumWidth, maximumWidth));
            if (x > 0 && x + width > availableWidth) { x = 0; y += height + gap; }
            if (apply) button.Bounds = new Rectangle(x, y, width, height);
            usedWidth = Math.Max(usedWidth, x + width);
            x += width + gap;
        }
        return new Size(Math.Max(30, usedWidth), _buttons.Count == 0 ? 0 : y + height);
    }

    private void RebuildButtons()
    {
        SuspendLayout();
        foreach (var button in _buttons) { Controls.Remove(button); button.Dispose(); }
        _buttons.Clear();
        for (var index = 0; index < Items.Count; index++)
        {
            var itemIndex = index;
            var item = Items[index];
            var button = new ChoiceButton
            {
                Text = item.ToString() ?? "", Font = Font, UseMnemonic = false,
                Image = _iconProvider?.Invoke(item), AccessibleName = item.ToString(),
                AccessibleDescription = AccessibleName, Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat, DisplayText = _displayTextProvider?.Invoke(item) ?? item.ToString() ?? "",
                ImageOnly = !ShowItemText
            };
            _tips.SetToolTip(button, item.ToString());
            button.Click += (_, _) => SelectedIndex = itemIndex;
            button.KeyDown += (_, args) =>
            {
                var direction = args.KeyCode is Keys.Right or Keys.Down ? 1 : args.KeyCode is Keys.Left or Keys.Up ? -1 : 0;
                if (direction == 0 || Items.Count == 0) return;
                SelectedIndex = (itemIndex + direction + Items.Count) % Items.Count;
                _buttons[SelectedIndex].Focus();
                args.Handled = true;
                args.SuppressKeyPress = true;
            };
            Controls.Add(button);
            _buttons.Add(button);
        }
        if (_selectedIndex >= Items.Count) _selectedIndex = -1;
        UpdateButtons();
        ResumeLayout(true);
        Parent?.PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    private void UpdateButtons()
    {
        for (var index = 0; index < _buttons.Count; index++)
        {
            _buttons[index].Selected = index == _selectedIndex;
            _buttons[index].TabStop = index == _selectedIndex || _selectedIndex < 0 && index == 0;
            _buttons[index].Invalidate();
        }
    }

    internal sealed class ChoiceItemCollection(MedievalChoiceSelector owner) : Collection<object>
    {
        public void AddRange(IEnumerable<object> items)
        {
            foreach (var item in items) Items.Add(item);
            owner.RebuildButtons();
        }
        protected override void InsertItem(int index, object item) { base.InsertItem(index, item); owner.RebuildButtons(); }
        protected override void RemoveItem(int index) { base.RemoveItem(index); owner.RebuildButtons(); }
        protected override void SetItem(int index, object item) { base.SetItem(index, item); owner.RebuildButtons(); }
        protected override void ClearItems() { base.ClearItems(); owner.RebuildButtons(); }
    }

    private sealed class ChoiceButton : Button
    {
        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Selected { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string DisplayText { get; set; } = "";
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ImageOnly { get; set; }

        protected override void OnPaint(PaintEventArgs args)
        {
            var active = Selected || ContainsFocus;
            using var background = new SolidBrush(active ? MedievalTheme.SurfaceRaised : MedievalTheme.Input);
            using var border = new Pen(active ? MedievalTheme.Gold : MedievalTheme.Border);
            args.Graphics.FillRectangle(background, ClientRectangle);
            args.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            var inset = (int)Math.Round(8 * DeviceDpi / 96f);
            var bounds = Rectangle.Inflate(ClientRectangle, -inset, 0);
            if (Image is not null)
            {
                args.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                var size = Math.Max((int)Math.Round(16 * DeviceDpi / 96f), Height - inset);
                args.Graphics.DrawImage(Image, new Rectangle(ImageOnly ? (Width - size) / 2 : bounds.X,
                    (Height - size) / 2, size, size));
                if (ImageOnly) return;
                bounds.X += size + 5;
                bounds.Width -= size + 5;
            }
            TextRenderer.DrawText(args.Graphics, DisplayText, Font, bounds,
                Enabled ? active ? MedievalTheme.GoldBright : MedievalTheme.TextMuted : MedievalTheme.TextSecondary,
                TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }
}

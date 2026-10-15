using System.Globalization;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Stages catalog target choices until the user confirms the popup.</summary>
internal sealed class CivilizationEffectTargetPickerDialog : Form
{
    private const int PageSize = 18;
    private readonly CivilizationCatalogItem[] _items;
    private readonly HashSet<int> _selected;
    private readonly HashSet<int> _valid;
    private readonly CivilizationGameIcons? _icons;
    private readonly IReadOnlyDictionary<int, CivilizationCatalogAvailability> _availability;
    private readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Search by name or object ID…", AccessibleName = "Search affected targets" };
    private readonly MedievalComboBox _kind = Filter("Target type filter");
    private readonly MedievalComboBox _civilization = Filter("Target civilization filter");
    private readonly MedievalComboBox _age = Filter("Target age filter");
    private readonly FlowLayoutPanel _cards = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, AccessibleName = "Affected target cards", Margin = Padding.Empty };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };
    private readonly Label _selection = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, AccessibleName = "Selected target count" };
    private readonly Label _unavailableWarning = new() { AutoSize = true, Dock = DockStyle.Top, Text = "Clear to remove unavailable targets, then choose replacements.", ForeColor = MedievalTheme.Gold, Margin = new Padding(0, 6, 0, 0), Visible = false, AccessibleName = "Unavailable target guidance" };
    private readonly Button _use;
    private readonly Button _previous;
    private readonly Button _next;
    private CivilizationCatalogItem[] _filtered;
    private int _page;

    internal IReadOnlyList<int> SelectedObjectIds => _selected.Order().ToArray();

    internal CivilizationEffectTargetPickerDialog(IReadOnlyList<CivilizationCatalogItem> items,
        IEnumerable<int> selectedIds, CivilizationGameIcons? icons, CivilizationGameData? gameData)
    {
        _items = items.Where(item => item.Category is CivilizationCategory.Units or CivilizationCategory.Buildings)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.SourceId).ToArray();
        _filtered = _items;
        _valid = _items.Select(item => item.SourceId).ToHashSet();
        _selected = selectedIds.ToHashSet();
        _icons = icons;
        _availability = gameData?.GetTargetAvailability() ?? new Dictionary<int, CivilizationCatalogAvailability>();
        Text = "Choose affected targets";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(860, 640);
        MinimumSize = new Size(660, 480);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = MedievalTheme.UiFont(9);
        BackColor = MedievalTheme.Sidebar;
        ForeColor = MedievalTheme.Text;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 34, 42, 48 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Choose units and buildings", Dock = DockStyle.Fill, Font = MedievalTheme.UiFont(13, FontStyle.Bold), ForeColor = MedievalTheme.Gold, Margin = Padding.Empty }, 0, 0);
        MedievalTheme.StyleTextInput(_search);
        layout.Controls.Add(MedievalTheme.FrameInput(_search), 0, 1);
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 6, 0, 8), Margin = Padding.Empty };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        _kind.Items.AddRange([new FilterChoice(-1, "Units & buildings"), new FilterChoice((int)CivilizationCategory.Units, "Units"), new FilterChoice((int)CivilizationCategory.Buildings, "Buildings")]);
        _civilization.Items.Add(new FilterChoice(-1, "All civilizations"));
        _age.Items.Add(new FilterChoice(-1, "All ages"));
        if (gameData is not null)
        {
            foreach (var civilization in CivilizationEditProject.CreateDefault().Civilizations.Where(civ => civ.SourceRowIndex is not null))
                _civilization.Items.Add(new FilterChoice(civilization.SourceRowIndex!.Value, civilization.Name));
            foreach (var age in gameData.GetAgeNames().Where(age => age.Id != CivilizationAgeNames.UnboundedAge))
                _age.Items.Add(new FilterChoice(age.Id, age.Name));
        }
        _civilization.Enabled = _age.Enabled = gameData is not null;
        _kind.SelectedIndex = _civilization.SelectedIndex = _age.SelectedIndex = 0;
        foreach (var control in new[] { _kind, _civilization, _age })
        {
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 0, control == _age ? 0 : 8, 0);
            MedievalTheme.StyleTextInput(control);
        }
        filters.Controls.Add(_kind, 0, 0);
        filters.Controls.Add(_civilization, 1, 0);
        filters.Controls.Add(_age, 2, 0);
        layout.Controls.Add(filters, 0, 2);
        layout.Controls.Add(_cards, 0, 3);
        var paging = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 5, 0, 0), Margin = Padding.Empty };
        paging.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        paging.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paging.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        _previous = Action("‹", () => ChangePage(-1), "Previous target page", 32);
        _next = Action("›", () => ChangePage(1), "Next target page", 32);
        paging.Controls.Add(_previous, 0, 0);
        paging.Controls.Add(_status, 1, 0);
        paging.Controls.Add(_next, 2, 0);
        layout.Controls.Add(paging, 0, 4);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 10, 0, 0), Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.Controls.Add(_selection, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
        var cancel = Action("Cancel", () => DialogResult = DialogResult.Cancel, "Cancel affected target selection", 82);
        cancel.DialogResult = DialogResult.Cancel;
        _use = Action("Use selected", () => { if (_selected.Count > 0) DialogResult = DialogResult.OK; }, "Use selected affected targets", 112);
        var clear = Action("Clear", () => { _selected.Clear(); UpdateSelection(); }, "Clear selected affected targets", 70);
        actions.Controls.Add(cancel);
        actions.Controls.Add(_use);
        actions.Controls.Add(clear);
        footer.Controls.Add(actions, 1, 0);
        footer.Controls.Add(_unavailableWarning, 0, 1);
        footer.SetColumnSpan(_unavailableWarning, 2);
        layout.Controls.Add(footer, 0, 5);
        AcceptButton = _use;
        CancelButton = cancel;
        MedievalDialogFrame.Apply(this, layout);

        _search.TextChanged += (_, _) => ApplyFilters();
        foreach (var filter in new[] { _kind, _civilization, _age }) filter.SelectedIndexChanged += (_, _) => ApplyFilters();
        _cards.SizeChanged += (_, _) => ArrangeCards();
        Shown += (_, _) => _search.Focus();
        var initialIndex = Array.FindIndex(_filtered, item => _selected.Contains(item.SourceId));
        _page = initialIndex < 0 ? 0 : initialIndex / PageSize;
        ShowCards();
    }

    private void ApplyFilters()
    {
        var text = _search.Text.Trim();
        var kind = (_kind.SelectedItem as FilterChoice)?.Id ?? -1;
        var civilization = (_civilization.SelectedItem as FilterChoice)?.Id ?? -1;
        var age = (_age.SelectedItem as FilterChoice)?.Id ?? -1;
        _filtered = _items.Where(item =>
            (kind < 0 || (int)item.Category == kind) &&
            (text.Length == 0 || item.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || item.SourceId.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.Ordinal)) &&
            (civilization < 0 || _availability.TryGetValue(item.SourceId, out var civil) && civil.CivilizationRows.Contains(civilization)) &&
            (age < 0 || _availability.TryGetValue(item.SourceId, out var range) && range.FirstAge <= age && range.LastAge >= age)).ToArray();
        _page = 0;
        ShowCards();
    }

    private void ChangePage(int direction)
    {
        _page = Math.Clamp(_page + direction, 0, Math.Max(0, (_filtered.Length - 1) / PageSize));
        ShowCards();
    }

    private void ShowCards()
    {
        _cards.SuspendLayout();
        foreach (var card in _cards.Controls.Cast<Control>().ToArray()) card.Dispose();
        _cards.Controls.Clear();
        var first = _page * PageSize;
        for (var index = first; index < Math.Min(first + PageSize, _filtered.Length); index++)
        {
            var item = _filtered[index];
            var detail = $"{(item.Category == CivilizationCategory.Units ? "Unit" : "Building")} · ID {item.SourceId}";
            var card = new TargetCard(item.Name, detail, _icons?.ForObject(item.SourceId) ?? ProgressionIcons.ForKind(item.Category.ToString()))
            { Tag = item.SourceId, AccessibleName = "Select target " + item.Name, AccessibleRole = AccessibleRole.CheckButton };
            card.Click += (_, _) => { if (!_selected.Add(item.SourceId)) _selected.Remove(item.SourceId); UpdateSelection(); };
            card.KeyDown += (_, args) => MoveCardFocus(item.SourceId, card, args);
            _cards.Controls.Add(card);
        }
        if (_filtered.Length == 0) _cards.Controls.Add(new Label { Text = "No targets match these filters.", AutoSize = true, ForeColor = MedievalTheme.TextSecondary, Margin = new Padding(4, 15, 4, 0) });
        _previous.Enabled = _page > 0;
        _next.Enabled = first + PageSize < _filtered.Length;
        _status.Text = _filtered.Length == 0 ? "0 results" : $"{_filtered.Length:N0} results · Page {_page + 1} of {(_filtered.Length + PageSize - 1) / PageSize}";
        UpdateSelection();
        _cards.ResumeLayout(true);
        ArrangeCards();
        _cards.AutoScrollPosition = Point.Empty;
    }

    private void UpdateSelection()
    {
        var unavailable = _selected.Count(id => !_valid.Contains(id));
        _selection.Text = $"{_selected.Count:N0} selected" + (unavailable > 0 ? $" ({unavailable} unavailable)" : "");
        _unavailableWarning.Visible = unavailable > 0;
        _use.Enabled = _selected.Count > 0 && unavailable == 0;
        foreach (var card in _cards.Controls.OfType<TargetCard>()) card.SetSelected(_selected.Contains((int)card.Tag!));
    }

    private void ArrangeCards()
    {
        var gap = Math.Max(8, DeviceDpi * 8 / 96);
        var available = Math.Max(1, _cards.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        var columns = Math.Max(1, available / Math.Max(190, DeviceDpi * 230 / 96));
        foreach (var card in _cards.Controls.OfType<TargetCard>())
        {
            card.Size = new Size(Math.Max(1, available / columns - gap), DeviceDpi * 88 / 96);
            card.Margin = new Padding(0, 0, gap, gap);
        }
    }

    private void MoveCardFocus(int id, TargetCard card, KeyEventArgs args)
    {
        if (args.KeyCode is not (Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End)) return;
        var index = Array.FindIndex(_filtered, item => item.SourceId == id);
        var columns = Math.Max(1, _cards.ClientSize.Width / Math.Max(1, card.Width + card.Margin.Horizontal));
        var step = args.KeyCode is Keys.Up or Keys.Down ? columns : 1;
        if (args.KeyCode is Keys.Left or Keys.Up) step = -step;
        var next = args.KeyCode == Keys.Home ? 0 : args.KeyCode == Keys.End ? _filtered.Length - 1 : Math.Clamp(index + step, 0, _filtered.Length - 1);
        if (_page != next / PageSize) { _page = next / PageSize; ShowCards(); }
        var nextCard = _cards.Controls.OfType<TargetCard>().First(candidate => Equals(candidate.Tag, _filtered[next].SourceId));
        nextCard.Focus();
        _cards.ScrollControlIntoView(nextCard);
        args.Handled = args.SuppressKeyPress = true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F)) { _search.Focus(); _search.SelectAll(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Enter)
        {
            if (_search.ContainsFocus) return true;
            if (_cards.Controls.OfType<TargetCard>().FirstOrDefault(card => card.Focused) is { } card) { card.PerformClick(); return true; }
        }
        return base.ProcessDialogKey(keyData);
    }

    private static MedievalComboBox Filter(string name) => new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = name, DropDownWidth = 250 };
    private static Button Action(string text, System.Action action, string name, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 30, AccessibleName = name, UseMnemonic = false, Margin = new Padding(6, 0, 0, 0) };
        MedievalTheme.StyleSecondaryButton(button);
        button.Click += (_, _) => action();
        return button;
    }
    private sealed record FilterChoice(int Id, string Name) { public override string ToString() => Name; }

    private sealed class TargetCard(string title, string detail, Image? icon) : Button
    {
        private bool _selected;
        private bool _hovered;
        internal void SetSelected(bool selected) { _selected = selected; AccessibleDescription = detail + (selected ? ", selected" : ", not selected"); Invalidate(); }
        protected override AccessibleObject CreateAccessibilityInstance() => new TargetCardAccessibility(this);
        protected override void OnCreateControl()
        {
            base.OnCreateControl();
            Text = title;
            UseMnemonic = false;
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
        protected override void OnMouseEnter(EventArgs args) { base.OnMouseEnter(args); _hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs args) { base.OnMouseLeave(args); _hovered = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs args) { base.OnGotFocus(args); Invalidate(); }
        protected override void OnLostFocus(EventArgs args) { base.OnLostFocus(args); Invalidate(); }
        protected override void OnPaint(PaintEventArgs args)
        {
            var scale = DeviceDpi / 96f;
            args.Graphics.Clear(_selected || _hovered ? MedievalTheme.SurfaceRaised : MedievalTheme.Input);
            using var border = new Pen(_selected ? MedievalTheme.GoldBright : MedievalTheme.Border, _selected ? 2 : 1);
            args.Graphics.DrawRectangle(border, 1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            var inset = (int)(10 * scale);
            var iconSize = (int)(40 * scale);
            if (icon is not null) args.Graphics.DrawImage(icon, new Rectangle(inset, (Height - iconSize) / 2, iconSize, iconSize));
            var left = inset + iconSize + (int)(10 * scale);
            var textWidth = Math.Max(1, Width - left - inset - (int)(18 * scale));
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(args.Graphics, title, Font, new Rectangle(left, inset, textWidth, (int)(38 * scale)), MedievalTheme.Text, flags | TextFormatFlags.WordBreak);
            TextRenderer.DrawText(args.Graphics, detail, Font, new Rectangle(left, Height - inset - (int)(22 * scale), textWidth, (int)(22 * scale)), MedievalTheme.TextSecondary, flags | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(args.Graphics, _selected ? "✓" : "○", Font, new Rectangle(Width - inset - (int)(18 * scale), inset, (int)(18 * scale), (int)(24 * scale)), _selected ? MedievalTheme.GoldBright : MedievalTheme.TextSecondary, flags);
            if (Focused) ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), MedievalTheme.Gold, BackColor);
        }
        private sealed class TargetCardAccessibility(TargetCard owner) : ControlAccessibleObject(owner)
        {
            public override AccessibleRole Role => AccessibleRole.CheckButton;
            public override AccessibleStates State => base.State | (owner._selected ? AccessibleStates.Checked : AccessibleStates.None);
            public override string DefaultAction => "Toggle selection";
            public override void DoDefaultAction() => owner.PerformClick();
        }
    }
}

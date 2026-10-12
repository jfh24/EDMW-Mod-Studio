using System.Globalization;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Stages edits to verified progression records until the project explicitly accepts them.</summary>
internal sealed class AgeProgressionDialog : Form
{
    private readonly CivilizationGameData _gameData;
    private readonly CivilizationDraft _original;
    private readonly CivilizationDraft _candidate;
    private readonly IReadOnlyList<CivilizationAgeProgressionItem> _items;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _gameValues = [];
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, AccessibleName = "Age progression items", Margin = Padding.Empty };
    private readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Find progression item…", AccessibleName = "Find progression item" };
    private readonly MedievalComboBox _kind = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Progression item type" };
    private readonly Panel _detail = new MedievalBufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = MedievalTheme.Surface, Padding = new Padding(16, 10, 16, 12), Margin = Padding.Empty };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = MedievalTheme.TextSecondary, Margin = new Padding(0, 0, 8, 0) };
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly ErrorProvider _errors;
    private readonly List<ProgressionField> _fields = [];
    private CivilizationAgeProgressionItem? _selected;
    private bool _updating;

    internal CivilizationDraft? EditedCivilization { get; private set; }
    internal bool HasChanges => !SameCivilization(_original, _candidate);

    public AgeProgressionDialog(CivilizationGameData gameData, CivilizationDraft civilization)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(civilization);
        _gameData = gameData;
        _original = civilization.Clone();
        _candidate = civilization.Clone();
        _items = gameData.GetAgeProgressionItems(civilization);
        Text = $"Edit progression · {gameData.GetDisplayName(civilization)}";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = MedievalTheme.UiFont(9);
        ForeColor = MedievalTheme.Text;
        BackColor = MedievalTheme.Sidebar;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1120, 660);
        MinimumSize = new Size(940, 560);
        _errors = new ErrorProvider { ContainerControl = this, BlinkStyle = ErrorBlinkStyle.NeverBlink };

        var content = new MedievalBufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16, 10, 16, 12), Margin = Padding.Empty, BackColor = MedievalTheme.Sidebar };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var introduction = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, BackColor = MedievalTheme.Sidebar };
        introduction.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        introduction.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = Label("Age progression", 17, true);
        title.Font = MedievalTheme.DisplayFont(17);
        introduction.Controls.Add(title, 0, 0);
        var help = Label("Choose a unit, building, technology, or age advance. Apply saves these edits to your project; Export over LAN loads them into the game.", 9);
        help.Dock = DockStyle.Fill;
        help.AutoSize = false;
        help.ForeColor = MedievalTheme.TextSecondary;
        introduction.Controls.Add(help, 0, 1);
        content.Controls.Add(introduction, 0, 0);

        var body = new MedievalBufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = MedievalTheme.Border };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 396));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var roster = new MedievalBufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, 1, 0), Padding = new Padding(10), BackColor = MedievalTheme.Sidebar };
        roster.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        roster.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        roster.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        roster.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        MedievalTheme.StyleTextInput(_search);
        roster.Controls.Add(MedievalTheme.FrameInput(_search), 0, 0);
        _kind.Items.Add("All progression items");
        foreach (var kind in _items.Select(item => item.Kind).Distinct(StringComparer.Ordinal).OrderBy(kind => kind, StringComparer.Ordinal)) _kind.Items.Add(kind);
        _kind.SelectedIndex = 0;
        MedievalTheme.StyleTextInput(_kind);
        roster.Controls.Add(MedievalTheme.FrameInput(_kind), 0, 1);
        _list.Columns.Add("Item", 214);
        _list.Columns.Add("Type", 90);
        _list.Columns.Add("Ages", 60);
        MedievalDialogFrame.StyleListView(_list);
        roster.Controls.Add(_list, 0, 2);
        body.Controls.Add(roster, 0, 0);
        _detail.HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(_detail);
        body.Controls.Add(_detail, 1, 0);
        content.Controls.Add(body, 0, 1);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 10, 0, 0), BackColor = MedievalTheme.Sidebar };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        footer.Controls.Add(_status, 0, 0);
        var apply = Button("Apply to project", ApplyEdits, "Apply progression edits to project");
        apply.Dock = DockStyle.Fill;
        apply.ForeColor = MedievalTheme.GoldBright;
        footer.Controls.Add(apply, 1, 0);
        var cancel = Button("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }, "Cancel progression edits");
        cancel.Dock = DockStyle.Fill;
        footer.Controls.Add(cancel, 2, 0);
        AcceptButton = apply;
        CancelButton = cancel;
        content.Controls.Add(footer, 0, 2);
        MedievalDialogFrame.Apply(this, content);

        _search.TextChanged += (_, _) => { if (!_updating && CommitSelected()) BindItems(); };
        _kind.SelectedIndexChanged += (_, _) => { if (!_updating && CommitSelected()) BindItems(); };
        _list.SelectedIndexChanged += (_, _) => SelectionChanged();
        BindItems();
    }

    internal static IReadOnlyList<CivilizationFieldDefinition> EditableFields(CivilizationGameData gameData, CivilizationEntityDraft entity)
    {
        var values = gameData.GetValues(entity);
        var fields = CivilizationSchema.GetFields(entity.Category, values)
            .Where(field => field.Key.EndsWith(".initialAge", StringComparison.Ordinal) || field.Key.EndsWith(".finalAge", StringComparison.Ordinal) ||
                field.Key.EndsWith(".foodCost", StringComparison.Ordinal) || field.Key.EndsWith(".woodCost", StringComparison.Ordinal) ||
                field.Key.EndsWith(".goldCost", StringComparison.Ordinal) || field.Key.EndsWith(".stoneCost", StringComparison.Ordinal) ||
                field.Key.EndsWith(".productionCoefficient", StringComparison.Ordinal))
            .Concat(gameData.GetEffectFields(entity))
            .Where(field => values.ContainsKey(field.Key) && gameData.CanWrite(entity, field.Key))
            .DistinctBy(field => field.Key).ToArray();
        return fields;
    }

    private IReadOnlyDictionary<string, string> GameValues(CivilizationAgeProgressionItem item)
    {
        if (!_gameValues.TryGetValue(item.Entity.Id, out var values)) _gameValues[item.Entity.Id] = values = _gameData.GetValues(item.Entity);
        return values;
    }

    private static CivilizationEntityDraft? FindEntity(CivilizationDraft civilization, CivilizationEntityDraft source) =>
        civilization.Entities.FirstOrDefault(entity => entity.Id == source.Id) ?? civilization.Entities.FirstOrDefault(entity =>
            entity.Category == source.Category && (source.SourceObjectId is int objectId ? entity.SourceObjectId == objectId :
                source.SourceTechnologyId is int technologyId && entity.SourceObjectId is null && entity.SourceTechnologyId == technologyId));

    private string EffectiveValue(CivilizationAgeProgressionItem item, string key) =>
        FindEntity(_candidate, item.Entity)?.Properties.GetValueOrDefault(key) ?? GameValues(item).GetValueOrDefault(key, "");

    private string AgeRange(CivilizationAgeProgressionItem item)
    {
        var prefix = item.Entity.Category switch { CivilizationCategory.Units => "unit", CivilizationCategory.Buildings => "building", _ => "technology" };
        var first = EffectiveValue(item, prefix + ".initialAge");
        var last = EffectiveValue(item, prefix + ".finalAge");
        return first == last ? first : $"{first}–{last}";
    }

    private void BindItems()
    {
        var selectedId = _selected?.Entity.Id;
        _updating = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        var query = _search.Text.Trim();
        foreach (var item in _items.Where(item => (query.Length == 0 || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Entity.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (_kind.SelectedIndex <= 0 || item.Kind == _kind.SelectedItem?.ToString())).OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var edited = FindEntity(_candidate, item.Entity)?.Properties.Keys.Intersect(EditableFields(_gameData, item.Entity).Select(field => field.Key)).Any() == true;
            var row = new ListViewItem((edited ? "• " : "") + item.DisplayName) { Tag = item, ToolTipText = item.DisplayName };
            row.SubItems.Add(item.Kind);
            row.SubItems.Add(AgeRange(item));
            _list.Items.Add(row);
            if (item.Entity.Id == selectedId) row.Selected = true;
        }
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
        _selected = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as CivilizationAgeProgressionItem : null;
        _list.EndUpdate();
        _updating = false;
        RenderSelected();
        _status.Text = _items.Count == 0 ? "No verified progression items are available for this civilization." : $"{_list.Items.Count} items shown · A bullet marks project overrides.";
    }

    private void SelectionChanged()
    {
        if (_updating || _list.SelectedItems.Count == 0) return;
        var target = _list.SelectedItems[0].Tag as CivilizationAgeProgressionItem;
        if (target?.Entity.Id == _selected?.Entity.Id) return;
        if (!CommitSelected())
        {
            _updating = true;
            foreach (ListViewItem row in _list.Items) row.Selected = (row.Tag as CivilizationAgeProgressionItem)?.Entity.Id == _selected?.Entity.Id;
            _updating = false;
            return;
        }
        _selected = target;
        RenderSelected();
    }

    private void RenderSelected()
    {
        _updating = true;
        _errors.Clear();
        _fields.Clear();
        _detail.SuspendLayout();
        foreach (Control control in _detail.Controls.Cast<Control>().ToArray()) control.Dispose();
        _detail.Controls.Clear();
        _detail.AutoScrollPosition = Point.Empty;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty, BackColor = MedievalTheme.Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _detail.Controls.Add(layout);
        void Add(Control control) { control.Dock = DockStyle.Top; layout.Controls.Add(control); }
        if (_selected is not { } item)
            Add(Label("Choose a progression item", 15, true));
        else
        {
            var title = Label(item.DisplayName, 17, true);
            title.Font = MedievalTheme.DisplayFont(17);
            title.MaximumSize = new Size(Math.Max(300, _detail.ClientSize.Width - 44), 0);
            Add(title);
            Add(Label(item.Kind, 9));
            var scope = Label(item.Scope, 9);
            scope.ForeColor = MedievalTheme.TextSecondary;
            scope.MaximumSize = new Size(Math.Max(300, _detail.ClientSize.Width - 44), 0);
            scope.Margin = new Padding(0, 5, 0, 12);
            Add(scope);
            var fields = EditableFields(_gameData, item.Entity);
            if (fields.Any(field => field.Key.Contains(".effect.", StringComparison.Ordinal)))
            {
                var effectScope = Label("Effect changes apply to every technology that references the same effect. Additive values take priority over relative values, then replacement values.", 9);
                effectScope.ForeColor = MedievalTheme.TextSecondary;
                effectScope.MaximumSize = new Size(Math.Max(300, _detail.ClientSize.Width - 44), 0);
                Add(effectScope);
            }
            foreach (var group in fields.GroupBy(field => field.Key.EndsWith(".initialAge", StringComparison.Ordinal) || field.Key.EndsWith(".finalAge", StringComparison.Ordinal) ? "Availability by age" : field.Section))
            {
                var sectionLabel = Label(group.Key.ToUpperInvariant(), 9, true);
                sectionLabel.Margin = new Padding(0, 12, 0, 5);
                Add(sectionLabel);
                if (group.First().Key.Contains(".effect.", StringComparison.Ordinal))
                {
                    var keyParts = group.First().Key.Split('.');
                    var description = GameValues(item).GetValueOrDefault(keyParts[0] + ".effectOperations", "")
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(line => line.StartsWith($"Effect #{keyParts[2]}:", StringComparison.Ordinal));
                    if (description is not null)
                    {
                        var effectDescription = Label(description, 9);
                        effectDescription.ForeColor = MedievalTheme.TextSecondary;
                        effectDescription.MaximumSize = new Size(Math.Max(300, _detail.ClientSize.Width - 44), 0);
                        Add(effectDescription);
                    }
                }
                var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(0, 0, 0, 3), Margin = Padding.Empty, BackColor = MedievalTheme.Surface };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
                var row = 0;
                foreach (var definition in group)
                {
                    var value = EffectiveValue(item, definition.Key);
                    Control input;
                    var age = definition.Key.EndsWith(".initialAge", StringComparison.Ordinal) || definition.Key.EndsWith(".finalAge", StringComparison.Ordinal);
                    if (!definition.Key.Contains(".effect.", StringComparison.Ordinal) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                        input = new MedievalNumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = age ? 100 : uint.MaxValue, Value = number, AccessibleName = definition.Label };
                    else
                    {
                        input = new TextBox { Dock = DockStyle.Fill, Text = value, AccessibleName = definition.Label, AccessibleDescription = definition.Help };
                    }
                    MedievalTheme.StyleTextInput(input);
                    var field = new ProgressionField(definition, input, ReadValue(input));
                    _fields.Add(field);
                    var label = Label(age ? definition.Key.EndsWith(".initialAge", StringComparison.Ordinal) ? "Available from age" : "Available through age" : definition.Label, 9);
                    label.Dock = DockStyle.Fill;
                    label.AutoSize = false;
                    label.TextAlign = ContentAlignment.MiddleLeft;
                    label.Margin = new Padding(0, 0, 8, 0);
                    grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
                    grid.Controls.Add(label, 0, row);
                    var frame = MedievalTheme.FrameInput(input);
                    frame.Margin = new Padding(0, 3, 0, 3);
                    grid.Controls.Add(frame, 1, row);
                    var reset = Button("↶", () => ResetField(field), $"Reset {definition.Label} to game value");
                    reset.Dock = DockStyle.Fill;
                    reset.Margin = new Padding(5, 3, 0, 3);
                    grid.Controls.Add(reset, 2, row++);
                    _tips.SetToolTip(input, definition.Help ?? "Editing this field creates a project override for the linked game record.");
                    _tips.SetToolTip(reset, "Inherit this field's current game value.");
                }
                Add(grid);
            }
            if (fields.Count == 0) Add(Label("No verified fields are available for this item.", 9));
            var resetItem = Button("Reset item to game values", ResetSelected, "Reset this progression item's editable fields");
            resetItem.Width = 218;
            resetItem.Margin = new Padding(0, 15, 0, 5);
            var actions = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, BackColor = MedievalTheme.Surface };
            actions.Controls.Add(resetItem);
            Add(actions);
        }
        _detail.ResumeLayout(true);
        _updating = false;
    }

    private void ResetField(ProgressionField field)
    {
        if (_selected is not { } item) return;
        SetValue(field.Input, GameValues(item).GetValueOrDefault(field.Definition.Key, ""));
        field.ResetRequested = true;
        _errors.SetError(field.Input, "");
    }

    private void ResetSelected()
    {
        foreach (var field in _fields) ResetField(field);
        _status.Text = "This item's progression fields will inherit game values when you apply.";
    }

    private bool CommitSelected()
    {
        if (_selected is not { } item) return true;
        _errors.Clear();
        foreach (var field in _fields)
        {
            var value = ReadValue(field.Input);
            var error = CivilizationSchema.ValidateValue(field.Definition, value);
            if (!field.Definition.Key.Contains(".effect.", StringComparison.Ordinal) &&
                !field.Definition.Key.EndsWith(".initialAge", StringComparison.Ordinal) && !field.Definition.Key.EndsWith(".finalAge", StringComparison.Ordinal) &&
                !uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                error = $"{field.Definition.Label} requires a whole number from 0 to 4294967295.";
            if (field.Definition.Key.Contains(".effect.", StringComparison.Ordinal) &&
                (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var effect) || !float.IsFinite(effect)))
                error = $"{field.Definition.Label} must be a finite game value.";
            if (error is null) continue;
            _errors.SetError(field.Input, error);
            _status.Text = error;
            field.Input.Focus();
            return false;
        }
        var first = _fields.FirstOrDefault(field => field.Definition.Key.EndsWith(".initialAge", StringComparison.Ordinal));
        var last = _fields.FirstOrDefault(field => field.Definition.Key.EndsWith(".finalAge", StringComparison.Ordinal));
        if (first is not null && last is not null && decimal.Parse(ReadValue(first.Input), CultureInfo.InvariantCulture) > decimal.Parse(ReadValue(last.Input), CultureInfo.InvariantCulture))
        {
            const string error = "Available through age must be at least available from age.";
            _errors.SetError(last.Input, error);
            _status.Text = error;
            last.Input.Focus();
            return false;
        }
        var existing = FindEntity(_candidate, item.Entity);
        var properties = existing?.Properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = false;
        foreach (var field in _fields)
        {
            var value = ReadValue(field.Input);
            if (!field.ResetRequested && SameNumber(value, field.InitialValue)) continue;
            changed = true;
            if (field.ResetRequested || SameNumber(value, GameValues(item).GetValueOrDefault(field.Definition.Key))) properties.Remove(field.Definition.Key);
            else properties[field.Definition.Key] = value;
        }
        if (!changed) return true;
        if (existing is null && properties.Count > 0)
        {
            existing = item.Entity.Clone();
            _candidate.Entities.Add(existing);
        }
        if (existing is not null)
        {
            existing.Properties = properties;
            if (properties.Count == 0 && existing.Name == item.Entity.Name) _candidate.Entities.Remove(existing);
        }
        foreach (var field in _fields) { field.InitialValue = ReadValue(field.Input); field.ResetRequested = false; }
        foreach (ListViewItem row in _list.Items)
        {
            if ((row.Tag as CivilizationAgeProgressionItem)?.Entity.Id != item.Entity.Id) continue;
            row.SubItems[2].Text = AgeRange(item);
            var edited = properties.Keys.Intersect(_fields.Select(field => field.Definition.Key)).Any();
            row.Text = (edited ? "• " : "") + item.DisplayName;
        }
        return true;
    }

    private void ApplyEdits()
    {
        if (!CommitSelected()) return;
        EditedCivilization = _candidate.Clone();
        DialogResult = DialogResult.OK;
        Close();
    }

    internal IReadOnlyList<CivilizationAgeProgressionItem> ItemsForVerification => _items;
    internal void SelectItemForVerification(string entityId)
    {
        if (!CommitSelected()) throw new InvalidDataException("Resolve invalid progression fields first.");
        _updating = true;
        _search.Text = "";
        _kind.SelectedIndex = 0;
        _selected = _items.Single(item => item.Entity.Id == entityId);
        _updating = false;
        BindItems();
    }
    internal void SetFieldForVerification(string key, string value) => SetValue(_fields.Single(field => field.Definition.Key == key).Input, value);
    internal string GetFieldForVerification(string key) => ReadValue(_fields.Single(field => field.Definition.Key == key).Input);
    internal IReadOnlyList<string> EditableKeysForVerification => _fields.Select(field => field.Definition.Key).ToArray();
    internal void ResetItemForVerification() => ResetSelected();
    internal bool ApplyForVerification()
    {
        if (!CommitSelected()) return false;
        EditedCivilization = _candidate.Clone();
        return true;
    }

    private static bool SameNumber(string? left, string? right) => left == right ||
        double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a == b;
    private static string ReadValue(Control input) => input is NumericUpDown numeric ? numeric.Value.ToString("0.############################", CultureInfo.InvariantCulture) : input.Text.Trim();
    private static void SetValue(Control input, string value)
    {
        if (input is NumericUpDown numeric) numeric.Value = decimal.Parse(value, CultureInfo.InvariantCulture);
        else input.Text = value;
    }
    private static bool SameProperties(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) && value == pair.Value);
    private static bool SameCivilization(CivilizationDraft first, CivilizationDraft second) =>
        first.Id == second.Id && first.Name == second.Name && first.SourceCivilizationId == second.SourceCivilizationId && first.SourceRowIndex == second.SourceRowIndex &&
        SameProperties(first.Properties, second.Properties) && first.Entities.Count == second.Entities.Count && first.Entities.All(entity => second.Entities.Any(other =>
            other.Id == entity.Id && other.Name == entity.Name && other.Category == entity.Category && other.SourceObjectId == entity.SourceObjectId && other.SourceTechnologyId == entity.SourceTechnologyId &&
            other.SourcePowerId == entity.SourcePowerId && SameProperties(entity.Properties, other.Properties)));
    private static Label Label(string text, float size, bool bold = false) => new() { Text = text, AutoSize = true, UseMnemonic = false, Font = MedievalTheme.UiFont(size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = bold ? MedievalTheme.GoldBright : MedievalTheme.TextMuted, Margin = new Padding(0, 2, 0, 3) };
    private static Button Button(string text, Action action, string accessibleName)
    {
        var button = new Button { Text = text, Height = 30, Margin = new Padding(0, 0, 8, 0), AccessibleName = accessibleName, UseMnemonic = false };
        MedievalTheme.StyleSecondaryButton(button);
        button.Click += (_, _) => action();
        return button;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tips.Dispose(); _errors.Dispose(); }
        base.Dispose(disposing);
    }
    private sealed class ProgressionField(CivilizationFieldDefinition definition, Control input, string initialValue)
    {
        public CivilizationFieldDefinition Definition { get; } = definition;
        public Control Input { get; } = input;
        public string InitialValue { get; set; } = initialValue;
        public bool ResetRequested { get; set; }
    }
}

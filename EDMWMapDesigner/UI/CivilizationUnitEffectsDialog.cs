using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Stages typed, undoable effect attachments for the experimental runtime.</summary>
internal sealed class CivilizationUnitEffectsDialog : Form
{
    private readonly CivilizationEntityDraft _source;
    private readonly List<CivilizationUnitEffect> _effects;
    private readonly CivilizationGameIcons? _gameIcons;
    private readonly int _defaultButtonId;
    private readonly IReadOnlyList<int> _availableButtonSlots;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, AccessibleName = "Attached custom effects" };
    private readonly TableLayoutPanel _fields = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(14), BackColor = MedievalTheme.Surface };
    private readonly TextBox _name = new() { MaxLength = 100, AccessibleName = "Effect name" };
    private readonly CheckBox _enabled = new() { Text = "Enabled in this project", AutoSize = true, AccessibleName = "Effect enabled" };
    private readonly MedievalComboBox _trigger = Choice("Effect trigger");
    private readonly MedievalComboBox _target = Choice("Effect target");
    private readonly NumericUpDown _amount = Number("Sight adjustment in tiles", -32, 32, 3);
    private readonly NumericUpDown _radius = Number("Proximity radius in tiles", 1, 64, 5);
    private readonly MedievalComboBox _mode = Choice("Ability activation mode");
    private readonly NumericUpDown _duration = Number("Ability duration in seconds", 1, 3600, 30);
    private readonly TextBox _caption = new() { MaxLength = 40, AccessibleName = "In-game button caption" };
    private readonly MedievalComboBox _icon = Choice("In-game button appearance");
    private readonly NumericUpDown _slot = Number("In-game button slot", 1, 41, 1);
    private readonly PictureBox _targetPreview = new() { SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "Selected target game icon" };
    private readonly PictureBox _buttonPreview = new() { SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "Selected in-game button icon" };
    private readonly Label _behavior = new() { AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = MedievalTheme.TextSecondary };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = MedievalTheme.TextSecondary, AccessibleName = "Effect editor status" };
    private readonly ErrorProvider _errors;
    private readonly Dictionary<Control, Control> _labels = [];
    private readonly Button _remove;
    private bool _updating;
    private string? _selectedId;

    internal IReadOnlyList<CivilizationUnitEffect>? EditedEffects { get; private set; }

    public CivilizationUnitEffectsDialog(CivilizationEntityDraft source, IReadOnlyList<CivilizationCatalogItem> objects,
        IReadOnlyList<(int Id, string Name)> buttonChoices, CivilizationGameIcons? gameIcons = null,
        IReadOnlyList<int>? availableButtonSlots = null)
    {
        _source = source.Clone();
        _gameIcons = gameIcons;
        _availableButtonSlots = availableButtonSlots ?? Enumerable.Range(1, 41).ToArray();
        var sourceButton = source.SourceObjectId is int objectId ? gameIcons?.ObjectReference(objectId)?.ButtonId : null;
        _defaultButtonId = sourceButton is int buttonId && buttonChoices.Any(choice => choice.Id == buttonId)
            ? buttonId : buttonChoices.FirstOrDefault().Id;
        _effects = CivilizationUnitEffects.Read(source).ToList();
        Text = $"Custom effects · {source.Name}";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = MedievalTheme.UiFont(9);
        ForeColor = MedievalTheme.Text;
        BackColor = MedievalTheme.Sidebar;
        ClientSize = new Size(1060, 700);
        MinimumSize = new Size(940, 600);
        _errors = new ErrorProvider { ContainerControl = this, BlinkStyle = ErrorBlinkStyle.NeverBlink };

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(14), BackColor = MedievalTheme.Sidebar };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        content.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, Text = "Experimental effects for existing units and buildings. Proximity affects nearby same-player targets; activated abilities affect the activating unit.\nToggle and timed ability state resets when loading a save. Live gameplay, save/load and multiplayer are unverified. Launch the game through Mod Studio.",
            ForeColor = MedievalTheme.TextSecondary, AccessibleName = "Custom effect prototype status"
        }, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 350));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var roster = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, 10, 0) };
        roster.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        roster.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        roster.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        _list.Columns.Add("Effect", 174);
        _list.Columns.Add("Trigger", 84);
        _list.Columns.Add("State", 69);
        MedievalDialogFrame.StyleListView(_list);
        roster.Controls.Add(_list, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 5, 0, 0) };
        actions.Controls.Add(ActionButton("Add effect", AddEffect, "Add custom effect", 108));
        _remove = ActionButton("Remove", RemoveEffect, "Remove selected custom effect", 92);
        actions.Controls.Add(_remove);
        roster.Controls.Add(actions, 0, 1);
        if (source.SourceObjectId == 749 && objects.Any(item => item.SourceId == 777))
        {
            var preset = ActionButton("Add Ranger → Sherman example", AddRangerExample, "Add Ranger Sherman proximity example", 330);
            roster.Controls.Add(preset, 0, 2);
        }
        body.Controls.Add(roster, 0, 0);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = MedievalTheme.Surface, Margin = Padding.Empty };
        _fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        _fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField("Name", _name);
        AddField("State", _enabled);
        _trigger.Items.AddRange(["Proximity aura", "Activated ability"]);
        AddField("Trigger", _trigger);
        _target.Items.AddRange(objects.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => (object)new NamedChoice(item.SourceId, item.Name)).ToArray());
        AddField("Affected unit / building", _target, _targetPreview);
        AddField("Stat", new Label { Text = "Line of sight", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        AddField("Sight adjustment (tiles)", _amount);
        AddField("Radius (tiles)", _radius);
        _mode.Items.AddRange(["Toggle on / off", "Timed duration"]);
        AddField("Activation", _mode);
        AddField("Duration (seconds)", _duration);
        AddField("Button caption", _caption);
        _icon.Items.AddRange(buttonChoices.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(choice => (object)new NamedChoice(choice.Id, choice.Name)).ToArray());
        AddField("Existing button appearance", _icon, _buttonPreview);
        AddField("Button slot", _slot);
        var behaviorRow = _fields.RowCount++;
        _fields.Controls.Add(_behavior, 0, behaviorRow);
        _fields.SetColumnSpan(_behavior, 2);
        scroll.Controls.Add(_fields);
        body.Controls.Add(scroll, 1, 0);
        content.Controls.Add(body, 0, 1);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 7, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        footer.Controls.Add(_status, 0, 0);
        var apply = ActionButton("Apply to project", Apply, "Apply custom effects to project", 144);
        var cancel = ActionButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }, "Cancel custom effect edits", 84);
        footer.Controls.Add(apply, 1, 0);
        footer.Controls.Add(cancel, 2, 0);
        AcceptButton = apply;
        CancelButton = cancel;
        content.Controls.Add(footer, 0, 2);
        MedievalDialogFrame.Apply(this, content);

        _list.SelectedIndexChanged += (_, _) => SelectionChanged();
        _trigger.SelectedIndexChanged += (_, _) => { if (!_updating) UpdateTriggerFields(); };
        _mode.SelectedIndexChanged += (_, _) => { if (!_updating) UpdateTriggerFields(); };
        _target.SelectedIndexChanged += (_, _) => _targetPreview.Image = _target.SelectedItem is NamedChoice target ? _gameIcons?.ForObject(target.Id) : null;
        _icon.SelectedIndexChanged += (_, _) => _buttonPreview.Image = _icon.SelectedItem is NamedChoice button ? _gameIcons?.ForButton(button.Id) : null;
        BindList(_effects.FirstOrDefault()?.Id);
    }

    private void AddField(string text, Control control, PictureBox? preview = null)
    {
        var row = _fields.RowCount++;
        _fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        var label = new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 4, 12, 4);
        if (control is TextBox or NumericUpDown or ComboBox) MedievalTheme.StyleTextInput(control);
        _fields.Controls.Add(label, 0, row);
        if (preview is null) _fields.Controls.Add(control, 1, row);
        else
        {
            var inputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            preview.Dock = DockStyle.Fill;
            preview.Margin = new Padding(0, 2, 6, 2);
            control.Margin = new Padding(0, 4, 4, 4);
            inputRow.Controls.Add(control, 0, 0);
            inputRow.Controls.Add(preview, 1, 0);
            _fields.Controls.Add(inputRow, 1, row);
        }
        _labels[control] = label;
    }

    private void BindList(string? selectedId)
    {
        _updating = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var effect in _effects)
        {
            var item = new ListViewItem(effect.Name) { Tag = effect.Id };
            item.SubItems.Add(effect.Trigger == CivilizationUnitEffectTrigger.Proximity ? "Proximity" : "Activated");
            item.SubItems.Add(effect.Enabled ? "Enabled" : "Disabled");
            _list.Items.Add(item);
            item.Selected = effect.Id == selectedId;
        }
        _list.EndUpdate();
        _selectedId = selectedId;
        _updating = false;
        LoadSelected();
    }

    private void SelectionChanged()
    {
        if (_updating || _list.SelectedItems.Count == 0) return;
        var nextId = (string)_list.SelectedItems[0].Tag!;
        if (nextId == _selectedId) return;
        if (!CommitSelected())
        {
            _updating = true;
            foreach (ListViewItem item in _list.Items) item.Selected = (string)item.Tag! == _selectedId;
            _updating = false;
            return;
        }
        _selectedId = nextId;
        LoadSelected();
    }

    private void LoadSelected()
    {
        _errors.Clear();
        var effect = _effects.FirstOrDefault(item => item.Id == _selectedId);
        _fields.Visible = effect is not null;
        _remove.Enabled = effect is not null;
        _status.Text = effect is null ? "No custom effects attached. Choose Add effect." : "Apply saves all attachments; Cancel discards this dialog's changes.";
        if (effect is null) return;
        _updating = true;
        _name.Text = effect.Name;
        _enabled.Checked = effect.Enabled;
        _trigger.SelectedIndex = effect.Trigger == CivilizationUnitEffectTrigger.Proximity ? 0 : 1;
        SelectChoice(_target, effect.TargetObjectId);
        _amount.Value = effect.Amount;
        _radius.Value = effect.RadiusTiles;
        _mode.SelectedIndex = effect.ActivationMode == CivilizationUnitEffectActivationMode.Toggle ? 0 : 1;
        _duration.Value = effect.DurationSeconds;
        _caption.Text = effect.ButtonCaption;
        SelectChoice(_icon, effect.ButtonIconId);
        _slot.Value = effect.ButtonSlot;
        _updating = false;
        UpdateTriggerFields();
    }

    private void UpdateTriggerFields()
    {
        var proximity = _trigger.SelectedIndex == 0;
        SetFieldEnabled(_target, proximity);
        SetFieldEnabled(_radius, proximity);
        foreach (var control in new Control[] { _mode, _caption, _icon, _slot }) SetFieldEnabled(control, !proximity);
        SetFieldEnabled(_duration, !proximity && _mode.SelectedIndex == 1);
        if (!proximity && _source.SourceObjectId is int sourceId) SelectChoice(_target, sourceId);
        _behavior.Text = proximity
            ? "Same-player targets only. Multiple nearby sources of this effect do not stack. The adjustment ends when the target leaves the radius or the source dies."
            : "Activate with the in-game command button. " +
                (_mode.SelectedIndex == 0 ? "Pressing it again turns the effect off." : "The effect ends after its duration; activating it again refreshes the duration.") +
                " Session-only prototype: activation state resets when loading a save. " +
                "Ability buttons own the complete command tables; combine attachments in one package or uninstall conflicting mods.";
    }

    private void SetFieldEnabled(Control control, bool enabled)
    {
        control.Enabled = enabled;
        _labels[control].ForeColor = enabled ? MedievalTheme.Text : MedievalTheme.TextMuted;
    }

    private bool CommitSelected()
    {
        if (_selectedId is null) return true;
        var index = _effects.FindIndex(item => item.Id == _selectedId);
        if (index < 0) return true;
        _errors.Clear();
        try
        {
            if (_target.SelectedItem is not NamedChoice target) throw new InvalidDataException("Select an affected unit or building from the catalog.");
            if (_trigger.SelectedIndex == 1 && _icon.SelectedItem is not NamedChoice)
                throw new InvalidDataException("Select an existing in-game button appearance.");
            var effect = _effects[index] with
            {
                Name = _name.Text.Trim(), Enabled = _enabled.Checked,
                Trigger = _trigger.SelectedIndex == 0 ? CivilizationUnitEffectTrigger.Proximity : CivilizationUnitEffectTrigger.Activated,
                TargetObjectId = target.Id, Amount = (int)_amount.Value, RadiusTiles = (int)_radius.Value,
                ActivationMode = _mode.SelectedIndex == 0 ? CivilizationUnitEffectActivationMode.Toggle : CivilizationUnitEffectActivationMode.Timed,
                DurationSeconds = (int)_duration.Value, ButtonCaption = _caption.Text.Trim(),
                ButtonIconId = (_icon.SelectedItem as NamedChoice)?.Id ?? _effects[index].ButtonIconId,
                ButtonSlot = (int)_slot.Value
            };
            var candidate = _effects.ToArray();
            candidate[index] = effect;
            CivilizationUnitEffects.Validate(_source, candidate);
            _effects[index] = effect;
            foreach (ListViewItem item in _list.Items)
                if ((string)item.Tag! == effect.Id)
                {
                    item.Text = effect.Name;
                    item.SubItems[1].Text = effect.Trigger == CivilizationUnitEffectTrigger.Proximity ? "Proximity" : "Activated";
                    item.SubItems[2].Text = effect.Enabled ? "Enabled" : "Disabled";
                }
            return true;
        }
        catch (InvalidDataException error)
        {
            _status.Text = error.Message;
            _errors.SetError(_name, error.Message);
            return false;
        }
    }

    private void AddEffect()
    {
        if (!CommitSelected()) return;
        if (_effects.Count >= CivilizationUnitEffects.MaximumAttachments) { _status.Text = "This entry already has the maximum number of effects."; return; }
        var effect = new CivilizationUnitEffect
        {
            Name = $"Custom effect {_effects.Count + 1}", TargetObjectId = _source.SourceObjectId!.Value,
            ButtonIconId = _defaultButtonId,
            ButtonSlot = _availableButtonSlots.FirstOrDefault(slot => !_effects.Any(item => item.Enabled && item.Trigger == CivilizationUnitEffectTrigger.Activated && item.ButtonSlot == slot), 1)
        };
        _effects.Add(effect);
        BindList(effect.Id);
        _name.Focus();
        _name.SelectAll();
    }

    private void AddRangerExample()
    {
        if (!CommitSelected()) return;
        if (_effects.Count >= CivilizationUnitEffects.MaximumAttachments) { _status.Text = "This entry already has the maximum number of effects."; return; }
        var effect = new CivilizationUnitEffect
        {
            Name = "Ranger spotting for Sherman", TargetObjectId = 777, RadiusTiles = 5, Amount = 3,
            ButtonIconId = _defaultButtonId
        };
        _effects.Add(effect);
        BindList(effect.Id);
    }

    private void RemoveEffect()
    {
        if (_selectedId is null) return;
        var index = _effects.FindIndex(item => item.Id == _selectedId);
        if (index < 0) return;
        _effects.RemoveAt(index);
        BindList(_effects.Count > 0 ? _effects[Math.Min(index, _effects.Count - 1)].Id : null);
    }

    private void Apply()
    {
        if (!CommitSelected()) return;
        CivilizationUnitEffects.Validate(_source, _effects);
        EditedEffects = _effects.ToArray();
        DialogResult = DialogResult.OK;
        Close();
    }

    internal void AddForVerification() => AddEffect();
    internal void RemoveForVerification() => RemoveEffect();
    internal void AddRangerExampleForVerification() => AddRangerExample();
    internal IReadOnlyList<CivilizationUnitEffect> SnapshotForVerification => _effects.ToArray();
    internal bool ApplyForVerification() { Apply(); return EditedEffects is not null; }

    private static MedievalComboBox Choice(string name) => new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = name, DropDownWidth = 420 };
    private static NumericUpDown Number(string name, int minimum, int maximum, int value) => new() { AccessibleName = name, Minimum = minimum, Maximum = maximum, Value = value, DecimalPlaces = 0 };
    private static Button ActionButton(string text, Action action, string name, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 30, AccessibleName = name, Margin = new Padding(0, 0, 6, 0), UseMnemonic = false };
        MedievalTheme.StyleSecondaryButton(button);
        button.Click += (_, _) => action();
        return button;
    }

    private static void SelectChoice(ComboBox combo, int id)
    {
        combo.SelectedIndex = -1;
        for (var index = 0; index < combo.Items.Count; index++)
            if (combo.Items[index] is NamedChoice choice && choice.Id == id) { combo.SelectedIndex = index; return; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _errors.Dispose();
        base.Dispose(disposing);
    }

    private sealed record NamedChoice(int Id, string Name) { public override string ToString() => Name; }
}

using System.Globalization;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Stages typed, undoable effect attachments for the experimental runtime.</summary>
internal sealed class CivilizationUnitEffectsDialog : Form
{
    private readonly CivilizationEntityDraft _source;
    private readonly List<CivilizationUnitEffect> _effects;
    private readonly CivilizationGameIcons? _gameIcons;
    private readonly CivilizationGameData? _gameData;
    private readonly IReadOnlyDictionary<int, CivilizationCatalogItem> _objects;
    private readonly Func<int, CivilizationEntityDraft?>? _projectTarget;
    private readonly ToolTip _overviewTip = new();
    private readonly int _defaultButtonId;
    private readonly IReadOnlyList<int> _availableButtonSlots;
    private readonly Panel _list = new() { Dock = DockStyle.Fill, AutoScroll = true, AccessibleName = "Attached custom effects" };
    private readonly TableLayoutPanel _cards = Stack();
    private readonly TableLayoutPanel _fields = Stack();
    private readonly Label _count = Hint("");
    private readonly TextBox _name = new() { MaxLength = 100, AccessibleName = "Effect name" };
    private readonly CheckBox _enabled = new() { Text = "Enabled", AutoSize = true, AccessibleName = "Effect enabled" };
    private readonly MedievalComboBox _trigger = Choice("Effect trigger");
    private readonly Button _target = new() { Text = "Choose target…", AccessibleName = "Effect target", TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
    private readonly List<int> _targetIds = [];
    private readonly Dictionary<string, int[]> _proximityTargets = [];
    private readonly TableLayoutPanel _outputsHost = Stack();
    private readonly List<OutputFields> _outputRows = [];
    private readonly Button _addOutput;
    private MedievalComboBox FirstStat => _outputRows[0].Stat;
    private NumericUpDown FirstAmount => _outputRows[0].Amount;
    private readonly NumericUpDown _radius = Number("Proximity radius in tiles", 1, 64, 5);
    private readonly MedievalComboBox _mode = Choice("Ability activation mode");
    private readonly NumericUpDown _duration = Number("Ability duration in seconds", 1, 3600, 30);
    private readonly TextBox _caption = new() { MaxLength = 40, AccessibleName = "In-game button caption" };
    private readonly MedievalComboBox _icon = Choice("In-game button appearance");
    private readonly MedievalComboBox _slot = Choice("In-game button position");
    private readonly PictureBox _targetPreview = new() { SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "Selected target game icon" };
    private readonly PictureBox _buttonPreview = new() { SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "Selected in-game button icon" };
    private readonly Label _statHelp = Hint("");
    private readonly Label _behavior = Hint("");
    private readonly Label _summary = Hint("");
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = MedievalTheme.TextSecondary, AccessibleName = "Effect editor status" };
    private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Create your first effect\n\nChoose Add effect, then select what triggers it and which stat changes.", ForeColor = MedievalTheme.TextSecondary };
    private readonly ErrorProvider _errors;
    private readonly Dictionary<Control, Label> _labels = [];
    private readonly Dictionary<Control, Control> _fieldHosts = [];
    private readonly Dictionary<string, EffectCard> _effectCards = [];
    private readonly TableLayoutPanel _buttonCard;
    private readonly Label _self = Hint("Affects each selected unit or building of this type.");
    private readonly Button _remove;
    private bool _updating;
    private string? _selectedId;

    internal IReadOnlyList<CivilizationUnitEffect>? EditedEffects { get; private set; }

    public CivilizationUnitEffectsDialog(CivilizationEntityDraft source, IReadOnlyList<CivilizationCatalogItem> objects,
        IReadOnlyList<(int Id, string Name)> buttonChoices, CivilizationGameIcons? gameIcons = null,
        IReadOnlyList<int>? availableButtonSlots = null, CivilizationGameData? gameData = null,
        Func<int, CivilizationEntityDraft?>? projectTarget = null)
    {
        _source = source.Clone();
        _gameIcons = gameIcons;
        _gameData = gameData;
        _objects = objects.ToDictionary(item => item.SourceId);
        _projectTarget = projectTarget;
        _availableButtonSlots = (availableButtonSlots ?? CivilizationUnitEffectBindings.CustomAbilityButtonSlots)
            .Where(CivilizationUnitEffectBindings.CustomAbilityButtonSlots.Contains).ToArray();
        var sourceButton = source.SourceObjectId is int objectId ? gameIcons?.ObjectReference(objectId)?.ButtonId : null;
        _defaultButtonId = sourceButton is int buttonId && buttonChoices.Any(choice => choice.Id == buttonId)
            ? buttonId : buttonChoices.FirstOrDefault().Id;
        _effects = CivilizationUnitEffects.Read(source).ToList();
        var occupiedCustomSlots = new HashSet<int>();
        for (var index = 0; index < _effects.Count; index++)
        {
            var effect = _effects[index];
            if (!effect.Enabled || effect.Trigger != CivilizationUnitEffectTrigger.Activated) continue;
            if (_availableButtonSlots.Contains(effect.ButtonSlot) && occupiedCustomSlots.Add(effect.ButtonSlot)) continue;
            var freeSlot = _availableButtonSlots.FirstOrDefault(slot => !occupiedCustomSlots.Contains(slot));
            if (freeSlot == 0) continue; // Apply reports exhausted space without choosing an occupied cell.
            occupiedCustomSlots.Add(freeSlot);
            _effects[index] = effect with { ButtonSlot = freeSlot };
        }
        _summary.Font = MedievalTheme.UiFont(10, FontStyle.Bold);
        _summary.ForeColor = MedievalTheme.Text;
        _summary.AccessibleName = "Effect preview";
        Text = $"Custom effects · {source.Name}";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = MedievalTheme.UiFont(9);
        ForeColor = MedievalTheme.Text;
        BackColor = MedievalTheme.Sidebar;
        ClientSize = new Size(1060, 720);
        MinimumSize = new Size(940, 640);
        _errors = new ErrorProvider { ContainerControl = this, BlinkStyle = ErrorBlinkStyle.NeverBlink };

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18, 12, 18, 12) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var header = Stack();
        AddRow(header, new Label { Text = "Build an effect", AutoSize = true, Font = MedievalTheme.UiFont(15, FontStyle.Bold), Margin = Padding.Empty });
        AddRow(header, Hint("Choose when it happens, who it affects, and what changes."));
        content.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var roster = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0, 0, 16, 0) };
        roster.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        roster.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        roster.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        roster.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        roster.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _count.Dock = DockStyle.Fill;
        roster.Controls.Add(_count, 0, 0);
        var add = ActionButton("+  Add effect", AddEffect, "Add custom effect", 220);
        add.Dock = DockStyle.Fill;
        add.Margin = new Padding(0, 0, 0, 10);
        roster.Controls.Add(add, 0, 1);
        _list.Controls.Add(_cards);
        _list.Margin = Padding.Empty;
        _list.Layout += (_, _) =>
        {
            var availableWidth = Math.Max(1, _list.ClientSize.Width);
            if (_cards.MaximumSize.Width != availableWidth) _cards.MaximumSize = new Size(availableWidth, 0);
        };
        roster.Controls.Add(_list, 0, 2);
        var actions = Stack();
        _remove = ActionButton("Remove selected", RemoveEffect, "Remove selected custom effect", 220);
        _remove.Dock = DockStyle.Top;
        _remove.Margin = new Padding(0, 10, 0, 0);
        AddRow(actions, _remove);
        if (source.SourceObjectId == 749 && objects.Any(item => item.SourceId == 777))
        {
            var preset = ActionButton("Ranger → Sherman example", AddRangerExample, "Add Ranger Sherman proximity example", 220);
            preset.Dock = DockStyle.Top;
            preset.Margin = new Padding(0, 8, 0, 0);
            AddRow(actions, preset);
        }
        roster.Controls.Add(actions, 0, 3);
        body.Controls.Add(roster, 0, 0);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        _fields.Padding = new Padding(0, 0, 5, 0);
        var identity = Card("Effect identity card", "EFFECT");
        var identityRow = Columns(78, 22);
        AddField(identityRow, 0, "Name", _name);
        _enabled.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _enabled.Margin = new Padding(12, 0, 0, 8);
        identityRow.Controls.Add(_enabled, 1, 0);
        AddRow(identity, identityRow);
        AddRow(_fields, identity);

        var grid = Columns(50, 50);
        var triggerCard = Card("Effect trigger card", "01  WHEN IT HAPPENS");
        triggerCard.Margin = new Padding(0, 0, 6, 12);
        _trigger.Items.AddRange(["Proximity aura", "Activated ability"]);
        AddField(triggerCard, "Trigger", _trigger);
        MedievalTheme.StyleSecondaryButton(_target);
        _target.Click += (_, _) => OpenTargetPicker();
        AddField(triggerCard, "Affected targets", _target, _targetPreview);
        _targetPreview.Cursor = Cursors.Hand;
        _targetPreview.TabStop = true;
        _targetPreview.AccessibleRole = AccessibleRole.PushButton;
        _targetPreview.AccessibleDescription = "Open the selected unit or building's stat overview.";
        _overviewTip.SetToolTip(_targetPreview, "View unit / building stats");
        _targetPreview.Click += (_, _) => OpenTargetOverview();
        _targetPreview.KeyDown += (_, args) =>
        {
            if (args.KeyCode is Keys.Enter or Keys.Space) { OpenTargetOverview(); args.SuppressKeyPress = true; }
        };
        AddField(triggerCard, "Radius (tiles)", _radius);
        _mode.Items.AddRange(["Toggle on / off", "Timed duration"]);
        AddField(triggerCard, "Activation", _mode);
        AddField(triggerCard, "Duration (seconds)", _duration);
        AddRow(triggerCard, _self);
        grid.Controls.Add(triggerCard, 0, 0);

        var outputCard = Card("Effect output card", "02  WHAT CHANGES");
        outputCard.Margin = new Padding(6, 0, 0, 12);
        _outputsHost.AccessibleName = "Effect outputs";
        AddRow(outputCard, _outputsHost);
        _addOutput = ActionButton("+  Add output", AddOutput, "Add effect output", 140);
        _addOutput.Dock = DockStyle.Top;
        AddRow(outputCard, _addOutput);
        AddRow(outputCard, _statHelp);
        grid.Controls.Add(outputCard, 1, 0);
        AddRow(_fields, grid);

        _buttonCard = Card("Ability button card", "03  IN-GAME BUTTON");
        var buttonGrid = Columns(66, 34);
        buttonGrid.Margin = new Padding(0, 0, 0, 10);
        AddField(buttonGrid, 0, "Button caption", _caption);
        AddField(buttonGrid, 1, "Free position", _slot);
        AddRow(_buttonCard, buttonGrid);
        _icon.Items.AddRange(buttonChoices.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(choice => (object)new NamedChoice(choice.Id, choice.Name)).ToArray());
        AddField(_buttonCard, "Existing button appearance", _icon, _buttonPreview);
        AddRow(_fields, _buttonCard);

        var previewCard = Card("Effect preview card", "PREVIEW");
        AddRow(previewCard, _summary);
        AddRow(previewCard, _behavior);
        AddRow(_fields, previewCard);
        AddRow(_fields, Hint("Experimental runtime · Launch through Mod Studio. Game saves with active effects are unsupported; gameplay and multiplayer unverified."));
        scroll.Controls.Add(_empty);
        scroll.Controls.Add(_fields);
        body.Controls.Add(scroll, 1, 0);
        content.Controls.Add(body, 0, 1);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 10, 0, 0), Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        footer.Controls.Add(_status, 0, 0);
        var apply = ActionButton("Apply to project", Apply, "Apply custom effects to project", 144);
        var cancel = ActionButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }, "Cancel custom effect edits", 84);
        apply.BackColor = MedievalTheme.SurfaceRaised;
        apply.ForeColor = MedievalTheme.GoldBright;
        footer.Controls.Add(apply, 1, 0);
        footer.Controls.Add(cancel, 2, 0);
        AcceptButton = apply;
        CancelButton = cancel;
        content.Controls.Add(footer, 0, 2);
        MedievalDialogFrame.Apply(this, content);

        _trigger.SelectedIndexChanged += (_, _) =>
        {
            if (_updating) return;
            if (_selectedId is string effectId)
            {
                if (_trigger.SelectedIndex == 1) _proximityTargets[effectId] = _targetIds.ToArray();
                else if (_proximityTargets.TryGetValue(effectId, out var targets)) SelectTargets(targets);
            }
            UpdateTriggerFields();
        };
        _mode.SelectedIndexChanged += (_, _) => { if (!_updating) UpdateTriggerFields(); };
        _icon.SelectedIndexChanged += (_, _) => _buttonPreview.Image = _icon.SelectedItem is NamedChoice button ? _gameIcons?.ForButton(button.Id) : null;
        _name.TextChanged += (_, _) => UpdatePreview();
        _enabled.CheckedChanged += (_, _) => UpdatePreview();
        foreach (var number in new[] { _radius, _duration }) number.ValueChanged += (_, _) => UpdatePreview();
        BindList(_effects.FirstOrDefault()?.Id);
    }

    private static TableLayoutPanel Stack()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static TableLayoutPanel Columns(float left, float right)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, left));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, right));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return table;
    }

    private static void AddRow(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(control, 0, row);
    }

    private static TableLayoutPanel Card(string name, string heading)
    {
        var card = Stack();
        card.AccessibleName = name;
        card.BackColor = MedievalTheme.Surface;
        card.Padding = new Padding(16, 12, 16, 12);
        card.Margin = new Padding(0, 0, 0, 12);
        card.Paint += (_, args) => { using var pen = new Pen(MedievalTheme.Border); args.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1); };
        AddRow(card, new Label { Text = heading, AutoSize = true, Font = MedievalTheme.UiFont(8, FontStyle.Bold), ForeColor = MedievalTheme.Gold, Margin = new Padding(0, 0, 0, 10) });
        return card;
    }

    private void AddField(TableLayoutPanel card, string text, Control control, PictureBox? preview = null)
    {
        var host = CreateField(text, control, preview);
        AddRow(card, host);
    }

    private void AddField(TableLayoutPanel grid, int column, string text, Control control)
    {
        var host = CreateField(text, control);
        host.Margin = column == 0 ? new Padding(0, 0, 6, 0) : new Padding(6, 0, 0, 0);
        grid.Controls.Add(host, column, 0);
    }

    private TableLayoutPanel CreateField(string text, Control control, PictureBox? preview = null)
    {
        var host = Stack();
        host.Margin = new Padding(0, 0, 0, 10);
        var label = new Label { Text = text, AutoSize = true, ForeColor = MedievalTheme.TextSecondary, Margin = new Padding(0, 0, 0, 5) };
        AddRow(host, label);
        control.Dock = DockStyle.Fill;
        control.Margin = Padding.Empty;
        if (control is TextBox or NumericUpDown or ComboBox) MedievalTheme.StyleTextInput(control);
        var input = control is TextBox ? MedievalTheme.FrameInput(control) : control;
        input.Dock = DockStyle.Fill;
        input.Margin = Padding.Empty;
        var inputRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 30, ColumnCount = preview is null ? 1 : 2, RowCount = 1, Margin = Padding.Empty };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        inputRow.Controls.Add(input, 0, 0);
        if (preview is not null)
        {
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            preview.Dock = DockStyle.Fill;
            preview.Margin = new Padding(6, 0, 0, 0);
            inputRow.Controls.Add(preview, 1, 0);
        }
        AddRow(host, inputRow);
        _labels[control] = label;
        _fieldHosts[control] = host;
        return host;
    }

    private static Label Hint(string text)
    {
        var label = new Label { Text = text, AutoSize = true, Dock = DockStyle.Top, ForeColor = MedievalTheme.TextSecondary, Margin = new Padding(0, 4, 0, 0) };
        // A constrained width makes the auto-sized row grow vertically, including at high DPI.
        label.ParentChanged += (_, _) =>
        {
            if (label.Parent is not { } parent) return;
            void ResizeHint(object? sender, EventArgs args) => label.MaximumSize = new Size(Math.Max(60, parent.ClientSize.Width - parent.Padding.Horizontal - label.Margin.Horizontal), 0);
            parent.SizeChanged += ResizeHint;
            ResizeHint(null, EventArgs.Empty);
        };
        return label;
    }

    private void BindList(string? selectedId)
    {
        _updating = true;
        _cards.SuspendLayout();
        foreach (var card in _effectCards.Values) card.Dispose();
        _effectCards.Clear();
        _cards.Controls.Clear();
        _cards.RowStyles.Clear();
        _cards.RowCount = 0;
        foreach (var effect in _effects)
        {
            var card = new EffectCard(effect, () => SelectEffect(effect.Id)) { Selected = effect.Id == selectedId };
            _effectCards.Add(effect.Id, card);
            AddRow(_cards, card);
        }
        _cards.ResumeLayout(true);
        _count.Text = $"EFFECTS  ·  {_effects.Count}";
        _selectedId = selectedId;
        _updating = false;
        LoadSelected();
        if (selectedId is not null && _effectCards.TryGetValue(selectedId, out var selected)) _list.ScrollControlIntoView(selected);
    }

    private void SelectEffect(string nextId)
    {
        if (_updating || nextId == _selectedId || !CommitSelected()) return;
        _selectedId = nextId;
        foreach (var item in _effectCards) item.Value.Selected = item.Key == nextId;
        LoadSelected();
    }

    private void LoadSelected()
    {
        _errors.Clear();
        var effect = _effects.FirstOrDefault(item => item.Id == _selectedId);
        _fields.Visible = effect is not null;
        _empty.Visible = effect is null;
        _remove.Enabled = effect is not null;
        _status.Text = effect is null ? "Add an effect to get started." : "Changes are saved when you apply.";
        if (effect is null) return;
        _updating = true;
        _name.Text = effect.Name;
        _enabled.Checked = effect.Enabled;
        _trigger.SelectedIndex = effect.Trigger == CivilizationUnitEffectTrigger.Proximity ? 0 : 1;
        SelectTargets(CivilizationUnitEffects.GetTargetObjectIds(effect));
        BindOutputs(CivilizationUnitEffects.GetOutputs(effect));
        _radius.Value = effect.RadiusTiles;
        _mode.SelectedIndex = effect.ActivationMode == CivilizationUnitEffectActivationMode.Toggle ? 0 : 1;
        _duration.Value = effect.DurationSeconds;
        _caption.Text = effect.ButtonCaption;
        SelectChoice(_icon, effect.ButtonIconId);
        _slot.Items.Clear();
        var claimed = _effects.Where(item => item.Id != effect.Id && item.Enabled &&
            item.Trigger == CivilizationUnitEffectTrigger.Activated).Select(item => item.ButtonSlot).ToHashSet();
        _slot.Items.AddRange(_availableButtonSlots.Where(slot => !claimed.Contains(slot))
            .Select(slot => (object)new NamedChoice(slot, $"Position {slot}")).ToArray());
        SelectChoice(_slot, effect.ButtonSlot);
        if (_slot.SelectedIndex < 0 && _slot.Items.Count > 0) _slot.SelectedIndex = 0;
        _updating = false;
        UpdateTriggerFields();
    }

    private void BindOutputs(IReadOnlyList<CivilizationUnitEffectOutput> outputs)
    {
        foreach (var row in _outputRows) DisposeOutput(row);
        _outputRows.Clear();
        _outputsHost.Controls.Clear();
        _outputsHost.RowStyles.Clear();
        _outputsHost.RowCount = 0;
        foreach (var output in outputs) CreateOutput(output);
        UpdateOutputActions();
    }

    private void CreateOutput(CivilizationUnitEffectOutput output)
    {
        var host = Stack();
        host.Margin = new Padding(0, 0, 0, 12);
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, Height = 28, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        var title = new Label { AutoSize = false, Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
        var remove = ActionButton("×", () => { }, "Remove effect output", 26);
        remove.Dock = DockStyle.Fill;
        remove.Margin = Padding.Empty;
        heading.Controls.Add(title, 0, 0);
        heading.Controls.Add(remove, 1, 0);
        AddRow(host, heading);
        var inputs = Columns(62, 38);
        var stat = Choice("Effect output stat");
        var amount = Number("Effect adjustment", -100000, 100000, 1);
        stat.Items.AddRange(CivilizationUnitEffects.StatChoices.Select(definition => (object)new StatChoice(definition.Stat, definition.Name)).ToArray());
        AddField(inputs, 0, "Stat", stat);
        AddField(inputs, 1, "Adjustment", amount);
        AddRow(host, inputs);
        var row = new OutputFields(host, stat, amount, title, remove);
        _outputRows.Add(row);
        AddRow(_outputsHost, host);
        stat.SelectedIndex = stat.Items.Cast<StatChoice>().ToList().FindIndex(choice => choice.Stat == output.Stat);
        UpdateStatFields(row, false);
        amount.Value = output.Amount;
        stat.SelectedIndexChanged += (_, _) => { if (!_updating) UpdateStatFields(row, true); };
        stat.Enter += (_, _) => ShowOutputHelp(row);
        amount.Enter += (_, _) => ShowOutputHelp(row);
        amount.ValueChanged += (_, _) => UpdatePreview();
        remove.Click += (_, _) => RemoveOutput(row);
    }

    private void DisposeOutput(OutputFields row)
    {
        foreach (var control in new Control[] { row.Stat, row.Amount })
        {
            _labels.Remove(control);
            _fieldHosts.Remove(control);
        }
        row.Host.Dispose();
    }

    private void UpdateOutputActions()
    {
        for (var index = 0; index < _outputRows.Count; index++)
        {
            var row = _outputRows[index];
            row.Title.Text = $"Output {index + 1}";
            row.Stat.AccessibleName = index == 0 ? "Effect output stat" : $"Effect output stat {index + 1}";
            row.Amount.AccessibleName = index == 0 ? "Effect adjustment" : $"Effect adjustment {index + 1}";
            row.Remove.AccessibleName = $"Remove effect output {index + 1}";
            row.Remove.Enabled = _outputRows.Count > 1;
        }
        _addOutput.Enabled = _outputRows.Count < CivilizationUnitEffects.MaximumOutputs;
    }

    private void AddOutput()
    {
        if (!CommitSelected()) return;
        var used = _outputRows.Select(row => ((StatChoice)row.Stat.SelectedItem!).Stat).ToHashSet();
        var next = CivilizationUnitEffects.StatChoices.FirstOrDefault(choice => !used.Contains(choice.Stat));
        if (next is null) return;
        _updating = true;
        CreateOutput(new(next.Stat, next.DefaultAmount));
        UpdateOutputActions();
        _updating = false;
        ShowOutputHelp(_outputRows[^1]);
        UpdatePreview();
        _outputRows[^1].Stat.Focus();
    }

    private void RemoveOutput(OutputFields row)
    {
        if (_outputRows.Count <= 1) return;
        var index = _outputRows.IndexOf(row);
        _outputRows.RemoveAt(index);
        DisposeOutput(row);
        // Rebuild row indices after removal; keep each remaining input and its draft value.
        _outputsHost.RowStyles.Clear();
        _outputsHost.RowCount = _outputRows.Count;
        for (var i = 0; i < _outputRows.Count; i++)
        {
            _outputsHost.SetRow(_outputRows[i].Host, i);
            _outputsHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        UpdateOutputActions();
        ShowOutputHelp(_outputRows[Math.Min(index, _outputRows.Count - 1)]);
        UpdatePreview();
    }

    private void ShowOutputHelp(OutputFields row)
    {
        if (row.Stat.SelectedItem is not StatChoice choice) return;
        var definition = CivilizationUnitEffects.GetStatDefinition(choice.Stat);
        _statHelp.Text = $"Units: {definition.Unit}. " + definition.Help + (definition.RuntimeSupported ? "" : " Draft only · runtime support pending; enabled exports are blocked.");
        _statHelp.ForeColor = definition.RuntimeSupported ? MedievalTheme.TextSecondary : MedievalTheme.Warning;
    }

    private void UpdateStatFields(OutputFields row, bool useDefault)
    {
        if (row.Stat.SelectedItem is not StatChoice choice) return;
        var definition = CivilizationUnitEffects.GetStatDefinition(choice.Stat);
        var amount = row.Amount;
        var updating = _updating;
        _updating = true;
        // Widen before setting the new range, so a previous large value is clamped safely.
        amount.Minimum = Math.Min(amount.Minimum, definition.MinimumAmount);
        amount.Maximum = Math.Max(amount.Maximum, definition.MaximumAmount);
        amount.Value = Math.Clamp(useDefault ? definition.DefaultAmount : amount.Value, definition.MinimumAmount, definition.MaximumAmount);
        amount.Minimum = definition.MinimumAmount;
        amount.Maximum = definition.MaximumAmount;
        amount.DecimalPlaces = definition.DecimalPlaces;
        amount.Increment = definition.DecimalPlaces == 0 ? 1 : 0.1m;
        var isLimit = choice.Stat is CivilizationUnitEffectStat.BuildLimit or CivilizationUnitEffectStat.QueueCapacity;
        _labels[amount].Text = isLimit ? "Limit" : "Change";
        amount.AccessibleDescription = $"{(isLimit ? "Limit" : "Adjustment")} in {definition.Unit}. {definition.Help}";
        ShowOutputHelp(row);
        _updating = updating;
        UpdatePreview();
    }

    private void UpdateTriggerFields()
    {
        _fields.SuspendLayout();
        var proximity = _trigger.SelectedIndex == 0;
        ShowField(_target, proximity);
        ShowField(_radius, proximity);
        ShowField(_mode, !proximity);
        ShowField(_duration, !proximity && _mode.SelectedIndex == 1);
        foreach (var control in new Control[] { _caption, _icon, _slot }) control.Enabled = !proximity;
        _buttonCard.Visible = !proximity;
        _self.Visible = !proximity;
        if (!proximity && _source.SourceObjectId is int sourceId) SelectTargets([sourceId]);
        _fields.ResumeLayout(true);
        UpdatePreview();
    }

    private void ShowField(Control control, bool visible)
    {
        control.Enabled = visible;
        _fieldHosts[control].Visible = visible;
    }

    internal CivilizationUnitOverviewDialog? CreateTargetOverviewForVerification()
    {
        if (_gameData is null || _targetIds.Count == 0 || !_objects.TryGetValue(_targetIds[0], out var item)) return null;
        var targetId = _targetIds[0];
        var entity = _projectTarget?.Invoke(targetId)?.Clone() ?? new CivilizationEntityDraft
        {
            Name = item.Name, Category = item.Category, SourceObjectId = item.SourceId
        };
        var values = _gameData.GetValues(entity).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var property in entity.Properties)
            if (!string.IsNullOrWhiteSpace(property.Value)) values[property.Key] = property.Value;
        return new CivilizationUnitOverviewDialog(entity, values, _gameIcons?.ForObject(targetId));
    }

    private void SelectTargets(IEnumerable<int> objectIds)
    {
        var targets = (_trigger.SelectedIndex == 1 && _source.SourceObjectId is int sourceId ? [sourceId] : objectIds)
            .Distinct().Order().ToArray();
        _targetIds.Clear();
        _targetIds.AddRange(targets);
        var first = targets.Length > 0 ? _objects.GetValueOrDefault(targets[0]) : null;
        _target.Text = targets.Length switch { 0 => "Choose targets…", 1 => TargetName(targets[0]) + "  ·  Choose…", _ => $"{targets.Length} targets selected  ·  Choose…" };
        _target.AccessibleDescription = targets.Length == 0 ? "Open the searchable unit and building catalog." :
            "Selected targets: " + string.Join(", ", targets.Select(id => $"{TargetName(id)}, object {id}")) + ". Open the searchable target catalog.";
        _targetPreview.Image = first is not null ? _gameIcons?.ForObject(first.SourceId) : null;
        _targetPreview.AccessibleDescription = first is null ? "Choose a target to view its stat overview." :
            $"View {first.Name} stats" + (targets.Length > 1 ? $", the first of {targets.Length} selected targets." : ".");
        _overviewTip.SetToolTip(_targetPreview, first is null ? "Choose targets to view stats" : $"View {first.Name} stats" + (targets.Length > 1 ? " (first selected target)" : ""));
        UpdatePreview();
    }

    private string TargetName(int id) => _objects.TryGetValue(id, out var item) ? item.Name : $"Object {id} (unavailable)";

    internal CivilizationEffectTargetPickerDialog CreateTargetPickerForVerification() =>
        new(_objects.Values.ToArray(), _targetIds, _gameIcons, _gameData);

    internal void SelectTargetForVerification(int objectId) => SelectTargets([objectId]);
    internal void SelectTargetsForVerification(IEnumerable<int> objectIds) => SelectTargets(objectIds);
    internal int? SelectedTargetIdForVerification => _targetIds.Count > 0 ? _targetIds[0] : null;
    internal IReadOnlyList<int> SelectedTargetIdsForVerification => _targetIds.ToArray();

    private void OpenTargetPicker()
    {
        if (_trigger.SelectedIndex != 0) return;
        try
        {
            using var picker = CreateTargetPickerForVerification();
            if (picker.ShowDialog(this) == DialogResult.OK) SelectTargets(picker.SelectedObjectIds);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _status.Text = error.Message;
        }
    }

    private void OpenTargetOverview()
    {
        try
        {
            using var overview = CreateTargetOverviewForVerification();
            if (overview is null) { _status.Text = "Load the game catalog to view this unit's stats."; return; }
            overview.ShowDialog(this);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _status.Text = error.Message;
        }
    }

    private CivilizationUnitEffect Draft(CivilizationUnitEffect previous) => previous with
    {
        Name = _name.Text.Trim(), Enabled = _enabled.Checked,
        Trigger = _trigger.SelectedIndex == 0 ? CivilizationUnitEffectTrigger.Proximity : CivilizationUnitEffectTrigger.Activated,
        Stat = (FirstStat.SelectedItem as StatChoice)?.Stat ?? previous.Stat,
        TargetObjectId = _targetIds.Count > 0 ? _targetIds[0] : previous.TargetObjectId,
        TargetObjectIds = _targetIds.ToArray(),
        Amount = FirstAmount.Value,
        Outputs = _outputRows.Select(row => new CivilizationUnitEffectOutput(((StatChoice)row.Stat.SelectedItem!).Stat, row.Amount.Value)).ToArray(),
        RadiusTiles = (int)_radius.Value,
        ActivationMode = _mode.SelectedIndex == 0 ? CivilizationUnitEffectActivationMode.Toggle : CivilizationUnitEffectActivationMode.Timed,
        DurationSeconds = (int)_duration.Value, ButtonCaption = _caption.Text.Trim(),
        ButtonIconId = (_icon.SelectedItem as NamedChoice)?.Id ?? previous.ButtonIconId,
        ButtonSlot = (_slot.SelectedItem as NamedChoice)?.Id ?? previous.ButtonSlot
    };

    private void UpdatePreview()
    {
        if (_updating || _effects.FirstOrDefault(item => item.Id == _selectedId) is not { } previous) return;
        var effect = Draft(previous);
        var proximity = effect.Trigger == CivilizationUnitEffectTrigger.Proximity;
        _summary.Text = string.Join("\n", CivilizationUnitEffects.GetOutputs(effect).Select(output =>
            FormatOutputAmount(output) + " " + CivilizationUnitEffects.GetStatDefinition(output.Stat).Name.ToLowerInvariant()));
        var target = _targetIds.Count == 1 ? TargetName(_targetIds[0]) : $"targets of {_targetIds.Count} selected types";
        _behavior.Text = proximity
            ? $"Affects other nearby {target} within {effect.RadiusTiles} tiles. Same-player targets only; sources of this effect do not stack. Ends on leaving the radius or when the source dies."
            : "Affects each selected unit or building of this type. " + (effect.ActivationMode == CivilizationUnitEffectActivationMode.Toggle
                ? "Press the ability button again to turn it off."
                : $"Lasts {effect.DurationSeconds} seconds; activating again restarts the timer.") + " Activation resets when loading a save.";
        if (!effect.Enabled) _summary.Text += "  ·  Disabled";
        if (_effectCards.TryGetValue(effect.Id, out var card)) card.UpdateEffect(effect);
    }

    private bool CommitSelected()
    {
        if (_selectedId is null) return true;
        var index = _effects.FindIndex(item => item.Id == _selectedId);
        if (index < 0) return true;
        _errors.Clear();
        try
        {
            if (_targetIds.Count == 0) throw new InvalidDataException("Select at least one affected unit or building from the catalog.");
            var unavailable = _targetIds.Where(id => !_objects.ContainsKey(id)).ToArray();
            if (unavailable.Length > 0) throw new InvalidDataException("Some selected targets are unavailable: " +
                string.Join(", ", unavailable.Select(TargetName)) + ". Choose affected targets and clear the unavailable selections before applying.");
            if (_outputRows.Count == 0 || _outputRows.Any(row => row.Stat.SelectedItem is not StatChoice)) throw new InvalidDataException("Select an output stat.");
            if (_trigger.SelectedIndex == 1 && _icon.SelectedItem is not NamedChoice)
                throw new InvalidDataException("Select an existing in-game button appearance.");
            if (_enabled.Checked && _trigger.SelectedIndex == 1 && _slot.SelectedItem is not NamedChoice)
                throw new InvalidDataException("No free button positions remain on this unit or building. Remove another activated ability or use a proximity effect.");
            var effect = Draft(_effects[index]);
            var candidate = _effects.ToArray();
            candidate[index] = effect;
            CivilizationUnitEffects.Validate(_source, candidate);
            _effects[index] = effect;
            _effectCards[effect.Id].UpdateEffect(effect);
            return true;
        }
        catch (InvalidDataException error)
        {
            _status.Text = error.Message;
            var invalid = _outputRows.FirstOrDefault(row => row.Amount.Value == 0);
            var field = invalid is not null ? (Control)invalid.Amount : _name;
            _errors.SetError(field, error.Message);
            field.Focus();
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
            // A proximity draft still needs a persisted numeric value. Activated
            // drafts can commit only with a free position selected above.
            ButtonSlot = _availableButtonSlots.FirstOrDefault(slot => !_effects.Any(item => item.Enabled && item.Trigger == CivilizationUnitEffectTrigger.Activated && item.ButtonSlot == slot),
                CivilizationUnitEffectBindings.CustomAbilityButtonSlots[0])
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
        if (_effects.Any(effect => effect.Enabled && effect.Trigger == CivilizationUnitEffectTrigger.Activated &&
            !_availableButtonSlots.Contains(effect.ButtonSlot)))
        {
            _status.Text = "No free button positions remain. Remove an activated ability or use a proximity effect.";
            return;
        }
        CivilizationUnitEffects.Validate(_source, _effects);
        EditedEffects = _effects.ToArray();
        DialogResult = DialogResult.OK;
        Close();
    }

    internal void AddForVerification() => AddEffect();
    internal void RemoveForVerification() => RemoveEffect();
    internal void AddRangerExampleForVerification() => AddRangerExample();
    internal void SelectForVerification(string id) => SelectEffect(id);
    internal void AddOutputForVerification() => AddOutput();
    internal void RemoveOutputForVerification(int index) => RemoveOutput(_outputRows[index]);
    internal IReadOnlyList<CivilizationUnitEffect> SnapshotForVerification => _effects.ToArray();
    internal bool ApplyForVerification() { Apply(); return EditedEffects is not null; }

    private static MedievalComboBox Choice(string name) => new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = name, DropDownWidth = 420 };
    private static NumericUpDown Number(string name, int minimum, int maximum, int value) => new() { AccessibleName = name, Minimum = minimum, Maximum = maximum, Value = value, DecimalPlaces = 0 };
    private static Button ActionButton(string text, Action action, string name, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 32, AccessibleName = name, Margin = new Padding(0, 0, 6, 0), UseMnemonic = false };
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
        if (disposing) { _errors.Dispose(); _overviewTip.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed record NamedChoice(int Id, string Name) { public override string ToString() => Name; }
    private static string FormatOutputAmount(CivilizationUnitEffectOutput output) =>
        output.Stat is CivilizationUnitEffectStat.BuildLimit or CivilizationUnitEffectStat.QueueCapacity
            ? "Limit " + output.Amount.ToString("0", CultureInfo.CurrentCulture)
            : output.Amount.ToString("+0.####;-0.####;0", CultureInfo.CurrentCulture);
    private sealed record StatChoice(CivilizationUnitEffectStat Stat, string Name) { public override string ToString() => Name; }
    private sealed record OutputFields(TableLayoutPanel Host, MedievalComboBox Stat, NumericUpDown Amount, Label Title, Button Remove);

    private sealed class EffectCard : Control
    {
        private CivilizationUnitEffect _effect;
        private bool _selected;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

        public EffectCard(CivilizationUnitEffect effect, Action select)
        {
            _effect = effect;
            Dock = DockStyle.Top;
            Height = 100;
            Margin = new Padding(0, 0, 0, 9);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            UpdateEffect(effect);
            Click += (_, _) => select();
            KeyDown += (_, args) => { if (args.KeyCode is Keys.Enter or Keys.Space) { select(); args.SuppressKeyPress = true; } };
            GotFocus += (_, _) => Invalidate();
            LostFocus += (_, _) => Invalidate();
        }

        public void UpdateEffect(CivilizationUnitEffect effect)
        {
            _effect = effect;
            AccessibleName = $"Effect attachment {effect.Name}";
            AccessibleDescription = CivilizationUnitEffects.Describe(effect);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            var graphics = args.Graphics;
            using var brush = new SolidBrush(Selected ? MedievalTheme.SurfaceRaised : MedievalTheme.Surface);
            graphics.FillRectangle(brush, ClientRectangle);
            using var border = new Pen(Selected ? MedievalTheme.Gold : MedievalTheme.Border);
            graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            if (Selected) { using var accent = new SolidBrush(MedievalTheme.Gold); graphics.FillRectangle(accent, 0, 0, 3, Height); }
            var scale = DeviceDpi / 96f;
            int S(int value) => (int)(value * scale);
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            using var title = MedievalTheme.UiFont(9.25f, FontStyle.Bold);
            using var detail = MedievalTheme.UiFont(8.5f);
            TextRenderer.DrawText(graphics, _effect.Name, title, new Rectangle(S(13), S(12), Width - S(26), S(22)), MedievalTheme.Text, flags);
            var outputs = CivilizationUnitEffects.GetOutputs(_effect);
            var first = outputs[0];
            var stat = CivilizationUnitEffects.GetStatDefinition(first.Stat);
            var change = FormatOutputAmount(first) + " " + stat.Name +
                (outputs.Count > 1 ? $"  +{outputs.Count - 1} more" : "");
            TextRenderer.DrawText(graphics, change, detail, new Rectangle(S(13), S(38), Width - S(26), S(20)), MedievalTheme.GoldBright, flags);
            var trigger = _effect.Trigger == CivilizationUnitEffectTrigger.Proximity ? "Proximity aura" : "Activated ability";
            TextRenderer.DrawText(graphics, trigger + "  ·  " + (_effect.Enabled ? "Enabled" : "Disabled"), detail, new Rectangle(S(13), S(65), Width - S(26), S(20)), MedievalTheme.TextSecondary, flags);
            if (Focused) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -5, -5));
        }
    }
}

using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>A read-only view of a target's game definition and project overrides.</summary>
internal sealed class CivilizationUnitOverviewDialog : Form
{
    private readonly TableLayoutPanel _sections = Stack();

    public CivilizationUnitOverviewDialog(CivilizationEntityDraft entity,
        IReadOnlyDictionary<string, string> currentValues, Image? icon = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(currentValues);
        var values = new Dictionary<string, string>(currentValues, StringComparer.Ordinal);
        foreach (var (key, value) in entity.Properties)
            if (!string.IsNullOrWhiteSpace(value)) values[key] = value;
        var fields = CivilizationSchema.GetFields(entity.Category, values);
        var numericBindings = CivilizationObjectBindings.Fields(entity.Category)
            .Where(field => field.Type is not (CivilizationBinaryType.String100 or CivilizationBinaryType.BooleanByte))
            .Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        var numeric = fields.Where(field => field.Kind == CivilizationFieldKind.Number || numericBindings.Contains(field.Key)).ToArray();
        var numericKeys = numeric.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        var prefix = entity.Category == CivilizationCategory.Buildings ? "building." : "unit.";
        var name = values.GetValueOrDefault(prefix + "displayName");
        if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(entity.Name) ? "Unit overview" : entity.Name;

        Text = $"Overview · {name}";
        AccessibleName = "Unit and building stats overview";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 700);
        MinimumSize = new Size(700, 560);
        Font = MedievalTheme.UiFont(9);
        ForeColor = MedievalTheme.Text;
        BackColor = MedievalTheme.Sidebar;

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(18, 12, 18, 12), Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 12) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, icon is null ? 0 : 78));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new PictureBox
        {
            Dock = DockStyle.Fill, Image = icon, SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 14, 0), AccessibleName = "Unit overview game icon"
        }, 0, 0);
        var heading = Stack();
        AddRow(heading, Wrap(name, MedievalTheme.UiFont(14, FontStyle.Bold), MedievalTheme.GoldBright));
        var identity = entity.Category == CivilizationCategory.Buildings ? "Building" : "Unit";
        if (entity.SourceObjectId is int objectId) identity += $"  ·  Game object {objectId}";
        AddRow(heading, Wrap(identity, Font, MedievalTheme.TextSecondary));
        AddRow(heading, Wrap($"{numeric.Length} stats  ·  Game definition and project edits", Font, MedievalTheme.TextMuted));
        header.Controls.Add(heading, 1, 0);
        content.Controls.Add(header, 0, 0);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, AccessibleName = "Unit overview sections" };
        scroll.Controls.Add(_sections);
        scroll.Layout += (_, _) =>
        {
            var width = Math.Max(1, scroll.ClientSize.Width);
            if (_sections.MaximumSize.Width != width) _sections.MaximumSize = new Size(width, 0);
        };

        if (values.TryGetValue(prefix + "description", out var description) && !string.IsNullOrWhiteSpace(description) && !IsFormatTemplate(description))
        {
            var card = Card("Description");
            AddRow(card, Wrap(description.Trim(), Font, MedievalTheme.TextSecondary));
            AddRow(_sections, card);
        }
        foreach (var group in numeric.GroupBy(field => field.Section))
        {
            var card = Card(group.Key);
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var index = 0;
            foreach (var field in group)
            {
                var row = index / 2;
                if (index % 2 == 0) { grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); }
                var cell = Field(field, values.GetValueOrDefault(field.Key));
                cell.Margin = index % 2 == 0 ? new Padding(0, 0, 12, 0) : new Padding(12, 0, 0, 0);
                grid.Controls.Add(cell, index % 2, row);
                index++;
            }
            AddRow(card, grid);
            AddRow(_sections, card);
        }

        var details = fields.Where(field => !numericKeys.Contains(field.Key) &&
            field.Key != prefix + "description" && field.Key != prefix + "displayName" &&
            values.TryGetValue(field.Key, out var value) && !string.IsNullOrWhiteSpace(value) && !IsFormatTemplate(value)).ToArray();
        foreach (var group in details.GroupBy(field => field.Section))
        {
            var card = Card(group.Key + " details");
            foreach (var field in group) AddRow(card, Field(field, values[field.Key], wide: true));
            AddRow(_sections, card);
        }
        content.Controls.Add(scroll, 0, 1);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        footer.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Read-only overview", ForeColor = MedievalTheme.TextMuted, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var close = new Button { Text = "Close", Dock = DockStyle.Fill, AccessibleName = "Close unit overview", Margin = Padding.Empty, DialogResult = DialogResult.OK };
        MedievalTheme.StyleSecondaryButton(close);
        close.Click += (_, _) => Close();
        footer.Controls.Add(close, 1, 0);
        content.Controls.Add(footer, 0, 2);
        AcceptButton = close;
        CancelButton = close;
        MedievalDialogFrame.Apply(this, content);
    }

    private TableLayoutPanel Field(CivilizationFieldDefinition field, string? value, bool wide = false)
    {
        var cell = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 7, 0, 7) };
        cell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, wide ? 28 : 64));
        cell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, wide ? 72 : 36));
        cell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label { Text = field.Label, Dock = DockStyle.Top, AutoSize = true, ForeColor = MedievalTheme.TextSecondary, Margin = new Padding(0, 0, 8, 0), UseMnemonic = false, AccessibleName = "Overview stat " + field.Key };
        var exposed = !string.IsNullOrWhiteSpace(value);
        if (field.Kind == CivilizationFieldKind.Boolean && bool.TryParse(value, out var boolean)) value = boolean ? "Yes" : "No";
        var number = new Label
        {
            Text = exposed ? value!.Trim() : "Not exposed", Dock = DockStyle.Top, AutoSize = true,
            ForeColor = exposed ? MedievalTheme.Text : MedievalTheme.TextMuted,
            Font = exposed && !wide ? MedievalTheme.UiFont(9, FontStyle.Bold) : Font,
            Margin = Padding.Empty, UseMnemonic = false, AccessibleName = "Overview value " + field.Key,
            AccessibleDescription = field.Help ?? field.Label
        };
        cell.Controls.Add(label, 0, 0);
        cell.Controls.Add(number, 1, 0);
        void ResizeLabels(object? sender, EventArgs args)
        {
            var width = Math.Max(1, cell.ClientSize.Width);
            label.MaximumSize = new Size(Math.Max(32, (int)(width * (wide ? 0.28f : 0.64f)) - 8), 0);
            number.MaximumSize = new Size(Math.Max(32, (int)(width * (wide ? 0.72f : 0.36f))), 0);
        }
        cell.SizeChanged += ResizeLabels;
        ResizeLabels(null, EventArgs.Empty);
        return cell;
    }

    private TableLayoutPanel Card(string title)
    {
        var card = Stack();
        card.AccessibleName = "Unit overview " + title + " card";
        card.BackColor = MedievalTheme.Surface;
        card.Padding = new Padding(16, 12, 16, 12);
        card.Margin = new Padding(0, 0, 0, 12);
        card.Paint += (_, args) => { using var pen = new Pen(MedievalTheme.Border); args.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1); };
        var heading = Wrap(title.ToUpperInvariant(), MedievalTheme.UiFont(8, FontStyle.Bold), MedievalTheme.Gold);
        heading.Margin = new Padding(0, 0, 0, 6);
        AddRow(card, heading);
        return card;
    }

    private static Label Wrap(string text, Font font, Color color)
    {
        var label = new Label { Text = text, AutoSize = true, Dock = DockStyle.Top, Font = font, ForeColor = color, Margin = Padding.Empty, UseMnemonic = false };
        label.ParentChanged += (_, _) =>
        {
            if (label.Parent is not { } parent) return;
            void ResizeLabel(object? sender, EventArgs args) => label.MaximumSize = new Size(Math.Max(32, parent.ClientSize.Width - parent.Padding.Horizontal - label.Margin.Horizontal), 0);
            parent.SizeChanged += ResizeLabel;
            ResizeLabel(null, EventArgs.Empty);
        };
        return label;
    }

    private static TableLayoutPanel Stack()
    {
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    private static bool IsFormatTemplate(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .All(token => token.StartsWith('/'));

    private static void AddRow(TableLayoutPanel table, Control child)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(child, 0, row);
    }
}

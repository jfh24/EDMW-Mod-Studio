using System.Globalization;
using System.ComponentModel;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Edits the selected civilization's progression in the Ages workspace.</summary>
internal sealed class AgeProgressionPanel : UserControl
{
    private readonly CivilizationGameData _gameData;
    private CivilizationDraft _original;
    private readonly CivilizationDraft _candidate;
    private readonly IReadOnlyList<CivilizationAgeProgressionItem> _items;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _gameValues = [];
    private readonly FlowLayoutPanel _cards = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, AccessibleName = "Age progression items", Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 0), BackColor = MedievalTheme.Sidebar };
    private readonly List<ProgressionCard> _visibleCards = [];
    private IReadOnlyList<CivilizationAgeProgressionItem> _filteredItems = [];
    private const int PageSize = 64;
    private int _pageIndex;
    private readonly Button _previousPage;
    private readonly Button _nextPage;
    private readonly Label _pageLabel = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, ForeColor = MedievalTheme.TextSecondary, AccessibleName = "Progression page", Margin = Padding.Empty };
    private readonly TableLayoutPanel _pager;
    private readonly RowStyle _pagerRow = new(SizeType.Absolute, 0);
    private readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Find progression item…", AccessibleName = "Find progression item" };
    private readonly MedievalChoiceSelector _kind = new() { Dock = DockStyle.Top, AccessibleName = "Progression item type" };
    private readonly MedievalChoiceSelector _membership = new() { Dock = DockStyle.Top, AccessibleName = "Progression list" };
    private readonly MedievalChoiceSelector _ageFilter = new() { Dock = DockStyle.Top, AccessibleName = "Filter progression by age" };
    private readonly Button _add;
    private readonly Button _remove;
    private readonly Button _restore;
    private readonly Panel _detail = new MedievalBufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = MedievalTheme.Surface, Padding = new Padding(16, 12, 16, 12), Margin = Padding.Empty, AccessibleName = "Progression item details" };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = MedievalTheme.TextSecondary, Margin = new Padding(0, 0, 8, 0) };
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly ErrorProvider _errors;
    private readonly List<ProgressionField> _fields = [];
    private readonly Dictionary<string, int> _producerSelections = new(StringComparer.Ordinal);
    private CivilizationAgeProgressionItem? _selected;
    private bool _updating;
    private bool _committing;
    private int _shownMembership;
    private int _shownKind;
    private int _shownAgeFilter;
    private bool _refreshQueued;
    private string _shownSearch = "";
    private Button? _applyItem;
    private Button? _discard;
    private Label? _editState;

    internal event Action<CivilizationDraft>? CivilizationChanged;
    internal event EventHandler? PendingChanged;
    internal string CivilizationId => _candidate.Id;
    internal CivilizationDraft EditedCivilization => _candidate.Clone();
    internal bool HasChanges => !SameCivilization(_original, _candidate);
    internal bool HasPendingChanges => _fields.Any(field => field.ResetRequested || !SameNumber(ReadValue(field.Input), field.InitialValue));
    internal AgeProgressionViewState CaptureViewState() => new(_selected?.Entity.Id, _search.Text, _kind.SelectedItem?.ToString(),
        _membership.SelectedIndex, (_ageFilter.SelectedItem as CivilizationAgeName)?.Id, _pageIndex);
    internal void RestoreViewState(AgeProgressionViewState state)
    {
        _updating = true;
        _selected = _items.FirstOrDefault(item => item.Entity.Id == state.SelectedId);
        _pageIndex = Math.Max(0, state.Page);
        _search.Text = state.Search;
        _kind.SelectedIndex = Math.Max(0, _kind.Items.Cast<object>().ToList().FindIndex(item => item.ToString() == state.Kind));
        _membership.SelectedIndex = Math.Clamp(state.Membership, 0, 2);
        if (state.Age is int restoredAge && !_ageFilter.Items.Any(item => item is CivilizationAgeName age && age.Id == restoredAge))
            _ageFilter.Items.Add(new CivilizationAgeName(restoredAge, _gameData.GetAgeName(restoredAge)));
        _ageFilter.SelectedIndex = state.Age is null ? 0 : Math.Max(0, _ageFilter.Items.Cast<object>().ToList().FindIndex(item => item is CivilizationAgeName age && age.Id == state.Age));
        _updating = false;
        BindItems();
    }

    public AgeProgressionPanel(CivilizationGameData gameData, CivilizationDraft civilization)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(civilization);
        _gameData = gameData;
        _original = civilization.Clone();
        _candidate = civilization.Clone();
        _items = gameData.GetAgeProgressionCandidates(civilization);
        AccessibleName = $"Age progression · {gameData.GetDisplayName(civilization)}";
        Font = MedievalTheme.UiFont(9);
        ForeColor = MedievalTheme.Text;
        BackColor = MedievalTheme.Surface;
        AutoScaleMode = AutoScaleMode.Dpi;
        Height = 530;
        MinimumSize = new Size(660, 430);
        Margin = Padding.Empty;
        _errors = new ErrorProvider { ContainerControl = this, BlinkStyle = ErrorBlinkStyle.NeverBlink };
        _status.TextChanged += (_, _) => _tips.SetToolTip(_status, _status.Text);

        var content = new MedievalBufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 6, 0, 0), Margin = Padding.Empty, BackColor = MedievalTheme.Surface };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        _add = Button("Add items", AddSelected, "Add progression items to this civilization");
        _add.Width = 108;
        _add.Margin = Padding.Empty;
        _remove = Button("Remove", RemoveSelected, "Remove selected progression item from this civilization");
        _remove.Width = 80;
        _restore = Button("Restore", RestoreSelected, "Restore selected progression item's original membership");
        _restore.Width = 80;
        _tips.SetToolTip(_add, "Choose existing compatible game items, then add them to this civilization's progression.");
        _tips.SetToolTip(_remove, "Remove this item from the civilization. Its field edits are retained for adding it again.");
        _tips.SetToolTip(_restore, "Restore original game membership for this item and its linked progression record. Field edits are retained.");
        var body = new MedievalBufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = MedievalTheme.Border };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var roster = new MedievalBufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = new Padding(0, 0, 1, 0), Padding = new Padding(10), BackColor = MedievalTheme.Sidebar };
        roster.AccessibleName = "Progression roster";
        roster.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        roster.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        roster.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        roster.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        roster.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        roster.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        roster.RowStyles.Add(_pagerRow);
        var rosterHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = MedievalTheme.Sidebar };
        rosterHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rosterHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        var rosterTitle = Label("PROGRESSION ITEMS", 9, true);
        rosterTitle.Dock = DockStyle.Fill;
        rosterTitle.AutoSize = false;
        rosterTitle.TextAlign = ContentAlignment.MiddleLeft;
        rosterTitle.Margin = Padding.Empty;
        rosterHeader.Controls.Add(rosterTitle, 0, 0);
        rosterHeader.Controls.Add(_add, 1, 0);
        roster.Controls.Add(rosterHeader, 0, 0);
        MedievalTheme.StyleTextInput(_search);
        var searchFrame = MedievalTheme.FrameInput(_search);
        searchFrame.Margin = new Padding(0, 2, 0, 2);
        roster.Controls.Add(searchFrame, 0, 1);
        _kind.Items.Add("All types");
        _kind.ShowItemText = false;
        _kind.MinimumButtonWidth = 44;
        _kind.DisplayTextProvider = value => value.ToString() == "All types" ? "All" : value.ToString() ?? "";
        _kind.IconProvider = value => ProgressionIcons.ForKind(value.ToString() ?? "");
        foreach (var kind in _items.Select(item => item.Kind).Distinct(StringComparer.Ordinal).OrderBy(kind => kind, StringComparer.Ordinal)) _kind.Items.Add(kind);
        _kind.SelectedIndex = 0;
        var filters = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 4, Margin = new Padding(0, 6, 0, 4), BackColor = MedievalTheme.Sidebar, AccessibleName = "Progression type and age filters" };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.Controls.Add(Label("TYPE", 8, true), 0, 0);
        filters.Controls.Add(_kind, 0, 1);
        _membership.Items.AddRange(["Included", "Removed", "Available"]);
        _membership.SelectedIndex = 0;
        _ageFilter.Items.Add("All ages");
        _ageFilter.DisplayTextProvider = value => value.ToString() == "All ages" ? "All" : value.ToString() ?? "";
        foreach (var age in _gameData.GetAgeNames().Where(age => age.Id is >= 5 and <= 9)) _ageFilter.Items.Add(age);
        _ageFilter.SelectedIndex = 0;
        filters.Controls.Add(Label("AGE", 8, true), 0, 2);
        filters.Controls.Add(_ageFilter, 0, 3);
        roster.Controls.Add(_membership, 0, 2);
        roster.Controls.Add(filters, 0, 3);
        roster.Controls.Add(_cards, 0, 4);
        _pager = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, BackColor = MedievalTheme.Sidebar, AccessibleName = "Progression page navigation", Visible = false };
        _pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        _pager.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        _previousPage = Button("‹", () => ChangePage(_pageIndex - 1), "Previous progression items page");
        _nextPage = Button("›", () => ChangePage(_pageIndex + 1), "Next progression items page");
        foreach (var button in new[] { _previousPage, _nextPage }) { button.Dock = DockStyle.Fill; button.Margin = new Padding(0, 3, 0, 0); }
        _tips.SetToolTip(_previousPage, "Show the previous page of matching items.");
        _tips.SetToolTip(_nextPage, "Show the next page of matching items.");
        _pager.Controls.Add(_previousPage, 0, 0);
        _pager.Controls.Add(_pageLabel, 1, 0);
        _pager.Controls.Add(_nextPage, 2, 0);
        roster.Controls.Add(_pager, 0, 5);
        _cards.HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(_cards);
        _cards.Resize += (_, _) => SizeItemCards();
        body.Controls.Add(roster, 0, 0);
        _detail.HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(_detail);
        body.Controls.Add(_detail, 1, 0);
        content.Controls.Add(body, 0, 0);

        content.Controls.Add(_status, 0, 1);
        Controls.Add(content);

        _search.TextChanged += (_, _) =>
        {
            if (_updating) return;
            if (CommitPending()) { _pageIndex = 0; BindItems(false); return; }
            _updating = true;
            _search.Text = _shownSearch;
            _search.SelectionStart = _search.Text.Length;
            _updating = false;
        };
        _kind.SelectedIndexChanged += (_, _) => ChangeFilter(_kind, _shownKind);
        _membership.SelectedIndexChanged += (_, _) => ChangeFilter(_membership, _shownMembership);
        _ageFilter.SelectedIndexChanged += (_, _) => ChangeFilter(_ageFilter, _shownAgeFilter);
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
        CivilizationAgeProgression.FindEntity(civilization, source);

    private string? SharedOverride(CivilizationAgeProgressionItem item, string key)
    {
        var own = FindEntity(_candidate, item.Entity)?.Properties.GetValueOrDefault(key);
        if (own is not null) return own;
        var suffix = key[(key.IndexOf('.') + 1)..];
        foreach (var alias in _items.Where(alias => alias.TechnologyId == item.TechnologyId && alias.Entity.Id != item.Entity.Id))
        {
            var prefix = alias.Entity.Category switch { CivilizationCategory.Units => "unit", CivilizationCategory.Buildings => "building", _ => "technology" };
            var value = FindEntity(_candidate, alias.Entity)?.Properties.GetValueOrDefault(prefix + "." + suffix);
            if (value is not null) return value;
        }
        return null;
    }

    private string EffectiveValue(CivilizationAgeProgressionItem item, string key) =>
        SharedOverride(item, key) ?? GameValues(item).GetValueOrDefault(key, "");

    private bool Included(CivilizationAgeProgressionItem item) =>
        bool.TryParse(SharedOverride(item, item.MembershipKey), out var value) ? value : item.IncludedByDefault;

    private bool HasMembershipOverride(CivilizationAgeProgressionItem item) =>
        SharedOverride(item, item.MembershipKey) is not null;

    private void SizeItemCards()
    {
        var width = Math.Max(120, _cards.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
        foreach (var card in _visibleCards) card.Width = width;
    }

    private string AgeRange(CivilizationAgeProgressionItem item)
    {
        var prefix = item.Entity.Category switch { CivilizationCategory.Units => "unit", CivilizationCategory.Buildings => "building", _ => "technology" };
        var first = EffectiveValue(item, prefix + ".initialAge");
        var last = EffectiveValue(item, prefix + ".finalAge");
        return int.TryParse(first, out var firstId) && int.TryParse(last, out var lastId) ? _gameData.FormatAgeRange(firstId, lastId) : "Unknown";
    }

    private bool MatchesAge(CivilizationAgeProgressionItem item)
    {
        if (_ageFilter.SelectedItem is not CivilizationAgeName age) return true;
        var prefix = item.Entity.Category switch { CivilizationCategory.Units => "unit", CivilizationCategory.Buildings => "building", _ => "technology" };
        return int.TryParse(EffectiveValue(item, prefix + ".initialAge"), out var first) &&
            int.TryParse(EffectiveValue(item, prefix + ".finalAge"), out var last) && first <= age.Id && last >= age.Id;
    }

    private void BindItems(bool revealSelectedPage = true)
    {
        var selectedId = _selected?.Entity.Id;
        _updating = true;
        _cards.SuspendLayout();
        foreach (var card in _visibleCards) card.Dispose();
        _visibleCards.Clear();
        _cards.Controls.Clear();
        var query = _search.Text.Trim();
        _filteredItems = _items.Where(item => (_membership.SelectedIndex switch
            {
                1 => !Included(item) && (item.IncludedByDefault || HasMembershipOverride(item)),
                2 => !Included(item),
                _ => Included(item)
            }) && MatchesAge(item) && (query.Length == 0 || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Entity.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (_kind.SelectedIndex <= 0 || item.Kind == _kind.SelectedItem?.ToString())).OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        var selectedIndex = _filteredItems.ToList().FindIndex(item => item.Entity.Id == selectedId);
        if (revealSelectedPage && selectedIndex >= 0) _pageIndex = selectedIndex / PageSize;
        _pageIndex = Math.Clamp(_pageIndex, 0, Math.Max(0, PageCount - 1));
        var displayScale = Math.Max(1f, _add.Height / 30f);
        foreach (var item in _filteredItems.Skip(_pageIndex * PageSize).Take(PageSize))
        {
            var edited = HasMembershipOverride(item) || FindEntity(_candidate, item.Entity)?.Properties.Keys.Intersect(EditableFields(_gameData, item.Entity).Select(field => field.Key)).Any() == true;
            var card = new ProgressionCard(item, AgeRange(item), edited)
            {
                Height = (int)Math.Round(68 * displayScale),
                Margin = new Padding(0, 0, 0, (int)Math.Round(6 * displayScale))
            };
            card.Click += (_, _) => SelectCard(item);
            card.KeyDown += (_, args) =>
            {
                if (args.KeyCode == Keys.Delete && _remove.Enabled) { RemoveSelected(); args.Handled = true; }
                if (args.KeyCode == Keys.Insert) { AddSelected(); args.Handled = true; }
                if (args.KeyCode is Keys.Up or Keys.Down)
                {
                    MoveCardSelection(item, args.KeyCode == Keys.Down ? 1 : -1);
                    args.Handled = true;
                    args.SuppressKeyPress = true;
                }
            };
            _tips.SetToolTip(card, $"{item.DisplayName}\n{item.Kind} · {AgeRange(item)}" + (edited ? "\nEdited in this project" : ""));
            _visibleCards.Add(card);
            _cards.Controls.Add(card);
        }
        _selected = _visibleCards.FirstOrDefault(card => card.Item.Entity.Id == selectedId)?.Item ?? _visibleCards.FirstOrDefault()?.Item;
        foreach (var card in _visibleCards) card.Selected = card.Item.Entity.Id == _selected?.Entity.Id;
        SizeItemCards();
        UpdatePager(displayScale);
        _cards.ResumeLayout(true);
        _updating = false;
        _shownKind = _kind.SelectedIndex;
        _shownMembership = _membership.SelectedIndex;
        _shownAgeFilter = _ageFilter.SelectedIndex;
        _shownSearch = _search.Text;
        RenderSelected();
        UpdateActions();
        _status.Text = _items.Count == 0 ? "No compatible game progression items are available for this civilization." :
            $"{_items.Count(Included)} included · {_filteredItems.Count} shown · • Edited in project";
    }

    private void ChangeFilter(MedievalChoiceSelector filter, int previous)
    {
        if (_updating) return;
        if (CommitPending()) { _pageIndex = 0; BindItems(false); return; }
        _updating = true;
        filter.SelectedIndex = previous;
        _updating = false;
    }

    private void SelectCard(CivilizationAgeProgressionItem target)
    {
        if (_updating || target.Entity.Id == _selected?.Entity.Id || !CommitSelected()) return;
        _selected = target;
        foreach (var card in _visibleCards) card.Selected = card.Item.Entity.Id == target.Entity.Id;
        RenderSelected();
        UpdateActions();
    }

    private int PageCount => (_filteredItems.Count + PageSize - 1) / PageSize;

    private void UpdatePager(float scale)
    {
        var show = PageCount > 1;
        _pager.Visible = show;
        _pagerRow.Height = show ? (int)Math.Round(34 * scale) : 0;
        _previousPage.Enabled = _pageIndex > 0;
        _nextPage.Enabled = _pageIndex + 1 < PageCount;
        _pageLabel.Text = _filteredItems.Count == 0 ? "No items" :
            $"{_pageIndex * PageSize + 1}–{Math.Min((_pageIndex + 1) * PageSize, _filteredItems.Count)} of {_filteredItems.Count}";
    }

    private void ChangePage(int page)
    {
        if (page < 0 || page >= PageCount || page == _pageIndex || !CommitPending()) return;
        _pageIndex = page;
        _selected = _filteredItems[page * PageSize];
        BindItems(false);
        _cards.AutoScrollPosition = Point.Empty;
    }

    private void MoveCardSelection(CivilizationAgeProgressionItem current, int direction)
    {
        var index = _filteredItems.ToList().FindIndex(item => item.Entity.Id == current.Entity.Id);
        if (index < 0 || !CommitPending()) return;
        var nextIndex = Math.Clamp(index + direction, 0, _filteredItems.Count - 1);
        var target = _filteredItems[nextIndex];
        if (nextIndex / PageSize != _pageIndex)
        {
            _pageIndex = nextIndex / PageSize;
            _selected = target;
            BindItems();
        }
        else SelectCard(target);
        var card = _visibleCards.FirstOrDefault(card => card.Item.Entity.Id == _selected?.Entity.Id);
        if (card is not null) { _cards.ScrollControlIntoView(card); card.Focus(); }
    }

    private void UpdateActions()
    {
        _add.Text = _membership.SelectedIndex == 2 ? "Add item" : "Add items";
        _add.Enabled = _membership.SelectedIndex != 2 || _selected is { } item && !Included(item);
        _remove.Enabled = _selected is { } selected && Included(selected);
        _restore.Enabled = _selected is { } current && HasMembershipOverride(current);
        _remove.Visible = _remove.Enabled;
        _restore.Visible = _restore.Enabled;
    }

    private void AddSelected()
    {
        if (!CommitSelected()) return;
        if (_membership.SelectedIndex != 2)
        {
            _membership.SelectedIndex = 2;
            return;
        }
        if (_selected is not { } item || Included(item)) return;
        if (!SetMembership(item, true, false)) return;
        _updating = true;
        _membership.SelectedIndex = 0;
        _updating = false;
        BindItems();
        var aliases = _items.Count(alias => alias.TechnologyId == item.TechnologyId);
        _status.Text = aliases > 1 ? $"Added {item.DisplayName} and {aliases - 1} linked items using its shared progression record. Edit their progression below." :
            $"Added {item.DisplayName} to this civilization. Edit its progression below.";
    }

    private void RemoveSelected()
    {
        if (!CommitSelected() || _selected is not { } item || !Included(item)) return;
        if (!SetMembership(item, false, false)) return;
        BindItems();
        var aliases = _items.Count(alias => alias.TechnologyId == item.TechnologyId);
        _status.Text = aliases > 1 ? $"Removed {item.DisplayName} and {aliases - 1} linked items using its shared progression record. Use Add items to add them again." :
            $"Removed {item.DisplayName} from this civilization. Use Add items to add it again.";
    }

    private void RestoreSelected()
    {
        if (!CommitSelected() || _selected is not { } item || !HasMembershipOverride(item)) return;
        if (!SetMembership(item, item.IncludedByDefault, true)) return;
        BindItems();
        _status.Text = $"Restored {item.DisplayName}'s original civilization membership.";
    }

    private bool SetMembership(CivilizationAgeProgressionItem item, bool included, bool inherit)
    {
        var before = _candidate.Clone();
        CivilizationAgeProgression.SetMembership(_candidate, _items, item, included, inherit,
            included && !inherit && item.AddedProducerIds.Count > 0 ? SelectedProducerId(item) : null);
        return PublishChange(before);
    }

    private void SetProducerOverride(CivilizationAgeProgressionItem item, int producerId)
    {
        foreach (var alias in _items.Where(alias => alias.TechnologyId == item.TechnologyId))
        {
            FindEntity(_candidate, alias.Entity)?.Properties.Remove(alias.ProducerKey);
            _producerSelections[alias.Entity.Id] = producerId;
        }
        var entity = FindEntity(_candidate, item.Entity);
        if (entity is null)
        {
            entity = item.Entity.Clone();
            _candidate.Entities.Add(entity);
        }
        entity.Properties[item.ProducerKey] = producerId.ToString(CultureInfo.InvariantCulture);
    }

    private void PruneEmptyEntries()
    {
        _candidate.Entities.RemoveAll(entity => entity.Properties.Count == 0 && _items.Any(item => item.Entity.Id == entity.Id && item.Entity.Name == entity.Name));
    }

    private bool PublishChange(CivilizationDraft before)
    {
        if (!HasChanges) return true;
        try
        {
            CivilizationChanged?.Invoke(_candidate.Clone());
            _original = _candidate.Clone();
            PendingChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            _candidate.Entities = before.Entities;
            _status.Text = error.Message;
            return false;
        }
    }

    private void RenderSelected()
    {
        _updating = true;
        _errors.Clear();
        _fields.Clear();
        _applyItem = null;
        _discard = null;
        _editState = null;
        _detail.SuspendLayout();
        _remove.Parent?.Controls.Remove(_remove);
        _restore.Parent?.Controls.Remove(_restore);
        foreach (Control control in _detail.Controls.Cast<Control>().ToArray()) control.Dispose();
        _detail.Controls.Clear();
        _detail.AutoScrollPosition = Point.Empty;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, MaximumSize = new Size(680, 0), Margin = Padding.Empty, BackColor = MedievalTheme.Surface, AccessibleName = "Progression editor form" };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _detail.Controls.Add(layout);
        void Add(Control control) { control.Dock = DockStyle.Top; layout.Controls.Add(control); }
        if (_selected is not { } item)
        {
            Add(Label(_membership.SelectedIndex == 2 ? "Choose an item to add" : "Choose a progression item", 15, true));
            Add(Label(_membership.SelectedIndex == 2 ? "Search the compatible game items on the left, then choose Add to civilization." :
                _membership.SelectedIndex == 1 ? "No removed items match this search." : "Use Add items to build this civilization's progression.", 9));
        }
        else
        {
            _remove.Visible = Included(item);
            _restore.Visible = HasMembershipOverride(item);
            var actionWidth = (_remove.Visible ? 86 : 0) + (_restore.Visible ? 86 : 0);
            var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = MedievalTheme.Surface, AccessibleName = "Selected progression item" };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, actionWidth));
            var title = Label(item.DisplayName, 17, true);
            title.Font = MedievalTheme.DisplayFont(17);
            title.MaximumSize = new Size(Math.Max(110, Math.Min(680, _detail.ClientSize.Width - 32) - actionWidth), 0);
            title.Dock = DockStyle.Fill;
            heading.Controls.Add(title, 0, 0);
            var membershipActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty, BackColor = MedievalTheme.Surface, AccessibleName = "Progression membership actions" };
            _remove.Margin = new Padding(0, 0, 6, 0);
            _restore.Margin = Padding.Empty;
            membershipActions.Controls.AddRange([_remove, _restore]);
            heading.Controls.Add(membershipActions, 1, 0);
            Add(heading);
            var membership = Label($"{item.Kind} · " + (Included(item) ? "Included" : "Not included"), 9, true);
            membership.ForeColor = Included(item) ? MedievalTheme.Gold : MedievalTheme.TextSecondary;
            membership.Margin = new Padding(0, 3, 0, 8);
            Add(membership);
            if (!Included(item)) Add(Label("Choose Add item to include this item and edit its progression.", 9));
            var aliases = _items.Where(alias => alias.TechnologyId == item.TechnologyId && alias.Entity.Id != item.Entity.Id).ToArray();
            if (aliases.Length > 0)
            {
                var sharedMembership = Label($"Adding or removing this item also changes {aliases.Length} linked " + (aliases.Length == 1 ? "item." : "items."), 9);
                sharedMembership.ForeColor = MedievalTheme.TextSecondary;
                sharedMembership.MaximumSize = new Size(Math.Max(260, Math.Min(680, _detail.ClientSize.Width - 32)), 0);
                _tips.SetToolTip(sharedMembership, "Linked items: " + string.Join(", ", aliases.Select(alias => alias.DisplayName)));
                Add(sharedMembership);
            }
            if (item.AddedProducerIds.Count > 0 && item.ProducerChoices.Count > 0)
            {
                var producerRow = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0, 8, 0, 8), BackColor = MedievalTheme.Surface, MaximumSize = new Size(560, 0) };
                producerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var producerLabel = Label(item.Kind == "Technology" || item.Kind == "Age advance" ? "RESEARCHED AT" : "PRODUCED AT", 9, true);
                producerRow.Controls.Add(producerLabel, 0, 0);
                var producer = new MedievalChoiceSelector { Dock = DockStyle.Top, AccessibleName = "Progression item's producer", MinimumButtonWidth = 100, MaximumButtonWidth = 250 };
                foreach (var choice in item.ProducerChoices) producer.Items.Add(new ProducerChoice(choice.ObjectId, choice.DisplayName));
                var selectedProducer = SelectedProducerId(item);
                producer.SelectedIndex = producer.Items.Cast<ProducerChoice>().ToList().FindIndex(choice => choice.Id == selectedProducer);
                if (producer.SelectedIndex < 0) producer.SelectedIndex = 0;
                producer.SelectedIndexChanged += (_, _) =>
                {
                    if (_updating || producer.SelectedItem is not ProducerChoice choice) return;
                    if (!CommitPending())
                    {
                        _updating = true;
                        producer.SelectedIndex = producer.Items.Cast<ProducerChoice>().ToList().FindIndex(value => value.Id == SelectedProducerId(item));
                        _updating = false;
                        return;
                    }
                    var previous = SelectedProducerId(item);
                    _producerSelections[item.Entity.Id] = choice.Id;
                    if (!Included(item)) return;
                    var before = _candidate.Clone();
                    SetProducerOverride(item, choice.Id);
                    if (PublishChange(before)) _status.Text = "Updated this item's production location in the project.";
                    else
                    {
                        _producerSelections[item.Entity.Id] = previous;
                        _updating = true;
                        producer.SelectedIndex = producer.Items.Cast<ProducerChoice>().ToList().FindIndex(value => value.Id == previous);
                        _updating = false;
                    }
                };
                producerRow.Controls.Add(producer, 0, 1);
                Add(producerRow);
            }
            var scope = Label("Inclusion is specific to this civilization. Costs and ages also affect other items and civilizations using the same record.", 9);
            scope.ForeColor = MedievalTheme.TextSecondary;
            scope.MaximumSize = new Size(Math.Max(260, Math.Min(680, _detail.ClientSize.Width - 32)), 0);
            scope.Margin = new Padding(0, 3, 0, 8);
            _tips.SetToolTip(scope, item.Scope);
            Add(scope);
            var fields = EditableFields(_gameData, item.Entity);
            if (fields.Any(field => field.Key.Contains(".effect.", StringComparison.Ordinal)))
            {
                var effectScope = Label("Effects are shared by technologies using the same effect record.", 9);
                effectScope.ForeColor = MedievalTheme.TextSecondary;
                effectScope.MaximumSize = new Size(Math.Max(260, Math.Min(680, _detail.ClientSize.Width - 32)), 0);
                _tips.SetToolTip(effectScope, "Effect changes apply to every technology that references the same effect. Additive values take priority over relative values, then replacement values.");
                Add(effectScope);
            }
            foreach (var group in fields.GroupBy(field => field.Key.EndsWith(".initialAge", StringComparison.Ordinal) || field.Key.EndsWith(".finalAge", StringComparison.Ordinal) ? "Available ages" : field.Section))
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
                        var effectDescription = Label(description.Contains(':') ? description[(description.IndexOf(':') + 1)..].Trim() : description, 9);
                        effectDescription.ForeColor = MedievalTheme.TextSecondary;
                        effectDescription.MaximumSize = new Size(Math.Max(260, Math.Min(560, _detail.ClientSize.Width - 32)), 0);
                        _tips.SetToolTip(effectDescription, description);
                        Add(effectDescription);
                    }
                }
                var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(0, 0, 0, 3), Margin = Padding.Empty, BackColor = MedievalTheme.Surface, MaximumSize = new Size(560, 0), AccessibleName = "Progression fields: " + group.Key };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
                var row = 0;
                foreach (var definition in group)
                {
                    var value = EffectiveValue(item, definition.Key);
                    Control input;
                    var age = definition.Key.EndsWith(".initialAge", StringComparison.Ordinal) || definition.Key.EndsWith(".finalAge", StringComparison.Ordinal);
                    if (age)
                    {
                        var choices = new MedievalChoiceSelector { Dock = DockStyle.Top, AccessibleName = definition.Label, MinimumButtonWidth = 54, MaximumButtonWidth = 160 };
                        foreach (var name in _gameData.GetAgeNames().Where(name => name.Id is >= 5 and <= 9 || name.Id == 14 && definition.Key.EndsWith(".finalAge", StringComparison.Ordinal))) choices.Items.Add(name);
                        var ageId = int.Parse(value, CultureInfo.InvariantCulture);
                        if (!choices.Items.Cast<CivilizationAgeName>().Any(name => name.Id == ageId)) choices.Items.Add(new CivilizationAgeName(ageId, _gameData.GetAgeName(ageId)));
                        choices.SelectedIndex = choices.Items.Cast<CivilizationAgeName>().ToList().FindIndex(name => name.Id == ageId);
                        input = choices;
                    }
                    else if (!definition.Key.Contains(".effect.", StringComparison.Ordinal) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                        input = new MedievalNumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = uint.MaxValue, Value = number, AccessibleName = definition.Label };
                    else
                    {
                        input = new TextBox { Dock = DockStyle.Fill, Text = value, AccessibleName = definition.Label, AccessibleDescription = definition.Help };
                    }
                    if (input is not MedievalChoiceSelector) MedievalTheme.StyleTextInput(input);
                    input.Enabled = Included(item);
                    var field = new ProgressionField(definition, input, ReadValue(input));
                    _fields.Add(field);
                    if (input is MedievalChoiceSelector choiceInput) choiceInput.SelectedIndexChanged += (_, _) => FieldChanged();
                    else if (input is NumericUpDown numeric) numeric.ValueChanged += (_, _) => FieldChanged();
                    else input.TextChanged += (_, _) => FieldChanged();
                    input.Validating += (_, args) =>
                    {
                        if (!_updating && !HasNonValidatingFocus(this)) args.Cancel = !CommitPending();
                    };
                    var label = Label(age ? definition.Key.EndsWith(".initialAge", StringComparison.Ordinal) ? "From" : "Through" : definition.Label, 9);
                    label.Margin = new Padding(0, 0, 8, 0);
                    if (age)
                    {
                        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                        grid.Controls.Add(label, 0, row);
                        grid.SetColumnSpan(label, 3);
                        row++;
                        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                        input.Margin = new Padding(0, 3, 0, 7);
                        grid.Controls.Add(input, 0, row);
                        grid.SetColumnSpan(input, 2);
                    }
                    else
                    {
                        label.Dock = DockStyle.Fill;
                        label.AutoSize = false;
                        label.TextAlign = ContentAlignment.MiddleLeft;
                        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                        grid.Controls.Add(label, 0, row);
                        var frame = MedievalTheme.FrameInput(input);
                        frame.Margin = new Padding(0, 3, 0, 3);
                        grid.Controls.Add(frame, 1, row);
                    }
                    var reset = Button("↶", () => ResetField(field), $"Reset {definition.Label} to game value");
                    reset.Dock = age ? DockStyle.Top : DockStyle.Fill;
                    reset.Enabled = Included(item);
                    reset.CausesValidation = false;
                    reset.Margin = new Padding(5, 3, 0, 3);
                    grid.Controls.Add(reset, 2, row++);
                    _tips.SetToolTip(input, definition.Help ?? "Editing this field creates a project override for the linked game record.");
                    _tips.SetToolTip(reset, "Inherit this field's current game value.");
                }
                Add(grid);
            }
            if (fields.Count == 0) Add(Label("No verified fields are available for this item.", 9));
            var resetItem = Button("Reset to game", ResetSelected, "Reset this progression item's editable fields");
            resetItem.Width = 112;
            resetItem.Enabled = Included(item);
            resetItem.CausesValidation = false;
            resetItem.Margin = new Padding(0, 2, 8, 5);
            _editState = Label("Saved to project", 8.5f);
            _editState.AccessibleName = "Progression edit state";
            _editState.Margin = new Padding(0, 10, 0, 4);
            Add(_editState);
            var actions = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, BackColor = MedievalTheme.Surface, AccessibleName = "Progression edit actions" };
            _applyItem = Button("Apply changes", () => { if (CommitPending()) _status.Text = "Item edits saved to the project."; }, "Apply selected progression item edits");
            _applyItem.Width = 116;
            _applyItem.BackColor = MedievalTheme.Gold;
            _applyItem.ForeColor = MedievalTheme.Input;
            _applyItem.Margin = new Padding(0, 2, 8, 5);
            actions.Controls.Add(_applyItem);
            actions.Controls.Add(resetItem);
            _discard = Button("Discard edits", DiscardPending, "Discard unapplied progression field edits");
            _discard.Width = 110;
            _discard.CausesValidation = false;
            _discard.Margin = new Padding(0, 2, 0, 5);
            actions.Controls.Add(_discard);
            Add(actions);
        }
        _detail.ResumeLayout(true);
        _updating = false;
        UpdateEditState();
    }

    private void FieldChanged()
    {
        if (_updating) return;
        UpdateEditState();
        PendingChanged?.Invoke(this, EventArgs.Empty);
        _status.Text = HasPendingChanges ? "Pending changes · Apply to save, or leave the field to save automatically." : "Item edits saved to the project.";
    }

    private void UpdateEditState()
    {
        var pending = HasPendingChanges;
        if (_applyItem is not null) _applyItem.Enabled = pending && _selected is { } item && Included(item);
        if (_discard is not null) _discard.Enabled = pending;
        if (_editState is not null)
        {
            var overrides = _selected is { } selected && _fields.Any(field => SharedOverride(selected, field.Definition.Key) is not null);
            _editState.Text = pending ? "Pending changes" : overrides ? "Saved to project" : "Using game values";
            _editState.ForeColor = pending ? MedievalTheme.GoldBright : MedievalTheme.TextSecondary;
        }
    }

    private int SelectedProducerId(CivilizationAgeProgressionItem item)
    {
        if (_producerSelections.TryGetValue(item.Entity.Id, out var choice)) return choice;
        var authored = SharedOverride(item, item.ProducerKey)?.Split(',')[0];
        if (int.TryParse(authored, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return id;
        return item.AddedProducerIds.FirstOrDefault();
    }

    private void ResetField(ProgressionField field)
    {
        if (_selected is not { } item) return;
        SetValue(field.Input, GameValues(item).GetValueOrDefault(field.Definition.Key, ""));
        field.ResetRequested = true;
        _errors.SetError(field.Input, "");
        if (!_updating) CommitPending();
    }

    private void ResetSelected()
    {
        _updating = true;
        foreach (var field in _fields) ResetField(field);
        _updating = false;
        if (CommitPending()) _status.Text = "This item's progression fields now inherit game values.";
    }

    internal bool CommitPending()
    {
        if (_updating || _committing) return true;
        _committing = true;
        try { return CommitSelected(); }
        finally { _committing = false; }
    }

    // Native focus reaches a destination button before nested ContainerControls
    // finish validation. Respect that destination's validation policy explicitly.
    internal static bool HasNonValidatingFocus(Control scope)
    {
        static bool Find(Control control) => control is Button { CausesValidation: false, Focused: true } ||
            control.Controls.Cast<Control>().Any(Find);
        return Find((Control?)scope.FindForm() ?? scope);
    }

    internal void DiscardPending()
    {
        RenderSelected();
        PendingChanged?.Invoke(this, EventArgs.Empty);
        _status.Text = "Discarded pending field edits. Saved project edits are unchanged.";
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
        var before = _candidate.Clone();
        var properties = existing?.Properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = false;
        foreach (var field in _fields)
        {
            var value = ReadValue(field.Input);
            if (!field.ResetRequested && SameNumber(value, field.InitialValue)) continue;
            changed = true;
            var suffix = field.Definition.Key[(field.Definition.Key.IndexOf('.') + 1)..];
            foreach (var alias in _items.Where(alias => alias.TechnologyId == item.TechnologyId && alias.Entity.Id != item.Entity.Id))
            {
                var authored = FindEntity(_candidate, alias.Entity);
                var prefix = alias.Entity.Category switch { CivilizationCategory.Units => "unit", CivilizationCategory.Buildings => "building", _ => "technology" };
                var aliasKey = prefix + "." + suffix;
                if (authored?.Properties.ContainsKey(aliasKey) != true) continue;
                if (field.ResetRequested || SameNumber(value, GameValues(item).GetValueOrDefault(field.Definition.Key))) authored.Properties.Remove(aliasKey);
                else authored.Properties[aliasKey] = value;
            }
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
        PruneEmptyEntries();
        if (!PublishChange(before)) return false;
        foreach (var field in _fields) { field.InitialValue = ReadValue(field.Input); field.ResetRequested = false; }
        foreach (var card in _visibleCards)
        {
            var alias = card.Item;
            if (alias.TechnologyId != item.TechnologyId) continue;
            card.AgeLabel = AgeRange(alias);
            _tips.SetToolTip(card, $"{alias.DisplayName}\n{alias.Kind} · {AgeRange(alias)}");
            var edited = HasMembershipOverride(alias) || _fields.Any(field => SharedOverride(alias, field.Definition.Key) is not null);
            card.Edited = edited;
            card.Invalidate();
        }
        UpdateEditState();
        PendingChanged?.Invoke(this, EventArgs.Empty);
        if (_ageFilter.SelectedItem is CivilizationAgeName && !MatchesAge(item)) RefreshFilteredItems();
        return true;
    }

    private void RefreshFilteredItems()
    {
        if (_refreshQueued || !IsHandleCreated || IsDisposed) return;
        _refreshQueued = true;
        BeginInvoke(() =>
        {
            _refreshQueued = false;
            if (!IsDisposed && !HasPendingChanges) BindItems();
        });
    }

    internal IReadOnlyList<CivilizationAgeProgressionItem> ItemsForVerification => _items;
    internal IReadOnlyList<CivilizationAgeProgressionItem> IncludedItemsForVerification => _items.Where(Included).ToArray();
    internal IReadOnlyList<CivilizationAgeProgressionItem> VisibleItemsForVerification => _visibleCards.Select(card => card.Item).ToArray();
    internal IReadOnlyList<CivilizationAgeProgressionItem> FullFilteredItemsForVerification => _filteredItems;
    internal int PageCountForVerification => PageCount;
    internal int CurrentPageForVerification => _pageIndex;
    internal void SetPageForVerification(int page)
    {
        if (page < 0 || page >= PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        ChangePage(page);
    }
    internal IReadOnlyList<string> VisibleAgeLabelsForVerification => _visibleCards.Select(card => card.AgeLabel).ToArray();
    internal void SetAgeFilterForVerification(int? age)
    {
        if (age is int id && !_ageFilter.Items.Cast<object>().Any(item => item is CivilizationAgeName choice && choice.Id == id))
            _ageFilter.Items.Add(new CivilizationAgeName(id, _gameData.GetAgeName(id)));
        _ageFilter.SelectedIndex = age is null ? 0 : _ageFilter.Items.Cast<object>().ToList().FindIndex(item => item is CivilizationAgeName choice && choice.Id == age);
        if (_ageFilter.SelectedIndex < 0) throw new ArgumentOutOfRangeException(nameof(age));
    }
    internal string? SelectedItemForVerification => _selected?.Entity.Id;
    internal bool IsIncludedForVerification(string entityId) => Included(_items.Single(item => item.Entity.Id == entityId));
    internal void CancelPendingForVerification() => DiscardPending();
    internal void SelectItemForVerification(string entityId)
    {
        if (!CommitSelected()) throw new InvalidDataException("Resolve invalid progression fields first.");
        _updating = true;
        _search.Text = "";
        _kind.SelectedIndex = 0;
        _ageFilter.SelectedIndex = 0;
        _selected = _items.Single(item => item.Entity.Id == entityId);
        _membership.SelectedIndex = Included(_selected) ? 0 : _selected.IncludedByDefault || HasMembershipOverride(_selected) ? 1 : 2;
        _updating = false;
        BindItems();
    }
    internal void SetFieldForVerification(string key, string value) => SetValue(_fields.Single(field => field.Definition.Key == key).Input, value);
    internal string GetFieldForVerification(string key) => ReadValue(_fields.Single(field => field.Definition.Key == key).Input);
    internal IReadOnlyList<string> EditableKeysForVerification => _fields.Select(field => field.Definition.Key).ToArray();
    internal void ResetItemForVerification() => ResetSelected();
    internal bool ApplyForVerification()
    {
        if (!CommitPending()) return false;
        if (_ageFilter.SelectedItem is CivilizationAgeName) BindItems();
        return true;
    }
    internal void AddItemForVerification(string entityId)
    {
        SelectItemForVerification(entityId);
        if (!SetMembership(_selected!, true, false)) throw new InvalidDataException(_status.Text);
        _updating = true;
        _membership.SelectedIndex = 0;
        _updating = false;
        BindItems();
    }
    internal void SetProducerForVerification(string entityId, int producerId)
    {
        SelectItemForVerification(entityId);
        var item = _selected!;
        if (!item.ProducerChoices.Any(choice => choice.ObjectId == producerId)) throw new ArgumentOutOfRangeException(nameof(producerId));
        _producerSelections[entityId] = producerId;
        if (Included(item))
        {
            var before = _candidate.Clone();
            SetProducerOverride(item, producerId);
            if (!PublishChange(before)) throw new InvalidDataException(_status.Text);
        }
        RenderSelected();
    }
    internal void RemoveItemForVerification(string entityId) { SelectItemForVerification(entityId); RemoveSelected(); }
    internal void RestoreItemForVerification(string entityId) { SelectItemForVerification(entityId); RestoreSelected(); }

    private static bool SameNumber(string? left, string? right) => left == right ||
        double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a == b;
    private static string ReadValue(Control input) => input is MedievalChoiceSelector choices ? (choices.SelectedItem as CivilizationAgeName)?.Id.ToString(CultureInfo.InvariantCulture) ?? "" :
        input is NumericUpDown numeric ? numeric.Value.ToString("0.############################", CultureInfo.InvariantCulture) : input.Text.Trim();
    private static void SetValue(Control input, string value)
    {
        if (input is MedievalChoiceSelector choices)
        {
            var age = decimal.Parse(value, CultureInfo.InvariantCulture);
            if (age == decimal.Truncate(age) && age is >= 0 and <= 100 && !choices.Items.Cast<CivilizationAgeName>().Any(item => item.Id == age))
                choices.Items.Add(new CivilizationAgeName((int)age, CivilizationAgeNames.GetName((int)age)));
            choices.SelectedIndex = choices.Items.Cast<CivilizationAgeName>().ToList().FindIndex(item => item.Id == age);
        }
        else if (input is NumericUpDown numeric) numeric.Value = decimal.Parse(value, CultureInfo.InvariantCulture);
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
    private sealed record ProducerChoice(int Id, string Name) { public override string ToString() => Name; }

    private sealed class ProgressionCard : Button
    {
        private bool _selected;
        private bool _hovered;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public CivilizationAgeProgressionItem Item { get; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string AgeLabel { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Edited { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Selected { get => _selected; set { TabStop = value; if (_selected != value) { _selected = value; Invalidate(); } } }

        public ProgressionCard(CivilizationAgeProgressionItem item, string ages, bool edited)
        {
            Item = item;
            AgeLabel = ages;
            Edited = edited;
            Height = 68;
            Width = 280;
            Margin = new Padding(0, 0, 0, 6);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = MedievalTheme.Input;
            ForeColor = MedievalTheme.Text;
            Font = MedievalTheme.UiFont(9.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            AccessibleName = "Select progression item: " + item.DisplayName;
            AccessibleDescription = item.Kind + " · " + ages;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; Invalidate(); }
        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Selected ? MedievalTheme.SurfaceRaised : MedievalTheme.Input);
            using var border = new Pen(Selected ? MedievalTheme.GoldBright : _hovered ? MedievalTheme.BorderStrong : MedievalTheme.Border, Selected ? 2 : 1);
            e.Graphics.DrawRectangle(border, 1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            var scale = Height / 68f;
            var inset = (int)(10 * scale);
            var iconSize = (int)(32 * scale);
            var icon = ProgressionIcons.ForKind(Item.Kind);
            if (icon is not null) e.Graphics.DrawImage(icon, new Rectangle(inset, (Height - iconSize) / 2, iconSize, iconSize));
            var textLeft = inset + iconSize + (int)(10 * scale);
            var titleBounds = new Rectangle(textLeft, (int)(8 * scale), Math.Max(1, Width - textLeft - inset), (int)(25 * scale));
            TextRenderer.DrawText(e.Graphics, (Edited ? "• " : "") + Item.DisplayName, Font, titleBounds, Selected ? MedievalTheme.GoldBright : MedievalTheme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            using var detailFont = new Font(Font.FontFamily, Math.Max(8, Font.Size - 1), FontStyle.Regular, GraphicsUnit.Point);
            TextRenderer.DrawText(e.Graphics, Item.Kind + " · " + AgeLabel, detailFont,
                new Rectangle(textLeft, (int)(36 * scale), Math.Max(1, Width - textLeft - inset), (int)(22 * scale)), MedievalTheme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), MedievalTheme.Gold, BackColor);
        }
    }
}

internal sealed record AgeProgressionViewState(string? SelectedId, string Search, string? Kind, int Membership, int? Age, int Page = 0);

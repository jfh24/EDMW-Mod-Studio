using EDMWMapDesigner.Civilizations;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace EDMWMapDesigner.UI;

/// <summary>A separate, undoable civilization workspace with explicit game install and restore.</summary>
public sealed class CivilizationEditorView : UserControl
{
    private readonly Func<string?> _locateGame;
    private readonly Action<CivilizationInstallerPackage>? _sendOverLan;
    private readonly CivilizationEditHistory _history = new(CivilizationEditProject.CreateDefault());
    private readonly TextBox _projectName = new() { Width = 220, Height = 27, MaxLength = 200, AccessibleName = "Civilization project name" };
    private readonly TextBox _civilizationSearch = new() { Dock = DockStyle.Fill, PlaceholderText = "Find civilization…", AccessibleName = "Find civilization" };
    private readonly TextBox _entitySearch = new() { Dock = DockStyle.Fill, PlaceholderText = "Find in roster…", AccessibleName = "Find roster entry" };
    private readonly EditorSelectionCards _civilizations = new("Civilizations", "Select civilization: ", 34);
    private readonly EditorSelectionCards _categories = new("Civilization categories", "Select civilization category: ", 36);
    private readonly EditorSelectionCards _entities = new("Civilization roster", "Select civilization roster item: ", 64);
    private readonly Panel _detail = new MedievalBufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = MedievalTheme.Surface, Padding = new Padding(20, 12, 20, 16), AccessibleName = "Civilization property editor" };
    private readonly TableLayoutPanel _body = new MedievalBufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = MedievalTheme.Border, Margin = Padding.Empty };
    private readonly Panel _modelHost = new MedievalBufferedPanel { Dock = DockStyle.Fill, BackColor = MedievalTheme.Sidebar, Padding = new Padding(8), Visible = false };
    private readonly Panel _entityPanel = new MedievalBufferedPanel { Dock = DockStyle.Fill, BackColor = MedievalTheme.Sidebar, Padding = new Padding(12) };
    private readonly Label _entityHeading = MakeLabel("ROSTER", 10, true);
    private readonly Label _status = MakeLabel("Ready", 8.75f);
    private readonly ToolStripMenuItem _undo;
    private readonly ToolStripMenuItem _redo;
    private readonly Button _duplicate;
    private readonly Button _remove;
    private readonly Button _catalogButton;
    private readonly ToolTip _tips = new() { AutoPopDelay = 12000 };
    private readonly Image _editorEmblem;
    private readonly ErrorProvider _errors;
    private readonly List<FieldBinding> _bindings = [];
    private readonly Dictionary<CivilizationCategory, string> _activeSections = [];
    private readonly Dictionary<string, PropertySection> _sections = new(StringComparer.Ordinal);
    private AgeProgressionPanel? _progressionPanel;
    private readonly Dictionary<string, AgeProgressionViewState> _progressionViewStates = new(StringComparer.Ordinal);
    private CivilizationGameCatalog? _catalog;
    private CivilizationGameData? _gameData;
    private readonly Dictionary<(string Civilization, CivilizationCategory Category), IReadOnlyList<CivilizationEntityDraft>> _sourceRosters = [];
    private CivilizationGameAssets? _gameAssets;
    private string? _projectPath;
    private string? _selectedCivilizationId;
    private string? _selectedEntityId;
    private CivilizationCategory _category = CivilizationCategory.Overview;
    private int _navigationIndex;
    private bool _changesVisible;
    private bool _updating;
    private bool _pending;
    private bool _modelPreviewVisible;
    private bool _showUnmappedFields;
    private bool _arrangingModelPreview;
    private (bool Visible, bool Stacked, float Scale)? _modelLayout;
    private string _shownProjectName = "";
    private readonly string[] _navigation = ["Overview", "Units", "Buildings", "Technologies", "Abilities", "Economy", "Ages", "Assets", "AI", "Changes"];

    public event EventHandler? StateChanged;
    public bool IsDirty => _history.IsDirty || _pending || _progressionPanel?.HasPendingChanges == true || _projectName.Text != _shownProjectName;
    public string ProjectName => string.IsNullOrWhiteSpace(_projectName.Text) ? _history.Current.Name : _projectName.Text.Trim();
    internal CivilizationEditProject SnapshotProject { get { if (!CommitPending()) throw new InvalidOperationException("Resolve invalid fields before reading the project."); return _history.Current.Clone(); } }

    public CivilizationEditorView(Func<string?> locateGame, Action<CivilizationInstallerPackage>? sendOverLan = null)
    {
        ArgumentNullException.ThrowIfNull(locateGame);
        _locateGame = locateGame;
        _sendOverLan = sendOverLan;
        Dock = DockStyle.Fill;
        BackColor = MedievalTheme.Shell;
        ForeColor = MedievalTheme.Text;
        Font = MedievalTheme.UiFont();
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        _errors = new ErrorProvider { ContainerControl = this, BlinkStyle = ErrorBlinkStyle.NeverBlink };
        _detail.HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(_detail);
        _editorEmblem = MedievalIconFactory.Create(MedievalIcon.Civilizations, 32);
        _civilizations.IconProvider = _ => _editorEmblem;
        _categories.IconProvider = item => ProgressionIcons.ForKind(item.ToString() ?? "");
        _entities.IconProvider = _ => ProgressionIcons.ForKind(_category.ToString());

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = MedievalTheme.Shell };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 51));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = Padding.Empty, Padding = new Padding(16, 9, 12, 8), BackColor = MedievalTheme.Sidebar };
        toolbar.Controls.Add(new PictureBox
        {
            Image = _editorEmblem, Size = new Size(32, 32), SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 10, 0), TabStop = false, AccessibleName = "Civilization editor emblem"
        });
        var title = MakeLabel("Civ Editor", 14, true);
        title.Font = MedievalTheme.DisplayFont(14);
        title.Margin = new Padding(0, 2, 22, 0);
        toolbar.Controls.Add(title);
        toolbar.Controls.Add(MakeActionMenu("Project", [
            MakeAction("New", NewProject, Keys.Control | Keys.N),
            MakeAction("Open…", OpenProject, Keys.Control | Keys.O),
            MakeAction("Save", () => SaveProject(), Keys.Control | Keys.S),
            MakeAction("Save as…", () => SaveProject(true), Keys.Control | Keys.Shift | Keys.S)
        ]));
        _undo = MakeAction("Undo", Undo, Keys.Control | Keys.Z);
        _redo = MakeAction("Redo", Redo, Keys.Control | Keys.Y);
        var unmappedFields = new ToolStripMenuItem("Show unsupported fields") { CheckOnClick = true };
        unmappedFields.CheckedChanged += (_, _) =>
        {
            if (!CommitPending()) { unmappedFields.Checked = _showUnmappedFields; return; }
            _showUnmappedFields = unmappedFields.Checked;
            RenderDetail();
        };
        toolbar.Controls.Add(MakeActionMenu("Edit", [_undo, _redo, new ToolStripSeparator(),
            MakeAction("Find", () => (IsEntityCategory ? _entitySearch : _civilizationSearch).Focus(), Keys.Control | Keys.F),
            MakeAction("Reset category to game values", ResetCategory),
            MakeAction("Review changes", () => { if (CommitPending()) _categories.SelectedIndex = 9; }),
            new ToolStripSeparator(), unmappedFields
        ]));
        toolbar.Controls.Add(MakeActionMenu("Game", [
            MakeAction("Reload current values", LoadGameCatalog), new ToolStripSeparator(),
            MakeAction("Export over LAN", ExportOverLan)
        ]));
        var nameLabel = MakeLabel("Project", 9);
        nameLabel.Margin = new Padding(16, 6, 7, 0);
        toolbar.Controls.Add(nameLabel);
        MedievalTheme.StyleTextInput(_projectName);
        _projectName.Margin = new Padding(0, 2, 0, 0);
        toolbar.Controls.Add(MedievalTheme.FrameInput(_projectName));
        // FrameInput normally fills a grid cell; this toolbar uses a fixed, compact frame.
        toolbar.Controls[^1].Dock = DockStyle.None;
        toolbar.Controls[^1].Size = new Size(220, 29);
        layout.Controls.Add(toolbar, 0, 0);

        var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = MedievalTheme.Border };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 208));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var rail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12), BackColor = MedievalTheme.Sidebar, Margin = new Padding(0, 1, 1, 0) };
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        rail.Controls.Add(MakeLabel("CIVILIZATIONS", 10, true), 0, 0);
        MedievalTheme.StyleTextInput(_civilizationSearch);
        rail.Controls.Add(MedievalTheme.FrameInput(_civilizationSearch), 0, 1);
        rail.Controls.Add(_civilizations, 0, 2);
        rail.Controls.Add(MakeLabel("EDIT CATEGORY", 10, true), 0, 3);
        rail.Controls.Add(_categories, 0, 4);
        _categories.Items.AddRange(_navigation);
        workspace.Controls.Add(rail, 0, 0);
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244));
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _body.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _body.Controls.Add(_entityPanel, 0, 0);
        _body.SetRowSpan(_entityPanel, 2);
        _body.Controls.Add(_detail, 1, 0);
        _body.Controls.Add(_modelHost, 2, 0);
        _body.SizeChanged += (_, _) => ArrangeModelPreview();
        DpiChangedAfterParent += (_, _) => ArrangeModelPreview();
        workspace.Controls.Add(_body, 1, 0);
        layout.Controls.Add(workspace, 0, 1);

        var roster = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        roster.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        roster.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        roster.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        roster.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        roster.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        roster.Controls.Add(_entityHeading, 0, 0);
        MedievalTheme.StyleTextInput(_entitySearch);
        roster.Controls.Add(MedievalTheme.FrameInput(_entitySearch), 0, 1);
        roster.Controls.Add(_entities, 0, 2);
        var addRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        _catalogButton = MakeButton("From catalog", PickCatalogItems, "Add entries from game catalog");
        _catalogButton.Width = 118;
        addRow.Controls.Add(_catalogButton);
        addRow.Controls.Add(MakeButton("Add draft", AddDraft, "Add draft roster entry"));
        roster.Controls.Add(addRow, 0, 3);
        var editRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        _duplicate = MakeButton("Duplicate", DuplicateEntity, "Duplicate roster entry");
        _remove = MakeButton("Remove", RemoveEntity, "Remove roster entry");
        editRow.Controls.Add(_duplicate);
        editRow.Controls.Add(_remove);
        roster.Controls.Add(editRow, 0, 4);
        _entityPanel.Controls.Add(roster);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(16, 3, 12, 3), BackColor = MedievalTheme.Sidebar, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 385));
        _status.Dock = DockStyle.Fill;
        _status.AutoSize = false;
        _status.AutoEllipsis = true;
        footer.Controls.Add(_status, 0, 0);
        var footnote = MakeLabel("Current game values · Export over LAN in Game menu", 8.5f);
        footnote.ForeColor = MedievalTheme.TextSecondary;
        footnote.Dock = DockStyle.Fill;
        footnote.AutoSize = false;
        footnote.AutoEllipsis = true;
        footnote.TextAlign = ContentAlignment.MiddleRight;
        footer.Controls.Add(footnote, 1, 0);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);

        _projectName.TextChanged += (_, _) => { if (!_updating) Notify(); };
        _projectName.Validating += (_, args) =>
        {
            if (!_updating && !AgeProgressionPanel.HasNonValidatingFocus(this)) args.Cancel = !CommitPending(false);
        };
        _civilizationSearch.TextChanged += (_, _) => { if (!_updating && CommitPending(false)) { BindCivilizations(); BindEntities(); RenderDetail(); } };
        _entitySearch.TextChanged += (_, _) => { if (!_updating && CommitPending(false)) { BindEntities(); RenderDetail(); } };
        _civilizations.SelectedIndexChanged += (_, _) => CivilizationSelectionChanged();
        _entities.SelectedIndexChanged += (_, _) => EntitySelectionChanged();
        _categories.SelectedIndexChanged += (_, _) => CategorySelectionChanged();
        RefreshWorkspace();
        LoadInitialCatalog();
    }

    public void LoadGameCatalog()
    {
        if (!CommitPending()) return;
        var path = ResolveGamePath();
        ClearSourceState();
        if (string.IsNullOrWhiteSpace(path)) { SetStatus("Choose the EDMW game folder in Settings, then load the catalog."); return; }
        try
        {
            _catalog = CivilizationGameCatalog.Load(path);
            _gameData = new CivilizationGameData(path, _catalog);
            _sourceRosters.Clear();
            _gameAssets = null;
            var identities = CivilizationGameCatalog.ImportCivilizations(path);
            _history.Change(project =>
            {
                project.GamePath = path;
                foreach (var imported in identities)
                {
                    var existing = project.Civilizations.FirstOrDefault(c => c.SourceCivilizationId == imported.SourceCivilizationId || c.Name.Equals(imported.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing is null) project.Civilizations.Add(imported);
                    else { existing.SourceCivilizationId = imported.SourceCivilizationId; existing.SourceRowIndex = imported.SourceRowIndex; }
                }
            });
            RefreshWorkspace();
            SetStatus($"Catalog ready · {_catalog.Items.Count:N0} entries.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            ClearSourceState();
            RefreshWorkspace();
            SetStatus($"Catalog could not be loaded: {ex.Message}");
        }
    }

    private string? ResolveGamePath() => File.Exists(Path.Combine(_history.Current.GamePath, "Data", "db", "dbobjects.dat"))
        ? _history.Current.GamePath : _locateGame();

    private void ClearSourceState()
    {
        _catalog = null;
        _gameData = null;
        _gameAssets = null;
        _sourceRosters.Clear();
    }

    private void ReadProjectSource()
    {
        ClearSourceState();
        var path = ResolveGamePath();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var catalog = CivilizationGameCatalog.Load(path);
            var data = new CivilizationGameData(path, catalog);
            _catalog = catalog;
            _gameData = data;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            ClearSourceState();
            SetStatus($"Current game values could not be loaded: {exception.Message}");
        }
    }

    public void SaveProject(bool saveAs = false)
    {
        if (!CommitPending()) return;
        var path = _projectPath;
        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            using var dialog = new SaveFileDialog { Title = "Save civilization project", Filter = "EDMW civilization project (*.edmwciv)|*.edmwciv", DefaultExt = "edmwciv", AddExtension = true, FileName = SafeFileName(_history.Current.Name) + ".edmwciv" };
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
            path = dialog.FileName;
        }
        try { SaveProjectFile(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { ShowError("Could not save the civilization project.", ex); }
    }

    public bool ConfirmClose()
    {
        if (!IsDirty) return true;
        var result = MedievalDialog.Show((IWin32Window?)FindForm() ?? this, "Save changes to this civilization project?", "Civilization project", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (result == DialogResult.Cancel) return false;
        if (result == DialogResult.No) return true;
        SaveProject();
        return !IsDirty;
    }

    public bool HandleShortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.S: SaveProject(); return true;
            case Keys.Control | Keys.Shift | Keys.S: SaveProject(true); return true;
            case Keys.Control | Keys.O: OpenProject(); return true;
            case Keys.Control | Keys.N: NewProject(); return true;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Control | Keys.Y:
            case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
            case Keys.Control | Keys.F: (IsEntityCategory && !_changesVisible ? _entitySearch : _civilizationSearch).Focus(); return true;
            default: return false;
        }
    }

    internal void SaveProjectFile(string path)
    {
        if (!CommitPending()) throw new InvalidDataException("Resolve invalid fields before saving.");
        _history.Current.Validate();
        _history.Current.Save(path);
        _projectPath = path;
        _history.MarkSaved();
        SetStatus($"Saved {Path.GetFileName(path)}.");
        Notify();
    }

    internal void OpenProjectFile(string path)
    {
        var project = CivilizationEditProject.Load(path);
        _history.Replace(project);
        _projectPath = path;
        _selectedCivilizationId = null;
        _selectedEntityId = null;
        _pending = false;
        ReadProjectSource();
        RefreshWorkspace();
        SetStatus($"Opened {Path.GetFileName(path)}.");
    }

    internal void SelectCivilization(string id)
    {
        if (!CommitPending()) return;
        _selectedCivilizationId = id;
        _selectedEntityId = null;
        RefreshWorkspace();
    }

    internal void SelectCategory(CivilizationCategory category)
    {
        if (!CommitPending()) return;
        _category = category;
        _changesVisible = false;
        _navigationIndex = (int)category;
        RefreshWorkspace();
    }

    internal void AddCatalogEntry(CivilizationCatalogItem item)
    {
        if (!CommitPending()) return;
        var civilization = CurrentCivilization;
        if (civilization is null) return;
        var entity = new CivilizationEntityDraft { Id = Guid.NewGuid().ToString("N"), Name = item.Name, Category = item.Category, SourceObjectId = item.SourceId };
        _history.Change(project => project.Civilizations.Single(c => c.Id == civilization.Id).Entities.Add(entity));
        _category = item.Category;
        _changesVisible = false;
        _navigationIndex = (int)_category;
        _selectedEntityId = entity.Id;
        _updating = true;
        _entitySearch.Text = "";
        _updating = false;
        RefreshWorkspace();
        SetStatus($"Added {item.Name} to {civilization.Name}'s project roster.");
    }

    internal void SetFieldForVerification(string key, string value)
    {
        var binding = _bindings.Single(b => b.Definition?.Key == key);
        SetInputValue(binding.Input, value);
        _pending = true;
        if (!CommitPending(false)) throw new InvalidDataException($"Invalid field: {key}");
    }

    internal IReadOnlyList<CivilizationEntityDraft> DisplayedEntriesForVerification => CurrentCivilization is { } civilization
        ? DisplayEntities(civilization).Select(entity => entity.Clone()).ToArray() : [];
    internal IReadOnlyList<string> EntityListNamesForVerification => _entities.Items.Cast<Item>().Select(item => item.ToString()).ToArray();
    internal IReadOnlyList<string> CivilizationListNamesForVerification => _civilizations.Items.Cast<Item>().Select(item => item.ToString()).ToArray();
    internal static Control CreateCatalogSelectionForVerification(IEnumerable<string> names)
    {
        var cards = new EditorSelectionCards("Game catalog entries", "Select catalog item: ", 64) { MultiSelect = true };
        cards.Items.AddRange(names.Cast<object>());
        return cards;
    }
    internal static IReadOnlyList<string> CatalogSelectionForVerification(Control control) => control is EditorSelectionCards cards
        ? cards.SelectedItems.Select(item => item.ToString() ?? "").ToArray() : throw new ArgumentException("Expected catalog selection cards.", nameof(control));
    internal bool IsFieldWritableForVerification(string key) => _bindings.Single(binding => binding.Definition?.Key == key).Writable;
    internal string GetFieldForVerification(string key) => GetInputValue(_bindings.Single(binding => binding.Definition?.Key == key).Input);
    internal void SelectEntityForVerification(string id)
    {
        if (!CommitPending(false)) throw new InvalidDataException("Resolve pending fields before selecting an item.");
        _selectedEntityId = id;
        BindEntities();
        RenderDetail();
    }
    internal void ResetCategoryForVerification() => ResetCategory();
    internal AgeProgressionPanel ProgressionPanelForVerification
    {
        get
        {
            if (_category != CivilizationCategory.Ages || _progressionPanel is null)
                throw new InvalidOperationException("Select Ages for a civilization with game progression before accessing its inline editor.");
            ApplyExpandedSection("Progression");
            return _progressionPanel;
        }
    }
    internal AgeProgressionDialog CreateAgeProgressionForVerification()
    {
        if (!CommitPending(false) || _gameData is null || CurrentCivilization is not { } civilization)
            throw new InvalidOperationException("Load game values and select a civilization before editing progression.");
        return new AgeProgressionDialog(_gameData, civilization);
    }
    internal void ApplyAgeProgressionForVerification(AgeProgressionDialog dialog)
    {
        if (dialog.EditedCivilization is null && !dialog.ApplyForVerification())
            throw new InvalidDataException("The progression edits contain invalid fields.");
        ApplyAgeProgression(dialog);
    }
    internal void ResetFieldForVerification(string key) => ResetBinding(_bindings.Single(binding => binding.Definition?.Key == key));
    internal void SelectGameModelForVerification(string name)
    {
        if (CurrentCivilization is not { } civilization || CurrentEntity is not { } entity)
            throw new InvalidOperationException("Select a game item before choosing a model.");
        AttachGameModel(civilization.Id, entity.Id, name);
    }

    private bool IsEntityCategory => _category is CivilizationCategory.Units or CivilizationCategory.Buildings or CivilizationCategory.Technologies or CivilizationCategory.Abilities;
    private CivilizationDraft? CurrentCivilization => _history.Current.Civilizations.FirstOrDefault(c => c.Id == _selectedCivilizationId);
    private CivilizationEntityDraft? CurrentEntity => CurrentCivilization is { } civilization
        ? DisplayEntities(civilization).FirstOrDefault(entity => entity.Id == _selectedEntityId && entity.Category == _category) : null;

    private IReadOnlyList<CivilizationEntityDraft> SourceRoster(CivilizationDraft civilization)
    {
        if (_gameData is null) return [];
        var key = (civilization.Id, _category);
        if (!_sourceRosters.TryGetValue(key, out var roster))
        {
            roster = _gameData.GetRoster(civilization, _category);
            _sourceRosters.Add(key, roster);
        }
        return roster;
    }

    private IEnumerable<CivilizationEntityDraft> DisplayEntities(CivilizationDraft civilization)
    {
        var drafts = civilization.Entities.Where(entity => entity.Category == _category).ToArray();
        return drafts.Concat(SourceRoster(civilization).Where(source =>
            !drafts.Any(draft => draft.Id == source.Id)));
    }

    private CivilizationEntityDraft MaterializeEntity(CivilizationEditProject project, string civilizationId, string entityId)
    {
        var civilization = project.Civilizations.Single(civ => civ.Id == civilizationId);
        var existing = civilization.Entities.FirstOrDefault(entity => entity.Id == entityId);
        if (existing is not null) return existing;
        var source = _sourceRosters.Values.SelectMany(roster => roster).Single(entity => entity.Id == entityId).Clone();
        civilization.Entities.Add(source);
        return source;
    }

    private void RemoveEmptySourceEntries(CivilizationEditProject project)
    {
        foreach (var civilization in project.Civilizations)
            civilization.Entities.RemoveAll(entity => entity.Properties.Count == 0 &&
                _sourceRosters.Values.SelectMany(roster => roster).Any(source =>
                    source.Id == entity.Id && source.Name == entity.Name));
    }

    private static void RemoveUnusedModels(CivilizationEditProject project)
    {
        var referenced = project.Civilizations.SelectMany(civilization => civilization.Entities)
            .SelectMany(entity => entity.Properties.Where(pair => pair.Key.EndsWith(".modelAsset", StringComparison.Ordinal)))
            .Select(pair => pair.Value).ToHashSet(StringComparer.Ordinal);
        project.ModelAssets.RemoveAll(asset => !referenced.Contains(asset.Id));
    }

    private void NewProject()
    {
        if (!ConfirmClose()) return;
        _history.Replace(CivilizationEditProject.CreateDefault());
        _projectPath = null;
        ClearSourceState();
        _selectedCivilizationId = null;
        _selectedEntityId = null;
        _pending = false;
        RefreshWorkspace();
        LoadInitialCatalog();
        SetStatus("New civilization project. Choose a civilization and category to edit.");
    }

    private void LoadInitialCatalog()
    {
        LoadGameCatalog();
        if (_catalog is null) return;
        // Reading source identities establishes the new draft's baseline, not a user edit.
        _history.Replace(_history.Current);
        Notify();
    }

    private void OpenProject()
    {
        if (!ConfirmClose()) return;
        using var dialog = new OpenFileDialog { Title = "Open civilization project", Filter = "EDMW civilization project (*.edmwciv)|*.edmwciv|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        try { OpenProjectFile(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        { ShowError("Could not open the civilization project.", ex); }
    }

    private void Undo()
    {
        if (!CommitPending()) return;
        if (!_history.Undo()) return;
        RefreshWorkspace();
        SetStatus("Undid the last civilization change.");
    }

    private void Redo()
    {
        if (!CommitPending()) return;
        if (!_history.Redo()) return;
        RefreshWorkspace();
        SetStatus("Redid the civilization change.");
    }

    private void CivilizationSelectionChanged()
    {
        if (_updating) return;
        var targetId = (_civilizations.SelectedItem as Item)?.Id;
        if (!CommitPending()) { RestoreSelection(_civilizations, _selectedCivilizationId); return; }
        _selectedCivilizationId = targetId;
        _selectedEntityId = null;
        RestoreSelection(_civilizations, _selectedCivilizationId);
        BindEntities();
        RenderDetail();
    }

    private void EntitySelectionChanged()
    {
        if (_updating) return;
        var targetId = (_entities.SelectedItem as Item)?.Id;
        if (!CommitPending()) { RestoreSelection(_entities, _selectedEntityId); return; }
        _selectedEntityId = targetId;
        RestoreSelection(_entities, _selectedEntityId);
        RenderDetail();
    }

    private void CategorySelectionChanged()
    {
        if (_updating || _categories.SelectedIndex < 0) return;
        if (!CommitPending()) { _updating = true; _categories.SelectedIndex = _navigationIndex; _updating = false; return; }
        _navigationIndex = _categories.SelectedIndex;
        _changesVisible = _navigationIndex == 9;
        if (!_changesVisible) _category = (CivilizationCategory)_navigationIndex;
        _selectedEntityId = null;
        BindEntities();
        RenderDetail();
    }

    private void RefreshWorkspace()
    {
        _updating = true;
        _projectName.Text = _history.Current.Name;
        _shownProjectName = _projectName.Text;
        _categories.SelectedIndex = _navigationIndex;
        _updating = false;
        BindCivilizations();
        BindEntities();
        RenderDetail();
        Notify();
    }

    private void BindCivilizations()
    {
        _updating = true;
        _civilizations.BeginUpdate();
        _civilizations.Items.Clear();
        foreach (var civilization in _history.Current.Civilizations.Where(c =>
            DisplayName(c).Contains(_civilizationSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains(_civilizationSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            _civilizations.Items.Add(new Item(civilization.Id, DisplayName(civilization)));
        SelectItem(_civilizations, _selectedCivilizationId);
        _selectedCivilizationId = (_civilizations.SelectedItem as Item)?.Id;
        if (_civilizations.Items.Count > 0) _civilizations.TopIndex = 0;
        _civilizations.EndUpdate();
        _updating = false;
    }

    private void BindEntities()
    {
        _updating = true;
        _entities.BeginUpdate();
        _entities.Items.Clear();
        if (CurrentCivilization is { } civilization)
            foreach (var entity in DisplayEntities(civilization).Where(e =>
                DisplayName(e).Contains(_entitySearch.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
                e.Name.Contains(_entitySearch.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase))
                _entities.Items.Add(new Item(entity.Id, DisplayName(entity),
                    !HasSource(entity) ? "Draft item" : civilization.Entities.Any(entry => entry.Id == entity.Id && entry.Properties.Count > 0) ? "Edited in project" : "Game values"));
        SelectItem(_entities, _selectedEntityId);
        _selectedEntityId = (_entities.SelectedItem as Item)?.Id;
        _entities.EndUpdate();
        _entityHeading.Text = $"{_category.ToString().ToUpperInvariant()} · {_entities.Items.Count}";
        UpdateEntryActions();
        _catalogButton.Visible = false;
        _updating = false;
    }

    private void UpdateEntryActions()
    {
        var selected = CurrentEntity;
        _duplicate.Enabled = selected is not null;
        var hasSource = selected is not null && HasSource(selected);
        _remove.Text = hasSource ? "Clear edits" : "Remove";
        _remove.AccessibleName = hasSource ? "Clear selected item's project edits" : "Remove draft item";
        _remove.Enabled = selected is not null && (!hasSource || CurrentCivilization!.Entities.Any(entry => entry.Id == selected.Id));
    }

    private void RenderDetail()
    {
        _updating = true;
        _errors.Clear();
        _bindings.Clear();
        _sections.Clear();
        if (_progressionPanel is not null)
        {
            _progressionViewStates[_progressionPanel.CivilizationId] = _progressionPanel.CaptureViewState();
            _progressionPanel.CivilizationChanged -= ApplyInlineAgeProgression;
        }
        _progressionPanel = null;
        _pending = false;
        _detail.SuspendLayout();
        foreach (Control control in _detail.Controls.Cast<Control>().ToArray()) control.Dispose();
        _detail.Controls.Clear();
        _detail.AutoScrollPosition = Point.Empty;
        _entityPanel.Visible = IsEntityCategory && !_changesVisible;
        _body.ColumnStyles[0].Width = _entityPanel.Visible ? 244 : 0;
        RefreshModelPreview();
        var form = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Top, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = MedievalTheme.Surface, MaximumSize = _category == CivilizationCategory.Ages ? Size.Empty : new Size(720, 0), AccessibleName = "Civilization editor form" };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _detail.Controls.Add(form);
        void Add(Control control) { control.Dock = DockStyle.Top; form.Controls.Add(control); }
        if (_changesVisible)
        {
            Add(Heading("Project changes"));
            Add(Hint("All overrides and roster entries in this project."));
            var summary = new TextBox { Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Height = Math.Max(420, _detail.ClientSize.Height - 100), Text = BuildChangeSummary(), AccessibleName = "Civilization project change summary" };
            MedievalTheme.StyleTextInput(summary);
            var summaryFrame = MedievalTheme.FrameInput(summary);
            summaryFrame.Height = summary.Height + 4;
            Add(summaryFrame);
        }
        else if (CurrentCivilization is not { } civilization)
        {
            Add(Heading("Choose a civilization"));
            Add(Hint("Choose a civilization on the left."));
        }
        else if (IsEntityCategory && CurrentEntity is not { })
        {
            Add(Heading($"{civilization.Name} · {_category}"));
            var rosterHint = Hint("No verified game items are available in this category.");
            _tips.SetToolTip(rosterHint, "Game items load automatically when their civilization ownership is mapped. Draft entries can be saved while additional game mappings are developed.");
            Add(rosterHint);
            var add = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 14, 0, 10) };
            add.Controls.Add(MakeButton("Add draft entry", AddDraft, "Add draft roster entry", 145));
            Add(add);
        }
        else
        {
            var entity = IsEntityCategory ? CurrentEntity : null;
            var heading = Heading(entity is null ? $"{DisplayName(civilization)} · {_category}" : DisplayName(entity));
            _tips.SetToolTip(heading, entity is null ? $"Source civilization ID: {civilization.SourceCivilizationId}; source table row: {civilization.SourceRowIndex}" : $"{civilization.Name} · {_category}\n{(entity.SourcePowerId is int powerId ? $"Source civilization power: {powerId}" : entity.SourceObjectId is int id ? $"Source catalog row: {id}" : entity.SourceTechnologyId is int technologyId ? $"Source technology ID: {technologyId}" : "Draft entry without a source definition")}");
            Add(heading);
            Add(Hint(entity is null
                ? "Edit project values, then Export over LAN to load them into the game."
                : $"{civilization.Name} · {_category} · Current game values"));
            if (_category == CivilizationCategory.Overview || entity is not null)
                Add(BuildNameRow(civilization, entity));
            var currentValues = entity is null ? _gameData?.GetValues(civilization) : _gameData?.GetValues(entity);
            if (_gameData is not null && entity is not null && HasSource(entity))
            {
                var effective = currentValues!.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                effective["available"] = SourceRoster(civilization).Any(source =>
                    entity.SourcePowerId is not null ? source.SourcePowerId == entity.SourcePowerId :
                    entity.SourceObjectId is not null ? source.SourceObjectId == entity.SourceObjectId :
                    source.SourceTechnologyId == entity.SourceTechnologyId) ? "true" : "false";
                if (entity.SourceObjectId is not null && int.TryParse(effective.GetValueOrDefault(entity.Category == CivilizationCategory.Units ? "unit.graphicsId" : "building.graphicsId"), out var graphics))
                    if (_gameAssets?.FindModelName(graphics) is { } modelName)
                        effective[entity.Category == CivilizationCategory.Units ? "unit.model" : "building.model"] = modelName;
                currentValues = effective;
            }
            var displayValues = currentValues?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? [];
            if (_category == CivilizationCategory.Ages && _gameData is not null && BuildProgressionPreview(civilization, displayValues))
            {
                currentValues = displayValues.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                Add(Hint("Project progression preview · These changes load into the game when you Export over LAN."));
            }
            foreach (var authored in entity?.Properties ?? civilization.Properties)
                displayValues.TryAdd(authored.Key, authored.Value);
            var definitions = CivilizationSchema.GetFields(_category, displayValues).ToDictionary(field => field.Key, StringComparer.Ordinal);
            if (entity is not null && _gameData is not null)
                foreach (var field in _gameData.GetEffectFields(entity)) definitions[field.Key] = field;
            var fields = definitions.Values
                .Where(field => !field.Key.EndsWith(".modelAsset", StringComparison.Ordinal))
                .Where(field => !field.Key.EndsWith(".progressionIncluded", StringComparison.Ordinal) && !field.Key.EndsWith(".progressionProducers", StringComparison.Ordinal))
                .Where(field => _showUnmappedFields ||
                    (entity is not null && !HasSource(entity)) ||
                    (entity?.Properties ?? civilization.Properties).ContainsKey(field.Key) ||
                    currentValues?.ContainsKey(field.Key) == true ||
                    (entity is not null ? _gameData?.CanWrite(entity, field.Key) == true : _gameData?.CanWrite(_category, field.Key, false) == true) ||
                    field.Key.EndsWith(".notes", StringComparison.Ordinal)).ToArray();
            if (fields.All(field => field.Key.EndsWith(".notes", StringComparison.Ordinal)))
                Add(Hint("Game editing for this category is not available yet."));
            foreach (var group in fields.GroupBy(f => f.Section))
            {
                var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Margin = Padding.Empty, Padding = new Padding(10, 6, 6, 8), BackColor = MedievalTheme.Surface, MaximumSize = _category == CivilizationCategory.Ages && group.Key == "Progression" ? Size.Empty : new Size(680, 0), AccessibleName = "Civilization fields: " + group.Key };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
                var row = 0;
                if (_category == CivilizationCategory.Ages && group.Key == "Progression")
                {
                    if (_gameData is not null && civilization.SourceRowIndex is not null)
                    {
                        _progressionPanel = new AgeProgressionPanel(_gameData, civilization)
                        {
                            Dock = DockStyle.Fill,
                            Height = Math.Max(500, _detail.ClientSize.Height - 245)
                        };
                        _progressionPanel.CivilizationChanged += ApplyInlineAgeProgression;
                        _progressionPanel.PendingChanged += (_, _) => Notify();
                        if (_progressionViewStates.TryGetValue(civilization.Id, out var progressionState)) _progressionPanel.RestoreViewState(progressionState);
                        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, _progressionPanel.Height));
                        grid.Controls.Add(_progressionPanel, 0, row++);
                        grid.SetColumnSpan(_progressionPanel, 3);
                    }
                    else
                    {
                        var unavailable = Hint("Load the game and select a mapped civilization to edit its progression.");
                        grid.Controls.Add(unavailable, 0, row++);
                        grid.SetColumnSpan(unavailable, 3);
                    }
                }
                foreach (var definition in group)
                {
                    if (_category == CivilizationCategory.Ages && group.Key == "Progression") continue;
                    var properties = entity?.Properties ?? civilization.Properties;
                    properties.TryGetValue(definition.Key, out var authoredValue);
                    var gameValue = currentValues?.GetValueOrDefault(definition.Key);
                    var value = string.IsNullOrWhiteSpace(authoredValue) ? gameValue ?? "" : authoredValue;
                    var input = CreateFieldInput(definition, value);
                    var isNote = definition.Key.EndsWith(".notes", StringComparison.Ordinal);
                    var writable = input is not NamedAgeSetInput && (isNote || (entity is not null && !HasSource(entity)) ||
                        (entity is not null ? _gameData?.CanWrite(entity, definition.Key) == true : _gameData?.CanWrite(_category, definition.Key, false) == true));
                    if (!writable)
                    {
                        if (input is TextBox text) { text.ReadOnly = true; text.PlaceholderText = gameValue is null ? "Not mapped" : "Current game value"; }
                        else if (input is not NamedAgeSetInput) input.Enabled = false;
                    }
                    var label = MakeLabel(definition.Label, 9);
                    label.Dock = DockStyle.Fill;
                    label.AutoSize = false;
                    label.TextAlign = ContentAlignment.MiddleLeft;
                    label.Margin = new Padding(0, 0, 10, 0);
                    var binding = new FieldBinding(civilization.Id, entity?.Id, definition, input, GetInputValue(input), false)
                    { GameValue = gameValue, HadOverride = !string.IsNullOrWhiteSpace(authoredValue) && !SameValue(definition, value, gameValue), Writable = writable };
                    _bindings.Add(binding);
                    var reset = MakeButton("↶", () => ResetBinding(binding), $"Inherit {definition.Label}", 29);
                    reset.Height = 28;
                    reset.CausesValidation = false;
                    reset.Margin = new Padding(5, 3, 0, 3);
                    reset.Visible = HasFieldOverride(binding);
                    binding.ResetButton = reset;
                    _tips.SetToolTip(reset, "Reset this property to its current game value.");
                    _tips.SetToolTip(input, !writable
                        ? gameValue is null ? "This property has no verified game-data mapping yet." : "Current game value; this property is read-only."
                        : definition.Help ?? "Current game value. Editing this field creates an override.");
                    grid.RowStyles.Add(input is MedievalChoiceSelector ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, definition.Kind == CivilizationFieldKind.Multiline ? 88 : 38));
                    grid.Controls.Add(label, 0, row);
                    var frame = input is MedievalChoiceSelector ? input : MedievalTheme.FrameInput(input);
                    frame.Margin = new Padding(0, 3, 0, 3);
                    grid.Controls.Add(frame, 1, row);
                    grid.Controls.Add(reset, 2, row++);
                }
                var sectionName = group.Key;
                var sectionHeader = MakeButton("", () => ToggleSection(sectionName), $"{sectionName} properties");
                sectionHeader.Dock = DockStyle.Fill;
                sectionHeader.Height = 33;
                sectionHeader.TextAlign = ContentAlignment.MiddleLeft;
                sectionHeader.Padding = new Padding(10, 0, 0, 0);
                sectionHeader.Margin = Padding.Empty;
                sectionHeader.BackColor = MedievalTheme.Sidebar;
                sectionHeader.ForeColor = MedievalTheme.Gold;
                sectionHeader.FlatAppearance.BorderColor = MedievalTheme.Border;
                sectionHeader.FlatAppearance.BorderSize = 1;
                sectionHeader.Font = MedievalTheme.UiFont(9, FontStyle.Bold);
                var section = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 7, 0, 0), Padding = Padding.Empty };
                section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                section.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
                section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                section.Controls.Add(sectionHeader, 0, 0);
                grid.Dock = DockStyle.Top;
                section.Controls.Add(grid, 0, 1);
                _sections.Add(sectionName, new PropertySection(sectionName, sectionHeader, grid));
                Add(section);
            }
            ApplyExpandedSection(RememberedSection(fields));
            var actionRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 16, 0, 8) };
            var resetCategory = MakeButton("Reset category to game", ResetCategory, "Reset this category's field overrides", 188);
            resetCategory.CausesValidation = false;
            actionRow.Controls.Add(resetCategory);
            Add(actionRow);
        }
        RefreshSupportedAgeDisplays();
        _detail.ResumeLayout(true);
        _updating = false;
        Notify();
    }

    private Control BuildNameRow(CivilizationDraft civilization, CivilizationEntityDraft? entity)
    {
        var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 7, 0, 7), Height = 36, MaximumSize = new Size(640, 0), AccessibleName = "Civilization entry label" };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        row.Controls.Add(MakeLabel(entity is null ? "Civilization label" : "Entry label", 9), 0, 0);
        var input = new TextBox { Dock = DockStyle.Fill, Text = entity?.Name ?? civilization.Name, MaxLength = 200, AccessibleName = entity is null ? "Civilization name" : "Roster entry name" };
        _tips.SetToolTip(input, "The item's label in this project. The current game display name appears in Identity.");
        MedievalTheme.StyleTextInput(input);
        HookInput(input);
        _bindings.Add(new FieldBinding(civilization.Id, entity?.Id, null, input, input.Text, true));
        row.Controls.Add(MedievalTheme.FrameInput(input), 1, 0);
        return row;
    }

    private bool BuildProgressionPreview(CivilizationDraft civilization, Dictionary<string, string> values)
    {
        if (_gameData is null || civilization.SourceRowIndex is null) return false;
        var items = _gameData.GetAgeProgressionCandidates(civilization);
        CivilizationEntityDraft? Edited(CivilizationEntityDraft source) => civilization.Entities.FirstOrDefault(entry => entry.Id == source.Id) ?? civilization.Entities.FirstOrDefault(entry =>
            entry.Category == source.Category && (source.SourceObjectId is int objectId ? entry.SourceObjectId == objectId :
                source.SourceTechnologyId is int technologyId && entry.SourceObjectId is null && entry.SourceTechnologyId == technologyId));
        var sourceValues = items.ToDictionary(item => item.Entity.Id, item => _gameData.GetValues(item.Entity), StringComparer.Ordinal);
        string Prefix(CivilizationEntityDraft source) => source.Category switch { CivilizationCategory.Units => "unit", CivilizationCategory.Buildings => "building", _ => "technology" };
        string Effective(CivilizationAgeProgressionItem item, string key) => Edited(item.Entity)?.Properties.GetValueOrDefault(key) ?? sourceValues[item.Entity.Id].GetValueOrDefault(key, "");
        bool Included(CivilizationAgeProgressionItem item) => bool.TryParse(Edited(item.Entity)?.Properties.GetValueOrDefault(item.MembershipKey), out var included) ? included : item.IncludedByDefault;
        var hasItemEdits = items.Any(item => Edited(item.Entity)?.Properties.Keys.Any(key =>
            key.EndsWith(".initialAge", StringComparison.Ordinal) || key.EndsWith(".finalAge", StringComparison.Ordinal) || key.EndsWith(".progressionIncluded", StringComparison.Ordinal) ||
            item.Kind == "Age advance" && AgeProgressionDialog.EditableFields(_gameData, item.Entity).Any(field => field.Key == key)) == true);
        if (!hasItemEdits && !civilization.Properties.ContainsKey("age.initialAge") && !civilization.Properties.ContainsKey("age.finalAge")) return false;
        int Bound(string key, int fallback) => int.TryParse(civilization.Properties.GetValueOrDefault(key) ?? values.GetValueOrDefault(key), out var value) ? value : fallback;
        var firstAge = Bound("age.initialAge", 0);
        var finalAge = Bound("age.finalAge", 100);
        var rosters = new List<string>();
        var unlocks = new List<string>();
        HashSet<string> previousResearch = [];
        for (var age = firstAge; age <= finalAge; age++)
        {
            var available = items.Where(item =>
            {
                var prefix = Prefix(item.Entity);
                return Included(item) && Effective(item, "available") != "false" &&
                    int.TryParse(Effective(item, prefix + ".initialAge"), out var lower) && int.TryParse(Effective(item, prefix + ".finalAge"), out var upper) && lower <= age && upper >= age;
            }).ToArray();
            var research = available.Where(item => item.Kind == "Technology").ToArray();
            rosters.Add($"{_gameData.GetAgeName(age)}:\r\nUnits: {string.Join(", ", available.Where(item => item.Kind == "Unit").Select(item => item.DisplayName))}\r\n" +
                $"Buildings: {string.Join(", ", available.Where(item => item.Kind == "Building").Select(item => item.DisplayName))}\r\n" +
                $"Research: {string.Join(", ", research.Select(item => item.DisplayName))}");
            var newResearch = research.Where(item => !previousResearch.Contains(item.Entity.Id)).ToArray();
            if (newResearch.Length > 0) unlocks.Add($"{_gameData.GetAgeName(age)} — {(age == firstAge ? "available at first supported age" : "newly available")}:\r\n" + string.Join("\r\n", newResearch.Select(item => item.DisplayName)));
            previousResearch = research.Select(item => item.Entity.Id).ToHashSet(StringComparer.Ordinal);
        }
        values["age.rostersByAge"] = string.Join("\r\n\r\n", rosters);
        values["age.technologyUnlocks"] = string.Join("\r\n\r\n", unlocks);
        var advances = items.Where(item => item.Kind == "Age advance" && Included(item)).ToArray();
        if (advances.Length > 0)
            values["age.advanceCosts"] = string.Join("\r\n", advances.Select(item =>
                $"{item.DisplayName}: food {Effective(item, "technology.foodCost")}, wood {Effective(item, "technology.woodCost")}, gold {Effective(item, "technology.goldCost")}, stone {Effective(item, "technology.stoneCost")}; research parameter {Effective(item, "technology.productionCoefficient")}"));
        return true;
    }

    private Control CreateFieldInput(CivilizationFieldDefinition definition, string value)
    {
        Control input;
        if (definition.Kind == CivilizationFieldKind.Boolean && bool.TryParse(value, out var boolean))
            value = boolean ? "true" : "false";
        if (_gameData is not null && definition.Key is "age.supportedAges" or "overview.allowedAges")
            input = new NamedAgeSetInput(value, _gameData.GetAgeName) { Dock = DockStyle.Fill, AccessibleName = definition.Label, AccessibleDescription = "Supported age names. Edit the initial and final ages to change age support." };
        else if (_gameData is not null && (definition.Key.EndsWith(".initialAge", StringComparison.Ordinal) || definition.Key.EndsWith(".finalAge", StringComparison.Ordinal)))
        {
            var combo = new MedievalChoiceSelector { Dock = DockStyle.Top, AccessibleName = definition.Label, MinimumButtonWidth = 54, MaximumButtonWidth = 180, Tag = "age-choice" };
            combo.Items.Add(new ValueChoice("", "Game value"));
            foreach (var age in _gameData.GetAgeNames().Where(age => age.Id is >= 5 and <= 9 || age.Id == 14 && definition.Key.EndsWith(".finalAge", StringComparison.Ordinal)))
                combo.Items.Add(new ValueChoice(age.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), age.Name));
            if (int.TryParse(value, out var currentAge) && !combo.Items.Cast<ValueChoice>().Any(choice => choice.Value == value))
                combo.Items.Add(new ValueChoice(value, _gameData.GetAgeName(currentAge)));
            combo.SelectedIndex = combo.Items.Cast<ValueChoice>().ToList().FindIndex(choice => choice.Value == value);
            input = combo;
        }
        else if (definition.Kind is CivilizationFieldKind.Boolean or CivilizationFieldKind.Choice)
        {
            var combo = new MedievalChoiceSelector { Dock = DockStyle.Top, AccessibleName = definition.Label, MinimumButtonWidth = 60, MaximumButtonWidth = 220 };
            combo.Items.Add(new ValueChoice("", "Game value"));
            if (definition.Kind == CivilizationFieldKind.Boolean)
            {
                combo.Items.Add(new ValueChoice("true", definition.Key == "available" ? "Available" : "Yes"));
                combo.Items.Add(new ValueChoice("false", definition.Key == "available" ? "Unavailable" : "No"));
            }
            else foreach (var option in definition.Options ?? []) combo.Items.Add(new ValueChoice(option, option));
            if (!string.IsNullOrEmpty(value) && !combo.Items.Cast<ValueChoice>().Any(c => c.Value == value)) combo.Items.Add(new ValueChoice(value, value));
            SetInputValue(combo, value);
            input = combo;
        }
        else
        {
            input = new TextBox { Dock = DockStyle.Fill, Text = value, Multiline = definition.Kind == CivilizationFieldKind.Multiline, ScrollBars = definition.Kind == CivilizationFieldKind.Multiline ? ScrollBars.Vertical : ScrollBars.None, PlaceholderText = "Inherit game value", AccessibleName = definition.Label, AccessibleDescription = definition.Help ?? "Blank inherits the game value." };
        }
        if (input is not MedievalChoiceSelector) MedievalTheme.StyleTextInput(input);
        HookInput(input);
        return input;
    }

    private void HookInput(Control input)
    {
        if (input is MedievalChoiceSelector combo) combo.SelectedIndexChanged += (_, _) => InputChanged();
        else input.TextChanged += (_, _) => InputChanged();
        input.Validating += (_, args) =>
        {
            if (!_updating && !AgeProgressionPanel.HasNonValidatingFocus(this)) args.Cancel = !CommitPending(false);
        };
    }

    private void InputChanged()
    {
        if (_updating) return;
        _pending = true;
        foreach (var binding in _bindings)
            if (binding.ResetButton is { } reset) reset.Visible = HasFieldOverride(binding);
        RefreshSectionHeaders();
        RefreshSupportedAgeDisplays();
        Notify();
    }

    private string RememberedSection(IReadOnlyList<CivilizationFieldDefinition> fields)
    {
        if (_activeSections.TryGetValue(_category, out var remembered)) return remembered;
        var preferred = _category switch
        {
            CivilizationCategory.Overview => "Identity",
            CivilizationCategory.Units => "Combat",
            CivilizationCategory.Buildings => "Stats",
            CivilizationCategory.Technologies => "Effects",
            CivilizationCategory.Abilities => "Behavior",
            CivilizationCategory.Economy => "Starting resources",
            CivilizationCategory.Ages => "Progression",
            CivilizationCategory.Assets => "Civilization presentation",
            CivilizationCategory.AI => "Strategy",
            _ => ""
        };
        return fields.Any(f => f.Section == preferred) ? preferred : fields.FirstOrDefault()?.Section ?? "";
    }

    private void ToggleSection(string section)
    {
        var target = _activeSections.GetValueOrDefault(_category) == section ? "" : section;
        ApplyExpandedSection(target);
    }

    private void ApplyExpandedSection(string section)
    {
        var wasUpdating = _updating;
        _updating = true;
        _activeSections[_category] = section;
        _detail.SuspendLayout();
        foreach (var item in _sections.Values) item.Content.Visible = item.Name == section;
        RefreshSectionHeaders();
        _detail.ResumeLayout(true);
        _updating = wasUpdating;
    }

    private void RefreshSectionHeaders()
    {
        foreach (var item in _sections.Values)
        {
            var expanded = _activeSections.GetValueOrDefault(_category) == item.Name;
            var count = _bindings.Count(b => b.Definition?.Section == item.Name && HasFieldOverride(b));
            item.Header.Text = $"{(expanded ? "▾" : "▸")}  {item.Name.ToUpperInvariant()}{(count > 0 ? $"  ·  {count} {(count == 1 ? "override" : "overrides")}" : "")}";
            item.Header.AccessibleDescription = $"{(expanded ? "Collapse" : "Expand")} {item.Name} properties. {count} overrides.";
        }
    }

    private static bool HasFieldOverride(FieldBinding binding)
    {
        var value = GetInputValue(binding.Input);
        return binding.Definition is { } definition && !string.IsNullOrWhiteSpace(value)
            && !SameValue(definition, value, binding.GameValue);
    }

    private void RevealBinding(FieldBinding binding)
    {
        if (binding.Definition is { } definition && _sections.ContainsKey(definition.Section)) ApplyExpandedSection(definition.Section);
        _detail.ScrollControlIntoView(binding.Input);
        binding.Input.Focus();
    }

    private bool CommitPending(bool showError = true)
    {
        if (_updating) return true;
        var projectName = _projectName.Text.Trim();
        if (projectName.Length == 0)
        {
            _errors.SetError(_projectName, "Enter a project name.");
            if (showError) { SetStatus("Enter a project name before continuing."); _projectName.Focus(); }
            return false;
        }
        _errors.SetError(_projectName, "");
        if (_progressionPanel?.CommitPending() == false)
        {
            ApplyExpandedSection("Progression");
            SetStatus("Resolve the highlighted progression fields before continuing.");
            return false;
        }
        var updates = new List<(FieldBinding Binding, string Value)>();
        foreach (var binding in _bindings)
        {
            var value = GetInputValue(binding.Input);
            var changed = value != binding.Original;
            var error = !changed ? null : binding.IsName ? (string.IsNullOrWhiteSpace(value) ? "Enter a name." : null) : CivilizationSchema.ValidateValue(binding.Definition!, value);
            _errors.SetError(binding.Input, error ?? "");
            if (error is not null)
            {
                SetStatus($"{binding.Definition?.Label ?? "Name"}: {error}");
                if (showError) RevealBinding(binding);
                return false;
            }
            if (value != binding.Original) updates.Add((binding, value));
        }
        if (updates.Count > 0 || projectName != _history.Current.Name)
        {
            try
            {
                _history.Change(project =>
                {
                    project.Name = projectName;
                    foreach (var (binding, value) in updates)
                    {
                        var civilization = project.Civilizations.Single(c => c.Id == binding.CivilizationId);
                        var entity = binding.EntityId is null ? null : civilization.Entities.FirstOrDefault(entry => entry.Id == binding.EntityId);
                        var inherit = !binding.IsName && (string.IsNullOrWhiteSpace(value) || SameValue(binding.Definition!, value, binding.GameValue));
                        if (binding.EntityId is not null && entity is null)
                        {
                            if (inherit) continue;
                            entity = MaterializeEntity(project, civilization.Id, binding.EntityId);
                        }
                        if (binding.IsName) { if (entity is null) civilization.Name = value.Trim(); else entity.Name = value.Trim(); }
                        else
                        {
                            var properties = entity?.Properties ?? civilization.Properties;
                            if (inherit) properties.Remove(binding.Definition!.Key);
                            else properties[binding.Definition!.Key] = value;
                        }
                    }
                    RemoveEmptySourceEntries(project);
                });
            }
            catch (InvalidDataException exception)
            {
                _pending = true;
                var invalidInput = updates.LastOrDefault().Binding?.Input ?? _projectName;
                _errors.SetError(invalidInput, exception.Message);
                SetStatus(exception.Message);
                if (showError)
                {
                    if (updates.LastOrDefault().Binding is { } binding) RevealBinding(binding);
                    else invalidInput.Focus();
                }
                return false;
            }
            _updating = true;
            foreach (var (binding, value) in updates)
            {
                var normalized = !binding.IsName && string.IsNullOrWhiteSpace(value) ? binding.GameValue ?? "" : value;
                SetInputValue(binding.Input, normalized);
                binding.Original = normalized;
                binding.HadOverride = binding.IsName || !SameValue(binding.Definition!, normalized, binding.GameValue);
                if (binding.ResetButton is not null) binding.ResetButton.Visible = binding.HadOverride;
            }
            _updating = false;
            if (updates.Count > 0) UpdateListNames();
            UpdateEntryActions();
            if (updates.Any(update => update.Binding.Definition?.Key.EndsWith(".graphicsId", StringComparison.Ordinal) == true)) RefreshModelPreview();
        }
        _updating = true;
        _projectName.Text = projectName;
        _updating = false;
        _shownProjectName = projectName;
        _pending = false;
        RefreshSupportedAgeDisplays();
        if (updates.Any(update => update.Binding.Definition?.Key is "age.initialAge" or "age.finalAge" or "age.supportedAges" or "overview.allowedAges"))
            RefreshProgressionForAgeSupport();
        Notify();
        return true;
    }

    private void RefreshProgressionForAgeSupport()
    {
        if (_category != CivilizationCategory.Ages || _gameData is null || CurrentCivilization is not { } civilization ||
            _progressionPanel is not { } previous || previous.Parent is not TableLayoutPanel grid) return;
        var state = previous.CaptureViewState();
        var replacement = new AgeProgressionPanel(_gameData, civilization) { Dock = DockStyle.Fill, Height = previous.Height };
        replacement.RestoreViewState(state);
        replacement.CivilizationChanged += ApplyInlineAgeProgression;
        replacement.PendingChanged += (_, _) => Notify();
        var row = grid.GetRow(previous);
        grid.SuspendLayout();
        previous.CivilizationChanged -= ApplyInlineAgeProgression;
        grid.Controls.Remove(previous);
        _progressionPanel = replacement;
        grid.Controls.Add(replacement, 0, row);
        grid.SetColumnSpan(replacement, 3);
        previous.Dispose();
        grid.ResumeLayout(true);
        _progressionViewStates[civilization.Id] = state;
    }

    private void RefreshSupportedAgeDisplays()
    {
        if (_gameData is null || CurrentCivilization is not { } civilization ||
            civilization.Properties.ContainsKey("age.supportedAges") || civilization.Properties.ContainsKey("overview.allowedAges")) return;
        var displays = _bindings.Select(binding => binding.Input).OfType<NamedAgeSetInput>().ToArray();
        if (displays.Length == 0) return;
        var values = _gameData.GetValues(civilization);
        string? Bound(string key) => _bindings.FirstOrDefault(binding => binding.Definition?.Key == key) is { } field
            ? string.IsNullOrWhiteSpace(GetInputValue(field.Input)) ? field.GameValue : GetInputValue(field.Input)
            : civilization.Properties.GetValueOrDefault(key) ?? values.GetValueOrDefault(key);
        if (!int.TryParse(Bound("age.initialAge"), out var first) || !int.TryParse(Bound("age.finalAge"), out var last) || first > last) return;
        var text = _gameData.FormatAgeRange(first, last);
        foreach (var display in displays) display.ShowNamedRange(text);
    }

    private void ResetBinding(FieldBinding binding)
    {
        SetInputValue(binding.Input, binding.GameValue ?? "");
        _pending = true;
        CommitPending(false);
    }

    private void UpdateListNames()
    {
        _updating = true;
        _civilizations.BeginUpdate();
        _entities.BeginUpdate();
        try
        {
            for (var i = 0; i < _civilizations.Items.Count; i++)
            {
                var item = (Item)_civilizations.Items[i];
                if (_history.Current.Civilizations.FirstOrDefault(c => c.Id == item.Id) is { } civilization)
                    _civilizations.Items[i] = new Item(item.Id, DisplayName(civilization));
            }
            for (var i = 0; i < _entities.Items.Count; i++)
            {
                var item = (Item)_entities.Items[i];
                if (CurrentCivilization?.Entities.FirstOrDefault(e => e.Id == item.Id) is { } entity)
                    _entities.Items[i] = new Item(item.Id, DisplayName(entity), !HasSource(entity) ? "Draft item" : entity.Properties.Count > 0 ? "Edited in project" : "Game values");
            }
        }
        finally
        {
            _civilizations.EndUpdate();
            _entities.EndUpdate();
            _updating = false;
        }
    }

    private void ResetCategory()
    {
        _updating = true;
        foreach (var binding in _bindings.Where(binding => !binding.IsName)) SetInputValue(binding.Input, binding.Original);
        _errors.Clear();
        _pending = _bindings.Any(binding => GetInputValue(binding.Input) != binding.Original);
        _updating = false;
        _progressionPanel?.DiscardPending();
        if (!CommitPending()) return;
        var civilization = CurrentCivilization;
        if (civilization is null) return;
        var entityId = CurrentEntity?.Id;
        var keys = _bindings.Where(binding => binding.Definition is not null).Select(binding => binding.Definition!.Key).ToArray();
        var progressionItems = _category == CivilizationCategory.Ages ? _gameData?.GetAgeProgressionCandidates(civilization) : null;
        _history.Change(project =>
        {
            var draft = project.Civilizations.Single(c => c.Id == civilization.Id);
            var properties = entityId is null ? draft.Properties : draft.Entities.FirstOrDefault(entity => entity.Id == entityId)?.Properties;
            if (properties is null) return;
            foreach (var key in keys) properties.Remove(key);
            if (progressionItems is not null && _gameData is not null)
                foreach (var item in progressionItems)
                {
                    var edited = draft.Entities.FirstOrDefault(entry => entry.Id == item.Entity.Id) ?? draft.Entities.FirstOrDefault(entry =>
                        entry.Category == item.Entity.Category && (item.Entity.SourceObjectId is int objectId ? entry.SourceObjectId == objectId :
                            item.Entity.SourceTechnologyId is int technologyId && entry.SourceObjectId is null && entry.SourceTechnologyId == technologyId));
                    if (edited is null) continue;
                    var progressionKeys = AgeProgressionPanel.EditableFields(_gameData, item.Entity);
                    foreach (var field in progressionKeys) edited.Properties.Remove(field.Key);
                    edited.Properties.Remove(item.MembershipKey);
                    edited.Properties.Remove(item.ProducerKey);
                    if (edited.Properties.Count == 0 && edited.Name == item.Entity.Name) draft.Entities.Remove(edited);
                }
            RemoveEmptySourceEntries(project);
            RemoveUnusedModels(project);
        });
        RenderDetail();
        SetStatus("Category fields now inherit their game values.");
    }

    private void ApplyInlineAgeProgression(CivilizationDraft civilization)
    {
        _history.Change(project =>
        {
            var draft = project.Civilizations.Single(candidate => candidate.Id == civilization.Id);
            draft.Entities = civilization.Entities.Select(entity => entity.Clone()).ToList();
        });
        RefreshSectionHeaders();
        UpdateEntryActions();
        SetStatus("Updated civilization progression. Export over LAN to load the project into the game.");
    }

    private void ApplyAgeProgression(AgeProgressionDialog dialog)
    {
        if (!dialog.HasChanges || dialog.EditedCivilization is not { } civilization) return;
        _history.Change(project =>
        {
            var index = project.Civilizations.FindIndex(candidate => candidate.Id == civilization.Id);
            if (index < 0) throw new InvalidOperationException("The progression editor's civilization is no longer in this project.");
            project.Civilizations[index] = civilization.Clone();
        });
        RefreshWorkspace();
        SetStatus("Applied progression edits to the project. Export over LAN to load them into the game.");
    }

    private void AddDraft()
    {
        if (!CommitPending() || CurrentCivilization is not { } civilization) return;
        var stem = _category switch { CivilizationCategory.Units => "New unit", CivilizationCategory.Buildings => "New building", CivilizationCategory.Technologies => "New technology", _ => "New ability" };
        var name = stem;
        var number = 2;
        while (civilization.Entities.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = $"{stem} {number++}";
        var entity = new CivilizationEntityDraft { Id = Guid.NewGuid().ToString("N"), Name = name, Category = _category };
        _history.Change(project => project.Civilizations.Single(c => c.Id == civilization.Id).Entities.Add(entity));
        _selectedEntityId = entity.Id;
        _entitySearch.Text = "";
        BindEntities();
        RenderDetail();
        _bindings.FirstOrDefault(b => b.IsName)?.Input.Focus();
        SetStatus($"Added {name}. Edit its name and fields.");
    }

    private void DuplicateEntity()
    {
        if (!CommitPending() || CurrentCivilization is not { } civilization || CurrentEntity is not { } source) return;
        var entity = source.Clone();
        entity.Id = Guid.NewGuid().ToString("N");
        entity.Name = source.Name + " copy";
        _history.Change(project => project.Civilizations.Single(c => c.Id == civilization.Id).Entities.Add(entity));
        _selectedEntityId = entity.Id;
        BindEntities();
        RenderDetail();
        SetStatus($"Duplicated {source.Name}. Source copies edit the same game definition.");
    }

    private void RemoveEntity()
    {
        if (!CommitPending() || CurrentCivilization is not { } civilization || CurrentEntity is not { } entity) return;
        _history.Change(project =>
        {
            project.Civilizations.Single(c => c.Id == civilization.Id).Entities.RemoveAll(e => e.Id == entity.Id);
            RemoveUnusedModels(project);
        });
        _selectedEntityId = null;
        BindEntities();
        RenderDetail();
        SetStatus(HasSource(entity)
            ? $"Cleared project overrides for {entity.Name}." : $"Removed {entity.Name} from the project roster. Undo restores it.");
    }

    private void PickCatalogItems()
    {
        if (!CommitPending()) return;
        if (_catalog is null) LoadGameCatalog();
        if (_catalog is null || CurrentCivilization is null) return;
        using var dialog = new Form { Text = $"Add {_category.ToString().ToLowerInvariant()} from game catalog", StartPosition = FormStartPosition.CenterParent, Size = new Size(680, 650), MinimumSize = new Size(500, 400), BackColor = MedievalTheme.Sidebar, ForeColor = MedievalTheme.Text, Font = MedievalTheme.UiFont(), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        MedievalWindowChrome.Apply(dialog);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        var hint = Hint("Choose one or more item cards, then add them to this civilization.");
        _tips.SetToolTip(hint, "Names are read from the game catalog. Source references are preserved; stock stats are not inferred.");
        hint.Dock = DockStyle.Fill;
        layout.Controls.Add(hint, 0, 0);
        var search = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Search catalog…", AccessibleName = "Search game catalog" };
        MedievalTheme.StyleTextInput(search);
        layout.Controls.Add(MedievalTheme.FrameInput(search), 0, 1);
        var list = new EditorSelectionCards("Game catalog entries", "Select catalog item: ", 64)
        { MultiSelect = true, IconProvider = _ => ProgressionIcons.ForKind(_category.ToString()) };
        layout.Controls.Add(list, 0, 2);
        var choices = _catalog.Items.Where(i => i.Category == _category).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        void Filter()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var item in choices.Where(i => i.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase))) list.Items.Add(new CatalogChoice(item));
            list.EndUpdate();
        }
        Filter();
        search.TextChanged += (_, _) => Filter();
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        var cancel = MakeButton("Cancel", () => dialog.DialogResult = DialogResult.Cancel, "Cancel catalog selection");
        var add = MakeButton("Add selected", () => { if (list.SelectedItems.Count > 0) dialog.DialogResult = DialogResult.OK; }, "Add selected catalog entries", 130);
        actions.Controls.Add(cancel);
        actions.Controls.Add(add);
        layout.Controls.Add(actions, 0, 3);
        MedievalDialogFrame.Apply(dialog, layout);
        dialog.AcceptButton = add;
        dialog.CancelButton = cancel;
        list.DoubleClick += (_, _) => { if (list.SelectedItems.Count > 0) dialog.DialogResult = DialogResult.OK; };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        var selected = list.SelectedItems.Cast<CatalogChoice>().Select(c => c.Item).ToArray();
        var civilizationId = _selectedCivilizationId!;
        string? lastId = null;
        _history.Change(project =>
        {
            var civilization = project.Civilizations.Single(c => c.Id == civilizationId);
            foreach (var item in selected)
            {
                var entity = new CivilizationEntityDraft { Id = Guid.NewGuid().ToString("N"), Name = item.Name, Category = item.Category, SourceObjectId = item.SourceId };
                civilization.Entities.Add(entity);
                lastId = entity.Id;
            }
        });
        _selectedEntityId = lastId;
        _updating = true;
        _entitySearch.Text = "";
        _updating = false;
        BindEntities();
        RenderDetail();
        SetStatus($"Added {selected.Length} catalog entries to this civilization's project roster.");
    }

    private string BuildChangeSummary()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine(_history.Current.Name);
        text.AppendLine($"{_history.Current.Civilizations.Count} civilizations · {_history.Current.Civilizations.Sum(c => c.Entities.Count)} roster entries");
        text.AppendLine();
        foreach (var civilization in _history.Current.Civilizations)
        {
            text.AppendLine(civilization.Name.ToUpperInvariant());
            if (civilization.SourceCivilizationId is int id) text.AppendLine($"  Source civilization ID: {id}; table row: {civilization.SourceRowIndex}");
            foreach (var property in civilization.Properties.OrderBy(p => p.Key)) text.AppendLine($"  {FieldLabel(property.Key)}: {property.Value.Replace("\r", "").Replace("\n", " | ")}");
            foreach (var entity in civilization.Entities)
            {
                text.AppendLine($"  {entity.Category}: {entity.Name} ({(entity.SourceObjectId is int source ? $"catalog row {source}" : "draft")})");
                foreach (var property in entity.Properties.OrderBy(p => p.Key)) text.AppendLine($"    {FieldLabel(property.Key)}: {property.Value.Replace("\r", "").Replace("\n", " | ")}");
            }
            if (civilization.Properties.Count == 0 && civilization.Entities.Count == 0) text.AppendLine("  No field overrides or roster entries.");
            text.AppendLine();
        }
        return text.ToString();
    }

    private static string FieldLabel(string key) => Enum.GetValues<CivilizationCategory>().SelectMany(CivilizationSchema.GetFields).FirstOrDefault(f => f.Key == key)?.Label ?? key;

    private Button MakeActionMenu(string title, ToolStripItem[] items)
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new MedievalToolStripRenderer(), BackColor = MedievalTheme.Sidebar,
            ForeColor = MedievalTheme.Text, ShowImageMargin = false, Font = MedievalTheme.UiFont()
        };
        menu.Items.AddRange(items);
        var button = MakeButton(title + " ▾", () => { }, title + " actions", 88);
        button.ContextMenuStrip = menu;
        button.Click += (_, _) => menu.Show(button, new Point(0, button.Height));
        return button;
    }

    private static ToolStripMenuItem MakeAction(string text, Action action, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(text)
        {
            ForeColor = MedievalTheme.Text, BackColor = MedievalTheme.Sidebar,
            Font = MedievalTheme.UiFont(), ShortcutKeys = shortcut
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static bool SameValue(CivilizationFieldDefinition field, string value, string? gameValue)
    {
        if (gameValue is null) return string.IsNullOrWhiteSpace(value);
        if (field.Kind == CivilizationFieldKind.Boolean && bool.TryParse(value, out var currentBoolean) && bool.TryParse(gameValue, out var baselineBoolean))
            return currentBoolean == baselineBoolean;
        if (field.Kind == CivilizationFieldKind.Number &&
            double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var current) &&
            double.TryParse(gameValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var baseline)) return current == baseline;
        return value == gameValue;
    }

    private string DisplayName(CivilizationDraft civilization) => _gameData?.GetDisplayName(civilization) ?? civilization.Name;
    private string DisplayName(CivilizationEntityDraft entity) => _gameData?.GetDisplayName(entity) ?? entity.Name;
    private static bool HasSource(CivilizationEntityDraft entity) => entity.SourceObjectId is not null || entity.SourceTechnologyId is not null || entity.SourcePowerId is not null;

    private void ExportOverLan()
    {
        if (!CommitPending() || _sendOverLan is null) return;
        var path = ResolveGamePath();
        if (string.IsNullOrWhiteSpace(path)) { SetStatus("Choose the EDMW game folder in Settings first."); return; }
        try
        {
            var package = CivilizationLanLocalExport.Prepare(_history.Current, path);
            _sendOverLan(package);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException)
        {
            SetStatus(exception.Message);
            MedievalDialog.Show((IWin32Window?)FindForm() ?? this, exception.Message,
                "LAN export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshModelPreview()
    {
        foreach (Control control in _modelHost.Controls.Cast<Control>().ToArray()) control.Dispose();
        _modelHost.Controls.Clear();
        var entity = CurrentEntity;
        var visible = !_changesVisible && entity?.Category is CivilizationCategory.Units or CivilizationCategory.Buildings;
        _modelPreviewVisible = visible;
        _modelHost.Visible = visible;
        ArrangeModelPreview();
        if (!visible || entity is null || CurrentCivilization is not { } civilization) return;
        try
        {
            var path = ResolveGamePath();
            if (_gameAssets is null && !string.IsNullOrWhiteSpace(path)) _gameAssets = new CivilizationGameAssets(path);
            if (_catalog is null && !string.IsNullOrWhiteSpace(path)) _catalog = CivilizationGameCatalog.Load(path);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _gameAssets = null;
        }
        var prefix = entity.Category == CivilizationCategory.Units ? "unit" : "building";
        int? graphicsId = int.TryParse(entity.Properties.GetValueOrDefault(prefix + ".graphicsId"), out var changedGraphics)
            ? changedGraphics : _catalog?.Items.FirstOrDefault(item => item.SourceId == entity.SourceObjectId)?.GraphicsId;
        string? sourceModel = null;
        if (_gameAssets is not null)
        {
            if (entity.Properties.TryGetValue(prefix + ".model", out var specified) && !string.IsNullOrWhiteSpace(specified))
                sourceModel = _gameAssets.ModelNames.FirstOrDefault(name =>
                    name.Equals(specified, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileNameWithoutExtension(name.Replace('\\', Path.DirectorySeparatorChar))
                        .Equals(Path.GetFileNameWithoutExtension(specified.Replace('\\', Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase));
            else if (graphicsId is int graphics)
                sourceModel = _gameAssets.FindModelName(graphics);
        }
        var replacement = _history.Current.GetModelAsset(entity.Properties.GetValueOrDefault(prefix + ".modelAsset"));
        var civilizationId = civilization.Id;
        var entityId = entity.Id;
        var preview = new CivilizationModelPreviewPanel(_gameAssets, sourceModel, replacement,
            asset => AttachReplacementModel(civilizationId, entityId, asset),
            () =>
            {
                if (!CommitPending()) return;
                _history.Change(project =>
                {
                    project.Civilizations.Single(civ => civ.Id == civilizationId)
                        .Entities.FirstOrDefault(entry => entry.Id == entityId)?.Properties.Remove(prefix + ".modelAsset");
                    RemoveEmptySourceEntries(project);
                    RemoveUnusedModels(project);
                });
                RenderDetail();
                SetStatus("Restored the game model reference.");
            },
            name =>
            {
                AttachGameModel(civilizationId, entityId, name);
            }, graphicsId) { Dock = DockStyle.Fill };
        _modelHost.Controls.Add(preview);
    }

    private void ArrangeModelPreview()
    {
        // At smaller window sizes, keep the property fields readable and put the
        // model beneath them. The roster keeps its full height in either layout.
        if (_arrangingModelPreview || _body.ColumnStyles.Count < 3 || _body.RowStyles.Count < 2) return;
        var dpiScale = DeviceDpi / 96f;
        var stacked = _modelPreviewVisible && _body.ClientSize.Width < 1080 * dpiScale;
        var target = (_modelPreviewVisible, stacked, dpiScale);
        if (_modelLayout == target) return;
        _arrangingModelPreview = true;
        _body.SuspendLayout();
        try
        {
            _body.SetColumnSpan(_detail, stacked ? 2 : 1);
            _body.SetColumnSpan(_modelHost, stacked ? 2 : 1);
            _body.SetCellPosition(_modelHost, stacked ? new TableLayoutPanelCellPosition(1, 1) : new TableLayoutPanelCellPosition(2, 0));
            _body.ColumnStyles[2].Width = _modelPreviewVisible && !stacked ? 330 * dpiScale : 0;
            _body.RowStyles[1].Height = stacked ? 365 * dpiScale : 0;
            _modelLayout = target;
        }
        finally
        {
            _body.ResumeLayout(true);
            _arrangingModelPreview = false;
        }
    }

    private void AttachReplacementModel(string civilizationId, string entityId, CivilizationModelAsset asset)
    {
        if (!CommitPending()) return;
        _ = CivilizationModelReader.Read(asset.Data, asset.OriginalFileName);
        _history.Change(project =>
        {
            var entity = MaterializeEntity(project, civilizationId, entityId);
            project.ModelAssets.Add(asset.Clone());
            var key = entity.Category == CivilizationCategory.Units ? "unit.modelAsset" : "building.modelAsset";
            entity.Properties[key] = asset.Id;
            entity.Properties.Remove(entity.Category == CivilizationCategory.Units ? "unit.model" : "building.model");
            RemoveUnusedModels(project);
        });
        RenderDetail();
        SetStatus($"Uploaded {asset.OriginalFileName}. The file is stored inside this project.");
    }

    private void AttachGameModel(string civilizationId, string entityId, string name)
    {
        try
        {
            if (_gameAssets is null) throw new InvalidDataException("The game models are unavailable.");
            var bytes = _gameAssets.ReadModel(name);
            var fileName = Path.GetFileName(name.Replace('\\', Path.DirectorySeparatorChar));
            var asset = new CivilizationModelAsset
            {
                Name = Path.GetFileNameWithoutExtension(fileName), OriginalFileName = fileName,
                Format = "cem", MimeType = "application/octet-stream", FileLength = bytes.Length,
                Data = bytes, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            };
            asset.Validate();
            AttachReplacementModel(civilizationId, entityId, asset);
            SetStatus($"Selected {fileName}. The model is stored inside this project.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            ShowError("Could not select the game model.", exception);
        }
    }

    internal void UploadModelForVerification(string path)
    {
        if (CurrentCivilization is not { } civilization || CurrentEntity is not { } entity)
            throw new InvalidOperationException("Select a unit or building before uploading a replacement.");
        AttachReplacementModel(civilization.Id, entity.Id, CivilizationModelAsset.Import(path));
    }
    private void Notify() { _undo.Enabled = _history.CanUndo || _pending || _progressionPanel?.HasPendingChanges == true || _projectName.Text != _shownProjectName; _redo.Enabled = _history.CanRedo; StateChanged?.Invoke(this, EventArgs.Empty); }
    private void SetStatus(string text) { _status.Text = text; _tips.SetToolTip(_status, text); Notify(); }
    private void ShowError(string message, Exception ex) { SetStatus(message); MedievalDialog.Show((IWin32Window?)FindForm() ?? this, message + "\n\n" + ex.Message, "Civilization project", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    private static string SafeFileName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private void RestoreSelection(EditorSelectionCards list, string? id) { _updating = true; SelectItem(list, id); _updating = false; }
    private static void SelectItem(EditorSelectionCards list, string? id)
    {
        var index = list.Items.Cast<Item>().ToList().FindIndex(i => i.Id == id);
        list.SelectedIndex = index >= 0 ? index : list.Items.Count > 0 ? 0 : -1;
    }

    private static string GetInputValue(Control input) => input is NamedAgeSetInput ages ? ages.Value : input is MedievalChoiceSelector combo ? (combo.SelectedItem as ValueChoice)?.Value ?? "" : input is TextBox { Multiline: true } ? input.Text : input.Text.Trim();
    private static void SetInputValue(Control input, string value)
    {
        if (input is NamedAgeSetInput ages) ages.Value = value;
        else if (input is MedievalChoiceSelector combo)
        {
            var choices = combo.Items.Cast<ValueChoice>().ToList();
            if (choices.Any(choice => choice.Value == "true") && choices.Any(choice => choice.Value == "false") && bool.TryParse(value, out var boolean))
                value = boolean ? "true" : "false";
            if (!choices.Any(choice => choice.Value == value) && combo.Tag?.ToString() == "age-choice" && int.TryParse(value, out var age) && age is >= 0 and <= 100)
            {
                var knownAge = new ValueChoice(value, CivilizationAgeNames.GetName(age));
                combo.Items.Add(knownAge);
                choices.Add(knownAge);
            }
            combo.SelectedIndex = choices.FindIndex(choice => choice.Value == value);
        }
        else input.Text = value;
    }

    private static Button MakeButton(string text, Action action, string accessibleName, int width = 79)
    {
        var button = new Button { Text = text, Width = width, Height = 29, Margin = new Padding(0, 2, 6, 2), AccessibleName = accessibleName, UseMnemonic = false };
        MedievalTheme.StyleSecondaryButton(button);
        button.Click += (_, _) => action();
        return button;
    }

    private static Control ToolbarDivider() => new Panel
    {
        Width = 1, Height = 25, BackColor = MedievalTheme.Border,
        Margin = new Padding(3, 3, 9, 0), TabStop = false
    };

    private static Label MakeLabel(string text, float size, bool bold = false) => new() { Text = text, AutoSize = true, UseMnemonic = false, Font = MedievalTheme.UiFont(size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = bold ? MedievalTheme.GoldBright : MedievalTheme.TextMuted, Margin = new Padding(0, 5, 0, 4) };
    private static Label Heading(string text) => new() { Text = text, AutoSize = true, UseMnemonic = false, Font = MedievalTheme.DisplayFont(19), ForeColor = MedievalTheme.GoldBright, Margin = new Padding(0, 0, 0, 6) };
    private static Label Hint(string text) => new() { Text = text, AutoSize = true, UseMnemonic = false, Font = MedievalTheme.UiFont(9), ForeColor = MedievalTheme.TextSecondary, Margin = new Padding(0, 4, 0, 8), MaximumSize = new Size(1400, 0) };

    private sealed class EditorSelectionCards : UserControl
    {
        private const int PageSize = 64;
        private readonly FlowLayoutPanel _flow = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = MedievalTheme.Sidebar, Margin = Padding.Empty };
        private readonly TableLayoutPanel _paging = new() { Dock = DockStyle.Bottom, ColumnCount = 3, RowCount = 1, BackColor = MedievalTheme.Sidebar, Margin = Padding.Empty, Padding = new Padding(0, 5, 0, 0), Visible = false, AccessibleName = "Item page navigation" };
        private readonly Button _previousPage;
        private readonly Button _nextPage;
        private readonly Label _pageStatus;
        private readonly List<SelectionCard> _cards = [];
        private readonly HashSet<int> _selectedIndices = [];
        private readonly HashSet<int> _presentationUpdates = [];
        private readonly ToolTip _tips = new();
        private readonly string _accessiblePrefix;
        private int _selectedIndex = -1;
        private int _selectionAnchor = -1;
        private int _updateDepth;
        private bool _queued;
        private bool _sizingCards;
        private int _page;
        private float _layoutScale = 1f;
        private int _viewportHeight;

        private int PageStart => _page * PageSize;
        private float CardScale => Math.Max(_layoutScale, DeviceDpi / 96f);

        public EditorSelectionCards(string name, string prefix, int itemHeight)
        {
            AccessibleName = name;
            _accessiblePrefix = prefix;
            ItemHeight = itemHeight;
            Items = new SelectionCollection(this);
            Dock = DockStyle.Fill;
            Margin = new Padding(0, 7, 0, 7);
            BackColor = MedievalTheme.Sidebar;
            ForeColor = MedievalTheme.Text;
            Font = MedievalTheme.UiFont(9);
            AutoScaleMode = AutoScaleMode.Dpi;
            _paging.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
            _paging.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _paging.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
            _paging.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _previousPage = MakeButton("‹", () => ChangePage(-1), "Previous items page", 32);
            _previousPage.Name = "EditorCardsPreviousPage";
            _previousPage.Dock = DockStyle.Fill;
            _previousPage.Margin = Padding.Empty;
            _nextPage = MakeButton("›", () => ChangePage(1), "Next items page", 32);
            _nextPage.Name = "EditorCardsNextPage";
            _nextPage.Dock = DockStyle.Fill;
            _nextPage.Margin = Padding.Empty;
            _pageStatus = new Label { Name = "EditorCardsPageStatus", Dock = DockStyle.Fill,
                ForeColor = MedievalTheme.TextMuted, TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true, UseMnemonic = false, Margin = Padding.Empty, AccessibleName = "Item pages" };
            _paging.Controls.Add(_previousPage, 0, 0);
            _paging.Controls.Add(_pageStatus, 1, 0);
            _paging.Controls.Add(_nextPage, 2, 0);
            Controls.Add(_flow);
            Controls.Add(_paging);
            UpdatePaging();
            _flow.HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(_flow);
            _flow.Resize += (_, _) =>
            {
                var previousHeight = _viewportHeight;
                _viewportHeight = _flow.ClientSize.Height;
                SizeCards();
                var visibleIndex = _selectedIndex - PageStart;
                if (_viewportHeight < previousHeight && visibleIndex >= 0 && visibleIndex < _cards.Count)
                    _flow.ScrollControlIntoView(_cards[visibleIndex]);
            };
            _flow.Layout += (_, _) => SizeCards();
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        public SelectionCollection Items { get; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int ItemHeight { get; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<object, Image?>? IconProvider { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool MultiSelect { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public IReadOnlyList<object> SelectedItems => MultiSelect ? _selectedIndices.Where(index => index >= 0 && index < Items.Count).Order().Select(index => Items[index]).ToArray() : SelectedItem is { } selected ? [selected] : [];
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (value < -1 || value >= Items.Count) throw new ArgumentOutOfRangeException(nameof(value));
                if (value == _selectedIndex) { EnsureSelectionVisible(); return; }
                _selectedIndex = value;
                UpdateSelection();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                EnsureSelectionVisible();
            }
        }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public object? SelectedItem => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int TopIndex
        {
            get => PageStart;
            set
            {
                if (value < 0 || value >= Math.Max(1, Items.Count)) return;
                if (_page != value / PageSize) { _page = value / PageSize; Rebuild(); }
                _flow.AutoScrollPosition = Point.Empty;
            }
        }
        public event EventHandler? SelectedIndexChanged;
        public void BeginUpdate() => _updateDepth++;
        public void EndUpdate()
        {
            if (_updateDepth > 0) _updateDepth--;
            if (_updateDepth != 0) return;
            if (_queued) { Rebuild(); EnsureSelectionVisible(); }
            else
            {
                var updates = _presentationUpdates.ToArray();
                _presentationUpdates.Clear();
                foreach (var index in updates) UpdatePresentation(index);
            }
        }

        private void Rebuild()
        {
            if (_updateDepth > 0) { _queued = true; return; }
            _queued = false;
            _presentationUpdates.Clear();
            _flow.SuspendLayout();
            foreach (var card in _cards) card.Dispose();
            _cards.Clear();
            _flow.Controls.Clear();
            if (_selectedIndex >= Items.Count) _selectedIndex = -1;
            _selectedIndices.RemoveWhere(index => index < 0 || index >= Items.Count);
            _page = Math.Clamp(_page, 0, Math.Max(0, (Items.Count - 1) / PageSize));
            for (var index = PageStart; index < Math.Min(Items.Count, PageStart + PageSize); index++)
            {
                var itemIndex = index;
                var item = Items[index];
                var subtitle = item is Item source ? source.Subtitle : item is CatalogChoice catalog ? catalog.Item.Category + " · Game item" : "";
                var card = new SelectionCard(item.ToString() ?? "", subtitle, IconProvider?.Invoke(item), ItemHeight)
                { AccessibleName = _accessiblePrefix + item, AccessibleDescription = subtitle, Font = Font,
                    Height = Math.Max(1, (int)Math.Round(ItemHeight * CardScale)),
                    Margin = new Padding(0, 0, 0, Math.Max(1, (int)Math.Round(4 * CardScale))) };
                card.Click += (_, _) =>
                {
                    if (!MultiSelect) { SelectedIndex = itemIndex; return; }
                    if ((ModifierKeys & Keys.Shift) != 0) MoveMultipleSelection(itemIndex, ModifierKeys, _selectedIndex);
                    else ToggleMultipleSelection(itemIndex);
                };
                card.DoubleClick += (_, args) => OnDoubleClick(args);
                card.KeyDown += (_, args) =>
                {
                    if (MultiSelect && args.KeyCode == Keys.Space)
                    {
                        ToggleMultipleSelection(itemIndex);
                        args.Handled = true;
                        args.SuppressKeyPress = true;
                        return;
                    }
                    if (MultiSelect && args.KeyCode == Keys.A && args.Control)
                    {
                        _selectedIndices.Clear();
                        _selectedIndices.UnionWith(Enumerable.Range(0, Items.Count));
                        _selectedIndex = itemIndex;
                        _selectionAnchor = itemIndex;
                        UpdateSelection();
                        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                        args.Handled = true;
                        args.SuppressKeyPress = true;
                        return;
                    }
                    var page = Math.Max(1, _flow.ClientSize.Height / Math.Max(1, card.Height + card.Margin.Vertical) - 1);
                    var target = args.KeyCode switch
                    {
                        Keys.Up => itemIndex - 1,
                        Keys.Down => itemIndex + 1,
                        Keys.Home => 0,
                        Keys.End => Items.Count - 1,
                        Keys.PageUp => itemIndex - page,
                        Keys.PageDown => itemIndex + page,
                        _ => (int?)null
                    };
                    if (target is null) return;
                    var next = Math.Clamp(target.Value, 0, Items.Count - 1);
                    if (MultiSelect) MoveMultipleSelection(next, args.Modifiers, itemIndex);
                    else SelectedIndex = next;
                    EnsureSelectionVisible();
                    var visibleIndex = _selectedIndex - PageStart;
                    if (visibleIndex >= 0 && visibleIndex < _cards.Count) _cards[visibleIndex].Focus();
                    args.Handled = true;
                    args.SuppressKeyPress = true;
                };
                _tips.SetToolTip(card, item + (subtitle.Length > 0 ? "\n" + subtitle : ""));
                _cards.Add(card);
                _flow.Controls.Add(card);
            }
            SizeCards();
            UpdateSelection();
            _flow.ResumeLayout(true);
            _flow.AutoScrollPosition = Point.Empty;
        }

        private void UpdatePresentation(int index)
        {
            if (_updateDepth > 0) { _presentationUpdates.Add(index); return; }
            if (index < 0 || index >= Items.Count) return;
            var visibleIndex = index - PageStart;
            if (visibleIndex < 0 || visibleIndex >= PageSize) return;
            if (visibleIndex >= _cards.Count) { Rebuild(); return; }
            var item = Items[index];
            var subtitle = item is Item source ? source.Subtitle : item is CatalogChoice catalog ? catalog.Item.Category + " · Game item" : "";
            var card = _cards[visibleIndex];
            card.UpdatePresentation(item.ToString() ?? "", subtitle, IconProvider?.Invoke(item));
            card.AccessibleName = _accessiblePrefix + item;
            card.AccessibleDescription = subtitle;
            _tips.SetToolTip(card, item + (subtitle.Length > 0 ? "\n" + subtitle : ""));
        }

        private void SizeCards()
        {
            if (_sizingCards) return;
            _sizingCards = true;
            try
            {
                var width = Math.Max(90, _flow.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 1);
                foreach (var card in _cards) if (card.Width != width) card.Width = width;
            }
            finally { _sizingCards = false; }
        }

        private void EnsureSelectionVisible()
        {
            if (_selectedIndex < 0 || _selectedIndex >= Items.Count) return;
            if (_page != _selectedIndex / PageSize)
            {
                _page = _selectedIndex / PageSize;
                Rebuild();
            }
            var visibleIndex = _selectedIndex - PageStart;
            if (visibleIndex >= 0 && visibleIndex < _cards.Count) _flow.ScrollControlIntoView(_cards[visibleIndex]);
        }

        private void UpdateSelection()
        {
            for (var index = 0; index < _cards.Count; index++)
            {
                var itemIndex = PageStart + index;
                _cards[index].SetSelected(MultiSelect ? _selectedIndices.Contains(itemIndex) : itemIndex == _selectedIndex);
                _cards[index].TabStop = itemIndex == _selectedIndex ||
                    (_selectedIndex < PageStart || _selectedIndex >= PageStart + _cards.Count) && index == 0;
            }
            UpdatePaging();
        }

        private void ChangePage(int direction)
        {
            var next = Math.Clamp(_page + direction, 0, Math.Max(0, (Items.Count - 1) / PageSize));
            if (next == _page) return;
            _page = next;
            Rebuild();
        }

        private void UpdatePaging()
        {
            _paging.Visible = Items.Count > PageSize;
            var scale = CardScale;
            _paging.Height = Math.Max((int)Math.Round(34 * scale), Font.Height + (int)Math.Round(14 * scale));
            _paging.Padding = new Padding(0, (int)Math.Round(5 * scale), 0, 0);
            _paging.ColumnStyles[0].Width = (int)Math.Round(34 * scale);
            _paging.ColumnStyles[2].Width = (int)Math.Round(34 * scale);
            _previousPage.Enabled = _page > 0;
            _nextPage.Enabled = PageStart + PageSize < Items.Count;
            var shown = Items.Count == 0 ? "0 items" :
                $"{PageStart + 1}–{Math.Min(Items.Count, PageStart + PageSize)} of {Items.Count:N0} · Page {_page + 1}/{Math.Max(1, (Items.Count + PageSize - 1) / PageSize)}";
            if (MultiSelect && _selectedIndices.Count > 0) shown += $" · {_selectedIndices.Count:N0} selected";
            _pageStatus.Text = shown;
            _tips.SetToolTip(_pageStatus, shown);
        }

        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            // New page controls must retain the scale applied to the existing container.
            _layoutScale *= factor.Height;
            base.ScaleControl(factor, specified);
        }

        private void ToggleMultipleSelection(int index)
        {
            if (!_selectedIndices.Add(index)) _selectedIndices.Remove(index);
            _selectedIndex = index;
            _selectionAnchor = index;
            UpdateSelection();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }

        private void MoveMultipleSelection(int index, Keys modifiers, int previousFocus)
        {
            var control = (modifiers & Keys.Control) != 0;
            if ((modifiers & Keys.Shift) != 0)
            {
                if (_selectionAnchor < 0 || _selectionAnchor >= Items.Count)
                    _selectionAnchor = previousFocus >= 0 ? previousFocus : index;
                if (!control) _selectedIndices.Clear();
                var first = Math.Min(_selectionAnchor, index);
                var last = Math.Max(_selectionAnchor, index);
                _selectedIndices.UnionWith(Enumerable.Range(first, last - first + 1));
            }
            else if (!control)
            {
                _selectedIndices.Clear();
                _selectedIndices.Add(index);
                _selectionAnchor = index;
            }
            _selectedIndex = index;
            UpdateSelection();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing) { if (disposing) _tips.Dispose(); base.Dispose(disposing); }

        public sealed class SelectionCollection(EditorSelectionCards owner) : Collection<object>
        {
            public void AddRange(IEnumerable<object> items) { foreach (var item in items) Items.Add(item); owner.Rebuild(); }
            protected override void InsertItem(int index, object item) { base.InsertItem(index, item); owner.Rebuild(); }
            protected override void RemoveItem(int index) { base.RemoveItem(index); owner.Rebuild(); }
            protected override void SetItem(int index, object item) { base.SetItem(index, item); owner.UpdatePresentation(index); }
            protected override void ClearItems() { base.ClearItems(); owner._selectedIndices.Clear(); owner._selectedIndex = -1; owner._selectionAnchor = -1; owner._page = 0; owner.Rebuild(); }
        }

        private sealed class SelectionCard(string title, string subtitle, Image? image, int height) : Button
        {
            private bool _selected;
            private bool _hovered;
            public void UpdatePresentation(string updatedTitle, string updatedSubtitle, Image? updatedImage)
            {
                title = updatedTitle;
                subtitle = updatedSubtitle;
                image = updatedImage;
                Text = updatedTitle;
                Invalidate();
            }
            public void SetSelected(bool selected) { _selected = selected; TabStop = selected; Invalidate(); }
            protected override void OnCreateControl()
            {
                base.OnCreateControl();
                Text = title;
                UseMnemonic = false;
                BackColor = MedievalTheme.Input;
                ForeColor = MedievalTheme.Text;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.StandardDoubleClick, true);
            }
            protected override void OnMouseEnter(EventArgs args) { base.OnMouseEnter(args); _hovered = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs args) { base.OnMouseLeave(args); _hovered = false; Invalidate(); }
            protected override void OnGotFocus(EventArgs args) { base.OnGotFocus(args); Invalidate(); }
            protected override void OnLostFocus(EventArgs args) { base.OnLostFocus(args); Invalidate(); }
            protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is
                Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(keyData);
            protected override void OnPaint(PaintEventArgs args)
            {
                args.Graphics.Clear(_selected ? MedievalTheme.SurfaceRaised : MedievalTheme.Input);
                using var border = new Pen(_selected ? MedievalTheme.GoldBright : _hovered ? MedievalTheme.BorderStrong : MedievalTheme.Border, _selected ? 2 : 1);
                args.Graphics.DrawRectangle(border, 1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
                var scale = Height / (float)height;
                var inset = (int)(8 * scale);
                var imageSize = (int)((subtitle.Length > 0 ? 32 : 22) * scale);
                if (image is not null) args.Graphics.DrawImage(image, new Rectangle(inset, (Height - imageSize) / 2, imageSize, imageSize));
                var left = image is not null ? inset + imageSize + (int)(8 * scale) : inset;
                var bounds = new Rectangle(left, subtitle.Length > 0 ? (int)(8 * scale) : 0, Math.Max(1, Width - left - inset), subtitle.Length > 0 ? (int)(24 * scale) : Height);
                TextRenderer.DrawText(args.Graphics, title, Font, bounds, _selected ? MedievalTheme.GoldBright : MedievalTheme.Text,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                if (subtitle.Length > 0)
                {
                    using var secondary = new Font(Font.FontFamily, Math.Max(8, Font.Size - .5f), FontStyle.Regular);
                    TextRenderer.DrawText(args.Graphics, subtitle, secondary, new Rectangle(left, (int)(34 * scale), Math.Max(1, Width - left - inset), (int)(22 * scale)), MedievalTheme.TextMuted,
                        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                }
                if (Focused) ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), MedievalTheme.Gold, BackColor);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _errors.Dispose(); _tips.Dispose(); _editorEmblem.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed record Item(string Id, string Name, string Subtitle = "") { public override string ToString() => Name; }
    private sealed record ValueChoice(string Value, string Label) { public override string ToString() => Label; }
    private sealed record CatalogChoice(CivilizationCatalogItem Item) { public override string ToString() => Item.Name; }
    private sealed class NamedAgeSetInput : Panel
    {
        private readonly TextBox _display = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, AccessibleName = "Supported age names" };
        private readonly Func<int, string> _name;
        private string _value = "";
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal string Value
        {
            get => _value;
            set
            {
                _value = value;
                _display.Text = string.Join(", ", value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(part => int.TryParse(part, out var age) ? _name(age) : part));
            }
        }
        internal NamedAgeSetInput(string value, Func<int, string> name)
        {
            _name = name;
            BackColor = MedievalTheme.Input;
            MedievalTheme.StyleTextInput(_display);
            _display.BorderStyle = BorderStyle.None;
            Controls.Add(_display);
            Value = value;
        }
        internal void ShowNamedRange(string text) => _display.Text = text;
    }
    private sealed record PropertySection(string Name, Button Header, Control Content);
    private sealed class FieldBinding(string civilizationId, string? entityId, CivilizationFieldDefinition? definition, Control input, string original, bool isName)
    {
        public string CivilizationId { get; } = civilizationId;
        public string? EntityId { get; } = entityId;
        public CivilizationFieldDefinition? Definition { get; } = definition;
        public Control Input { get; } = input;
        public string Original { get; set; } = original;
        public bool IsName { get; } = isName;
        public Button? ResetButton { get; set; }
        public string? GameValue { get; init; }
        public bool HadOverride { get; set; }
        public bool Writable { get; init; } = true;
    }
}

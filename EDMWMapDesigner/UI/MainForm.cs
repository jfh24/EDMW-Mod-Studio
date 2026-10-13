using EDMWMapDesigner.Analysis;
using EDMWMapDesigner.Generation;
using EDMWMapDesigner.Models;
using EDMWMapDesigner.Collaboration;
using EDMWMapDesigner.Networking;
using EDMWMapDesigner.Civilizations;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EDMWMapDesigner.UI;

public sealed partial class MainForm : Form
{
    private const int SceneRailWidth = 264;
    private readonly bool _verificationMode;
    private enum AppView { Home, MapGenerator, Civilizations }
    private enum BrushAugment { Length, Width, Scale, Rotation }

    private MapProject _project = MapProject.CreateDefault();
    private string? _projectPath;
    private bool _dirty;
    private readonly Stack<UndoEntry> _undo = new();
    private readonly Stack<UndoEntry> _redo = new();
    private readonly LanCollaborationSession _collaboration = new();
    private readonly FlowLayoutPanel _collaboratorList = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly MedievalToggleSwitch _hostListeningToggle = new() { Text = "Host", AutoSize = true };
    private readonly Button _joinCollaborationButton = new() { Text = "Join", AutoSize = true };
    private readonly Dictionary<Guid, string> _connectedCollaborators = new();
    private Panel? _collaborationPanel;
    private Button? _collaborationPanelTab;
    private bool _updatingCollaborationPanel;
    private string? _hostLanAddress;
    private bool _applyingRemoteProject;
    private MapProject? _strokeSnapshot;
    private MapProject? _lastSharedProject;
    private readonly System.Windows.Forms.Timer _collaborationSyncTimer = new() { Interval = 45 };
    private readonly SemaphoreSlim _collaborationSendLock = new(1, 1);
    private bool _collaborationUpdatePending;
    private bool _collaborationUpdateInFlight;
    private bool _collaborationFullProjectPending;

    private sealed record UndoEntry(MapDelta Undo, MapDelta Redo);

    private readonly MapCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly SplitContainer _mainSplit = new()
    {
        Dock = DockStyle.Fill,
        Size = new Size(1200, 700),
        FixedPanel = FixedPanel.Panel2,
        IsSplitterFixed = true,
        Panel2MinSize = 520,
        SplitterWidth = 4,
        BackColor = MedievalTheme.Border
    };
    private readonly TextBox _nameBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _authorBox = new() { Dock = DockStyle.Fill };
    private readonly MedievalNumericInput _gridSize = new() { Minimum = 64, Maximum = 512, Increment = 64, Value = 512, Width = 90 };
    private readonly MedievalSlider _brushAugmentSlider = new() { Dock = DockStyle.Fill };
    private readonly Label _brushAugmentValue = new() { AutoSize = true };
    private readonly ComboBox _usStateBrush = new MedievalComboBox
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        DisplayMember = nameof(UsStateBrush.Name),
        MaxDropDownItems = 12,
        AccessibleName = "US States",
        AccessibleDescription = "Select a state-shaped brush. Use the brush augments to resize or rotate it."
    };
    private readonly MedievalToggleSwitch _stateMountainToggle = new()
    {
        Text = "Mountain ranges", AutoSize = true, Dock = DockStyle.Fill,
        Enabled = false, AccessibleName = "State mountain ranges"
    };
    private readonly MedievalToggleSwitch _stateWaterToggle = new()
    {
        Text = "Water bodies", AutoSize = true, Dock = DockStyle.Fill,
        Enabled = false, AccessibleName = "State water bodies"
    };
    private readonly MedievalToggleSwitch _stateForestToggle = new()
    {
        Text = "Forests", AutoSize = true, Dock = DockStyle.Fill,
        Enabled = false, AccessibleName = "State forests"
    };
    private readonly Dictionary<string, (bool Mountains, bool Water, bool Forests)> _stateFeaturePreferences = [];
    private BrushAugment _selectedBrushAugment = BrushAugment.Scale;
    private int _brushLengthPercent = 100;
    private int _brushWidthPercent = 100;
    private int _brushScale = 9;
    private int _brushRotationDegrees;
    private bool _updatingBrushAugment;
    private readonly MedievalSlider _variance = new() { Minimum = 0, Maximum = 50, TickFrequency = 10, Value = 20, Dock = DockStyle.Fill };
    private readonly Label _varianceValue = new() { AutoSize = true, Text = "20%" };
    private readonly MedievalSlider _randomLandOverwrite = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Value = 0, Dock = DockStyle.Fill };
    private readonly Label _randomLandOverwriteValue = new() { AutoSize = true, Text = "0%" };
    private readonly TextBox _generationSeed = new()
    {
        Dock = DockStyle.Fill, MaxLength = 64,
        AccessibleName = "Map generation seed",
        AccessibleDescription = "Whole-number seed. Leave empty to choose a random seed."
    };
    private readonly Button _newGenerationSeed = new()
    {
        Text = "New seed", Dock = DockStyle.Fill, AutoSize = true,
        Margin = new Padding(5, 0, 0, 0), AccessibleName = "Choose a new map seed"
    };
    private readonly Button _generateSeededMap = new()
    {
        Text = "Generate map", Dock = DockStyle.Fill, AutoSize = true,
        Margin = new Padding(0, 6, 0, 3),
        AccessibleDescription = "Replace the map terrain using this seed. Undo restores the previous map."
    };
    private bool _generatingSeededMap;
    private readonly CheckBox _anchorMode = new MedievalToggleSwitch { Text = "Assign component anchors", AutoSize = true, Dock = DockStyle.Fill };
    private readonly CheckBox _elevationMode = new MedievalToggleSwitch { Text = "Paint elevation mask", AutoSize = true, Dock = DockStyle.Fill };
    private readonly CheckBox _paintCliffs = new MedievalToggleSwitch
    {
        Text = "Paint cliffs", AutoSize = true, Dock = DockStyle.Fill,
        AccessibleName = "Paint cliffs",
        AccessibleDescription = "Paint the selected elevation with cliff edges. Cliffs override ramps and automatic slopes."
    };
    private readonly CheckBox _rampMode = new MedievalToggleSwitch { Text = "Draw manual ramps", AutoSize = true, Dock = DockStyle.Fill };
    private readonly MedievalToggleSwitch _manualResourceToggle = new() { Text = "Edit resources", AutoSize = true, Dock = DockStyle.Fill };
    private readonly MedievalToggleSwitch _manualPlayerStartToggle = new() { Text = "Edit player starts", AutoSize = true, Dock = DockStyle.Fill };
    private readonly MedievalToggleSwitch _hideMapPreviewToggle = new()
    {
        Text = "Hide map preview",
        AutoSize = true
    };
    private readonly MedievalSlider _elevation = new() { Minimum = -100, Maximum = 100, TickFrequency = 25, Value = 25, Dock = DockStyle.Fill };
    private readonly Label _elevationValue = new() { AutoSize = true, Text = "+25" };
    private readonly ComboBox _anchorRole = new MedievalComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label _landMetric = new();
    private readonly Label _islandsMetric = new();
    private readonly Label _forestMetric = new();
    private readonly Label _ruggedMetric = new();
    private readonly Label _coastAnchorLabel = new() { AutoSize = true };
    private readonly Label _elevationSummaryLabel = new() { AutoSize = true };
    private readonly Label _warningLabel = new() { AutoSize = true, Dock = DockStyle.Top, ForeColor = MedievalTheme.Warning };
    private Control? _warningHeading;
    private Control? _warningCard;
    private Panel? _mapStatusOverlay;
    private Panel? _mapStatusOverlayBody;
    private Button? _mapStatusOverlayToggle;
    private bool _mapStatusOverlayExpanded;
    private readonly Label _activeTerrain = new() { AutoSize = true, Text = "Active terrain: Grassland" };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, Text = "Ready", TextAlign = ContentAlignment.MiddleLeft };
    private readonly Dictionary<TerrainKind, Button> _paletteButtons = new();
    private readonly Dictionary<ManualResourceKind, Button> _resourcePaletteButtons = new();
    private Button? _forestResourceButton;
    private bool _forestResourceSelected;
    private TerrainKind _terrainBeforeResourceEditing = TerrainKind.Grassland;
    private string? _selectedGamePath = GameInstallationLocator.LoadSelectedPath();
    private ToolStripButton? _maximizeWindowButton;
    private ToolStripButton? _homeButton;
    private ToolStripButton? _viewSwitchButton;
    private ToolStrip? _toolbar;
    private MedievalToolStripRenderer? _toolbarRenderer;
    private readonly System.Windows.Forms.Timer _toolbarAutoHideTimer = new() { Interval = 100 };
    private DateTime _toolbarHideAfterUtc;
    private bool _toolbarHidden;
    private readonly Panel _workspaceHost = new() { Dock = DockStyle.Fill };
    private Control? _homeView;
    private Control? _mapDesignerView;
    private CivilizationEditorView? _civilizationEditorView;
    private AppView _activeView = AppView.Home;
    private readonly List<ToolStripItem> _mapToolbarItems = [];
    private ToolStripButton? _launchButton;
    private Action? _centerToolbarActions;
    private Action? _refreshCustomMaps;
    private CivilizationInstalledEditsPanel? _civilizationInstalledEditsPanel;
    private CivilizationLanTransfer? _civilizationLanTransfer;
    private readonly CivilizationLanPreferences _civilizationLanPreferences = CivilizationLanPreferences.Load();
    private MapLanTransfer? _mapLanTransfer;
    private readonly MapLanPreferences _mapLanPreferences = MapLanPreferences.Load();
    private readonly SemaphoreSlim _lanReviewGate = new(1, 1);
    private string? _civilizationLanError;
    private string? _mapLanError;
    private bool _preparingMapLanExport;
    private bool _closingDesigner;

    private const int EnumCurrentSettings = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPixel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int IcmMethod;
        public int IcmIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    public MainForm(bool verificationMode = false)
    {
        _verificationMode = verificationMode;
        _online = new OnlineRelayClient(_onlinePreferences);
        _onlineCollaboration = new OnlineCollaborationSession(_online);
        Text = "EDMW Mod Studio";
        MinimumSize = new Size(1200, 760);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        BackColor = MedievalTheme.Shell;
        ForeColor = MedievalTheme.Text;
        Font = MedievalTheme.UiFont();
        DoubleBuffered = true;
        Width = 1500;
        Height = 900;
        Opacity = 0;
        AutoScaleMode = AutoScaleMode.Dpi;
        HandleCreated += (_, _) =>
        {
            MedievalWindowChrome.Apply(this);
            UpdateMaximizedBounds();
            if (!_verificationMode) WindowState = FormWindowState.Maximized;
        };

        MedievalTheme.StyleTextInput(_nameBox);
        MedievalTheme.StyleTextInput(_authorBox);
        MedievalTheme.StyleTextInput(_anchorRole);
        MedievalTheme.StyleTextInput(_usStateBrush);
        MedievalTheme.StyleTextInput(_generationSeed);
        MedievalTheme.StyleSecondaryButton(_newGenerationSeed);
        MedievalTheme.StyleSecondaryButton(_generateSeededMap);
        _newGenerationSeed.Click += (_, _) =>
            _generationSeed.Text = NewGenerationSeed().ToString(CultureInfo.InvariantCulture);
        _generateSeededMap.Click += async (_, _) => await GenerateSeededMapAsync();
        _generationSeed.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await GenerateSeededMapAsync();
        };
        StyleSlider(_brushAugmentSlider);
        StyleSlider(_variance);
        StyleSlider(_randomLandOverwrite);
        StyleCheckBox(_anchorMode);
        StyleCheckBox(_stateMountainToggle);
        StyleCheckBox(_stateWaterToggle);
        StyleCheckBox(_stateForestToggle);
        StyleCheckBox(_elevationMode);
        StyleCheckBox(_paintCliffs);
        StyleCheckBox(_rampMode);
        StyleCheckBox(_manualResourceToggle);
        StyleCheckBox(_manualPlayerStartToggle);
        StyleCheckBox(_hideMapPreviewToggle);
        StyleSlider(_elevation);
        _warningLabel.Font = MedievalTheme.UiFont(9f);
        _activeTerrain.ForeColor = MedievalTheme.GoldBright;
        _activeTerrain.Font = MedievalTheme.UiFont(9f, FontStyle.Bold);

        Controls.Add(BuildWorkspaceLayout());
        Controls.Add(BuildToolStrip());
        InitializeOnlineSharing();
        _canvas.Project = _project;
        _canvas.StrokeStarted += (_, _) => _strokeSnapshot = _project.Clone();
        _canvas.StrokeEnded += (_, _) =>
        {
            if (_strokeSnapshot is not null) CommitUndo(_strokeSnapshot);
            _strokeSnapshot = null;
            UpdateAnalysis();
        };
        _canvas.MapChanged += (_, _) =>
        {
            MarkDirty();
            if (!_applyingRemoteProject && MapSessionConnected)
                QueueCollaborationUpdate();
        };
        _canvas.TerrainPicked += (_, terrain) => SelectTerrain(terrain);
        _canvas.ElevationPicked += (_, elevation) => SetElevation(elevation);
        _canvas.HoverTextChanged += (_, text) => _status.Text = string.IsNullOrEmpty(text) ? StatusSummary() : text;
        _canvas.CursorMoved += async (_, cell) =>
        {
            if (MapSessionConnected)
                await SendCollaborationCursorAsync(cell);
        };
        // Apply document messages synchronously in receive-stream order. This keeps
        // revision N from being queued behind N+1 on the WinForms message pump.
        _collaboration.ProjectReceived += (_, project) => Invoke(() => ApplyRemoteProject(project));
        _collaboration.DeltaReceived += (_, delta) => Invoke(() => ApplyRemoteDelta(delta));
        _collaboration.CursorReceived += (_, cursor) => BeginInvoke(() => _canvas.SetRemoteCursor(cursor));
        _collaboration.CollaboratorConnected += (_, collaborator) => BeginInvoke(() =>
        {
            _connectedCollaborators[collaborator.Id] = collaborator.Author;
            UpdateCollaborationPanel();
        });
        _collaboration.CollaboratorDisconnected += (_, id) => BeginInvoke(() =>
        {
            _canvas.RemoveRemoteCursor(id);
            _connectedCollaborators.Remove(id);
            UpdateCollaborationPanel();
        });
        _collaboration.StatusChanged += (_, message) => BeginInvoke(() => { _status.Text = message; UpdateCollaborationPanel(); });
        _collaborationSyncTimer.Tick += async (_, _) => await FlushCollaborationUpdateAsync();
        _toolbarAutoHideTimer.Tick += (_, _) => UpdateToolbarAutoHide();

        _nameBox.TextChanged += (_, _) =>
        {
            if (_applyingRemoteProject) return;
            _project.Name = _nameBox.Text;
            MarkDirty();
            if (MapSessionConnected) QueueCollaborationUpdate();
        };
        _authorBox.TextChanged += (_, _) =>
        {
            if (_applyingRemoteProject) return;
            _project.Author = _authorBox.Text;
            MarkDirty();
            if (MapSessionConnected) QueueCollaborationUpdate();
        };
        _gridSize.ValueChanged += (_, _) => ResizeGrid();
        _brushAugmentSlider.ValueChanged += (_, _) => ApplyBrushAugmentSlider();
        _variance.ValueChanged += (_, _) =>
        {
            if (_applyingRemoteProject) return;
            _project.Variance = _variance.Value;
            _varianceValue.Text = $"{_variance.Value}%";
            MarkDirty();
            UpdateAnalysis();
            if (MapSessionConnected) QueueCollaborationUpdate();
        };
        _randomLandOverwrite.ValueChanged += (_, _) =>
            _randomLandOverwriteValue.Text = $"{_randomLandOverwrite.Value}%";
        _manualResourceToggle.CheckedChanged += (_, _) =>
        {
            if (_applyingRemoteProject) return;
            if (_manualResourceToggle.Checked)
            {
                _anchorMode.Checked = false;
                _elevationMode.Checked = false;
                _rampMode.Checked = false;
                _manualPlayerStartToggle.Checked = false;
            }
            ApplyResourceDrawingTool();
            _status.Text = _manualResourceToggle.Checked
                ? _forestResourceSelected
                    ? "Editing forests with the active brush."
                    : "Editing manual resources: left-click to place; right-click to remove."
                : "Resource editing closed; resources and forests remain active in game.";
        };
        _manualPlayerStartToggle.CheckedChanged += (_, _) =>
        {
            if (_applyingRemoteProject) return;
            _canvas.ManualPlayerStartMode = _manualPlayerStartToggle.Checked;
            if (_manualPlayerStartToggle.Checked)
            {
                _anchorMode.Checked = false;
                _elevationMode.Checked = false;
                _rampMode.Checked = false;
                _manualResourceToggle.Checked = false;
            }
            _canvas.Cursor = _manualPlayerStartToggle.Checked ? Cursors.Hand : Cursors.Cross;
            _status.Text = _manualPlayerStartToggle.Checked
                ? "Editing player starts: left-click to place P1-P8 in order; right-click removes the last placement."
                : "Player-start editing closed; placed starts remain active in game.";
        };
        _anchorMode.CheckedChanged += (_, _) =>
        {
            if (_anchorMode.Checked) { _elevationMode.Checked = false; _rampMode.Checked = false; _manualResourceToggle.Checked = false; _manualPlayerStartToggle.Checked = false; }
            _canvas.SemanticAnchorMode = _anchorMode.Checked;
            _canvas.Cursor = _anchorMode.Checked ? Cursors.Hand : Cursors.Cross;
            _status.Text = _anchorMode.Checked
                ? "Select land to place anchors; right-click to remove."
                : StatusSummary();
        };
        _elevationMode.CheckedChanged += (_, _) =>
        {
            if (_elevationMode.Checked) { _anchorMode.Checked = false; _rampMode.Checked = false; _manualResourceToggle.Checked = false; _manualPlayerStartToggle.Checked = false; }
            _canvas.ElevationMaskMode = _elevationMode.Checked;
            _canvas.Cursor = _elevationMode.Checked ? Cursors.Cross : Cursors.Cross;
            _status.Text = _elevationMode.Checked
                ? ElevationPaintStatus()
                : StatusSummary();
        };
        _paintCliffs.CheckedChanged += (_, _) =>
        {
            _canvas.PaintCliffs = _paintCliffs.Checked;
            if (_elevationMode.Checked) _status.Text = ElevationPaintStatus();
        };
        _rampMode.CheckedChanged += (_, _) =>
        {
            if (_rampMode.Checked) { _anchorMode.Checked = false; _elevationMode.Checked = false; _manualResourceToggle.Checked = false; _manualPlayerStartToggle.Checked = false; }
            _canvas.RampMaskMode = _rampMode.Checked;
            _status.Text = _rampMode.Checked
                ? "Drag over land to paint ramps; right-click to erase."
                : StatusSummary();
        };
        _elevation.ValueChanged += (_, _) => SetElevation(_elevation.Value);
        _anchorRole.SelectedIndexChanged += (_, _) =>
        {
            if (_anchorRole.SelectedItem is SemanticAnchorRole role)
            {
                _canvas.SelectedAnchorRole = role;
                if (_anchorMode.Checked) _status.Text = $"Anchor mode: {role.DisplayName()}";
            }
        };
        FormClosing += OnFormClosing;
        Shown += (_, _) =>
        {
            if (_verificationMode) return;
            StartCivilizationLanReceiver();
            StartMapLanReceiver();
            BeginInvoke(new Action(() =>
            {
                MaximizeToCurrentScreen();
                UpdateMaximizeWindowButton();
                if (_activeView == AppView.MapGenerator) MatchMapRegionToDrawableSquare();
                _toolbarHideAfterUtc = DateTime.UtcNow.AddMilliseconds(700);
                _toolbarAutoHideTimer.Start();
                BeginInvoke(new Action(() => Opacity = 1));
            }));
        };
        _mainSplit.SizeChanged += (_, _) =>
        {
            if (_activeView == AppView.MapGenerator) MatchMapRegionToDrawableSquare();
        };
        Resize += (_, _) => UpdateMaximizeWindowButton();
        Activated += (_, _) =>
        {
            if (_activeView == AppView.Home) _civilizationInstalledEditsPanel?.RefreshInstalledPackages();
        };
        KeyPreview = true;
        KeyDown += OnKeyDown;
        SetProject(_project, null, false);
        UpdateCollaborationPanel();
        ApplyBrushAugmentsToCanvas();
        SelectTerrain(TerrainKind.Grassland);
        _anchorRole.SelectedItem = SemanticAnchorRole.Center;
        ShowView(AppView.Home, confirm: false);
    }

    protected override bool ShowWithoutActivation => _verificationMode || base.ShowWithoutActivation;

    private Control BuildWorkspaceLayout()
    {
        _homeView = BuildHomeView();
        _mapDesignerView = BuildMainLayout();
        _workspaceHost.Controls.Add(_mapDesignerView);
        _workspaceHost.Controls.Add(_homeView);
        return _workspaceHost;
    }

    private void ShowView(AppView view, bool confirm = true)
    {
        if (view == _activeView && confirm) return;
        if (view == AppView.Civilizations && _civilizationEditorView is null)
        {
            _civilizationEditorView = new CivilizationEditorView(FindGamePath, SendCivilizationsOverLan, SendCivilizationsToFriends);
            _civilizationEditorView.StateChanged += (_, _) =>
            {
                if (_activeView != AppView.Civilizations) return;
                UpdateTitle();
                _status.Text = CivilizationStatusSummary();
            };
            _workspaceHost.Controls.Add(_civilizationEditorView);
        }
        if (view != AppView.Home && !_verificationMode)
            MaximizeToCurrentScreen();

        Control? targetView = view switch
        {
            AppView.Home => _homeView,
            AppView.MapGenerator => _mapDesignerView,
            AppView.Civilizations => _civilizationEditorView,
            _ => null
        };
        _activeView = view;
        _workspaceHost.SuspendLayout();
        try
        {
            _workspaceHost.Padding = view != AppView.Home
                ? new Padding(0, _toolbar?.Height ?? 50, 0, 0)
                : Padding.Empty;
            foreach (Control workspace in _workspaceHost.Controls) workspace.Visible = false;
            if (targetView is not null)
            {
                targetView.Visible = false;
                targetView.Dock = DockStyle.Fill;
                targetView.Bounds = _workspaceHost.DisplayRectangle;
            }
            _workspaceHost.PerformLayout();
            targetView?.BringToFront();
            if (targetView is not null) targetView.Visible = true;
            targetView?.PerformLayout();
            if (view == AppView.MapGenerator) PrepareMapDesignerView();
        }
        finally
        {
            _workspaceHost.ResumeLayout(performLayout: false);
        }
        if (view == AppView.Home)
        {
            _refreshCustomMaps?.Invoke();
            _civilizationInstalledEditsPanel?.RefreshInstalledPackages();
        }
        if (_homeButton is not null) _homeButton.Visible = view != AppView.Home;
        if (_viewSwitchButton is not null)
        {
            _viewSwitchButton.Visible = view != AppView.Home;
            _viewSwitchButton.Text = view == AppView.Civilizations ? "Map Generator" : "Civilizations";
            var previousIcon = _viewSwitchButton.Image;
            _viewSwitchButton.Image = MedievalIconFactory.Create(
                view == AppView.Civilizations ? MedievalIcon.Generator : MedievalIcon.Civilizations);
            previousIcon?.Dispose();
            _viewSwitchButton.ToolTipText = view == AppView.Civilizations
                ? "Return to your map workspace" : "Open your civilization workspace";
        }
        foreach (var item in _mapToolbarItems) item.Visible = view == AppView.MapGenerator;
        if (_launchButton is not null) _launchButton.Visible = view != AppView.Home;
        if (view != AppView.Home)
            SetToolbarHidden(false);
        else
            _toolbarHideAfterUtc = DateTime.UtcNow.AddMilliseconds(850);
        if (_toolbarRenderer is not null)
        {
            _toolbarRenderer.IsHomeScreen = view == AppView.Home;
            _toolbar?.Invalidate();
        }
        _centerToolbarActions?.Invoke();
        UpdateTitle();
        _status.Text = view switch
        {
            AppView.Home => "Choose a tool to begin",
            AppView.Civilizations => CivilizationStatusSummary(),
            _ => StatusSummary()
        };
    }

    private Control BuildHomeView()
    {
        var dashboard = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            BackColor = MedievalTheme.Shell,
            BackgroundImageLayout = ImageLayout.Stretch
        };
        var generator = new ToolChoiceCard("Map Generator",
            "Design terrain and export multiplayer-ready maps.",
            LoadDashboardAsset("EDMWMapDesigner.Assets.dashboard-compass-icon.png"),
            "Open Map Generator", () => ShowView(AppView.MapGenerator))
        {
            Dock = DockStyle.None,
            Size = new Size(324, 285)
        };
        var launch = new ToolChoiceCard("Launch EDMW", "Start Empires: Dawn of the Modern World.",
            LoadDashboardAsset("EDMWMapDesigner.Assets.dashboard-warrior-icon.png"),
            "Launch EDMW", LaunchEdmw)
        {
            Dock = DockStyle.None,
            Size = new Size(286, 255)
        };
        var civilizations = new ToolChoiceCard("Civ Editor",
            "Shape units, buildings, technologies, and civilization rules.",
            LoadDashboardAsset("EDMWMapDesigner.Assets.dashboard-civilizations-icon.png"),
            "Open Civilization Editor", () => ShowView(AppView.Civilizations))
        {
            Dock = DockStyle.None,
            Size = new Size(324, 285)
        };
        var maps = BuildCustomMapsCard();
        var civilizationEdits = new CivilizationInstalledEditsPanel(FindGamePath, () =>
        {
            _civilizationEditorView?.LoadGameCatalog();
            _status.Text = "Civilization edits uninstalled.";
        });
        _civilizationInstalledEditsPanel = civilizationEdits;
        dashboard.Controls.Add(generator);
        dashboard.Controls.Add(civilizations);
        dashboard.Controls.Add(launch);
        dashboard.Controls.Add(maps);
        dashboard.Controls.Add(civilizationEdits);

        void ArrangeCards()
        {
            var width = Math.Max(1, dashboard.ClientSize.Width);
            var height = Math.Max(1, dashboard.ClientSize.Height);
            Rectangle Frame(float x, float y, float w, float h) => new(
                (int)Math.Round(width * x),
                (int)Math.Round(height * y),
                (int)Math.Round(width * w),
                (int)Math.Round(height * h));

            // Keep the tool cards above two equally sized installed-content panels.
            generator.Bounds = Frame(0.112f, 0.105f, 0.224f, 0.368f);
            launch.Bounds = Frame(0.386f, 0.105f, 0.228f, 0.368f);
            civilizations.Bounds = Frame(0.663f, 0.105f, 0.225f, 0.368f);
            maps.Bounds = Frame(0.188f, 0.535f, 0.298f, 0.319f);
            civilizationEdits.Bounds = Frame(0.514f, 0.535f, 0.298f, 0.319f);
        }

        generator.TabIndex = 0;
        launch.TabIndex = 1;
        civilizations.TabIndex = 2;
        maps.TabIndex = 3;
        civilizationEdits.TabIndex = 4;
        dashboard.Resize += (_, _) => ArrangeCards();
        dashboard.HandleCreated += (_, _) =>
        {
            var priorImage = dashboard.BackgroundImage;
            dashboard.BackgroundImage = LoadDashboardFrame(Screen.FromHandle(Handle));
            priorImage?.Dispose();
            ArrangeCards();
        };
        dashboard.Disposed += (_, _) => dashboard.BackgroundImage?.Dispose();
        return dashboard;
    }

    private static Image LoadDashboardFrame(Screen screen)
    {
        var desktop = GetPhysicalDesktopResolution(screen);
        var resourceName = desktop.Width > 2560 || desktop.Height > 1440
            ? "EDMWMapDesigner.Assets.dashboard-harbor-installed-content-2160p.png"
            : desktop.Width > 1920 || desktop.Height > 1080
                ? "EDMWMapDesigner.Assets.dashboard-harbor-installed-content-1440p.png"
                : "EDMWMapDesigner.Assets.dashboard-harbor-installed-content-1080p.png";
        return LoadDashboardAsset(resourceName);
    }

    private static Size GetPhysicalDesktopResolution(Screen screen)
    {
        var devMode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        if (EnumDisplaySettings(screen.DeviceName, EnumCurrentSettings, ref devMode) &&
            devMode.PelsWidth > 0 && devMode.PelsHeight > 0)
            return new Size(devMode.PelsWidth, devMode.PelsHeight);

        return screen.Bounds.Size;
    }

    private static Image LoadDashboardAsset(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Dashboard artwork is missing: {resourceName}.");
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    private Panel BuildCustomMapsCard()
    {
        var host = new Panel { BackColor = Color.Transparent, BorderStyle = BorderStyle.None, Padding = Padding.Empty };
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(16) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        header.Controls.Add(new DashboardDisplayLabel { Text = "CUSTOM MAPS", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = MedievalTheme.DisplayFont(14), ForeColor = MedievalTheme.Text });
        var refresh = new DashboardPlaqueButton { Text = "Refresh", Dock = DockStyle.Right, Width = 88, TabStop = false, Margin = new Padding(0, 3, 0, 7) };
        header.Controls.Add(refresh);
        var listWell = new DashboardRecessedWell { Dock = DockStyle.Fill, Padding = new Padding(4) };
        var list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0) };
        list.HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(list);
        listWell.Controls.Add(list);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(listWell, 0, 1);
        card.Controls.Add(layout);
        host.Controls.Add(card);

        void ResizeMapRows()
        {
            var width = Math.Max(1, list.ClientSize.Width - (list.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
            foreach (var row in list.Controls.OfType<DashboardMapRow>()) row.Width = width;
        }

        list.SizeChanged += (_, _) => ResizeMapRows();

        void RefreshMaps()
        {
            list.SuspendLayout();
            try
            {
                foreach (Control control in list.Controls.Cast<Control>().ToArray()) control.Dispose();
                list.Controls.Clear();
                var gamePath = FindGamePath();
                if (gamePath is null)
                {
                    list.Controls.Add(new Label { Text = "Select a game folder in Settings.", AutoSize = true, ForeColor = MedievalTheme.TextMuted, Margin = new Padding(8) });
                    return;
                }
                var maps = CustomMapInstallationCatalog.Discover(gamePath);
                if (maps.Count == 0)
                {
                    list.Controls.Add(new Label { Text = "No custom maps found.", AutoSize = true, ForeColor = MedievalTheme.TextMuted, Margin = new Padding(8) });
                    return;
                }
                foreach (var map in maps)
                {
                    var row = new DashboardMapRow { Size = new Size(Math.Max(1, list.ClientSize.Width), 54) };
                    row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102));
                    var label = new Label { Text = map.IsComplete ? map.Name : $"{map.Name} (incomplete)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = MedievalTheme.Text, Font = MedievalTheme.UiFont(9.5f), Padding = new Padding(10, 0, 0, 0) };
                    var uninstall = new DashboardPlaqueButton { Text = "Uninstall", Dock = DockStyle.Fill, TabStop = false, Margin = new Padding(3) };
                    uninstall.Click += (_, _) =>
                    {
                        if (IsEdmwRunning())
                        {
                            MedievalDialog.Show(this, "Close EDMW before uninstalling a map.", "Close EDMW", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        if (MedievalDialog.Show(this, $"Remove {map.Name}?", "Remove Map", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                        try
                        {
                            CustomMapInstallationCatalog.Uninstall(map);
                            RefreshMaps();
                        }
                        catch (Exception ex)
                        {
                            MedievalDialog.Show(this, $"The map could not be fully removed.\n\n{ex.Message}", "Removal Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    };
                    row.Controls.Add(label, 0, 0);
                    row.Controls.Add(uninstall, 1, 0);
                    list.Controls.Add(row);
                }
            }
            finally
            {
                list.ResumeLayout();
                ResizeMapRows();
            }
        }

        refresh.Click += (_, _) => RefreshMaps();
        _refreshCustomMaps = RefreshMaps;
        RefreshMaps();
        return host;
    }

    private Control BuildMainLayout()
    {
        var battlefield = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = MedievalTheme.Shell,
            Padding = new Padding(4),
            Margin = Padding.Empty
        };
        battlefield.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SceneRailWidth));
        battlefield.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        battlefield.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var sceneRail = new MedievalSceneRail { Margin = new Padding(0, 0, 4, 0) };
        sceneRail.Controls.Add(BuildBrushControlsRail());
        var mapFrame = new OrnateFramePanel(10) { Dock = DockStyle.Fill, Margin = Padding.Empty };
        mapFrame.Controls.Add(_canvas);
        var collaborationPanel = BuildCollaborationPanel();
        _collaborationPanel = collaborationPanel as Panel;
        mapFrame.Controls.Add(collaborationPanel);
        _collaborationPanelTab = new Button
        {
            Text = "▶",
            Size = new Size(32, 38),
            Location = new Point(18, 18),
            Visible = false,
            AccessibleName = "Show collaborators"
        };
        MedievalTheme.StyleSecondaryButton(_collaborationPanelTab);
        _collaborationPanelTab.Click += (_, _) => SetCollaborationPanelCollapsed(false);
        mapFrame.Controls.Add(_collaborationPanelTab);
        var mapStatusOverlay = BuildMapStatusOverlay();
        mapFrame.Controls.Add(mapStatusOverlay);
        mapFrame.Resize += (_, _) => PositionMapStatusOverlay();
        collaborationPanel.BringToFront();
        mapStatusOverlay.BringToFront();
        battlefield.Controls.Add(sceneRail, 0, 0);
        battlefield.Controls.Add(mapFrame, 1, 0);
        _mainSplit.Panel1.Controls.Add(battlefield);
        _mainSplit.Panel1.BackColor = MedievalTheme.Canvas;
        _mainSplit.Panel2.BackColor = MedievalTheme.Sidebar;
        var sidebarFrame = new OrnateFramePanel(12) { Dock = DockStyle.Fill, Margin = Padding.Empty };
        sidebarFrame.Controls.Add(BuildSidebarShell());
        _mainSplit.Panel2.Controls.Add(sidebarFrame);
        return _mainSplit;
    }

    private Control BuildCollaborationPanel()
    {
        var panel = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(18, 18),
            Padding = new Padding(10, 8, 10, 9),
            BackColor = Color.FromArgb(232, MedievalTheme.Surface),
            ForeColor = MedievalTheme.Text
        };
        var body = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Fill };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.Controls.Add(new Label { Text = "COLLABORATORS", AutoSize = true, Font = MedievalTheme.UiFont(8.5f, FontStyle.Bold), ForeColor = MedievalTheme.GoldBright });
        _collaboratorList.Margin = new Padding(0, 5, 0, 5);
        _collaboratorList.BackColor = Color.Transparent;
        body.Controls.Add(_collaboratorList);
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        _hostListeningToggle.ForeColor = MedievalTheme.Text;
        _hostListeningToggle.Margin = new Padding(0, 0, 8, 0);
        _hostListeningToggle.CheckedChanged += async (_, _) =>
        {
            if (_updatingCollaborationPanel) return;
            if (_hostListeningToggle.Checked) await HostCollaborationAsync();
            else DisconnectCollaboration();
        };
        MedievalTheme.StyleSecondaryButton(_joinCollaborationButton);
        _joinCollaborationButton.Click += async (_, _) => await JoinCollaborationAsync();
        var collapse = new Button { Text = "◀", Size = new Size(30, 28), AccessibleName = "Hide collaborators" };
        MedievalTheme.StyleSecondaryButton(collapse);
        collapse.Click += (_, _) => SetCollaborationPanelCollapsed(true);
        actions.Controls.Add(_hostListeningToggle);
        actions.Controls.Add(_joinCollaborationButton);
        actions.Controls.Add(collapse);
        body.Controls.Add(actions);
        panel.Controls.Add(body);
        return panel;
    }

    private void SetCollaborationPanelCollapsed(bool collapsed)
    {
        if (collapsed)
        {
            if (_collaborationPanel is not null) _collaborationPanel.Visible = false;
            if (_collaborationPanelTab is not null)
            {
                _collaborationPanelTab.Visible = true;
                _collaborationPanelTab.BringToFront();
            }
            return;
        }

        if (_collaborationPanelTab is not null) _collaborationPanelTab.Visible = false;
        if (_collaborationPanel is not null)
        {
            _collaborationPanel.Visible = true;
            _collaborationPanel.BringToFront();
        }
    }

    private Control BuildMapStatusOverlay()
    {
        var overlay = new Panel
        {
            Width = 500,
            Height = 38,
            BackColor = Color.FromArgb(242, MedievalTheme.Surface),
            ForeColor = MedievalTheme.Text,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom
        };
        _mapStatusOverlay = overlay;
        overlay.Paint += (_, e) =>
        {
            using var border = new Pen(Color.FromArgb(210, MedievalTheme.Gold));
            e.Graphics.DrawRectangle(border, 0, 0,
                Math.Max(0, overlay.Width - 1), Math.Max(0, overlay.Height - 1));
        };

        var toggle = new Button
        {
            Text = "MAP STATUS & WARNINGS   ▲",
            Dock = DockStyle.Top,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            BackColor = MedievalTheme.SurfaceRaised,
            ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.UiFont(8.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 8, 0),
            Cursor = Cursors.Hand,
            AccessibleName = "Expand map status and warnings"
        };
        toggle.FlatAppearance.BorderSize = 0;
        toggle.Click += (_, _) => SetMapStatusOverlayExpanded(!_mapStatusOverlayExpanded);
        _mapStatusOverlayToggle = toggle;

        var bodyHost = new Panel
        {
            Dock = DockStyle.Fill,
            Visible = false,
            AutoScroll = true,
            Padding = new Padding(10, 8, 10, 10),
            BackColor = Color.FromArgb(236, MedievalTheme.Surface)
        };
        _mapStatusOverlayBody = bodyHost;
        var body = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            Margin = Padding.Empty,
            BackColor = Color.Transparent
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var metrics = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            Margin = Padding.Empty
        };
        for (var column = 0; column < 4; column++)
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        metrics.Controls.Add(MetricTile("LAND", _landMetric), 0, 0);
        metrics.Controls.Add(MetricTile("ISLANDS", _islandsMetric), 1, 0);
        metrics.Controls.Add(MetricTile("FOREST", _forestMetric), 2, 0);
        metrics.Controls.Add(MetricTile("RUGGED", _ruggedMetric), 3, 0);
        body.Controls.Add(metrics);
        _coastAnchorLabel.Margin = new Padding(2, 7, 2, 1);
        _elevationSummaryLabel.Margin = new Padding(2, 2, 2, 5);
        body.Controls.Add(_coastAnchorLabel);
        body.Controls.Add(_elevationSummaryLabel);

        _warningHeading = new Label
        {
            Text = "WARNINGS",
            AutoSize = true,
            ForeColor = MedievalTheme.Warning,
            Font = MedievalTheme.UiFont(8f, FontStyle.Bold),
            Margin = new Padding(2, 5, 2, 3)
        };
        body.Controls.Add(_warningHeading);
        _warningLabel.MaximumSize = new Size(452, 0);
        _warningCard = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(9, 7, 9, 7),
            BackColor = Color.FromArgb(66, 25, 20),
            Margin = new Padding(0, 0, 0, 2)
        };
        _warningCard.Controls.Add(_warningLabel);
        body.Controls.Add(_warningCard);
        bodyHost.Controls.Add(body);
        overlay.Controls.Add(bodyHost);
        overlay.Controls.Add(toggle);
        toggle.BringToFront();
        return overlay;
    }

    private void SetMapStatusOverlayExpanded(bool expanded)
    {
        _mapStatusOverlayExpanded = expanded;
        if (_mapStatusOverlayBody is not null) _mapStatusOverlayBody.Visible = expanded;
        if (_mapStatusOverlayToggle is not null)
        {
            _mapStatusOverlayToggle.Text = expanded
                ? "MAP STATUS & WARNINGS   ▼"
                : "MAP STATUS & WARNINGS   ▲";
            _mapStatusOverlayToggle.AccessibleName = expanded
                ? "Collapse map status and warnings"
                : "Expand map status and warnings";
        }
        if (_mapStatusOverlay is not null)
            _mapStatusOverlay.Height = expanded
                ? (_warningCard?.Visible == true ? 250 : 178)
                : 38;
        PositionMapStatusOverlay();
    }

    private void PositionMapStatusOverlay()
    {
        if (_mapStatusOverlay?.Parent is not Control host) return;
        _mapStatusOverlay.Width = Math.Min(500, Math.Max(320, host.ClientSize.Width - 36));
        _mapStatusOverlay.Location = new Point(
            Math.Max(18, host.ClientSize.Width - _mapStatusOverlay.Width - 18),
            Math.Max(18, host.ClientSize.Height - _mapStatusOverlay.Height - 18));
    }

    private void SelectBrushAugment(BrushAugment augment)
    {
        _selectedBrushAugment = augment;
        _updatingBrushAugment = true;
        try
        {
            (_brushAugmentSlider.Minimum,
                _brushAugmentSlider.Maximum,
                _brushAugmentSlider.TickFrequency,
                _brushAugmentSlider.Value) = augment switch
            {
                BrushAugment.Length => (25, 200, 5, _brushLengthPercent),
                BrushAugment.Width => (25, 200, 5, _brushWidthPercent),
                BrushAugment.Scale => (1, 128, 1, _brushScale),
                BrushAugment.Rotation => (0, 359, 1, _brushRotationDegrees),
                _ => throw new ArgumentOutOfRangeException(nameof(augment))
            };
        }
        finally
        {
            _updatingBrushAugment = false;
        }
        _brushAugmentSlider.AccessibleName = $"Brush {augment.ToString().ToLowerInvariant()}";
        UpdateBrushAugmentValue();
    }

    private void ApplyBrushAugmentSlider()
    {
        if (_updatingBrushAugment) return;
        switch (_selectedBrushAugment)
        {
            case BrushAugment.Length:
                _brushLengthPercent = _brushAugmentSlider.Value;
                break;
            case BrushAugment.Width:
                _brushWidthPercent = _brushAugmentSlider.Value;
                break;
            case BrushAugment.Scale:
                _brushScale = _brushAugmentSlider.Value;
                break;
            case BrushAugment.Rotation:
                _brushRotationDegrees = _brushAugmentSlider.Value;
                break;
        }
        ApplyBrushAugmentsToCanvas();
        UpdateBrushAugmentValue();
    }

    private void ApplyBrushAugmentsToCanvas()
    {
        _canvas.BrushSize = _brushScale;
        _canvas.BrushLengthPercent = _brushLengthPercent;
        _canvas.BrushWidthPercent = _brushWidthPercent;
        _canvas.BrushRotationDegrees = _brushRotationDegrees;
        _canvas.Invalidate();
    }

    private void UpdateBrushAugmentValue()
    {
        _brushAugmentValue.Text = _selectedBrushAugment switch
        {
            BrushAugment.Length => $"Length  {_brushLengthPercent}%",
            BrushAugment.Width => $"Width  {_brushWidthPercent}%",
            BrushAugment.Scale => $"Scale  {_brushScale} cells",
            BrushAugment.Rotation => $"Rotation  {_brushRotationDegrees}°",
            _ => string.Empty
        };
    }

    private Control BuildBrushControlsRail()
    {
        var panel = new MedievalLeatherPanel { Dock = DockStyle.Fill, Padding = Padding.Empty, AutoScroll = true };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 8, 8, 10),
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildSeededMapPanel());
        var brushSection = CreateRailFeature("Brush controls", light: true);
        layout.Controls.Add(brushSection);
        brushSection.Controls.Add(ThemedFieldLabel("Brush augment"));
        var augmentPanel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(4),
            Margin = new Padding(0, 0, 0, 10),
            BackColor = MedievalTheme.Input
        };
        var augmentButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 68,
            ColumnCount = 4,
            RowCount = 2,
            Margin = Padding.Empty
        };
        for (var column = 0; column < 4; column++)
            augmentButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        augmentButtons.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        augmentButtons.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        var augmentButtonMap = new Dictionary<BrushAugment, Button>();
        foreach (var augment in Enum.GetValues<BrushAugment>())
        {
            var column = (int)augment;
            var button = new Button
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(2),
                Image = LoadBrushAugmentIcon(augment),
                ImageAlign = ContentAlignment.MiddleCenter,
                AccessibleName = $"Adjust brush {augment.ToString().ToLowerInvariant()}",
                Tag = augment
            };
            MedievalTheme.StyleSecondaryButton(button);
            button.Click += (_, _) =>
            {
                SelectBrushAugment(augment);
                foreach (var pair in augmentButtonMap)
                {
                    pair.Value.FlatAppearance.BorderColor = pair.Key == augment
                        ? MedievalTheme.GoldBright
                        : MedievalTheme.BorderStrong;
                    pair.Value.FlatAppearance.BorderSize = pair.Key == augment ? 2 : 1;
                }
            };
            augmentButtonMap[augment] = button;
            augmentButtons.Controls.Add(button, column, 0);
            augmentButtons.Controls.Add(new Label
            {
                Text = augment == BrushAugment.Rotation ? "Rotate" : augment.ToString(),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = MedievalTheme.TextMuted,
                Font = MedievalTheme.UiFont(7.5f, FontStyle.Bold),
                Margin = Padding.Empty
            }, column, 1);
        }
        augmentButtonMap[BrushAugment.Scale].FlatAppearance.BorderColor = MedievalTheme.GoldBright;
        augmentButtonMap[BrushAugment.Scale].FlatAppearance.BorderSize = 2;
        augmentPanel.Controls.Add(augmentButtons);
        _brushAugmentValue.ForeColor = MedievalTheme.GoldBright;
        _brushAugmentValue.Font = MedievalTheme.UiFont(9f, FontStyle.Bold);
        _brushAugmentValue.Margin = new Padding(4, 5, 4, 0);
        augmentPanel.Controls.Add(_brushAugmentValue);
        _brushAugmentSlider.Margin = new Padding(0, 0, 0, 2);
        augmentPanel.Controls.Add(_brushAugmentSlider);
        augmentPanel.Paint += (_, e) =>
        {
            using var border = new Pen(MedievalTheme.BorderStrong);
            e.Graphics.DrawRectangle(border, 0, 0,
                Math.Max(0, augmentPanel.Width - 1), Math.Max(0, augmentPanel.Height - 1));
        };
        brushSection.Controls.Add(augmentPanel);
        SelectBrushAugment(BrushAugment.Scale);

        _usStateBrush.Items.Add("Select a state…");
        foreach (var state in UsStateBrushCatalog.All)
            _usStateBrush.Items.Add(state);
        _usStateBrush.SelectedIndex = 0;
        brushSection.Controls.Add(ThemedLabeled("US States", _usStateBrush));
        _stateMountainToggle.Font = MedievalTheme.UiFont(8.5f);
        _stateWaterToggle.Font = MedievalTheme.UiFont(8.5f);
        _stateForestToggle.Font = MedievalTheme.UiFont(8.5f);
        brushSection.Controls.Add(_stateMountainToggle);
        brushSection.Controls.Add(_stateWaterToggle);
        brushSection.Controls.Add(_stateForestToggle);

        var updatingFeatures = false;
        void ApplyStateFeatureToggles()
        {
            if (updatingFeatures) return;
            _canvas.StateMountainsEnabled = _stateMountainToggle.Checked;
            _canvas.StateWaterEnabled = _stateWaterToggle.Checked;
            _canvas.StateForestsEnabled = _stateForestToggle.Checked;
            if (_canvas.StateBrush is { } state)
                _stateFeaturePreferences[state.Name] =
                    (_stateMountainToggle.Checked, _stateWaterToggle.Checked, _stateForestToggle.Checked);
            _canvas.Invalidate();
        }
        _stateMountainToggle.CheckedChanged += (_, _) => ApplyStateFeatureToggles();
        _stateWaterToggle.CheckedChanged += (_, _) => ApplyStateFeatureToggles();
        _stateForestToggle.CheckedChanged += (_, _) => ApplyStateFeatureToggles();

        void RestoreStateFeatureToggles()
        {
            updatingFeatures = true;
            var state = _canvas.StateBrush;
            var preference = state is null ? default : _stateFeaturePreferences.GetValueOrDefault(state.Name);
            _stateMountainToggle.Enabled = state is not null;
            _stateWaterToggle.Enabled = state is not null;
            _stateForestToggle.Enabled = state is not null;
            _stateMountainToggle.Checked = preference.Mountains;
            _stateWaterToggle.Checked = preference.Water;
            _stateForestToggle.Checked = preference.Forests;
            updatingFeatures = false;
            ApplyStateFeatureToggles();
        }

        brushSection.Controls.Add(ThemedFieldLabel("Brush shape"));
        var shapes = Enum.GetValues<BrushShape>();
        const int shapeColumnCount = 2;
        var shapeRowCount = (int)Math.Ceiling(shapes.Length / (double)shapeColumnCount);
        var shapeRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = shapeRowCount * 36,
            ColumnCount = shapeColumnCount,
            RowCount = shapeRowCount,
            Margin = new Padding(0, 0, 0, 8)
        };
        shapeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        shapeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < shapeRowCount; row++)
            shapeRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / shapeRowCount));
        var shapeButtons = new Dictionary<BrushShape, Button>();
        foreach (var shape in shapes)
        {
            var displayName = BrushShapeDisplayName(shape);
            var button = new Button
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(2),
                Image = CreateBrushShapeIcon(shape),
                AccessibleName = $"{displayName} brush",
                AccessibleDescription = $"Select the {displayName.ToLowerInvariant()} terrain brush",
                Tag = shape
            };
            MedievalTheme.StyleSecondaryButton(button);
            button.Click += (_, _) =>
            {
                _canvas.BrushShape = shape;
                _canvas.StateBrush = null;
                _usStateBrush.SelectedIndex = 0;
                RestoreStateFeatureToggles();
                UpdateShapeSelection();
                _canvas.Invalidate();
                _status.Text = $"{displayName} brush selected.";
            };
            shapeButtons[shape] = button;
            shapeRow.Controls.Add(button);
        }
        shapeButtons[BrushShape.Round].FlatAppearance.BorderColor = MedievalTheme.GoldBright;
        shapeButtons[BrushShape.Round].FlatAppearance.BorderSize = 2;
        brushSection.Controls.Add(shapeRow);

        void UpdateShapeSelection()
        {
            foreach (var pair in shapeButtons)
            {
                var selected = _canvas.StateBrush is null && pair.Key == _canvas.BrushShape;
                pair.Value.FlatAppearance.BorderColor = selected
                    ? MedievalTheme.GoldBright
                    : MedievalTheme.BorderStrong;
                pair.Value.FlatAppearance.BorderSize = selected ? 2 : 1;
            }
        }

        _usStateBrush.SelectedIndexChanged += (_, _) =>
        {
            _canvas.StateBrush = _usStateBrush.SelectedItem as UsStateBrush;
            RestoreStateFeatureToggles();
            UpdateShapeSelection();
            _canvas.Invalidate();
            _status.Text = $"{_canvas.StateBrush?.Name ?? BrushShapeDisplayName(_canvas.BrushShape)} brush selected.";
        };

        var randomSection = CreateRailFeature("Random land", light: false);
        layout.Controls.Add(randomSection);
        var overwriteHeader = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = Padding.Empty
        };
        overwriteHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        overwriteHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        overwriteHeader.Controls.Add(ThemedFieldLabel("Overwrite"));
        _randomLandOverwriteValue.ForeColor = MedievalTheme.GoldBright;
        _randomLandOverwriteValue.Font = MedievalTheme.UiFont(9f, FontStyle.Bold);
        overwriteHeader.Controls.Add(_randomLandOverwriteValue);
        randomSection.Controls.Add(overwriteHeader);
        randomSection.Controls.Add(_randomLandOverwrite);
        var randomLand = new Button
        {
            Text = "Generate",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 0, 3),
            AccessibleName = "Add random land to the map"
        };
        MedievalTheme.StyleSecondaryButton(randomLand);
        randomLand.Click += (_, _) => AddRandomLand();
        randomSection.Controls.Add(randomLand);

        panel.Controls.Add(layout);
        return panel;
    }

    private Control BuildSeededMapPanel()
    {
        var section = CreateRailFeature("Seeded map", light: false);
        section.Controls.Add(ThemedFieldLabel("Seed"));
        var seedRow = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        seedRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        seedRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        seedRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var seedInput = MedievalTheme.FrameInput(_generationSeed);
        seedInput.Margin = new Padding(0, 3, 0, 3);
        seedRow.Controls.Add(seedInput, 0, 0);
        seedRow.Controls.Add(_newGenerationSeed, 1, 0);
        section.Controls.Add(seedRow);
        var seedHint = ThemedFieldLabel("Leave blank for a random seed.", new Padding(0, 4, 0, 0));
        seedHint.Dock = DockStyle.Fill;
        section.Controls.Add(seedHint);
        section.Controls.Add(_generateSeededMap);
        return section;
    }

    private Control BuildElevationAndRampPanels()
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(2, 0, 2, 2)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _elevationMode.Text = "Paint";
        _elevationMode.AccessibleName = "Paint elevation";
        _rampMode.Text = "Draw";
        _rampMode.AccessibleName = "Draw ramps";
        var elevationContent = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1
        };
        elevationContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var resetElevation = CompactResetButton();
        resetElevation.AccessibleName = "Reset elevation";
        resetElevation.Click += (_, _) => ClearAllElevation();
        elevationContent.Controls.Add(ModeActionRow(_elevationMode, resetElevation));
        _paintCliffs.Margin = new Padding(0, 3, 0, 2);
        elevationContent.Controls.Add(_paintCliffs);
        var elevationHeader = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 7, 0, 1) };
        elevationHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        elevationHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        elevationHeader.Controls.Add(ThemedFieldLabel("Height"));
        _elevationValue.ForeColor = MedievalTheme.GoldBright;
        _elevationValue.Font = MedievalTheme.UiFont(9f, FontStyle.Bold);
        elevationHeader.Controls.Add(_elevationValue);
        elevationContent.Controls.Add(elevationHeader);
        _elevation.BackColor = MedievalTheme.Surface;
        elevationContent.Controls.Add(_elevation);
        var elevationSection = BuildInspectorSection("Elevation", elevationContent);
        elevationSection.Margin = new Padding(0, 0, 5, 0);
        row.Controls.Add(elevationSection, 0, 0);

        var rampContent = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1
        };
        rampContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var resetRamps = CompactResetButton();
        resetRamps.AccessibleName = "Reset ramps";
        resetRamps.Click += (_, _) => ClearAllRamps();
        rampContent.Controls.Add(ModeActionRow(_rampMode, resetRamps));
        var rampHint = ThemedFieldLabel("Drag over land.\nRight-click to erase.", new Padding(0, 7, 0, 0));
        rampHint.Dock = DockStyle.Fill;
        rampContent.Controls.Add(rampHint);
        var rampSection = BuildInspectorSection("Ramps", rampContent);
        rampSection.Margin = new Padding(5, 0, 0, 0);
        row.Controls.Add(rampSection, 1, 0);
        return row;
    }

    private void ShowStateGeographyDetails(IWin32Window? owner = null) => MedievalDialog.Show(owner ?? this,
        "State outlines: U.S. Census Bureau, 2024 cartographic boundaries (public domain).\n" +
        "Mountain ranges: © Global Mountain Biodiversity Assessment. Snethlage et al. (2022), " +
        "GMBA Mountain Inventory v2, CC BY 4.0 (creativecommons.org/licenses/by/4.0/). " +
        "Modified by projecting, rasterizing, and clipping mapped mountain regions to states. " +
        "Mountain ranges paint as cliffs.\n" +
        "Publication: doi.org/10.1038/s41597-022-01256-y\n" +
        "Dataset: doi.org/10.48601/earthenv-t9k2-1407\n" +
        "Water: Made with Natural Earth (public domain). Hawaii, Delaware and Rhode Island hydrography: " +
        "U.S. Geological Survey, National Hydrography Dataset (The National Map, public domain).\n" +
        "naturalearthdata.com • usgs.gov/national-hydrography\n\n" +
        "Forests: © ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021) " +
        "processed by ESA WorldCover consortium. CC BY 4.0. Modified by selecting dense forest cores, " +
        "projecting, and rasterizing.\n" +
        "Dataset: doi.org/10.5281/zenodo.7254221",
        "Geography Credits", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static Button CompactResetButton()
    {
        var button = new Button
        {
            Text = "Reset",
            AutoSize = false,
            Size = new Size(62, 28),
            Dock = DockStyle.Fill,
            Margin = new Padding(7, 2, 0, 2)
        };
        MedievalTheme.StyleSecondaryButton(button);
        return button;
    }

    private static Control ModeActionRow(CheckBox toggle, Button reset)
    {
        toggle.Dock = DockStyle.Fill;
        toggle.Margin = new Padding(0, 2, 0, 2);
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0, 2, 0, 3)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 69));
        row.Controls.Add(toggle, 0, 0);
        row.Controls.Add(reset, 1, 0);
        return row;
    }

    private static TableLayoutPanel CreateRailFeature(string title, bool light)
    {
        var section = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(11, 12, 11, 14),
            Margin = new Padding(0, 0, 0, 22),
            BackColor = light ? Color.FromArgb(52, 39, 29) : Color.FromArgb(17, 15, 13),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = MedievalTheme.CreateSectionHeading(title, 0);
        heading.Dock = DockStyle.Fill;
        heading.TextAlign = ContentAlignment.MiddleCenter;
        heading.Margin = new Padding(0, 0, 0, 4);
        section.Controls.Add(heading);
        section.Controls.Add(new Panel
        {
            Height = 1,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(150, MedievalTheme.Gold),
            Margin = new Padding(0, 0, 0, 7)
        });
        section.Paint += (_, e) =>
        {
            using var border = new Pen(Color.FromArgb(150, MedievalTheme.BorderStrong));
            e.Graphics.DrawRectangle(border, 0, 0,
                Math.Max(0, section.Width - 1), Math.Max(0, section.Height - 1));
        };
        return section;
    }

    private static Bitmap CreateBrushShapeIcon(BrushShape shape)
    {
        var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        using var fill = new SolidBrush(Color.FromArgb(150, MedievalTheme.Gold));
        using var edge = new Pen(MedievalTheme.GoldBright, 1f);
        const int radius = 7;
        const float cellSize = 1.55f;
        var origin = 16f - (radius + 0.5f) * cellSize;
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (!BrushShapeGeometry.Contains(shape, dx, dy, radius)) continue;
                var x = origin + (dx + radius) * cellSize;
                var y = origin + (dy + radius) * cellSize;
                graphics.FillRectangle(fill, x, y, cellSize + 0.2f, cellSize + 0.2f);
                if (!BrushShapeGeometry.Contains(shape, dx - 1, dy, radius))
                    graphics.DrawLine(edge, x, y, x, y + cellSize);
                if (!BrushShapeGeometry.Contains(shape, dx + 1, dy, radius))
                    graphics.DrawLine(edge, x + cellSize, y, x + cellSize, y + cellSize);
                if (!BrushShapeGeometry.Contains(shape, dx, dy - 1, radius))
                    graphics.DrawLine(edge, x, y, x + cellSize, y);
                if (!BrushShapeGeometry.Contains(shape, dx, dy + 1, radius))
                    graphics.DrawLine(edge, x, y + cellSize, x + cellSize, y + cellSize);
            }
        }
        return bitmap;
    }

    private static Bitmap LoadBrushAugmentIcon(BrushAugment augment)
    {
        var resourceName = augment switch
        {
            BrushAugment.Length => "EDMWMapDesigner.Assets.brush-augment-length.png",
            BrushAugment.Width => "EDMWMapDesigner.Assets.brush-augment-width.png",
            BrushAugment.Scale => "EDMWMapDesigner.Assets.brush-augment-scale.png",
            BrushAugment.Rotation => "EDMWMapDesigner.Assets.brush-augment-rotation.png",
            _ => throw new ArgumentOutOfRangeException(nameof(augment))
        };
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing brush augment icon: {resourceName}");
        using var source = Image.FromStream(stream, useEmbeddedColorManagement: false,
            validateImageData: true);
        var icon = new Bitmap(30, 30, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(icon);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, icon.Width, icon.Height));
        return icon;
    }

    private static string BrushShapeDisplayName(BrushShape shape) => shape switch
    {
        BrushShape.Crescent => "Crescent",
        BrushShape.ReverseCrescent => "Reverse crescent",
        BrushShape.RisingSwoop => "Rising swoop",
        BrushShape.FallingSwoop => "Falling swoop",
        BrushShape.SCurve => "Tapered S-curve",
        BrushShape.ReverseSCurve => "Reverse tapered S-curve",
        BrushShape.TaperedBlade => "Tapered blade",
        BrushShape.ReverseTaperedBlade => "Reverse tapered blade",
        BrushShape.HorizontalWave => "Horizontal wave",
        BrushShape.VerticalWave => "Vertical wave",
        BrushShape.ForwardDiagonal => "Forward diagonal",
        BrushShape.BackwardDiagonal => "Backward diagonal",
        _ => shape.ToString()
    };

    private void MatchMapRegionToDrawableSquare()
    {
        if (_mainSplit.ClientSize.Height <= 0) return;

        var maximumDistance = _mainSplit.ClientSize.Width
            - _mainSplit.Panel2MinSize
            - _mainSplit.SplitterWidth;
        if (maximumDistance < _mainSplit.Panel1MinSize) return;

        // The battlefield and frame use equal horizontal and vertical padding,
        // so adding the rail width makes the map host square without resizing its drawable area.
        var targetDistance = Math.Clamp(
            _mainSplit.ClientSize.Height + SceneRailWidth,
            _mainSplit.Panel1MinSize,
            maximumDistance);
        if (_mainSplit.SplitterDistance != targetDistance)
            _mainSplit.SplitterDistance = targetDistance;
    }

    private void PrepareMapDesignerView()
    {
        if (_mapDesignerView is null) return;

        _mapDesignerView.Dock = DockStyle.Fill;
        _mapDesignerView.Bounds = _workspaceHost.DisplayRectangle;
        _mapDesignerView.PerformLayout();
        _mainSplit.PerformLayout();
        MatchMapRegionToDrawableSquare();
        _canvas.Invalidate();
    }

    private Control BuildSidebarShell()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = MedievalTheme.Sidebar,
            Margin = Padding.Empty
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(18, 11, 18, 8), BackColor = MedievalTheme.Surface };
        header.Controls.Add(new Label { Text = "Map Generator", AutoSize = true, Font = MedievalTheme.DisplayFont(18), ForeColor = MedievalTheme.Text });
        header.Controls.Add(new Label { Text = "Terrain, rules, and multiplayer output", AutoSize = true, Font = MedievalTheme.UiFont(9.5f), ForeColor = MedievalTheme.TextMuted });
        shell.Controls.Add(header, 0, 0);

        shell.Controls.Add(BuildMapTab(), 0, 1);
        return shell;
    }

    private Control BuildMapTab()
    {
        var page = CreateThemedTab(out var panel);
        panel.ColumnCount = 1;
        panel.ColumnStyles.Clear();
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Padding = new Padding(12, 12, 12, 14);

        var projectRow = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        projectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        projectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        var nameField = ThemedLabeled("Map name", _nameBox);
        nameField.Margin = new Padding(0, 0, 7, 0);
        var authorField = ThemedLabeled("Author", _authorBox);
        authorField.Margin = new Padding(7, 0, 0, 0);
        projectRow.Controls.Add(nameField, 0, 0);
        projectRow.Controls.Add(authorField, 1, 0);
        var projectSection = BuildInspectorSection("Project", projectRow);
        projectSection.Margin = new Padding(2, 2, 2, 10);
        panel.Controls.Add(projectSection, 0, 0);

        var terrainSection = BuildInspectorSection("Terrain", BuildTerrainPalette(4));
        terrainSection.Margin = new Padding(2, 0, 2, 10);
        panel.Controls.Add(terrainSection, 0, 1);
        var resourceSection = BuildInspectorSection("Resources", BuildResourcePalette(4));
        resourceSection.Margin = new Padding(2, 0, 2, 10);
        panel.Controls.Add(resourceSection, 0, 2);
        var playerStartSection = BuildInspectorSection("Player starts", BuildPlayerStartPanel());
        playerStartSection.Margin = new Padding(2, 0, 2, 10);
        panel.Controls.Add(playerStartSection, 0, 3);
        panel.Controls.Add(BuildElevationAndRampPanels(), 0, 4);
        return page;
    }

    private static TableLayoutPanel BuildInspectorSection(string title, Control content)
    {
        var section = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12, 10, 12, 12),
            BackColor = MedievalTheme.Surface,
            Margin = Padding.Empty
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var headingRow = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.Transparent
        };
        headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = MedievalTheme.CreateSectionHeading(title, 0);
        heading.Margin = Padding.Empty;
        headingRow.Controls.Add(heading, 0, 0);
        headingRow.Controls.Add(new Panel
        {
            Height = 1,
            Dock = DockStyle.Fill,
            BackColor = MedievalTheme.Border,
            Margin = new Padding(14, 10, 0, 0)
        }, 1, 0);
        section.Controls.Add(headingRow);
        content.Dock = DockStyle.Fill;
        content.Margin = Padding.Empty;
        section.Controls.Add(content);
        section.Paint += (_, e) =>
        {
            using var border = new Pen(MedievalTheme.BorderStrong);
            e.Graphics.DrawRectangle(border, 0, 0,
                Math.Max(0, section.Width - 1), Math.Max(0, section.Height - 1));
        };
        return section;
    }

    private Control BuildTerrainPalette(int columns = 2)
    {
        var palette = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = columns,
            Margin = new Padding(0, 3, 0, 0)
        };
        for (var column = 0; column < columns; column++)
            palette.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        foreach (var terrain in TerrainCatalog.All)
        {
            if (terrain == TerrainKind.Forest) continue;
            var button = new Button
            {
                Text = terrain.DisplayName(),
                Tag = terrain,
                Height = 50,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = MedievalTheme.SurfaceRaised,
                ForeColor = MedievalTheme.Text,
                Font = MedievalTheme.UiFont(9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Margin = new Padding(3),
                Image = TerrainSwatch(terrain.Color()),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(7, 0, 3, 0)
            };
            button.FlatAppearance.BorderColor = MedievalTheme.Border;
            button.FlatAppearance.BorderSize = 1;
            button.Click += (_, _) => SelectTerrain((TerrainKind)button.Tag);
            _paletteButtons[terrain] = button;
            palette.Controls.Add(button);
        }
        return palette;
    }

    private static Bitmap TerrainSwatch(Color color)
    {
        var bitmap = new Bitmap(18, 18);
        using var graphics = Graphics.FromImage(bitmap);
        using var fill = new SolidBrush(color);
        graphics.FillRectangle(fill, 1, 1, 16, 16);
        using var border = new Pen(MedievalTheme.BorderStrong);
        graphics.DrawRectangle(border, 0, 0, 17, 17);
        return bitmap;
    }

    private Control BuildResourcePalette(int columns = 2)
    {
        var container = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Margin = new Padding(0, 3, 0, 0)
        };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var reset = new Button
        {
            Text = "Reset",
            AutoSize = false,
            Size = new Size(70, 30),
            Dock = DockStyle.Fill,
            Margin = new Padding(8, 0, 0, 5)
        };
        MedievalTheme.StyleSecondaryButton(reset);
        reset.Click += (_, _) => ClearAllManualResources();
        _manualResourceToggle.Text = "Edit resources";
        _manualResourceToggle.Margin = new Padding(3, 0, 0, 5);
        container.Controls.Add(ModeActionRow(_manualResourceToggle, reset));

        var palette = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = columns,
            Margin = Padding.Empty
        };
        for (var column = 0; column < columns; column++)
            palette.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        _forestResourceButton = ResourcePaletteButton("Forest", TerrainKind.Forest.Color());
        _forestResourceButton.Tag = TerrainKind.Forest;
        _forestResourceButton.Click += (_, _) => SelectForestResource();
        palette.Controls.Add(_forestResourceButton);
        foreach (var kind in ManualResourceKindExtensions.PaletteKinds)
        {
            var button = ResourcePaletteButton(kind.DisplayName(), ManualResourceColor(kind));
            button.Tag = kind;
            button.Click += (_, _) => SelectManualResource((ManualResourceKind)button.Tag);
            _resourcePaletteButtons[kind] = button;
            palette.Controls.Add(button);
        }
        container.Controls.Add(palette);
        SelectManualResource(ManualResourceKind.Gold);
        return container;
    }

    private Control BuildPlayerStartPanel()
    {
        var container = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Margin = new Padding(0, 3, 0, 0)
        };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var reset = new Button
        {
            Text = "Reset",
            AutoSize = false,
            Size = new Size(70, 30),
            Dock = DockStyle.Fill,
            Margin = new Padding(8, 0, 0, 5)
        };
        MedievalTheme.StyleSecondaryButton(reset);
        reset.Click += (_, _) => ClearAllManualPlayerStarts();
        _manualPlayerStartToggle.Text = "Edit player starts";
        _manualPlayerStartToggle.Margin = new Padding(3, 0, 0, 5);
        var modeRow = ModeActionRow(_manualPlayerStartToggle, reset);
        container.Controls.Add(modeRow);

        return container;
    }

    private static Button ResourcePaletteButton(string text, Color color)
    {
        var button = new Button
        {
            Text = text,
            Height = 50,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = MedievalTheme.SurfaceRaised,
            ForeColor = MedievalTheme.Text,
            Font = MedievalTheme.UiFont(9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(3),
            Image = TerrainSwatch(color),
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(7, 0, 3, 0)
        };
        button.FlatAppearance.BorderColor = MedievalTheme.Border;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    private void SelectManualResource(ManualResourceKind kind)
    {
        if (!kind.IsUserPlaceable()) return;
        _forestResourceSelected = false;
        _canvas.SelectedManualResource = kind;
        if (_forestResourceButton is not null)
        {
            _forestResourceButton.FlatAppearance.BorderColor = MedievalTheme.Border;
            _forestResourceButton.FlatAppearance.BorderSize = 1;
        }
        foreach (var pair in _resourcePaletteButtons)
        {
            var selected = pair.Key == kind;
            pair.Value.FlatAppearance.BorderColor = selected ? MedievalTheme.GoldBright : MedievalTheme.Border;
            pair.Value.FlatAppearance.BorderSize = selected ? 3 : 1;
        }
        ApplyResourceDrawingTool();
        _status.Text = $"Selected resource: {kind.DisplayName()} ({(kind.RequiresWater() ? "water" : "land")} only)";
    }

    private void SelectForestResource()
    {
        _forestResourceSelected = true;
        foreach (var button in _resourcePaletteButtons.Values)
        {
            button.FlatAppearance.BorderColor = MedievalTheme.Border;
            button.FlatAppearance.BorderSize = 1;
        }
        if (_forestResourceButton is not null)
        {
            _forestResourceButton.FlatAppearance.BorderColor = MedievalTheme.GoldBright;
            _forestResourceButton.FlatAppearance.BorderSize = 3;
        }
        ApplyResourceDrawingTool();
        _status.Text = _manualResourceToggle.Checked
            ? "Selected resource brush: Forest"
            : "Forest selected; enable Edit resources to paint it.";
    }

    private void ApplyResourceDrawingTool()
    {
        _canvas.StateFeaturesAllowed = !_manualResourceToggle.Checked;
        _canvas.Invalidate();
        if (!_manualResourceToggle.Checked)
        {
            _canvas.ManualResourceMode = false;
            if (_forestResourceSelected && _canvas.SelectedTerrain == TerrainKind.Forest)
                ApplyTerrainSelection(_terrainBeforeResourceEditing);
            return;
        }

        if (_forestResourceSelected)
        {
            if (_canvas.SelectedTerrain != TerrainKind.Forest)
                _terrainBeforeResourceEditing = _canvas.SelectedTerrain;
            _canvas.ManualResourceMode = false;
            ApplyTerrainSelection(TerrainKind.Forest);
            _activeTerrain.Text = "Active resource brush: Forest";
        }
        else
        {
            if (_canvas.SelectedTerrain == TerrainKind.Forest)
                ApplyTerrainSelection(_terrainBeforeResourceEditing);
            _canvas.ManualResourceMode = true;
        }
    }

    private static Color ManualResourceColor(ManualResourceKind kind) => kind switch
    {
        ManualResourceKind.Gold => Color.Gold,
        ManualResourceKind.Stone => Color.SlateGray,
        ManualResourceKind.BerryBush => Color.MediumVioletRed,
        ManualResourceKind.Fish => Color.DeepSkyBlue,
        ManualResourceKind.Wildlife => Color.SaddleBrown,
        ManualResourceKind.Deer => Color.Peru,
        ManualResourceKind.Goat => Color.DarkKhaki,
        ManualResourceKind.BighornSheep => Color.Tan,
        ManualResourceKind.Tiger => Color.DarkOrange,
        ManualResourceKind.Bear => Color.Sienna,
        ManualResourceKind.Shark => Color.SteelBlue,
        ManualResourceKind.Squirrel => Color.Chocolate,
        ManualResourceKind.Sheep => Color.Wheat,
        ManualResourceKind.BlueBird => Color.Goldenrod,
        ManualResourceKind.Walrus => Color.RosyBrown,
        ManualResourceKind.Wolf => Color.DimGray,
        ManualResourceKind.Chicken => Color.Firebrick,
        ManualResourceKind.Horse => Color.DarkGoldenrod,
        ManualResourceKind.Dolphin => Color.DodgerBlue,
        ManualResourceKind.WildPig => Color.IndianRed,
        ManualResourceKind.Elk => Color.OliveDrab,
        _ => Color.White
    };

    private static Control CreateThemedTab(out TableLayoutPanel panel)
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = MedievalTheme.Sidebar,
            ForeColor = MedievalTheme.Text,
            Padding = Padding.Empty
        };
        panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            Padding = new Padding(18, 10, 18, 18),
            BackColor = Color.Transparent
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(panel);
        return page;
    }

    private static Control ThemedLabeled(string label, Control control)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Margin = new Padding(0, 3, 0, 7)
        };
        panel.Controls.Add(ThemedFieldLabel(label));
        panel.Controls.Add(MedievalTheme.FrameInput(control));
        return panel;
    }

    private static Control MetricTile(string title, Label value)
    {
        value.AutoSize = false;
        value.Dock = DockStyle.Fill;
        value.Height = 28;
        value.TextAlign = ContentAlignment.MiddleCenter;
        value.ForeColor = MedievalTheme.GoldBright;
        value.Font = MedievalTheme.UiFont(12f, FontStyle.Bold);
        var tile = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(4, 5, 4, 5),
            Margin = new Padding(2),
            BackColor = MedievalTheme.Input
        };
        tile.Controls.Add(new Label
        {
            Text = title,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 17,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = MedievalTheme.TextMuted,
            Font = MedievalTheme.UiFont(7.5f, FontStyle.Bold)
        });
        tile.Controls.Add(value);
        return tile;
    }

    private static void AddThemedSection(TableLayoutPanel panel, string text, int topMargin)
    {
        panel.Controls.Add(MedievalTheme.CreateSectionHeading(text, topMargin));
        panel.Controls.Add(new Panel
        {
            Height = 1,
            Dock = DockStyle.Fill,
            BackColor = MedievalTheme.Border,
            Margin = new Padding(0, 0, 0, 8)
        });
    }

    private static Label ThemedFieldLabel(string text, Padding? margin = null) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = MedievalTheme.TextMuted,
        Font = MedievalTheme.UiFont(8.75f),
        Margin = margin ?? new Padding(0, 0, 0, 4)
    };

    private static void StyleSlider(MedievalSlider slider)
    {
        slider.BackColor = MedievalTheme.Sidebar;
        slider.ForeColor = MedievalTheme.Gold;
    }

    private static void StyleCheckBox(CheckBox checkBox)
    {
        checkBox.ForeColor = MedievalTheme.Text;
        checkBox.BackColor = Color.Transparent;
        checkBox.FlatStyle = FlatStyle.Flat;
        checkBox.Margin = new Padding(0, 4, 0, 4);
    }

    private ToolStrip BuildToolStrip()
    {
        var renderer = new MedievalToolStripRenderer { IsHomeScreen = _activeView == AppView.Home };
        var strip = new ToolStrip
        {
            Dock = DockStyle.None,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Location = Point.Empty,
            Width = ClientSize.Width,
            GripStyle = ToolStripGripStyle.Hidden,
            AutoSize = false,
            Height = 76,
            ImageScalingSize = new Size(18, 18),
            BackColor = MedievalTheme.Shell,
            ForeColor = MedievalTheme.Text,
            Padding = new Padding(8, 6, 8, 6),
            Renderer = renderer
        };
        _toolbarRenderer = renderer;
        _toolbar = strip;
        strip.MouseDown += HeaderMouseDown;
        strip.MouseDoubleClick += HeaderMouseDoubleClick;

        strip.Items.Add(WindowButton("\uE8BB", "Close", "window-close", (_, _) => Close()));
        _maximizeWindowButton = WindowButton("\uE922", "Maximize", "window-maximize", (_, _) => ToggleMaximize());
        strip.Items.Add(_maximizeWindowButton);
        strip.Items.Add(WindowButton("\uE921", "Minimize", "window-minimize", (_, _) => WindowState = FormWindowState.Minimized));
        var settingsButton = WindowButton("\uE713", "Game installation settings", "settings", (_, _) => ShowGameSettings());
        settingsButton.Size = new Size(38, 34);
        strip.Items.Add(settingsButton);
        var studioLabel = new ToolStripLabel("EDMW Mod Studio")
        {
            Image = MedievalIconFactory.Create(MedievalIcon.Generator, 20),
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Font = MedievalTheme.DisplayFont(12f),
            ForeColor = MedievalTheme.GoldBright,
            Margin = new Padding(4, 0, 16, 0)
        };
        strip.Items.Add(studioLabel);
        _homeButton = Button("Home", MedievalIcon.Generator, "Return to the workspace chooser", (_, _) => ShowView(AppView.Home));
        strip.Items.Add(_homeButton);
        _viewSwitchButton = Button("Civilizations", MedievalIcon.Civilizations,
            "Open your civilization workspace", (_, _) => ShowView(
                _activeView == AppView.Civilizations ? AppView.MapGenerator : AppView.Civilizations));
        strip.Items.Add(_viewSwitchButton);
        _friendsButton = Button("Friends", MedievalIcon.Bundle, "Connect to your online friends, share a map, and manage requests", (_, _) => ShowFriends());
        strip.Items.Add(_friendsButton);

        var centerSpacer = new ToolStripLabel { AutoSize = false, Width = 0 };
        var newButton = Button("New", MedievalIcon.NewMap, "Create a new map project", (_, _) => NewProject());
        var openButton = Button("Open", MedievalIcon.Open, "Open a map project", (_, _) => OpenProject());
        var saveButton = Button("Save", MedievalIcon.Save, "Save the current map project", (_, _) => SaveProject(false));
        var lanExportButton = Button("Export over LAN", MedievalIcon.Bundle,
            "Install this map here and send it to every designer on the LAN for acceptance", (_, _) => ExportMapOverLan(), true);
        var friendsExportButton = Button("Export to friends", MedievalIcon.Bundle,
            "Install this map here and offer it to selected online friends", async (_, _) => await ExportMapToFriendsAsync(), true);
        _hideMapPreviewToggle.Margin = Padding.Empty;
        var exportOptions = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        exportOptions.Controls.Add(_hideMapPreviewToggle, 0, 0);
        var exportOptionsHost = new ToolStripControlHost(exportOptions)
        {
            AutoSize = true,
            Margin = new Padding(8, 0, 8, 0),
            ToolTipText = "Hide the map preview sent to other players"
        };
        _launchButton = Button("Launch EDMW", MedievalIcon.Generator, "Launch Empires: Dawn of the Modern World", (_, _) => LaunchEdmw());
        strip.Items.Add(centerSpacer);
        foreach (var item in new ToolStripItem[]
                 {
                     newButton, openButton, saveButton, new ToolStripSeparator(),
                     lanExportButton, friendsExportButton, exportOptionsHost
                 })
        {
            _mapToolbarItems.Add(item);
            strip.Items.Add(item);
        }
        strip.Items.Add(_launchButton);
        void CenterPrimaryActions()
        {
            // Keep both export choices and Friends directly accessible on the
            // supported 1200-pixel workspace; icon actions retain their labels
            // for tooltips and accessibility.
            var compact = strip.ClientSize.Width < 1500 && _activeView == AppView.MapGenerator;
            studioLabel.Visible = !compact;
            foreach (var action in new[] { newButton, openButton, saveButton })
                action.DisplayStyle = compact ? ToolStripItemDisplayStyle.Image : ToolStripItemDisplayStyle.ImageAndText;
            _viewSwitchButton.DisplayStyle = compact ? ToolStripItemDisplayStyle.Image : ToolStripItemDisplayStyle.ImageAndText;
            var groupWidth = _mapToolbarItems
                .Where(item => item.Visible)
                .Sum(item => item.Width + item.Margin.Horizontal);
            if (_launchButton.Visible)
                groupWidth += _launchButton.Width + _launchButton.Margin.Horizontal;
            var leftWidth = strip.Items.Cast<ToolStripItem>()
                .TakeWhile(item => item != centerSpacer)
                .Where(item => item.Alignment == ToolStripItemAlignment.Left)
                .Sum(item => item.Width + item.Margin.Horizontal);
            centerSpacer.Width = Math.Max(0, (strip.ClientSize.Width - groupWidth) / 2 - leftWidth);
        }
        _centerToolbarActions = CenterPrimaryActions;
        strip.SizeChanged += (_, _) => CenterPrimaryActions();
        strip.HandleCreated += (_, _) => BeginInvoke(CenterPrimaryActions);
        return strip;
    }

    private static void ConfigureDropDown(ToolStripDropDownButton menu)
    {
        menu.ForeColor = MedievalTheme.Text;
        menu.DropDown.BackColor = MedievalTheme.Sidebar;
        menu.DropDown.Padding = new Padding(2);
        menu.DropDown.Renderer = new MedievalToolStripRenderer();
    }

    private static ToolStripMenuItem MenuItem(
        string text, MedievalIcon icon, EventHandler click, string shortcut = "")
    {
        var item = new ToolStripMenuItem(text)
        {
            AutoSize = false,
            Size = new Size(238, 34),
            Image = MedievalIconFactory.Create(icon),
            ShortcutKeyDisplayString = shortcut,
            Font = MedievalTheme.UiFont(9f),
            ForeColor = MedievalTheme.Text,
            BackColor = MedievalTheme.Sidebar
        };
        item.Click += click;
        return item;
    }

    private static ToolStripButton WindowButton(string glyph, string toolTip, string tag, EventHandler click)
    {
        var button = new ToolStripButton(glyph)
        {
            Alignment = ToolStripItemAlignment.Right,
            AutoSize = false,
            Size = new Size(44, 34),
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Font = new Font("Segoe MDL2 Assets", 9f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = MedievalTheme.TextMuted,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Tag = tag,
            ToolTipText = toolTip,
            AccessibleName = toolTip
        };
        button.Click += click;
        return button;
    }

    private void HeaderMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || sender is not ToolStrip strip) return;
        if (strip.GetItemAt(e.Location) is not null) return;
        MedievalWindowChrome.BeginDrag(this);
    }

    private void HeaderMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || sender is not ToolStrip strip) return;
        if (strip.GetItemAt(e.Location) is ToolStripButton) return;
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
            return;
        }

        UpdateMaximizedBounds();
        WindowState = FormWindowState.Maximized;
    }

    private void UpdateMaximizedBounds()
    {
        if (!IsHandleCreated) return;
        MaximizedBounds = Screen.FromHandle(Handle).Bounds;
    }

    private void MaximizeToCurrentScreen()
    {
        if (!IsHandleCreated) return;

        UpdateMaximizedBounds();
        var targetBounds = Screen.FromHandle(Handle).Bounds;
        if (WindowState == FormWindowState.Maximized && Bounds == targetBounds) return;

        WindowState = FormWindowState.Normal;
        WindowState = FormWindowState.Maximized;
    }

    private void UpdateMaximizeWindowButton()
    {
        if (_maximizeWindowButton is null) return;
        var maximized = WindowState == FormWindowState.Maximized;
        _maximizeWindowButton.Text = maximized ? "\uE923" : "\uE922";
        _maximizeWindowButton.ToolTipText = maximized ? "Restore" : "Maximize";
        _maximizeWindowButton.AccessibleName = _maximizeWindowButton.ToolTipText;
    }

    private StatusStrip BuildStatusStrip()
    {
        var strip = new StatusStrip
        {
            AutoSize = false,
            Height = 27,
            BackColor = MedievalTheme.Shell,
            ForeColor = MedievalTheme.TextMuted,
            SizingGrip = false,
            Padding = new Padding(4, 1, 4, 1),
            Renderer = new MedievalStatusStripRenderer()
        };
        _status.ForeColor = MedievalTheme.TextMuted;
        _status.Font = MedievalTheme.UiFont(8.5f);
        _status.Margin = new Padding(3, 1, 3, 1);
        strip.Items.Add(_status);
        return strip;
    }

    private static ToolStripButton Button(string text, MedievalIcon icon, string toolTip, EventHandler click, bool accent = false)
    {
        var button = new ToolStripButton(text)
        {
            AutoSize = true,
            ToolTipText = toolTip,
            Image = MedievalIconFactory.Create(icon),
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Font = MedievalTheme.UiFont(9f, accent ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(4, 0, 4, 0),
            Padding = new Padding(6, 2, 7, 2),
            Tag = accent ? "primary" : null
        };
        button.Click += click;
        return button;
    }

    private void SelectTerrain(TerrainKind terrain)
    {
        if (terrain == TerrainKind.Forest)
        {
            SelectForestResource();
            _manualResourceToggle.Checked = true;
            return;
        }
        if (_manualResourceToggle.Checked) _manualResourceToggle.Checked = false;
        ApplyTerrainSelection(terrain);
        _status.Text = $"Selected: {terrain.DisplayName()}";
    }

    private void ApplyTerrainSelection(TerrainKind terrain)
    {
        _canvas.SelectedTerrain = terrain;
        foreach (var pair in _paletteButtons)
        {
            var selected = pair.Key == terrain;
            pair.Value.FlatAppearance.BorderColor = selected ? MedievalTheme.GoldBright : MedievalTheme.Border;
            pair.Value.FlatAppearance.BorderSize = selected ? 3 : 1;
        }
        _activeTerrain.Text = $"Active terrain: {terrain.DisplayName()}";
    }

    private void SetElevation(int elevation)
    {
        elevation = Math.Clamp(elevation, -100, 100);
        if (_elevation.Value != elevation) _elevation.Value = elevation;
        _canvas.SelectedElevation = checked((sbyte)elevation);
        _elevationValue.Text = elevation.ToString("+#;-#;0");
        if (_elevationMode.Checked)
            _status.Text = ElevationPaintStatus();
    }

    private string ElevationPaintStatus() => _paintCliffs.Checked
        ? $"Paint cliffs at elevation {_elevation.Value:+#;-#;0}; right-click to sample."
        : $"Paint elevation {_elevation.Value:+#;-#;0}; right-click to sample.";

    private async void ClearAllAnchors()
    {
        if (!_project.ComponentRoles.Any(role => role != (byte)SemanticAnchorRole.None)) return;
        var snapshot = _project.Clone();
        _canvas.ClearSemanticAnchors();
        CommitUndo(snapshot);
        MarkDirty();
        UpdateAnalysis();
        if (MapSessionConnected)
            await ShareCurrentMapAsync();
    }

    private async void ClearAllElevation()
    {
        if (!_project.ElevationMask.Any(value => value != MapProject.AutomaticElevation) &&
            !_project.CliffMask.Any(value => value != 0)) return;
        var snapshot = _project.Clone();
        _canvas.ClearElevationMask();
        CommitUndo(snapshot);
        MarkDirty();
        UpdateAnalysis();
        if (MapSessionConnected)
            await ShareCurrentMapAsync();
    }

    private async void ClearAllRamps()
    {
        if (!_project.RampMask.Any(value => value != 0)) return;
        var snapshot = _project.Clone();
        _canvas.ClearRampMask();
        CommitUndo(snapshot);
        MarkDirty();
        UpdateAnalysis();
        if (MapSessionConnected)
            await ShareCurrentMapAsync();
    }

    private async void ClearAllManualResources()
    {
        if (_project.ManualResources.Count == 0) return;
        var snapshot = _project.Clone();
        _project.ManualResources.Clear();
        _canvas.Invalidate();
        CommitUndo(snapshot);
        MarkDirty();
        UpdateAnalysis();
        if (MapSessionConnected) await ShareCurrentMapAsync();
    }

    private async void ClearAllManualPlayerStarts()
    {
        if (_project.ManualPlayerStarts.Count == 0) return;
        var snapshot = _project.Clone();
        _canvas.ClearManualPlayerStarts();
        CommitUndo(snapshot);
        MarkDirty();
        UpdateAnalysis();
        if (MapSessionConnected) await ShareCurrentMapAsync();
    }

    private static int NewGenerationSeed() => (int)Random.Shared.NextInt64(int.MinValue, (long)int.MaxValue + 1);

    private void RefreshGenerationSeed() =>
        _generationSeed.Text = _project.GenerationSeed?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private async Task GenerateSeededMapAsync()
    {
        if (_generatingSeededMap) return;
        var input = _generationSeed.Text.Trim();
        var seed = NewGenerationSeed();
        if (input.Length > 0 && !int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
        {
            _status.Text = "Seed must be a whole number from -2147483648 to 2147483647.";
            MedievalDialog.Show(this, _status.Text, "Invalid Seed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _generationSeed.Focus();
            _generationSeed.SelectAll();
            return;
        }

        _generatingSeededMap = true;
        _generateSeededMap.Enabled = false;
        _newGenerationSeed.Enabled = false;
        UseWaitCursor = true;
        try
        {
            MapProject generated;
            try
            {
                generated = SeededMapGenerator.Generate(_project.GridSize, seed);
                generated.Name = _project.Name;
                generated.Author = _project.Author;
                generated.Variance = _project.Variance;
                generated.GenerationSeed = seed;
                generated.Validate();
            }
            catch (Exception)
            {
                ShowError("Could not generate the map");
                return;
            }

            if (MapDelta.Create(_project, generated) is null)
            {
                RefreshGenerationSeed();
                _status.Text = $"Map already matches seed {seed}.";
                return;
            }
            var snapshot = _project;
            _project = generated;
            _canvas.Project = generated;
            RefreshGenerationSeed();
            ResetManualDrawingTools();
            CommitUndo(snapshot);
            MarkDirty();
            UpdateAnalysis();
            _status.Text = $"Generated map with seed {seed}. Ctrl+Z to undo.";
            if (MapSessionConnected) await ShareCurrentMapAsync();
        }
        finally
        {
            UseWaitCursor = false;
            _generatingSeededMap = false;
            _generateSeededMap.Enabled = true;
            _newGenerationSeed.Enabled = true;
        }
    }

    private async void AddRandomLand()
    {
        var overwritePercentage = _randomLandOverwrite.Value;
        var requestedTerrain = _canvas.SelectedTerrain;
        var terrain = requestedTerrain.IsLand() ? requestedTerrain : TerrainKind.Grassland;
        var hasWater = _project.Cells.Any(value => ((TerrainKind)value).IsWater());
        var hasDifferentLand = _project.Cells.Any(value =>
        {
            var existing = (TerrainKind)value;
            return existing.IsLand() && existing != terrain;
        });

        if (!hasWater && overwritePercentage == 0)
        {
            _status.Text = "The drawing area is entirely land. Increase overwrite to repaint some of it.";
            return;
        }
        if (!hasWater && !hasDifferentLand)
        {
            _status.Text = $"All existing land is already {terrain.DisplayName()}.";
            return;
        }

        var snapshot = _project.Clone();
        try
        {
            var result = RandomLandGenerator.Add(_project, terrain, overwritePercentage);
            if (result.ChangedCells == 0)
            {
                _status.Text = $"The random patches did not touch any eligible cells for {terrain.DisplayName()}.";
                return;
            }

            CommitUndo(snapshot);
            _canvas.Project = _project;
            MarkDirty();
            UpdateAnalysis();
            if (MapSessionConnected)
                await ShareCurrentMapAsync();
            var fallback = requestedTerrain == terrain
                ? string.Empty
                : " (Grassland substituted for the selected water terrain)";
            _status.Text = $"Added {result.AddedCells:N0} new and overwrote " +
                           $"{result.OverwrittenCells:N0} existing {terrain.DisplayName()} cells " +
                           $"({result.OverwritePercentage}%) across {result.PatchesAdded} random land patches{fallback}.";
        }
        catch (Exception)
        {
            _project = snapshot;
            _canvas.Project = snapshot;
            UpdateAnalysis();
            ShowError("Could not add random land");
        }
    }

    private void SetProject(MapProject project, string? path, bool dirty)
    {
        _project = project;
        _projectPath = path;
        RefreshGenerationSeed();
        _canvas.Project = project;
        _canvas.ResetView();
        _nameBox.Text = project.Name;
        _authorBox.Text = project.Author;
        _gridSize.Value = project.GridSize;
        _variance.Value = project.Variance;
        _varianceValue.Text = $"{project.Variance}%";
        ResetManualDrawingTools();
        if (!_onlineCollaboration.IsConnected) _lastSharedProject = project.Clone();
        _undo.Clear();
        _redo.Clear();
        _dirty = dirty;
        UpdateAnalysis();
        UpdateTitle();
    }

    private void ResizeGrid()
    {
        if (_applyingRemoteProject) return;
        var newSize = (int)_gridSize.Value;
        if (newSize == _project.GridSize) return;
        var snapshot = _project.Clone();
        _project.Resize(newSize);
        CommitUndo(snapshot);
        _canvas.Project = _project;
        _canvas.ResetView();
        MarkDirty();
        UpdateAnalysis();
        if (MapSessionConnected) QueueCollaborationUpdate();
    }

    private void CommitUndo(MapProject before)
    {
        var redo = MapDelta.Create(before, _project);
        var undo = MapDelta.Create(_project, before);
        if (redo is null || undo is null) return;
        _undo.Push(new UndoEntry(undo, redo));
        while (_undo.Count > 50)
        {
            var keep = _undo.Take(50).Reverse().ToArray();
            _undo.Clear();
            foreach (var item in keep) _undo.Push(item);
        }
        _redo.Clear();
    }

    private async void Undo()
    {
        if (_undo.Count == 0) return;
        var operation = _undo.Pop();
        operation.Undo.ApplyTo(_project);
        if (operation.Undo.GenerationSeedChanged) RefreshGenerationSeed();
        _redo.Push(operation);
        _canvas.Project = _project;
        ResetManualDrawingTools();
        MarkDirty();
        UpdateAnalysis();
        if (MapSessionConnected) await ShareCurrentMapAsync();
    }

    private async void Redo()
    {
        if (_redo.Count == 0) return;
        var operation = _redo.Pop();
        operation.Redo.ApplyTo(_project);
        if (operation.Redo.GenerationSeedChanged) RefreshGenerationSeed();
        _undo.Push(operation);
        _canvas.Project = _project;
        ResetManualDrawingTools();
        MarkDirty();
        UpdateAnalysis();
        if (MapSessionConnected) await ShareCurrentMapAsync();
    }

    private void RestoreSnapshot(MapProject snapshot)
    {
        _project = snapshot;
        _canvas.Project = snapshot;
        RefreshGenerationSeed();
        _gridSize.Value = snapshot.GridSize;
        _nameBox.Text = snapshot.Name;
        _authorBox.Text = snapshot.Author;
        _variance.Value = snapshot.Variance;
        ResetManualDrawingTools();
        MarkDirty();
        UpdateAnalysis();
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();
    }

    private void ResetManualDrawingTools()
    {
        _manualResourceToggle.Checked = false;
        _canvas.ManualResourceMode = false;
        _manualPlayerStartToggle.Checked = false;
        _canvas.ManualPlayerStartMode = false;
    }

    private async Task HostCollaborationAsync()
    {
        if (MapSessionConnected || _collaboration.IsHosting)
        {
            _status.Text = "Collaboration is already active.";
            UpdateCollaborationPanel();
            return;
        }

        // Start listening before displaying the address. The host dialog is modal, so
        // waiting until it closes would make an otherwise correct join attempt fail.
        Task hostingTask;
        try { hostingTask = _collaboration.StartHostingAsync(() => _project.Clone(), _project.Author); }
        catch (Exception)
        {
            _status.Text = "Hosting could not be started.";
            UpdateCollaborationPanel();
            return;
        }
        _lastSharedProject = _project.Clone();
        _collaborationFullProjectPending = false;
        _connectedCollaborators.Clear();
        _hostLanAddress = $"{LocalLanAddresses()[0]}:{LanCollaborationSession.DefaultPort}";
        UpdateCollaborationPanel();
        _status.Text = $"Hosting on {_hostLanAddress}.";
        await hostingTask;
        UpdateCollaborationPanel();
    }

    private async Task JoinCollaborationAsync()
    {
        if (MapSessionConnected || _collaboration.IsHosting)
        {
            _status.Text = "Leave the current session before joining another.";
            return;
        }

        _status.Text = "Looking for LAN editors...";
        var hosts = await LanCollaborationSession.DiscoverHostsAsync();
        var selection = ChooseLanHost(hosts);
        if (selection is null)
        {
            return;
        }
        _connectedCollaborators.Clear();
        _collaborationFullProjectPending = false;
        _hostLanAddress = $"{selection.Address}:{selection.Port}";
        Enabled = false;
        UseWaitCursor = true;
        try
        {
            _ = await _collaboration.JoinAsync(selection.Address, _project.Author, selection.Port);
        }
        catch (Exception)
        {
            _hostLanAddress = null;
            _status.Text = "Could not connect to the collaboration host.";
        }
        finally
        {
            UseWaitCursor = false;
            Enabled = true;
        }
        UpdateCollaborationPanel();
    }

    private LanHost? ChooseLanHost(IReadOnlyList<LanHost> hosts)
    {
        using var dialog = new Form
        {
            Text = "Join LAN collaboration",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(460, Math.Max(150, 86 + Math.Min(hosts.Count, 6) * 42)),
            BackColor = MedievalTheme.Surface,
            ForeColor = MedievalTheme.Text,
            Font = MedievalTheme.UiFont()
        };
        var title = new Label
        {
            Text = hosts.Count == 0 ? "No editors are listening on this LAN." : "Editors listening on this LAN:",
            AutoSize = true,
            Location = new Point(18, 16),
            ForeColor = MedievalTheme.Text
        };
        dialog.Controls.Add(title);
        LanHost? selected = null;
        var list = new FlowLayoutPanel { Location = new Point(18, 43), Size = new Size(424, Math.Max(35, Math.Min(hosts.Count, 6) * 40)), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = hosts.Count > 6 };
        foreach (var host in hosts)
        {
            var row = new Panel { Size = new Size(408, 36), BackColor = MedievalTheme.Input };
            row.Controls.Add(new Label { Text = $"{host.Author} · {host.Address}:{host.Port}", AutoSize = true, Location = new Point(8, 10), ForeColor = MedievalTheme.Text });
            var connect = new Button { Text = "Connect", Size = new Size(82, 26), Location = new Point(318, 5), DialogResult = DialogResult.OK };
            MedievalTheme.StyleSecondaryButton(connect);
            connect.Click += (_, _) => selected = host;
            row.Controls.Add(connect);
            list.Controls.Add(row);
        }
        dialog.Controls.Add(list);
        var manual = new Button { Text = "Enter address...", Size = new Size(118, 28), Location = new Point(18, dialog.ClientSize.Height - 40) };
        MedievalTheme.StyleSecondaryButton(manual);
        manual.Click += (_, _) =>
        {
            var endpoint = PromptForText("Join LAN collaboration", "Host address (for example, 192.168.1.25:42817):");
            if (string.IsNullOrWhiteSpace(endpoint) || !TryParseEndpoint(endpoint.Trim(), out var address, out var port)) return;
            selected = new LanHost(address, port, address);
            dialog.DialogResult = DialogResult.OK;
        };
        dialog.Controls.Add(manual);
        var cancel = new Button { Text = "Cancel", Size = new Size(82, 28), Location = new Point(360, dialog.ClientSize.Height - 40), DialogResult = DialogResult.Cancel };
        MedievalTheme.StyleSecondaryButton(cancel);
        dialog.Controls.Add(cancel);
        return dialog.ShowDialog(this) == DialogResult.OK ? selected : null;
    }

    private void DisconnectCollaboration()
    {
        if (!_collaboration.IsConnected && !_collaboration.IsHosting) return;
        _collaboration.Stop();
        _collaborationSyncTimer.Stop();
        _collaborationUpdatePending = false;
        _collaborationUpdateInFlight = false;
        _collaborationFullProjectPending = false;
        foreach (var collaborator in _connectedCollaborators.Keys.ToArray()) _canvas.RemoveRemoteCursor(collaborator);
        _connectedCollaborators.Clear();
        _hostLanAddress = null;
        _status.Text = "Collaboration disconnected.";
        UpdateCollaborationPanel();
    }

    private void UpdateCollaborationPanel()
    {
        _updatingCollaborationPanel = true;
        try
        {
            _hostListeningToggle.Checked = _collaboration.IsHosting;
            _hostListeningToggle.Enabled = !_onlineCollaboration.IsConnected;
            _joinCollaborationButton.Enabled = !MapSessionConnected;
            _collaboratorList.SuspendLayout();
            _collaboratorList.Controls.Clear();
            if (!MapSessionConnected)
            {
                _collaboratorList.Controls.Add(new Label { Text = "No active session", AutoSize = true, ForeColor = MedievalTheme.TextMuted, Font = MedievalTheme.UiFont(8.25f) });
                return;
            }
            var you = _onlineCollaboration.IsConnected
                ? $"You · {_online.User?.DisplayName} · Online"
                : _collaboration.IsHosting && !string.IsNullOrWhiteSpace(_hostLanAddress)
                ? $"You · {_project.Author} · {_hostLanAddress}"
                : $"You · {_project.Author}";
            _collaboratorList.Controls.Add(new Label { Text = you, AutoSize = true, ForeColor = MedievalTheme.GoldBright, Font = MedievalTheme.UiFont(8.25f, FontStyle.Bold) });
            var collaborators = _connectedCollaborators.Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(author => author).ToArray();
            for (var index = 0; index < collaborators.Length; index++)
            {
                var label = !_collaboration.IsHosting && index == 0 && !string.IsNullOrWhiteSpace(_hostLanAddress)
                    ? $"{collaborators[index]} · {_hostLanAddress}"
                    : collaborators[index];
                _collaboratorList.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = MedievalTheme.Text, Font = MedievalTheme.UiFont(8.25f) });
            }
        }
        finally
        {
            _collaboratorList.ResumeLayout();
            _updatingCollaborationPanel = false;
        }
    }

    private void QueueCollaborationUpdate()
    {
        _collaborationUpdatePending = true;
        if (!_collaborationSyncTimer.Enabled) _collaborationSyncTimer.Start();
    }

    private async Task FlushCollaborationUpdateAsync()
    {
        if (_collaborationUpdateInFlight) return;
        if (!_collaborationUpdatePending)
        {
            _collaborationSyncTimer.Stop();
            return;
        }
        _collaborationUpdatePending = false;
        _collaborationUpdateInFlight = true;
        try { await ShareCurrentMapAsync(); }
        finally { _collaborationUpdateInFlight = false; }
    }

    private async Task<bool> ShareCurrentMapAsync()
    {
        await _collaborationSendLock.WaitAsync();
        try
        {
            await ShareCurrentMapCoreAsync();
            return true;
        }
        catch (OnlineReplacementRejectedException error)
        {
            _collaborationUpdatePending = false;
            _collaborationFullProjectPending = false;
            if (!_closingDesigner && !IsDisposed && IsHandleCreated) _status.Text = error.Message;
            return false;
        }
        catch (Exception)
        {
            if (_closingDesigner || IsDisposed || !IsHandleCreated)
            {
                _collaborationUpdatePending = false;
                return false;
            }
            _collaborationUpdatePending = MapSessionConnected;
            if (_collaborationUpdatePending && !_collaborationSyncTimer.Enabled)
                _collaborationSyncTimer.Start();
            PostOnlineUi(() => _status.Text = _collaborationUpdatePending
                ? "Map update could not be shared; retrying." : "Map update could not be shared; session disconnected.");
            return false;
        }
        finally { _collaborationSendLock.Release(); }
    }

    private async Task ShareCurrentMapCoreAsync()
    {
        var before = _lastSharedProject ?? _project.Clone();
        var snapshot = _project.Clone();
        if (_collaborationFullProjectPending || before.GridSize != snapshot.GridSize)
        {
            if (_onlineCollaboration.IsConnected)
            {
                // The ordered echo updates the baseline before this resumes;
                // a newer collaborator revision may already have arrived too.
                _onlineSubmittedProject = snapshot;
                try { await _onlineCollaboration.SendProjectAsync(snapshot); }
                catch (InvalidOperationException) { await CancelRejectedOnlineReplacementAsync(); }
                catch (TimeoutException)
                {
                    await CancelRejectedOnlineReplacementAsync("The replacement could not be confirmed. Shared map refreshed; retry only if still needed.");
                }
                finally { _onlineSubmittedProject = null; }
                return;
            }
            await _collaboration.SendProjectAsync(snapshot);
            _collaborationFullProjectPending = false;
            _lastSharedProject = snapshot;
            return;
        }

        var delta = MapDelta.Create(before, snapshot);
        if (delta is null) return;
        if (_onlineCollaboration.IsConnected)
        {
            _onlineSubmittedDelta = delta;
            try { await _onlineCollaboration.SendDeltaAsync(delta); }
            finally { _onlineSubmittedDelta = null; }
            return;
        }
        await _collaboration.SendDeltaAsync(delta);
        var acknowledgedBaseline = _lastSharedProject ?? before.Clone();
        if (acknowledgedBaseline.GridSize == before.GridSize)
            delta.ApplyTo(acknowledgedBaseline);
        _lastSharedProject = acknowledgedBaseline;
    }

    private async Task SendCollaborationCursorAsync(Point cell)
    {
        try
        {
            if (_onlineCollaboration.IsConnected) await _onlineCollaboration.SendCursorAsync(cell);
            else await _collaboration.SendCursorAsync(cell);
        }
        catch (Exception)
        {
            if (_closingDesigner || IsDisposed || !IsHandleCreated) return;
            PostOnlineUi(() => _status.Text = "Cursor position could not be shared.");
        }
    }

    private void ApplyRemoteProject(MapProject project)
    {
        _applyingRemoteProject = true;
        try
        {
            var gridSizeChanged = _project.GridSize != project.GridSize;
            _project = project;
            _canvas.Project = project;
            RefreshGenerationSeed();
            if (gridSizeChanged) _canvas.ResetView();
            _nameBox.Text = project.Name;
            _authorBox.Text = project.Author;
            _gridSize.Value = project.GridSize;
            _variance.Value = project.Variance;
            _varianceValue.Text = $"{project.Variance}%";
            ResetManualDrawingTools();
            _undo.Clear();
            _redo.Clear();
            _lastSharedProject = project.Clone();
            _collaborationFullProjectPending = false;
            MarkDirty();
            UpdateAnalysis();
            _status.Text = "Map updated by collaborator.";
        }
        finally { _applyingRemoteProject = false; }
    }

    private void ApplyRemoteDelta(MapDelta delta)
    {
        _applyingRemoteProject = true;
        try
        {
            delta.ApplyTo(_project);
            if (delta.GenerationSeedChanged) RefreshGenerationSeed();
            if (_lastSharedProject is null) _lastSharedProject = _project.Clone();
            else delta.ApplyTo(_lastSharedProject);
            _canvas.Project = _project;
            _nameBox.Text = _project.Name;
            _authorBox.Text = _project.Author;
            _variance.Value = _project.Variance;
            _varianceValue.Text = $"{_project.Variance}%";
            // Keep the active point-placement tool selected. Every acknowledged LAN
            // edit is echoed as a canonical delta, including edits made by this user.
            MarkDirty();
            UpdateAnalysis();
            _status.Text = "Map updated by collaborator.";
        }
        catch (Exception) { _status.Text = "Shared map update could not be applied."; }
        finally { _applyingRemoteProject = false; }
    }

    private static IReadOnlyList<string> LocalLanAddresses()
    {
        try
        {
            var addresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Where(address => address.GetAddressBytes() is var bytes && !(bytes[0] == 169 && bytes[1] == 254))
                .Select(address => address.ToString())
                .Distinct()
                .ToArray();
            return addresses.Length > 0 ? addresses : ["localhost"];
        }
        catch (SocketException) { return ["localhost"]; }
    }

    private static bool TryParseEndpoint(string value, out string host, out int port)
    {
        host = value;
        port = LanCollaborationSession.DefaultPort;
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1) return !string.IsNullOrWhiteSpace(value);
        if (!int.TryParse(value[(separator + 1)..], out var parsedPort) || parsedPort is < 1 or > 65535)
            return false;
        host = value[..separator].Trim();
        port = parsedPort;
        return !string.IsNullOrWhiteSpace(host);
    }

    private string? PromptForText(string title, string label)
    {
        using var dialog = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(410, 130),
            BackColor = MedievalTheme.Surface,
            ForeColor = MedievalTheme.Text,
            Font = MedievalTheme.UiFont()
        };
        var caption = new Label { Text = label, AutoSize = true, Location = new Point(18, 18), ForeColor = MedievalTheme.Text };
        var input = new TextBox { Location = new Point(18, 45), Size = new Size(374, 27) };
        MedievalTheme.StyleTextInput(input);
        var connect = new Button { Text = "Connect", DialogResult = DialogResult.OK, Location = new Point(218, 88), Size = new Size(84, 28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(308, 88), Size = new Size(84, 28) };
        MedievalTheme.StyleSecondaryButton(connect);
        MedievalTheme.StyleSecondaryButton(cancel);
        dialog.Controls.AddRange([caption, input, connect, cancel]);
        dialog.AcceptButton = connect;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }

    private void UpdateTitle()
    {
        Text = _activeView switch
        {
            AppView.Home => "EDMW Mod Studio",
            AppView.Civilizations => $"{(_civilizationEditorView?.IsDirty == true ? "*" : "")}{_civilizationEditorView?.ProjectName ?? "Civilizations"} — EDMW Mod Studio",
            _ => $"{(_dirty ? "*" : "")}{_project.Name} — EDMW Mod Studio"
        };
    }

    private string CivilizationStatusSummary() =>
        $"Civilization project · {(_civilizationEditorView?.IsDirty == true ? "Unsaved changes" : "No pending changes")} · Ctrl+S save · Ctrl+Z undo · Ctrl+Y redo";

    internal CivilizationEditorView OpenCivilizationEditorForVerification()
    {
        ShowView(AppView.Civilizations, confirm: false);
        return _civilizationEditorView!;
    }

    internal void ShowWorkspaceForVerification(bool mapDesigner) =>
        ShowView(mapDesigner ? AppView.MapGenerator : AppView.Home, confirm: false);

    private void UpdateAnalysis()
    {
        var analysis = MapAnalyzer.Analyze(_project);
        var anchors = SemanticAnchorAnalyzer.Analyze(_project);
        var anchorText = anchors.HasAny
            ? $" · Anchors: center {anchors.Count(SemanticAnchorRole.Center)}, inner {anchors.Count(SemanticAnchorRole.InnerNeutral)}, outer {anchors.Count(SemanticAnchorRole.OuterNeutral)}, player {anchors.Count(SemanticAnchorRole.PlayerLand)}"
            : " · Anchors: none";
        var paintedElevation = _project.ElevationMask
            .Where(value => value is not (MapProject.AutomaticElevation or MapProject.CliffElevation))
            .Select(value => (int)value)
            .ToArray();
        var elevationText = paintedElevation.Length == 0
            ? "Elevation automatic"
            : $"Elevation {paintedElevation.Min():+#;-#;0} to {paintedElevation.Max():+#;-#;0} · {paintedElevation.Length:N0} cells";
        var cliffCount = Enumerable.Range(0, _project.Cells.Length).Count(index =>
            _project.ElevationMask[index] == MapProject.CliffElevation || _project.CliffMask[index] != 0);
        if (cliffCount > 0) elevationText += $" · Cliffs {cliffCount:N0} cells";
        _landMetric.Text = analysis.LandFraction.ToString("P0");
        _islandsMetric.Text = analysis.LandComponents.ToString("N0");
        _forestMetric.Text = analysis.ForestFractionOfLand.ToString("P0");
        _ruggedMetric.Text = analysis.RuggedFractionOfLand.ToString("P0");
        _coastAnchorLabel.Text = $"Coast {analysis.CoastComplexity:P0}{anchorText}";
        _elevationSummaryLabel.Text = elevationText;
        var result = RmvGenerator.Generate(_project);
        _warningLabel.Text = string.Join("\n", result.Notes);
        var hasWarnings = result.Notes.Count > 0;
        if (_warningHeading is not null) _warningHeading.Visible = hasWarnings;
        if (_warningCard is not null) _warningCard.Visible = hasWarnings;
        if (_mapStatusOverlayExpanded && _mapStatusOverlay is not null)
        {
            _mapStatusOverlay.Height = hasWarnings ? 250 : 178;
            PositionMapStatusOverlay();
        }
        if (_activeView == AppView.MapGenerator) _status.Text = StatusSummary();
    }

    private string StatusSummary()
    {
        var a = MapAnalyzer.Analyze(_project);
        var starts = _project.ManualPlayerStarts.Count > 0
            ? $" · {_project.ManualPlayerStarts.Count} player start(s)"
            : string.Empty;
        var resources = _project.ManualResources.Count > 0
            ? $" · {_project.ManualResources.Count} resource(s)"
            : string.Empty;
        return $"{_project.GridSize}×{_project.GridSize} · {a.LandFraction:P0} land · {a.LandComponents} land component(s){starts}{resources}";
    }

    private async void NewProject()
    {
        if (!ConfirmDiscard()) return;
        SetProject(MapProject.CreateDefault((int)_gridSize.Value), null, false);
        if (MapSessionConnected)
        {
            _collaborationFullProjectPending = true;
            QueueCollaborationUpdate();
            if (!await ShareCurrentMapAsync() && _collaborationUpdatePending)
                _status.Text = "New map could not be shared; retrying.";
        }
    }

    private async void OpenProject()
    {
        if (!ConfirmDiscard()) return;
        using var dialog = new OpenFileDialog
        {
            Filter = "EDMW map projects (*.edmwmap)|*.edmwmap|JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var loaded = MapProject.Load(dialog.FileName);
            SetProject(loaded, dialog.FileName, false);
            if (loaded.ManualResources.Any(resource => resource.Kind.IsLegacyUnsupportedFauna()))
                MedievalDialog.Show(this,
                    "This project contains fauna types that are no longer placeable. They are preserved as inert compatibility data and will not spawn or replace animals in game.",
                    "Removed Fauna Markers", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (MapSessionConnected)
            {
                _collaborationFullProjectPending = true;
                QueueCollaborationUpdate();
                if (!await ShareCurrentMapAsync() && _collaborationUpdatePending)
                    _status.Text = "Opened map could not be shared; retrying.";
            }
        }
        catch (Exception) { ShowError("Could not open the project"); }
    }

    private bool SaveProject(bool saveAs)
    {
        var path = _projectPath;
        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "EDMW map projects (*.edmwmap)|*.edmwmap",
                FileName = SafeFileName(_project.Name) + ".edmwmap",
                DefaultExt = "edmwmap",
                AddExtension = true,
                CheckPathExists = true,
                OverwritePrompt = true,
                RestoreDirectory = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            path = dialog.FileName;
        }
        try
        {
            _project.Save(path!);
            _projectPath = path;
            _dirty = false;
            UpdateTitle();
            _status.Text = "Project saved.";
            return true;
        }
        catch (Exception) { ShowError("Could not save the project"); return false; }
    }

    private async void ExportMapOverLan()
    {
        if (_closingDesigner || _preparingMapLanExport) return;
        var gamePath = FindGamePath();
        if (gamePath is null)
        {
            _status.Text = "Choose the EDMW game folder in Settings first.";
            return;
        }
        if (_mapLanTransfer is null) StartMapLanReceiver();
        var transfer = _mapLanTransfer;
        if (transfer is null) return;
        // Export the current document directly; LAN delivery does not use the
        // collaboration session, its peer list, or its synchronization barrier.
        var project = _project.Clone();
        var hidePreview = _hideMapPreviewToggle.Checked;
        _preparingMapLanExport = true;
        _status.Text = "Preparing LAN export…";
        try
        {
            var package = await Task.Run(() => PrepareMapLanPackage(project, gamePath, hidePreview));
            if (_closingDesigner || IsDisposed) return;
            using var dialog = new LanExportStatusDialog("Map LAN export", package.MapName,
                token => transfer.DiscoverAsync(null, token),
                (peer, progress, token) => transfer.SendAsync(package, peer, progress, token),
                installLocal: async token =>
                {
                    var result = await InstallLocalMapForLanAsync(package, gamePath, token);
                    if (result.Status == CivilizationLanStatus.Installed) RefreshLocalLanInstall(civilizations: false);
                    return result;
                });
            dialog.ShowDialog(this);
            _status.Text = "Map LAN export finished.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException)
        {
            if (!_closingDesigner && !IsDisposed)
                MedievalDialog.Show(this, error.Message, "Map LAN export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _preparingMapLanExport = false; }
    }

    internal static MapLanPackage PrepareMapLanPackage(MapProject project, string gamePath, bool hidePreview)
    {
        if (MapAnalyzer.Analyze(project).LandFraction <= 0)
            throw new InvalidOperationException("The map contains no land. Paint at least one playable landmass before exporting.");
        var stagingFolder = Path.Combine(Path.GetTempPath(), "EDMW Mod Studio", "LAN Map Export", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingFolder);
            var package = GeneratedMapPackage.Write(project,
                Path.Combine(stagingFolder, SafeFileName(project.Name) + ".rmv"),
                Path.Combine(gamePath, "Data", "Random Map Scripts"));
            var archive = MultiplayerPackageExporter.Export(project, package, Path.Combine(stagingFolder, "map.zip"),
                hideMapPreview: hidePreview);
            return MapLanPackage.FromArchive(File.ReadAllBytes(archive));
        }
        finally
        {
            try { if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static async Task<CivilizationLanResult> InstallLocalMapForLanAsync(MapLanPackage package,
        string gamePath, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            package.Install(gamePath);
        }).ConfigureAwait(false);
        return new(CivilizationLanStatus.Installed, "Installed on this computer.");
    }

    private void RefreshLocalLanInstall(bool civilizations)
    {
        if (_closingDesigner || IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (_closingDesigner || IsDisposed) return;
                try
                {
                    if (civilizations)
                    {
                        _civilizationInstalledEditsPanel?.RefreshInstalledPackages();
                        _civilizationEditorView?.LoadGameCatalog();
                    }
                    else _refreshCustomMaps?.Invoke();
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                { _status.Text = "Export installed locally. Return Home to refresh installed content."; }
            }));
        }
        catch (InvalidOperationException) when (_closingDesigner || IsDisposed || !IsHandleCreated) { }
    }

    private void LaunchEdmw()
    {
        var gamePath = FindGamePath();
        if (gamePath is null && !SelectGameInstallPath()) return;
        gamePath = FindGamePath();
        if (gamePath is null) return;

        try
        {
            var executable = Path.Combine(gamePath, GameInstallationLocator.GameExecutableName);
            if (!File.Exists(executable))
                throw new FileNotFoundException("The EDMW executable was not found.", executable);

            if (!IsEdmwRunning())
                _ = Process.Start(new ProcessStartInfo(executable)
                {
                    WorkingDirectory = gamePath,
                    UseShellExecute = true
                }) ?? throw new InvalidOperationException("Windows did not start EDMW.");
            _status.Text = "EDMW launched.";
        }
        catch (Exception) { ShowError("Could not launch EDMW"); }
    }

    private static bool IsEdmwRunning()
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(
            GameInstallationLocator.GameExecutableName));
        try { return processes.Length > 0; }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var choice = MedievalDialog.Show(this, "Save changes before closing?", "Unsaved Changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return choice switch { DialogResult.Yes => SaveProject(false), DialogResult.No => true, _ => false };
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_civilizationEditorView is not null && !_civilizationEditorView.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }
        _toolbarAutoHideTimer.Stop();
        _closingDesigner = true;
        _civilizationLanTransfer?.Dispose();
        _mapLanTransfer?.Dispose();
        _collaboration.Dispose();
        _onlineLifetime.Cancel();
        _onlineCollaboration.Dispose();
        _online.Dispose();
    }

    private void UpdateToolbarAutoHide()
    {
        if (_toolbar is null || !Visible || WindowState == FormWindowState.Minimized) return;
        if (_activeView != AppView.Home)
        {
            SetToolbarHidden(false);
            return;
        }
        var pointer = PointToClient(Cursor.Position);
        if (_toolbarHidden)
        {
            if (pointer.Y <= 3) SetToolbarHidden(false);
            return;
        }

        if (_toolbar.Bounds.Contains(pointer))
        {
            _toolbarHideAfterUtc = DateTime.UtcNow.AddMilliseconds(850);
            return;
        }
        if (pointer.Y > _toolbar.Bottom + 8 && DateTime.UtcNow >= _toolbarHideAfterUtc)
            SetToolbarHidden(true);
    }

    private void SetToolbarHidden(bool hidden)
    {
        if (_toolbar is null || _toolbarHidden == hidden) return;
        _toolbarHidden = hidden;
        _toolbar.Visible = !hidden;
        if (!hidden)
        {
            _toolbar.BringToFront();
            _toolbarHideAfterUtc = DateTime.UtcNow.AddMilliseconds(850);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_activeView == AppView.Civilizations)
        {
            if (_civilizationEditorView?.HandleShortcut(e.KeyData) == true) e.SuppressKeyPress = true;
            return;
        }
        if (_activeView != AppView.MapGenerator) return;
        if (e.Control && e.KeyCode == Keys.Z) { Undo(); e.SuppressKeyPress = true; }
        else if (e.Control && e.KeyCode == Keys.Y) { Redo(); e.SuppressKeyPress = true; }
        else if (e.Control && e.KeyCode == Keys.S) { SaveProject(false); e.SuppressKeyPress = true; }
        else if (_activeView == AppView.MapGenerator && e.Control &&
                 e.KeyCode is Keys.Oemplus or Keys.Add)
        {
            _canvas.ZoomIn();
            e.SuppressKeyPress = true;
        }
        else if (_activeView == AppView.MapGenerator && e.Control &&
                 e.KeyCode is Keys.OemMinus or Keys.Subtract)
        {
            _canvas.ZoomOut();
            e.SuppressKeyPress = true;
        }
        else if (_activeView == AppView.MapGenerator && e.Control &&
                 e.KeyCode is Keys.D0 or Keys.NumPad0)
        {
            _canvas.ResetView();
            e.SuppressKeyPress = true;
        }
    }

    private bool SelectGameInstallPath()
    {
        var currentPath = FindGamePath() ?? _selectedGamePath;
        if (!MedievalGameFolderDialog.Choose(this, currentPath, out var selectedPath)) return false;
        if (!GameInstallationLocator.IsValid(selectedPath))
        {
            MedievalDialog.Show(this,
                $"Select the folder containing {GameInstallationLocator.GameExecutableName}.",
                "Game Folder Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _selectedGamePath = selectedPath;
        try
        {
            GameInstallationLocator.SaveSelectedPath(selectedPath);
        }
        catch (Exception)
        {
            MedievalDialog.Show(this,
                "The folder will be used for this session.",
                "Preference Not Saved", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _status.Text = "Game folder selected.";
        _refreshCustomMaps?.Invoke();
        _civilizationInstalledEditsPanel?.RefreshInstalledPackages();
        _civilizationEditorView?.LoadGameCatalog();
        return true;
    }

    private string? FindGamePath() => GameInstallationLocator.Locate(_selectedGamePath);

    private void StartCivilizationLanReceiver()
    {
        if (_verificationMode || _closingDesigner) return;
        _civilizationLanTransfer?.Dispose();
        _civilizationLanError = null;
        _civilizationLanTransfer = new CivilizationLanTransfer(ReviewIncomingCivilizationsAsync,
            Environment.MachineName, _civilizationLanPreferences.Port);
        if (!_civilizationLanPreferences.ReceiveEnabled) return;
        try { _civilizationLanTransfer.Start(); }
        catch (Exception error) when (error is SocketException or IOException or InvalidOperationException)
        {
            _civilizationLanError = error.Message;
            _status.Text = "LAN receiver unavailable. See Settings.";
        }
    }

    private void StartMapLanReceiver()
    {
        if (_verificationMode || _closingDesigner) return;
        _mapLanTransfer?.Dispose();
        _mapLanError = null;
        _mapLanTransfer = new MapLanTransfer(ReviewIncomingMapAsync,
            Environment.MachineName, _mapLanPreferences.Port);
        if (!_mapLanPreferences.ReceiveEnabled) return;
        try { _mapLanTransfer.Start(); }
        catch (Exception error) when (error is SocketException or IOException or InvalidOperationException)
        {
            _mapLanError = error.Message;
            _status.Text = "Map LAN receiver unavailable. See Settings.";
        }
    }

    private void SendCivilizationsOverLan(CivilizationInstallerPackage package)
    {
        if (_closingDesigner) return;
        if (_civilizationLanTransfer is null) StartCivilizationLanReceiver();
        if (_civilizationLanTransfer is null) return;
        var gamePath = FindGamePath();
        if (gamePath is null) { _status.Text = "Choose the EDMW game folder in Settings first."; return; }
        using var dialog = new CivilizationLanSendDialog(package, _civilizationLanTransfer,
            installLocal: async token =>
            {
                var result = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    return CivilizationLanLocalExport.Install(package, gamePath);
                }).ConfigureAwait(false);
                if (result.Status == CivilizationLanStatus.Installed) RefreshLocalLanInstall(civilizations: true);
                return result;
            });
        dialog.ShowDialog(this);
    }

    private async Task<CivilizationLanDecision> ReviewIncomingCivilizationsAsync(CivilizationLanOffer offer, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gamePath = FindGamePath();
            if (gamePath is not null && await Task.Run(() => CivilizationLanLocalExport.IsInstalled(offer.Package, gamePath),
                cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(CivilizationLanStatus.Installed, "Already installed on this computer.");
            }
            await _lanReviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_closingDesigner || IsDisposed || !IsHandleCreated || cancellationToken.IsCancellationRequested)
                    return new(CivilizationLanStatus.Denied, "Designer closed.");
                var completion = new TaskCompletionSource<CivilizationLanDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var designerClosing = _onlineLifetime.Token.Register(() => completion.TrySetResult(
                    new(CivilizationLanStatus.Denied, "Designer closed.")));
                BeginInvoke(new Action(() =>
                {
                    if (_closingDesigner || IsDisposed || cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetResult(new(CivilizationLanStatus.Denied, "Offer cancelled."));
                        return;
                    }
                    try
                    {
                        using var dialog = new CivilizationLanReviewDialog(offer, FindGamePath(), offerCancellation: cancellationToken);
                        dialog.ShowDialog(this);
                        if (dialog.InstalledGamePath is { } installedPath)
                        {
                            _selectedGamePath = installedPath;
                            _status.Text = $"Installed {offer.Package.ProjectName}.";
                            try { GameInstallationLocator.SaveSelectedPath(installedPath); }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                            try
                            {
                                _civilizationInstalledEditsPanel?.RefreshInstalledPackages();
                                _civilizationEditorView?.LoadGameCatalog();
                            }
                            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                            { _status.Text = "Edits installed. Reload current values to refresh the editor."; }
                        }
                        completion.TrySetResult(dialog.Decision);
                    }
                    catch (Exception error)
                    {
                        completion.TrySetResult(new(CivilizationLanStatus.Failed, error.Message));
                    }
                }));
                // The review closes on withdrawal; an explicitly accepted atomic
                // install may finish even if the sender disconnects meanwhile.
                return await completion.Task.ConfigureAwait(false);
            }
            finally { _lanReviewGate.Release(); }
        }
        catch (OperationCanceledException) { return new(CivilizationLanStatus.Denied, "Offer cancelled."); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or IOException or UnauthorizedAccessException)
        { return new(CivilizationLanStatus.Failed, error.Message); }
    }

    private async Task<CivilizationLanDecision> ReviewIncomingMapAsync(MapLanOffer offer, CancellationToken cancellationToken)
    {
        try
        {
            await _lanReviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_closingDesigner || IsDisposed || !IsHandleCreated || cancellationToken.IsCancellationRequested)
                    return new(CivilizationLanStatus.Denied, "Designer closed.");
                var completion = new TaskCompletionSource<CivilizationLanDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var designerClosing = _onlineLifetime.Token.Register(() => completion.TrySetResult(
                    new(CivilizationLanStatus.Denied, "Designer closed.")));
                BeginInvoke(new Action(() =>
                {
                    if (_closingDesigner || IsDisposed || cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetResult(new(CivilizationLanStatus.Denied, "Offer cancelled."));
                        return;
                    }
                    try
                    {
                        using var dialog = new MapLanReviewDialog(offer, FindGamePath(), offerCancellation: cancellationToken);
                        dialog.ShowDialog(this);
                        if (dialog.InstalledGamePath is { } installedPath)
                        {
                            _selectedGamePath = installedPath;
                            _status.Text = $"Installed {offer.Package.MapName}.";
                            try { GameInstallationLocator.SaveSelectedPath(installedPath); }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                            try { _refreshCustomMaps?.Invoke(); }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                            { _status.Text = "Map installed. Return Home to refresh installed maps."; }
                        }
                        completion.TrySetResult(dialog.Decision);
                    }
                    catch (Exception error)
                    {
                        completion.TrySetResult(new(CivilizationLanStatus.Failed, error.Message));
                    }
                }));
                return await completion.Task.ConfigureAwait(false);
            }
            finally { _lanReviewGate.Release(); }
        }
        catch (OperationCanceledException) { return new(CivilizationLanStatus.Denied, "Offer cancelled."); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        { return new(CivilizationLanStatus.Failed, error.Message); }
    }

    private void ShowGameSettings()
    {
        using var dialog = new Form
        {
            Text = "EDMW Mod Studio Settings",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ShowInTaskbar = false,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(600, 370),
            BackColor = MedievalTheme.Sidebar,
            ForeColor = MedievalTheme.Text,
            Font = MedievalTheme.UiFont()
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            Padding = new Padding(18, 15, 18, 14),
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = "Game installation",
            AutoSize = true,
            ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.DisplayFont(14f),
            Margin = new Padding(0, 0, 0, 4)
        });
        layout.Controls.Add(new Label
        {
            Text = $"Choose the folder containing {GameInstallationLocator.GameExecutableName}.",
            AutoSize = true,
            ForeColor = MedievalTheme.TextMuted,
            Margin = new Padding(0, 0, 0, 10)
        });
        var pathLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 34,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = MedievalTheme.Text,
            BackColor = MedievalTheme.Input,
            Padding = new Padding(9, 0, 9, 0),
            Text = FindGamePath() ?? "Game folder not selected"
        };
        layout.Controls.Add(pathLabel);
        var lanRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 10, 0, 0) };
        lanRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        lanRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lanRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        var receive = new MedievalToggleSwitch
        {
            Text = "Receive civ edits over LAN", AutoSize = true, Checked = _civilizationLanPreferences.ReceiveEnabled,
            ForeColor = MedievalTheme.Text, Margin = new Padding(0, 5, 0, 0)
        };
        StyleCheckBox(receive);
        var port = new MedievalNumericUpDown
        {
            Minimum = 1, Maximum = 65535, Value = _civilizationLanPreferences.Port,
            Dock = DockStyle.Fill, AccessibleName = "LAN civilization receiver port", Margin = new Padding(5, 1, 0, 0),
            BackColor = MedievalTheme.Input, ForeColor = MedievalTheme.Text, BorderStyle = BorderStyle.FixedSingle
        };
        MedievalTheme.StyleTextInput(port);
        lanRow.Controls.Add(receive, 0, 0);
        lanRow.Controls.Add(new Label { Text = "Port", AutoSize = true, ForeColor = MedievalTheme.TextMuted, Margin = new Padding(8, 5, 0, 0) }, 1, 0);
        lanRow.Controls.Add(port, 2, 0);
        layout.Controls.Add(lanRow);
        var lanStatus = new Label { AutoSize = false, Height = 32, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = MedievalTheme.TextMuted };
        void UpdateLanStatus()
        {
            lanStatus.Text = _civilizationLanError ?? (_civilizationLanTransfer?.IsListening == true
                ? $"Listening · {string.Join(", ", LocalLanAddresses().Select(address => address + ":" + _civilizationLanPreferences.Port))}"
                : "Receiving off");
        }
        void ApplyLanPreferences()
        {
            _civilizationLanPreferences.ReceiveEnabled = receive.Checked;
            _civilizationLanPreferences.Port = (int)port.Value;
            try { _civilizationLanPreferences.Save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _status.Text = "LAN settings apply for this session."; }
            StartCivilizationLanReceiver();
            UpdateLanStatus();
        }
        UpdateLanStatus();
        receive.CheckedChanged += (_, _) => ApplyLanPreferences();
        port.ValueChanged += (_, _) => ApplyLanPreferences();
        layout.Controls.Add(lanStatus);
        var mapLanRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 10, 0, 0) };
        mapLanRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mapLanRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mapLanRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        var receiveMaps = new MedievalToggleSwitch
        {
            Text = "Receive maps over LAN", AutoSize = true, Checked = _mapLanPreferences.ReceiveEnabled,
            ForeColor = MedievalTheme.Text, Margin = new Padding(0, 5, 0, 0)
        };
        StyleCheckBox(receiveMaps);
        var mapPort = new MedievalNumericUpDown
        {
            Minimum = 1, Maximum = 65535, Value = _mapLanPreferences.Port,
            Dock = DockStyle.Fill, AccessibleName = "LAN map receiver port", Margin = new Padding(5, 1, 0, 0),
            BackColor = MedievalTheme.Input, ForeColor = MedievalTheme.Text, BorderStyle = BorderStyle.FixedSingle
        };
        MedievalTheme.StyleTextInput(mapPort);
        mapLanRow.Controls.Add(receiveMaps, 0, 0);
        mapLanRow.Controls.Add(new Label { Text = "Port", AutoSize = true, ForeColor = MedievalTheme.TextMuted, Margin = new Padding(8, 5, 0, 0) }, 1, 0);
        mapLanRow.Controls.Add(mapPort, 2, 0);
        layout.Controls.Add(mapLanRow);
        var mapLanStatus = new Label { AutoSize = false, Height = 32, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = MedievalTheme.TextMuted };
        void UpdateMapLanStatus()
        {
            mapLanStatus.Text = _mapLanError ?? (_mapLanTransfer?.IsListening == true
                ? $"Listening · {string.Join(", ", LocalLanAddresses().Select(address => address + ":" + _mapLanPreferences.Port))}"
                : "Receiving off");
        }
        void ApplyMapLanPreferences()
        {
            _mapLanPreferences.ReceiveEnabled = receiveMaps.Checked;
            _mapLanPreferences.Port = (int)mapPort.Value;
            try { _mapLanPreferences.Save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _status.Text = "LAN settings apply for this session."; }
            StartMapLanReceiver();
            UpdateMapLanStatus();
        }
        UpdateMapLanStatus();
        receiveMaps.CheckedChanged += (_, _) => ApplyMapLanPreferences();
        mapPort.ValueChanged += (_, _) => ApplyMapLanPreferences();
        layout.Controls.Add(mapLanStatus);
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0)
        };
        var done = new Button { Text = "Done", AutoSize = false, Size = new Size(86, 30), DialogResult = DialogResult.OK };
        var change = new Button { Text = "Change...", AutoSize = false, Size = new Size(100, 30) };
        var geographyCredits = new Button { Text = "Geography credits", AutoSize = false, Size = new Size(150, 30) };
        MedievalTheme.StyleSecondaryButton(done);
        done.FlatAppearance.BorderColor = MedievalTheme.GoldBright;
        done.FlatAppearance.BorderSize = 2;
        MedievalTheme.StyleSecondaryButton(change);
        MedievalTheme.StyleSecondaryButton(geographyCredits);
        geographyCredits.Click += (_, _) => ShowStateGeographyDetails(dialog);
        change.Click += (_, _) =>
        {
            if (!SelectGameInstallPath()) return;
            pathLabel.Text = FindGamePath() ?? "Game folder not selected";
        };
        actions.Controls.Add(done);
        actions.Controls.Add(change);
        actions.Controls.Add(geographyCredits);
        layout.Controls.Add(actions);
        dialog.AcceptButton = done;
        dialog.CancelButton = done;
        MedievalDialogFrame.Apply(dialog, layout);
        dialog.ShowDialog(this);
    }

    private static string SafeFileName(string value)
    {
        var result = string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(result) ? "Custom Map" : result;
    }

    private void ShowError(string title) =>
        MedievalDialog.Show(this, ErrorMessage(title), title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static string ErrorMessage(string title) => title switch
    {
        "Could not add random land" => "Random land could not be added.",
        "Could not generate the map" => "The map could not be generated. Your current map has been kept.",
        "Could not open the project" => "The project could not be opened.",
        "Could not save the project" => "The project could not be saved.",
        "Could not launch EDMW" => "EDMW could not be launched.",
        _ => "The operation could not be completed."
    };

    private static Color ContrastColor(Color color) => color.GetBrightness() > 0.55f ? Color.FromArgb(25, 30, 32) : Color.White;

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != MedievalWindowChrome.NonClientHitTestMessage ||
            message.Result != (IntPtr)MedievalWindowChrome.ClientHit ||
            WindowState != FormWindowState.Normal)
            return;

        var point = PointToClient(Cursor.Position);
        if (MedievalWindowChrome.TryGetResizeHit(this, point, out var hit))
            message.Result = (IntPtr)hit;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_activeView == AppView.Home) return;
        using var border = new Pen(MedievalTheme.BorderStrong);
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1));
    }

}

using System.Diagnostics;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Game materials, looping animation previews and portable replacement import.</summary>
internal sealed class CivilizationModelPreviewPanel : UserControl
{
    private readonly CivilizationGameAssets? _gameAssets;
    private readonly string? _sourceModel;
    private readonly int? _graphicsId;
    private readonly CivilizationModelAsset? _replacement;
    private readonly Action<CivilizationModelAsset> _replace;
    private readonly Action _restore;
    private readonly Action<string> _selectGameModel;
    private readonly CivilizationModelViewport _viewport = new() { Dock = DockStyle.Fill };
    private readonly Label _caption = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = MedievalTheme.TextMuted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolTip _tips = new() { AutoPopDelay = 12000 };
    private readonly ContextMenuStrip _viewMenu = new()
    {
        Renderer = new MedievalToolStripRenderer(), BackColor = MedievalTheme.Sidebar,
        ForeColor = MedievalTheme.Text, ShowImageMargin = false
    };
    private readonly Button _compare;
    private readonly FlowLayoutPanel _animations = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Margin = Padding.Empty };
    private readonly TableLayoutPanel _animationSection;
    private readonly RowStyle _animationRow;
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 33 };
    private readonly Stopwatch _playbackClock = new();
    private readonly List<Control> _visibilityOwners = [];
    private ModelMesh? _baseMesh;
    private ModelAppearance? _baseAppearance;
    private ModelAnimationClip? _activeAnimation;
    private readonly Dictionary<ModelAnimationClip, (ModelMesh Mesh, ModelAppearance? Appearance)> _animationScenes = [];
    private ModelAnimationClip[] _selectedAnimations = [];
    private bool _updatingAnimations;
    private bool _showingReplacement;

    public CivilizationModelPreviewPanel(CivilizationGameAssets? gameAssets, string? sourceModel,
        CivilizationModelAsset? replacement, Action<CivilizationModelAsset> replace,
        Action restore, Action<string> selectGameModel, int? graphicsId = null)
    {
        _gameAssets = gameAssets;
        _sourceModel = sourceModel;
        _graphicsId = graphicsId;
        _replacement = replacement;
        _replace = replace;
        _restore = restore;
        _selectGameModel = selectGameModel;
        BackColor = MedievalTheme.Sidebar;
        Padding = new Padding(1);
        Height = 390;
        MinimumSize = new Size(290, 320);
        Font = MedievalTheme.UiFont(8.5f);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty, Padding = new Padding(8), BackColor = MedievalTheme.Sidebar };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        _animationRow = new RowStyle(SizeType.Absolute, 0);
        layout.RowStyles.Add(_animationRow);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        header.Controls.Add(new Label { Text = "MODEL", ForeColor = MedievalTheme.Gold, Font = MedievalTheme.UiFont(9, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty });
        _compare = SmallButton("View original", ToggleComparison, "Compare with original game model", 108);
        _compare.Visible = replacement is not null && sourceModel is not null;
        header.Controls.Add(_compare, 1, 0);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_viewport, 0, 1);
        layout.Controls.Add(_caption, 0, 2);
        _tips.SetToolTip(_viewport, "Drag to rotate · Scroll to zoom · Double-click to reset");
        var colors = new ToolStripMenuItem("Player color");
        foreach (var color in gameAssets?.PlayerColors ?? [])
        {
            var item = new ToolStripMenuItem(color.Name) { Checked = colors.DropDownItems.Count == 0 };
            item.Click += (_, _) =>
            {
                _viewport.PlayerColorArgb = color.Argb;
                foreach (var entry in colors.DropDownItems.OfType<ToolStripMenuItem>()) entry.Checked = ReferenceEquals(entry, item);
            };
            colors.DropDownItems.Add(item);
        }
        if (colors.DropDownItems.Count > 0) _viewMenu.Items.Add(colors);
        _viewMenu.Items.Add("Reset view", null, (_, _) => _viewport.ResetView());
        _viewport.ContextMenuStrip = _viewMenu;
        _animationSection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Visible = false };
        _animationSection.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        _animationSection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _animationSection.Controls.Add(new Label { Text = "ANIMATIONS", Dock = DockStyle.Fill, ForeColor = MedievalTheme.Gold,
            Font = MedievalTheme.UiFont(8.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, 0, 0);
        _animationSection.Controls.Add(_animations, 0, 1);
        layout.Controls.Add(_animationSection, 0, 3);
        _tips.SetToolTip(_animations, "Checked animations repeat in order. Uncheck all to pause.");
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
        actions.Controls.Add(SmallButton("Upload model…", Upload, "Upload replacement model (.cem or .obj)", 104));
        var browse = SmallButton("Game models", BrowseGameModels, "Browse installed game models", 93);
        browse.Enabled = gameAssets is not null;
        actions.Controls.Add(browse);
        var restoreButton = SmallButton("Restore", _restore, "Remove this replacement and use the game model", 63);
        restoreButton.Enabled = replacement is not null;
        actions.Controls.Add(restoreButton);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
        _animationTimer.Tick += (_, _) =>
        {
            UpdateAnimationTimer();
            if (_animationTimer.Enabled) AdvanceAnimation(_playbackClock.Elapsed.TotalSeconds);
        };
        VisibleChanged += (_, _) => UpdateAnimationTimer();
        HandleCreated += (_, _) => { BindVisibilityOwners(); UpdateAnimationTimer(); };
        HandleDestroyed += (_, _) => { _animationTimer.Stop(); _playbackClock.Stop(); };
        _showingReplacement = replacement is not null;
        ShowModel();
    }

    private void ShowModel()
    {
        _animationTimer.Stop();
        _playbackClock.Reset();
        try
        {
            ModelMesh? mesh = null;
            if (_showingReplacement && _replacement is not null)
            {
                mesh = CivilizationModelReader.Read(_replacement.Data, _replacement.OriginalFileName);
                _caption.Text = _replacement.OriginalFileName;
                _compare.Text = "View original";
            }
            else if (_gameAssets is not null && _sourceModel is not null)
            {
                mesh = CivilizationModelReader.Read(_gameAssets.ReadModel(_sourceModel), _sourceModel);
                _caption.Text = Path.GetFileName(_sourceModel.Replace('\\', Path.DirectorySeparatorChar));
                _compare.Text = "View replacement";
            }
            else
            {
                _viewport.EmptyMessage = "Choose a game model\nor upload a replacement";
                _caption.Text = "";
            }
            var appearance = mesh is not null ? _gameAssets?.ReadModelAppearance(mesh, _graphicsId) : null;
            _baseMesh = mesh;
            _baseAppearance = appearance;
            _viewport.SetModel(mesh, appearance);
            LoadAnimations(mesh, appearance?.Animations ?? []);
            if (mesh is not null && appearance is not null)
            {
                var missing = mesh.Materials.Where(material => material.TriangleCount > 0 &&
                    !material.Name.Equals("player color", StringComparison.OrdinalIgnoreCase) && appearance.FindTexture(material) is null).ToArray();
                if (missing.Length > 0)
                {
                    _caption.Text += " · Missing texture";
                    _tips.SetToolTip(_caption, "Missing game textures: " + string.Join(", ", missing.Select(material => material.TextureName.Length > 0 ? material.TextureName : material.Name).Distinct()));
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException or UnauthorizedAccessException)
        {
            _viewport.SetModel(null);
            _baseMesh = null;
            _baseAppearance = null;
            LoadAnimations(null, []);
            _viewport.EmptyMessage = "Model preview unavailable";
            _caption.Text = exception.Message;
        }
        _tips.SetToolTip(_caption, _caption.Text);
    }

    private void LoadAnimations(ModelMesh? mesh, IReadOnlyList<ModelAnimationClip> clips)
    {
        _updatingAnimations = true;
        _animations.SuspendLayout();
        try
        {
            foreach (var control in _animations.Controls.Cast<Control>().ToArray()) control.Dispose();
            _animations.Controls.Clear();
            _selectedAnimations = [];
            _activeAnimation = null;
            _animationScenes.Clear();
            ModelAnimationClip[] valid = mesh is null ? [] : clips.Where(clip => clip.StartFrame >= 0 &&
                clip.EndFrame >= clip.StartFrame && (clip.ModelName is not null || clip.EndFrame < mesh.FrameCount) &&
                double.IsFinite(clip.FramesPerSecond) && clip.FramesPerSecond > 0).ToArray();
            if (valid.Length == 0 && mesh?.FrameCount > 1)
                valid = [new ModelAnimationClip("All frames", 0, mesh.FrameCount - 1, 15)];
            foreach (var clip in valid)
            {
                var check = new CheckBox
                {
                    Text = clip.Name, Tag = clip, AutoSize = true, ForeColor = MedievalTheme.Text,
                    BackColor = MedievalTheme.Sidebar, FlatStyle = FlatStyle.Flat,
                    Margin = new Padding(0, 2, 12, 3), AccessibleName = $"Loop {clip.Name} animation"
                };
                _tips.SetToolTip(check, $"Loop {clip.Name} · Frames {clip.StartFrame}–{clip.EndFrame}\nChecked animations repeat in order.");
                check.CheckedChanged += (_, _) => ChangeAnimationSelection();
                _animations.Controls.Add(check);
            }
            _animationSection.Visible = valid.Length > 0;
            _animationRow.Height = valid.Length > 0 ? 96 * DeviceDpi / 96f : 0;
            // The resting pose should be the unit's standing state, when it is defined.
            var idle = valid.FirstOrDefault(clip => clip.Name.Equals("Idle", StringComparison.OrdinalIgnoreCase)
                || clip.Name.Equals("Stand", StringComparison.OrdinalIgnoreCase));
            if (idle is not null) _viewport.FramePosition = idle.StartFrame;
        }
        finally
        {
            _animations.ResumeLayout(true);
            _updatingAnimations = false;
        }
    }

    private void ChangeAnimationSelection()
    {
        if (_updatingAnimations) return;
        _updatingAnimations = true;
        try
        {
            foreach (var check in _animations.Controls.OfType<CheckBox>().Where(check => check.Checked))
            {
                try { _ = GetAnimationScene((ModelAnimationClip)check.Tag!); }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OverflowException or UnauthorizedAccessException)
                {
                    check.Checked = false;
                    _caption.Text = $"{check.Text} unavailable";
                    _tips.SetToolTip(_caption, error.Message);
                }
            }
        }
        finally { _updatingAnimations = false; }
        _selectedAnimations = _animations.Controls.OfType<CheckBox>().Where(check => check.Checked)
            .Select(check => (ModelAnimationClip)check.Tag!).ToArray();
        _playbackClock.Reset();
        AdvanceAnimation(0);
        UpdateAnimationTimer();
    }

    private void UpdateAnimationTimer()
    {
        var play = !IsDisposed && IsHandleCreated && Visible && _selectedAnimations.Length > 0 &&
            _visibilityOwners.All(owner => owner.Visible) && FindForm()?.WindowState != FormWindowState.Minimized;
        if (play) { _playbackClock.Start(); _animationTimer.Start(); }
        else { _animationTimer.Stop(); _playbackClock.Stop(); }
    }

    private void BindVisibilityOwners()
    {
        foreach (var owner in _visibilityOwners)
        {
            owner.VisibleChanged -= VisibilityOwnerChanged;
            if (owner is Form form) form.Resize -= VisibilityOwnerChanged;
        }
        _visibilityOwners.Clear();
        for (var owner = Parent; owner is not null; owner = owner.Parent)
        {
            _visibilityOwners.Add(owner);
            owner.VisibleChanged += VisibilityOwnerChanged;
            if (owner is Form form) form.Resize += VisibilityOwnerChanged;
        }
    }

    private void VisibilityOwnerChanged(object? sender, EventArgs args) => UpdateAnimationTimer();

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        BindVisibilityOwners();
        UpdateAnimationTimer();
    }

    private void AdvanceAnimation(double seconds)
    {
        if (_selectedAnimations.Length == 0) return;
        var sample = SampleAnimationClip(_selectedAnimations, seconds);
        if (!Equals(_activeAnimation, sample.Clip))
        {
            var scene = GetAnimationScene(sample.Clip);
            _viewport.SetModel(scene.Mesh, scene.Appearance, resetView: false);
            _activeAnimation = sample.Clip;
        }
        _viewport.FramePosition = sample.Frame;
    }

    private (ModelMesh Mesh, ModelAppearance? Appearance) GetAnimationScene(ModelAnimationClip clip)
    {
        if (_animationScenes.TryGetValue(clip, out var scene)) return scene;
        var mesh = _baseMesh ?? throw new InvalidDataException("The animation has no model.");
        var appearance = _baseAppearance;
        if (clip.ModelName is not null && _gameAssets is not null)
        {
            mesh = CivilizationModelReader.Read(_gameAssets.ReadModel(clip.ModelName), clip.ModelName);
            appearance = _gameAssets.ReadModelAppearance(mesh, _graphicsId);
        }
        if (clip.EndFrame >= mesh.FrameCount) throw new InvalidDataException("The animation references a missing frame.");
        scene = (mesh, appearance?.ForAnimation(clip));
        _animationScenes.Add(clip, scene);
        return scene;
    }

    internal static float SampleAnimation(IReadOnlyList<ModelAnimationClip> clips, double seconds)
    {
        if (clips.Count == 0 || !double.IsFinite(seconds)) return 0;
        return SampleAnimationClip(clips, seconds).Frame;
    }

    private static (ModelAnimationClip Clip, float Frame) SampleAnimationClip(IReadOnlyList<ModelAnimationClip> clips, double seconds)
    {
        var total = clips.Sum(clip => clip.DurationSeconds);
        if (!double.IsFinite(seconds) || !double.IsFinite(total) || total <= 0) return (clips[0], clips[0].StartFrame);
        var within = Math.Max(0, seconds) % total;
        for (var index = 0; index < clips.Count; index++)
        {
            if (within < clips[index].DurationSeconds || index == clips.Count - 1)
                return (clips[index], (float)Math.Min(clips[index].EndFrame, clips[index].StartFrame + within * clips[index].FramesPerSecond));
            within -= clips[index].DurationSeconds;
        }
        return (clips[0], clips[0].StartFrame);
    }

    internal CivilizationModelViewport ViewportForVerification => _viewport;
    internal IReadOnlyList<CheckBox> AnimationChecksForVerification => _animations.Controls.OfType<CheckBox>().ToArray();
    internal bool AnimationTimerRunningForVerification => _animationTimer.Enabled;
    internal void AdvanceAnimationForVerification(double seconds) => AdvanceAnimation(seconds);
    internal void LoadAnimationsForVerification(IReadOnlyList<ModelAnimationClip> clips) => LoadAnimations(_baseMesh, clips);

    private void ToggleComparison()
    {
        _showingReplacement = !_showingReplacement;
        ShowModel();
    }

    private void Upload()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Upload replacement model",
            Filter = "Model files (*.cem;*.obj)|*.cem;*.obj|EDMW models (*.cem)|*.cem|Wavefront geometry (*.obj)|*.obj",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            var asset = CivilizationModelAsset.Import(dialog.FileName);
            _ = CivilizationModelReader.Read(asset.Data, asset.OriginalFileName);
            _replace(asset);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException or UnauthorizedAccessException)
        {
            MedievalDialog.Show((IWin32Window?)FindForm() ?? this, exception.Message,
                "Could not upload model", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void BrowseGameModels()
    {
        if (_gameAssets is null) return;
        using var dialog = new GameModelPicker(_gameAssets, _sourceModel, _graphicsId);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.SelectedModel is not string selected) return;
        try
        {
            _ = CivilizationModelReader.Read(_gameAssets.ReadModel(selected), selected);
            _selectGameModel(selected);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            MedievalDialog.Show((IWin32Window?)FindForm() ?? this, exception.Message,
                "Could not preview model", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal Form CreateGameModelPickerForVerification() => new GameModelPicker(
        _gameAssets ?? throw new InvalidOperationException("The game model library is unavailable."),
        _sourceModel, _graphicsId);

    /// <summary>Bounded card browsing with one interactive preview; model files are read only on selection.</summary>
    private sealed class GameModelPicker : Form
    {
        private const int PageSize = 18;
        private readonly CivilizationGameAssets _assets;
        private readonly int? _graphicsId;
        private readonly string? _sourceModel;
        private readonly TextBox _search;
        private readonly FlowLayoutPanel _cards;
        private readonly CivilizationModelViewport _preview;
        private readonly Label _name;
        private readonly Label _detail;
        private readonly Label _pageStatus;
        private readonly Button _previous;
        private readonly Button _next;
        private readonly Button _use;
        private readonly ToolTip _tips = new() { AutoPopDelay = 12000 };
        private string[] _filtered;
        private string? _selectedModel;
        private int _page;

        internal string? SelectedModel => _selectedModel;

        internal GameModelPicker(CivilizationGameAssets assets, string? sourceModel, int? graphicsId)
        {
            _assets = assets;
            _graphicsId = graphicsId;
            _sourceModel = sourceModel;
            _filtered = assets.ModelNames.ToArray();
            Text = "Game model library";
            Name = "GameModelPicker";
            ClientSize = new Size(1000, 620);
            MinimumSize = new Size(760, 470);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = MedievalTheme.Sidebar;
            ForeColor = MedievalTheme.Text;
            Font = MedievalTheme.UiFont();

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                Padding = new Padding(16), Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.Controls.Add(new Label
            {
                Text = "Choose a model", Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright,
                Font = MedievalTheme.DisplayFont(17), Margin = Padding.Empty, AutoEllipsis = true
            }, 0, 0);

            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var library = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                Padding = new Padding(0, 0, 12, 0), Margin = Padding.Empty
            };
            library.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            library.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            library.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            library.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            _search = new TextBox
            {
                Name = "GameModelsSearch", Dock = DockStyle.Fill,
                PlaceholderText = "Search game models…", AccessibleName = "Find game model"
            };
            MedievalTheme.StyleTextInput(_search);
            library.Controls.Add(MedievalTheme.FrameInput(_search), 0, 0);
            _cards = new FlowLayoutPanel
            {
                Name = "GameModelCards", Dock = DockStyle.Fill, AutoScroll = true,
                WrapContents = true, BackColor = MedievalTheme.Sidebar,
                Padding = Padding.Empty, Margin = new Padding(0, 6, 0, 0),
                AccessibleName = "Installed game model cards"
            };
            library.Controls.Add(_cards, 0, 1);
            var paging = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                Margin = Padding.Empty, Padding = new Padding(0, 7, 0, 0)
            };
            paging.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 35));
            paging.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            paging.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 35));
            _previous = SmallButton("‹", () => ChangePage(-1), "Previous model page", 32);
            _previous.Name = "GameModelsPreviousPage";
            _previous.Margin = Padding.Empty;
            _next = SmallButton("›", () => ChangePage(1), "Next model page", 32);
            _next.Name = "GameModelsNextPage";
            _next.Margin = Padding.Empty;
            _pageStatus = new Label
            {
                Name = "GameModelsPageStatus", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter, ForeColor = MedievalTheme.TextMuted,
                AutoEllipsis = true, Margin = Padding.Empty
            };
            paging.Controls.Add(_previous, 0, 0);
            paging.Controls.Add(_pageStatus, 1, 0);
            paging.Controls.Add(_next, 2, 0);
            library.Controls.Add(paging, 0, 2);
            body.Controls.Add(library, 0, 0);

            var inspector = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12),
                Margin = Padding.Empty, BackColor = MedievalTheme.Surface
            };
            inspector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inspector.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            inspector.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            inspector.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            inspector.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            _name = new Label
            {
                Name = "GameModelPreviewName", Text = "Model preview", Dock = DockStyle.Fill,
                Font = MedievalTheme.DisplayFont(13), ForeColor = MedievalTheme.GoldBright,
                AutoEllipsis = true, Margin = Padding.Empty
            };
            _preview = new CivilizationModelViewport
            {
                Name = "GameModelPickerPreview", Dock = DockStyle.Fill,
                EmptyMessage = "Select a model card to preview it", Margin = Padding.Empty
            };
            _detail = new Label
            {
                Name = "GameModelPreviewDetail", Dock = DockStyle.Fill, AutoEllipsis = true,
                ForeColor = MedievalTheme.TextMuted, Padding = new Padding(0, 8, 0, 0),
                Margin = Padding.Empty, UseMnemonic = false
            };
            inspector.Controls.Add(_name, 0, 0);
            inspector.Controls.Add(_preview, 0, 1);
            inspector.Controls.Add(_detail, 0, 2);
            inspector.Controls.Add(new Label
            {
                Text = "Drag to rotate · Scroll to zoom", Dock = DockStyle.Fill,
                ForeColor = MedievalTheme.TextSecondary, Font = MedievalTheme.UiFont(8),
                TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
            }, 0, 3);
            body.Controls.Add(inspector, 1, 0);
            layout.Controls.Add(body, 0, 1);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 12, 0, 0)
            };
            var cancel = SmallButton("Cancel", () => DialogResult = DialogResult.Cancel,
                "Cancel game model selection", 90);
            cancel.DialogResult = DialogResult.Cancel;
            _use = SmallButton("Use selected model", UseSelectedModel, "Use selected game model", 155);
            _use.Name = "GameModelsUseSelection";
            _use.Enabled = false;
            actions.Controls.Add(cancel);
            actions.Controls.Add(_use);
            AcceptButton = _use;
            CancelButton = cancel;
            layout.Controls.Add(actions, 0, 2);
            MedievalDialogFrame.Apply(this, layout);

            _search.TextChanged += (_, _) =>
            {
                _filtered = _assets.ModelNames.Where(name => name.Contains(_search.Text.Trim(),
                    StringComparison.OrdinalIgnoreCase)).ToArray();
                _page = 0;
                ShowCards();
            };
            // Enter in search stays in the library; choosing a model is an explicit action.
            _search.KeyDown += (_, args) =>
            {
                if (args.KeyCode != Keys.Enter) return;
                args.Handled = true;
                args.SuppressKeyPress = true;
            };
            _cards.SizeChanged += (_, _) => ArrangeCards();
            var initial = sourceModel is null ? -1 : Array.FindIndex(_filtered,
                name => name.Equals(sourceModel, StringComparison.OrdinalIgnoreCase));
            if (initial >= 0)
            {
                _page = initial / PageSize;
                SelectModel(_filtered[initial]);
            }
            ShowCards();
        }

        private void UseSelectedModel()
        {
            if (_selectedModel is not null && _use.Enabled) DialogResult = DialogResult.OK;
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Enter && _search.ContainsFocus) return true;
            return base.ProcessDialogKey(keyData);
        }

        private void ChangePage(int direction)
        {
            _page = Math.Clamp(_page + direction, 0, Math.Max(0, (_filtered.Length - 1) / PageSize));
            ShowCards();
        }

        private void ShowCards()
        {
            _cards.SuspendLayout();
            foreach (var control in _cards.Controls.Cast<Control>().ToArray()) control.Dispose();
            _cards.Controls.Clear();
            var first = _page * PageSize;
            for (var index = first; index < Math.Min(_filtered.Length, first + PageSize); index++)
            {
                var name = _filtered[index];
                var card = new GameModelCard(name)
                {
                    Name = "GameModelCard" + index, Tag = name,
                    Margin = new Padding(0, 0, 7, 7),
                    AccessibleName = "Select " + ModelDisplayName(name), AccessibleDescription = name
                };
                card.SetSelected(name.Equals(_selectedModel, StringComparison.OrdinalIgnoreCase));
                card.Click += (_, _) => SelectModel(name);
                card.KeyDown += (_, args) =>
                {
                    if (args.KeyCode is not (Keys.Left or Keys.Right or Keys.Up or Keys.Down)) return;
                    var columns = Math.Max(1, _cards.ClientSize.Width / Math.Max(1, card.Width + card.Margin.Horizontal));
                    var step = args.KeyCode is Keys.Up or Keys.Down ? columns : 1;
                    if (args.KeyCode is Keys.Left or Keys.Up) step = -step;
                    var next = Math.Clamp(Array.IndexOf(_filtered, name) + step, 0, _filtered.Length - 1);
                    _page = next / PageSize;
                    SelectModel(_filtered[next]);
                    ShowCards();
                    _cards.Controls.OfType<GameModelCard>().FirstOrDefault(candidate =>
                        Equals(candidate.Tag, _filtered[next]))?.Focus();
                    args.Handled = true;
                    args.SuppressKeyPress = true;
                };
                _tips.SetToolTip(card, name);
                _cards.Controls.Add(card);
            }
            if (_filtered.Length == 0)
                _cards.Controls.Add(new Label
                {
                    Text = "No models match this search.", AutoSize = true,
                    ForeColor = MedievalTheme.TextMuted, Margin = new Padding(4, 14, 4, 0)
                });
            _previous.Enabled = _page > 0;
            _next.Enabled = first + PageSize < _filtered.Length;
            _pageStatus.Text = _filtered.Length == 0 ? "0 models" :
                $"{_filtered.Length:N0} models · Page {_page + 1} of {(_filtered.Length + PageSize - 1) / PageSize}";
            _cards.ResumeLayout(true);
            ArrangeCards();
            _cards.AutoScrollPosition = Point.Empty;
        }

        private void ArrangeCards()
        {
            var gap = Math.Max(7, DeviceDpi * 7 / 96);
            var width = Math.Max(100, _cards.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - gap);
            var columns = Math.Max(1, width / Math.Max(170, DeviceDpi * 205 / 96));
            foreach (var card in _cards.Controls.OfType<GameModelCard>())
            {
                card.Size = new Size(Math.Max(100, width / columns - gap), DeviceDpi * 90 / 96);
                card.Margin = new Padding(0, 0, gap, gap);
            }
        }

        private void SelectModel(string name)
        {
            _selectedModel = name;
            _name.Text = ModelDisplayName(name);
            _tips.SetToolTip(_name, name);
            _use.Enabled = false;
            foreach (var card in _cards.Controls.OfType<GameModelCard>())
                card.SetSelected(Equals(card.Tag, name));
            try
            {
                var mesh = CivilizationModelReader.Read(_assets.ReadModel(name), name);
                _preview.SetModel(mesh, _assets.ReadModelAppearance(mesh,
                    name.Equals(_sourceModel, StringComparison.OrdinalIgnoreCase) ? _graphicsId : null));
                _detail.Text = $"{name}\n{mesh.TriangleIndices.Count / 3:N0} triangles · {mesh.FrameCount:N0} frames";
                _use.Enabled = true;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException or UnauthorizedAccessException)
            {
                _preview.SetModel(null);
                _preview.EmptyMessage = "Model preview unavailable";
                _detail.Text = exception.Message;
            }
            _tips.SetToolTip(_detail, _detail.Text);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tips.Dispose();
            base.Dispose(disposing);
        }
    }

    private static string ModelDisplayName(string path) => Path.GetFileNameWithoutExtension(
        path.Replace('\\', Path.DirectorySeparatorChar)).Replace('_', ' ');

    private sealed class GameModelCard : Button
    {
        private readonly string _modelPath;
        private bool _selected;
        private bool _hovered;

        internal GameModelCard(string modelPath)
        {
            _modelPath = modelPath;
            Text = ModelDisplayName(modelPath);
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            UseMnemonic = false;
            BackColor = MedievalTheme.Input;
            ForeColor = MedievalTheme.Text;
        }

        internal void SetSelected(bool selected) { _selected = selected; Invalidate(); }

        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnMouseEnter(EventArgs args) { base.OnMouseEnter(args); _hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs args) { base.OnMouseLeave(args); _hovered = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs args)
        {
            var active = _selected || ContainsFocus;
            using var background = new SolidBrush(active || _hovered ? MedievalTheme.SurfaceRaised : MedievalTheme.Input);
            using var border = new Pen(active ? MedievalTheme.Gold : MedievalTheme.Border);
            args.Graphics.FillRectangle(background, ClientRectangle);
            args.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            if (_selected)
            {
                using var accent = new SolidBrush(MedievalTheme.Gold);
                args.Graphics.FillRectangle(accent, 0, 0, Math.Max(3, DeviceDpi * 3 / 96), Height);
            }
            var padding = DeviceDpi * 10 / 96;
            var iconSize = DeviceDpi * 46 / 96;
            if (ProgressionIcons.ForKind("Assets") is { } icon)
            {
                args.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                args.Graphics.DrawImage(icon, new Rectangle(padding, (Height - iconSize) / 2, iconSize, iconSize));
            }
            var left = padding * 2 + iconSize;
            using var nameFont = MedievalTheme.UiFont(9, FontStyle.Bold);
            using var detailFont = MedievalTheme.UiFont(8);
            TextRenderer.DrawText(args.Graphics, ModelDisplayName(_modelPath), nameFont,
                new Rectangle(left, padding + 3, Math.Max(1, Width - left - padding), DeviceDpi * 36 / 96),
                active ? MedievalTheme.GoldBright : MedievalTheme.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(args.Graphics, _selected ? "Selected model" : ".cem game model", detailFont,
                new Rectangle(left, Height - padding - detailFont.Height - 2, Math.Max(1, Width - left - padding), detailFont.Height + 2),
                _selected ? MedievalTheme.Gold : MedievalTheme.TextMuted,
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private static Button SmallButton(string text, Action action, string name, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 27, Margin = new Padding(0, 2, 5, 0), AccessibleName = name };
        MedievalTheme.StyleSecondaryButton(button);
        button.Font = MedievalTheme.UiFont(8);
        button.Click += (_, _) => action();
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Stop();
            _animationTimer.Dispose();
            _playbackClock.Stop();
            foreach (var owner in _visibilityOwners)
            {
                owner.VisibleChanged -= VisibilityOwnerChanged;
                if (owner is Form form) form.Resize -= VisibilityOwnerChanged;
            }
            _visibilityOwners.Clear();
            _tips.Dispose();
            _viewMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}

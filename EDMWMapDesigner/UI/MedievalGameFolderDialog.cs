using System.Security;

namespace EDMWMapDesigner.UI;

/// <summary>A read-only folder chooser that uses the editor's own dialog chrome and controls.</summary>
internal sealed class MedievalGameFolderDialog : Form
{
    private readonly TextBox _path;
    private readonly TreeView _folders;
    private readonly Label _status;
    private readonly Button _select;
    private readonly Button _cancel;
    private readonly TableLayoutPanel _actions;
    private readonly bool _verificationMode;
    private readonly string? _initialPath;
    private bool _initializing;

    internal MedievalGameFolderDialog(string? initialPath = null, bool verificationMode = false, string? verificationRoot = null)
    {
        _verificationMode = verificationMode;
        _initialPath = NormalizeExistingPath(initialPath);
        Text = "Choose game folder";
        ClientSize = new Size(650, 530);
        MinimumSize = new Size(510, 410);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = MedievalTheme.Sidebar;
        ForeColor = MedievalTheme.Text;
        Font = MedievalTheme.UiFont();
        DoubleBuffered = true;

        var layout = new MedievalBufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6,
            Margin = Padding.Empty, BackColor = MedievalTheme.Sidebar
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(new Label
        {
            Text = "Game installation folder", Dock = DockStyle.Fill, Margin = Padding.Empty,
            ForeColor = MedievalTheme.GoldBright, Font = MedievalTheme.DisplayFont(17),
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Select a folder or enter its full path.", Dock = DockStyle.Fill, Margin = Padding.Empty,
            ForeColor = MedievalTheme.TextMuted, TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);
        _path = new TextBox
        {
            Dock = DockStyle.Fill, Text = _initialPath ?? "", AccessibleName = "Full folder path", TabIndex = 0
        };
        MedievalTheme.StyleTextInput(_path);
        var input = MedievalTheme.FrameInput(_path);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 4, 0, 6);
        layout.Controls.Add(input, 0, 2);

        _folders = new TreeView
        {
            Dock = DockStyle.Fill, BackColor = MedievalTheme.Input, ForeColor = MedievalTheme.Text,
            BorderStyle = BorderStyle.None, LineColor = MedievalTheme.BorderStrong,
            HideSelection = false, FullRowSelect = true, ShowLines = true,
            ShowPlusMinus = true, ShowRootLines = true, ItemHeight = 25,
            DrawMode = TreeViewDrawMode.OwnerDrawText, TabIndex = 1,
            AccessibleName = "Folders on this computer"
        };
        _folders.HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(_folders);
        _folders.DrawNode += DrawFolder;
        _folders.BeforeExpand += async (_, args) =>
        {
            if (args.Node is not TreeNode node || node.Tag is not FolderState state || state.Loaded) return;
            args.Cancel = true;
            if (state.Loading) return;
            await PopulateAsync(node);
            if (!IsDisposed && !node.IsExpanded) node.Expand();
        };
        _folders.AfterSelect += (_, args) =>
        {
            if (!_initializing && args.Node?.Tag is FolderState state) _path.Text = state.Path;
        };
        _folders.NodeMouseDoubleClick += (_, args) =>
        {
            if (args.Node?.Tag is FolderState state) _path.Text = state.Path;
        };
        var treeFrame = MedievalTheme.FrameInput(_folders);
        treeFrame.Dock = DockStyle.Fill;
        treeFrame.Margin = Padding.Empty;
        layout.Controls.Add(treeFrame, 0, 3);

        _status = new Label
        {
            Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextMuted, AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
            AccessibleName = "Folder selection status"
        };
        layout.Controls.Add(_status, 0, 4);
        _actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty
        };
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
        _actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _cancel = ActionButton("Cancel");
        _cancel.DialogResult = DialogResult.Cancel;
        _cancel.Click += (_, _) => Close();
        _select = ActionButton("Select Folder");
        _select.Click += (_, _) => SelectFolder();
        _actions.Controls.Add(_cancel, 1, 0);
        _actions.Controls.Add(_select, 2, 0);
        layout.Controls.Add(_actions, 0, 5);
        CancelButton = _cancel;
        AcceptButton = _select;
        _path.TextChanged += (_, _) => ValidateSelection();
        MedievalDialogFrame.Apply(this, layout);

        if (verificationRoot is not null) AddRoot(Path.GetFullPath(verificationRoot));
        else
        {
            foreach (var root in Directory.GetLogicalDrives()) AddRoot(root);
            if (_initialPath is not null && Path.GetPathRoot(_initialPath) is string initialRoot &&
                !_folders.Nodes.Cast<TreeNode>().Any(node => node.Tag is FolderState state && state.Path.Equals(initialRoot, StringComparison.OrdinalIgnoreCase)))
                AddRoot(initialRoot);
        }
        ValidateSelection();
        Shown += async (_, _) =>
        {
            await SelectInitialPathAsync();
            if (!IsDisposed && !_verificationMode) { _path.Focus(); _path.SelectAll(); }
        };
    }

    public string SelectedPath { get; private set; } = "";
    protected override bool ShowWithoutActivation => _verificationMode || base.ShowWithoutActivation;
    internal IReadOnlyList<Button> ActionButtonsForVerification => [_cancel, _select];
    internal Control ActionPanelForVerification => _actions;
    internal TextBox PathForVerification => _path;
    internal TreeView TreeForVerification => _folders;
    internal Control StatusForVerification => _status;
    internal void SelectForVerification() => SelectFolder();

    public static bool Choose(IWin32Window owner, string? initialPath, out string selectedPath)
    {
        using var dialog = new MedievalGameFolderDialog(initialPath);
        if (dialog.ShowDialog(owner) == DialogResult.OK) { selectedPath = dialog.SelectedPath; return true; }
        selectedPath = ""; return false;
    }

    private void AddRoot(string path)
    {
        var node = MakeNode(path, path);
        _folders.Nodes.Add(node);
    }

    private static TreeNode MakeNode(string path, string? label = null)
    {
        var node = new TreeNode(label ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(path))) { Tag = new FolderState(path) };
        // A placeholder exposes the expand affordance; child directories are enumerated only on demand.
        node.Nodes.Add(new TreeNode("…"));
        return node;
    }

    private async Task PopulateAsync(TreeNode node)
    {
        if (node.Tag is not FolderState state || state.Loaded || state.Loading) return;
        state.Loading = true;
        try
        {
            var directories = await Task.Run(() => Directory.EnumerateDirectories(state.Path)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray());
            if (IsDisposed) return;
            _folders.BeginUpdate();
            try
            {
                node.Nodes.Clear();
                foreach (var directory in directories) node.Nodes.Add(MakeNode(directory));
                state.Loaded = true;
            }
            finally { _folders.EndUpdate(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            if (IsDisposed) return;
            node.Nodes.Clear(); state.Loaded = true;
            _status.ForeColor = MedievalTheme.Warning;
            _status.Text = "This folder's contents could not be opened. You can still enter a different full path.";
        }
        finally { state.Loading = false; }
    }

    private async Task SelectInitialPathAsync()
    {
        if (_initialPath is null || IsDisposed) return;
        _initializing = true;
        try
        {
            var node = _folders.Nodes.Cast<TreeNode>().FirstOrDefault(item => item.Tag is FolderState state && WithinFolder(_initialPath, state.Path));
            if (node is null) return;
            while (node.Tag is FolderState state && !Path.TrimEndingDirectorySeparator(state.Path).Equals(_initialPath, StringComparison.OrdinalIgnoreCase))
            {
                await PopulateAsync(node);
                if (IsDisposed) return;
                var next = node.Nodes.Cast<TreeNode>().FirstOrDefault(item => item.Tag is FolderState child && WithinFolder(_initialPath, child.Path));
                node.Expand();
                if (next is null) break;
                node = next;
            }
            _folders.SelectedNode = node; node.EnsureVisible();
        }
        finally { _initializing = false; }
    }

    private void ValidateSelection()
    {
        var path = NormalizeExistingPath(_path.Text);
        _select.Enabled = path is not null;
        _status.ForeColor = path is null && !string.IsNullOrWhiteSpace(_path.Text) ? MedievalTheme.Warning : MedievalTheme.TextMuted;
        _status.Text = path is not null ? "Folder selected." : string.IsNullOrWhiteSpace(_path.Text) ? "Choose a folder." : "That folder could not be found.";
    }

    private void SelectFolder()
    {
        var path = NormalizeExistingPath(_path.Text);
        if (path is null) { ValidateSelection(); return; }
        SelectedPath = path; DialogResult = DialogResult.OK; Close();
    }

    private static string? NormalizeExistingPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim().Trim('"')));
            return Directory.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or SecurityException) { return null; }
    }

    private static bool WithinFolder(string path, string folder) =>
        path.Equals(Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static Button ActionButton(string text)
    {
        var button = new DashboardPlaqueButton { Text = text, Dock = DockStyle.Fill, Margin = new Padding(5, 4, 0, 4) };
        MedievalTheme.StyleSecondaryButton(button); return button;
    }

    private void DrawFolder(object? sender, DrawTreeNodeEventArgs args)
    {
        if (args.Node is null) return;
        var selected = (args.State & TreeNodeStates.Selected) != 0;
        using var background = new SolidBrush(selected ? MedievalTheme.SurfaceRaised : MedievalTheme.Input);
        args.Graphics.FillRectangle(background, new Rectangle(args.Bounds.X, args.Bounds.Y, Math.Max(0, _folders.ClientSize.Width - args.Bounds.X), args.Bounds.Height));
        TextRenderer.DrawText(args.Graphics, args.Node.Text, _folders.Font, args.Bounds,
            selected ? MedievalTheme.GoldBright : MedievalTheme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private sealed class FolderState(string path)
    {
        public string Path { get; } = path;
        public bool Loaded { get; set; }
        public bool Loading { get; set; }
    }
}

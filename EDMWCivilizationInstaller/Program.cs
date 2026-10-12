using EDMWMapDesigner.Civilizations;
using Microsoft.Win32;

namespace EDMWCivilizationInstaller;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--verify-layout")
        {
            try
            {
                if (args.Length != 2) throw new ArgumentException("Usage: --verify-layout <game folder>");
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                ApplicationConfiguration.Initialize();
                return CivilizationInstallerLayoutVerification.Run(args[1]);
            }
            catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
        }
        if (args.Length > 0 && args[0] == "--verify-package")
        {
            try
            {
                if (args.Length != 2) throw new ArgumentException("Usage: --verify-package <game folder>");
                var package = CivilizationInstallerPackageCodec.ReadExecutable(Environment.ProcessPath
                    ?? throw new InvalidOperationException("The installer executable path is unavailable."));
                var issues = CivilizationPackageManager.Review(package, args[1]);
                if (issues.Count > 0)
                {
                    foreach (var issue in issues) Console.Error.WriteLine(issue);
                    return 1;
                }
                Console.WriteLine($"Verified civilization package: {package.ProjectName}; {package.PackageId}; {package.Changes.Count} changes; {package.Files.Count(file => file.WholeFile || file.Patches.Count > 0)} target files.");
                Console.WriteLine("Package integrity, supported game version, source identities, semantic ranges and required assets passed. No installation was performed.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => ShowError(args.Exception);
        try
        {
            var package = CivilizationInstallerPackageCodec.ReadExecutable(Environment.ProcessPath
                ?? throw new InvalidOperationException("The installer executable path is unavailable."));
            Application.Run(new CivilizationInstallerForm(package));
        }
        catch (Exception error) { ShowError(error); }
        return 0;
    }
    private static void ShowError(Exception error) => MessageBox.Show(error.Message,
        "EDMW Mod Studio Civilization Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
}

internal sealed class CivilizationInstallerForm : Form
{
    private static readonly Color Gold = Color.FromArgb(234, 190, 112);
    private static readonly Color Surface = Color.FromArgb(28, 25, 22);
    private static readonly Color Ink = Color.FromArgb(233, 224, 205);
    private readonly CivilizationInstallerPackage _package;
    private readonly TextBox _path;
    private readonly Label _status;
    private readonly Button _install;
    private readonly Button _uninstall;
    private readonly Button _recover;
    private readonly Button _close;
    private readonly TableLayoutPanel _actions;
    private readonly ListView _changes;
    private readonly bool _verificationMode;

    public CivilizationInstallerForm(CivilizationInstallerPackage package, string? gamePath = null, bool verificationMode = false)
    {
        _package = package;
        _verificationMode = verificationMode;
        Text = package.ProjectName + " — EDMW Mod Studio Civilization Installer";
        ClientSize = new Size(980, 690);
        MinimumSize = new Size(820, 540);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(17, 15, 13);
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10f);
        DoubleBuffered = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 5, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.Controls.Add(new Label { Text = package.ProjectName, ForeColor = Gold, Font = new Font("Georgia", 20f, FontStyle.Bold), Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }, 0, 0);
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        _path = new TextBox { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true, AccessibleName = "Selected EDMW game installation", Margin = new Padding(0, 8, 12, 0) };
        var choose = Button("Game folder…");
        choose.Click += (_, _) => ChooseFolder();
        pathRow.Controls.Add(_path, 0, 0); pathRow.Controls.Add(choose, 1, 0); layout.Controls.Add(pathRow, 0, 1);
        _changes = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            HideSelection = false, BackColor = Surface, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = "Changes included in this civilization package" };
        _changes.Columns.Add("Item", 240);
        _changes.Columns.Add("Field", 165);
        _changes.Columns.Add("Change", 240);
        _changes.Columns.Add("Scope", 440);
        foreach (var change in package.Changes)
        {
            var row = new ListViewItem(change.CivilizationName + (change.EntityName is null ? "" : " / " + change.EntityName));
            row.SubItems.Add(change.Label); row.SubItems.Add(change.Before + " → " + change.After); row.SubItems.Add(DisplayScope(change));
            row.ToolTipText = change.Description; _changes.Items.Add(row);
        }
        _changes.ShowItemToolTips = true;
        layout.Controls.Add(_changes, 0, 2);
        _status = new Label { Dock = DockStyle.Fill, ForeColor = Gold, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            AccessibleName = "Installer validation status" };
        layout.Controls.Add(_status, 0, 3);
        _actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty };
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        _actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _install = Button("Install");
        _install.Click += (_, _) => Apply(false);
        _uninstall = Button("Uninstall");
        _uninstall.Click += (_, _) => Apply(true);
        _recover = Button("Recover");
        _recover.Click += (_, _) => Recover();
        _close = Button("Close"); _close.Click += (_, _) => Close();
        _actions.Controls.Add(_recover, 1, 0);
        _actions.Controls.Add(_uninstall, 2, 0);
        _actions.Controls.Add(_close, 3, 0);
        _actions.Controls.Add(_install, 4, 0);
        layout.Controls.Add(_actions, 0, 4);
        Controls.Add(layout);
        AcceptButton = _install;
        CancelButton = _close;
        _path.Text = gamePath ?? LocateGame() ?? "";
        RefreshReview();
    }

    protected override bool ShowWithoutActivation => _verificationMode || base.ShowWithoutActivation;
    internal IReadOnlyList<Button> ActionButtonsForVerification => [_install, _uninstall, _recover, _close];
    internal Control ActionPanelForVerification => _actions;
    internal Control StatusForVerification => _status;
    internal void SetRecoveryVisibilityForVerification(bool visible) => _recover.Visible = visible;

    private static string DisplayScope(CivilizationInstallChange change) => change.Scope switch
    {
        var scope when scope.StartsWith("Global object record", StringComparison.Ordinal) =>
            change.Key.StartsWith("building.", StringComparison.Ordinal) ? "All uses of this building" : "All uses of this unit",
        var scope when scope.StartsWith("Shared production technology", StringComparison.Ordinal) => "Shared production",
        var scope when scope.StartsWith("Shared technology", StringComparison.Ordinal) => "Shared research or command",
        var scope when scope.StartsWith("Shared modifier effect", StringComparison.Ordinal) => "Linked technologies and targets",
        var scope when scope.StartsWith("Global model replacement", StringComparison.Ordinal) => "All uses of this model",
        "Existing civilization record" => "This civilization",
        _ => change.Scope
    };

    private static Button Button(string label) => new()
    {
        Text = label, Height = 39, Dock = DockStyle.Fill, Margin = new Padding(6, 4, 0, 4), AutoSize = false,
        MinimumSize = new Size(80, 32),
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(54, 44, 29), ForeColor = Gold,
        UseVisualStyleBackColor = false, AccessibleName = label
    };
    private void ChooseFolder()
    {
        using var chooser = new FolderBrowserDialog { Description = "Choose the folder containing Empires_DMW.exe.",
            UseDescriptionForTitle = true, ShowNewFolderButton = false, InitialDirectory = Directory.Exists(_path.Text) ? _path.Text : "" };
        if (chooser.ShowDialog(this) != DialogResult.OK) return;
        _path.Text = Path.GetFullPath(chooser.SelectedPath);
        RefreshReview();
    }
    private void RefreshReview()
    {
        _install.Enabled = false;
        _uninstall.Enabled = false;
        _recover.Visible = false;
        if (!Directory.Exists(_path.Text)) { _status.Text = "Choose a game folder."; return; }
        try
        {
            var installed = CivilizationPackageManager.ReadInstalled(_path.Text);
            _recover.Visible = installed.Any(item => item.Status.Contains("recovery required", StringComparison.OrdinalIgnoreCase));
            _recover.Enabled = _recover.Visible;
            var own = installed.FirstOrDefault(item => item.PackageId == _package.PackageId);
            if (own is not null)
            {
                _uninstall.Enabled = true;
                _status.Text = own.Status == "Installed" ? "Installed" : own.Status;
                return;
            }
            var issues = CivilizationPackageManager.Review(_package, _path.Text);
            _install.Enabled = issues.Count == 0;
            _status.Text = issues.Count == 0 ? $"Ready · {_package.Changes.Count} {(_package.Changes.Count == 1 ? "change" : "changes")}" : string.Join(Environment.NewLine, issues);
        }
        catch (Exception error) { _status.Text = error.Message; }
    }
    private void Apply(bool uninstall)
    {
        if (MessageBox.Show(this, uninstall ? "Uninstall this package?" : "Install these changes?",
            Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            UseWaitCursor = true; _install.Enabled = false; _uninstall.Enabled = false;
            var result = uninstall ? CivilizationPackageManager.Uninstall(_path.Text, _package.PackageId) : CivilizationPackageManager.Install(_package, _path.Text);
            MessageBox.Show(this, result.Message, Text, MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { UseWaitCursor = false; RefreshReview(); }
    }
    private void Recover()
    {
        if (MessageBox.Show(this, "Recover interrupted changes?",
            Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            UseWaitCursor = true; _recover.Enabled = false;
            var result = CivilizationPackageManager.Recover(_path.Text);
            MessageBox.Show(this, result.Message, Text, MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { UseWaitCursor = false; RefreshReview(); }
    }
    private static string? LocateGame()
    {
        foreach (var path in new[] { "C:\\GAMES\\EMPIRES DAWN OF THE MODERN WORLD", "C:\\GOG Games\\Empires Dawn of the Modern World" })
            if (File.Exists(Path.Combine(path, "Empires_DMW.exe"))) return path;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive.Name == Registry.LocalMachine.Name ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
                    using var key = root.OpenSubKey("SOFTWARE\\Sierra\\Empires - Dawn of the Modern World");
                    if (key?.GetValue("InstallPath") is string path && File.Exists(Path.Combine(path, "Empires_DMW.exe"))) return path;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }
}

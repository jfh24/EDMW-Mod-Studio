using System.Globalization;
using System.Text;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Home-page inventory of managed civilization packages for the selected game.</summary>
internal sealed class CivilizationInstalledEditsPanel : UserControl
{
    private readonly Func<string?> _locateGame;
    private readonly Action _installedPackagesChanged;
    private readonly Func<string, CivilizationInstalledPackage, bool, bool, Task>? _share;
    private readonly BufferedEntriesPanel _entries = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty
    };
    private string? _listedGamePath;
    private string[] _installedNames = [];

    public CivilizationInstalledEditsPanel(Func<string?> locateGame, Action installedPackagesChanged,
        Func<string, CivilizationInstalledPackage, bool, bool, Task>? share = null)
    {
        _locateGame = locateGame ?? throw new ArgumentNullException(nameof(locateGame));
        _installedPackagesChanged = installedPackagesChanged ?? throw new ArgumentNullException(nameof(installedPackagesChanged));
        _share = share;
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        ForeColor = MedievalTheme.Text;
        BorderStyle = BorderStyle.None;
        Padding = new Padding(16);
        AccessibleName = "Installed civilization edits";
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, BackColor = Color.Transparent
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        header.Controls.Add(new DashboardDisplayLabel
        {
            Text = "CIVILIZATION EDITS", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = MedievalTheme.Text,
            Font = MedievalTheme.DisplayFont(14), Margin = Padding.Empty
        }, 0, 0);
        var refresh = new DashboardPlaqueButton
        {
            Text = "Refresh", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 7),
            AccessibleName = "Refresh installed civilization edits"
        };
        refresh.Click += (_, _) => RefreshInstalledPackages();
        header.Controls.Add(refresh, 1, 0);
        var well = new DashboardRecessedWell { Dock = DockStyle.Fill, Padding = new Padding(4), Margin = Padding.Empty };
        well.Controls.Add(_entries);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(well, 0, 1);
        Controls.Add(layout);
        _entries.HandleCreated += (_, _) => MedievalWindowChrome.ApplyDarkControlTheme(_entries);
        _entries.SizeChanged += (_, _) => ResizeRows();
    }

    internal IReadOnlyList<string> InstalledProjectNamesForVerification => Array.AsReadOnly(_installedNames);

    public void RefreshInstalledPackages()
    {
        _entries.SuspendLayout();
        try
        {
            foreach (Control control in _entries.Controls.Cast<Control>().ToArray()) control.Dispose();
            _entries.Controls.Clear();
            _installedNames = [];
            _listedGamePath = _locateGame();
            if (string.IsNullOrWhiteSpace(_listedGamePath))
            {
                AddMessage("Choose a game folder in Settings to view installed edits.");
                return;
            }
            var packages = CivilizationPackageManager.ReadInstalled(_listedGamePath)
                .OrderByDescending(package => package.InstalledAt).ToArray();
            _installedNames = packages.Select(package => package.ProjectName).ToArray();
            if (packages.Length == 0)
            {
                AddMessage("No installed civilization edits.");
                return;
            }
            foreach (var package in packages) _entries.Controls.Add(CreatePackageRow(package));
        }
        catch (Exception exception) when (Expected(exception))
        {
            AddMessage("Installed edits could not be read. " + exception.Message);
        }
        finally
        {
            _entries.ResumeLayout();
            ResizeRows();
        }
    }

    private Control CreatePackageRow(CivilizationInstalledPackage package)
    {
        var complete = string.Equals(package.Status, "Installed", StringComparison.OrdinalIgnoreCase);
        var listedGamePath = _listedGamePath;
        var canShare = complete && package.PackageId != "legacy" && listedGamePath is not null && _share is not null;
        return new DashboardInstalledItemRow(package.ProjectName,
            (complete ? "Installed " : "Recorded ") + package.InstalledAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            complete ? (package.HasRuntimeEffects ? "Runtime effects " + (package.RuntimeEffectsEnabled ? "enabled" : "disabled") : null) : package.Status,
            canShare, () => ShowDetails(package), () => Uninstall(package),
            destination => _share!(listedGamePath!, package, destination == InstalledShareDestination.Lan,
                destination == InstalledShareDestination.Friends),
            package.PackageId == "legacy" ? "Reinstall this edit as a managed package to enable sharing." : null,
            package.RuntimeEffectsEnabled ? "Disable runtime effects" : "Enable runtime effects",
            complete && package.HasRuntimeEffects ? () => ToggleEffects(package) : null);
    }

    private void ToggleEffects(CivilizationInstalledPackage package)
    {
        try
        {
            var path = _locateGame();
            if (path is null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(_listedGamePath!), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected game folder changed. Refresh the mod list.");
            CivilizationPackageManager.SetRuntimeEffectsEnabled(path, package.PackageId, !package.RuntimeEffectsEnabled);
            RefreshInstalledPackages();
            _installedPackagesChanged();
        }
        catch (Exception exception) when (Expected(exception))
        {
            MedievalDialog.Show((IWin32Window?)FindForm() ?? this, exception.Message,
                "Could not change runtime effects", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowDetails(CivilizationInstalledPackage package)
    {
        using var dialog = new Form
        {
            Text = "Installed civilization edits — " + package.ProjectName,
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
            ClientSize = new Size(830, 530), MinimumSize = new Size(620, 380),
            BackColor = MedievalTheme.Sidebar, ForeColor = MedievalTheme.Text,
            Font = MedievalTheme.UiFont()
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(new Label
        {
            Text = $"{package.ProjectName} · {package.Status} · {package.Changes.Count} changes · " +
                package.InstalledAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            ForeColor = MedievalTheme.GoldBright
        }, 0, 0);
        var text = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
            ScrollBars = ScrollBars.Both, Text = BuildDetails(package),
            AccessibleName = "Installed civilization change details"
        };
        MedievalTheme.StyleTextInput(text);
        layout.Controls.Add(MedievalTheme.FrameInput(text), 0, 1);
        var close = new Button { Text = "Close", Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.OK, Margin = new Padding(0, 8, 0, 0) };
        MedievalTheme.StyleSecondaryButton(close);
        layout.Controls.Add(close, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        MedievalWindowChrome.Apply(dialog);
        dialog.ShowDialog((IWin32Window?)FindForm() ?? this);
    }

    private void Uninstall(CivilizationInstalledPackage package)
    {
        var path = _locateGame();
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetFullPath(path),
            Path.GetFullPath(_listedGamePath ?? path), StringComparison.OrdinalIgnoreCase))
        {
            RefreshInstalledPackages();
            return;
        }
        var owner = (IWin32Window?)FindForm() ?? this;
        if (MedievalDialog.Show(owner, $"Uninstall {package.ProjectName} and restore its previous game values?",
            "Uninstall civilization edits", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            var result = CivilizationPackageManager.Uninstall(path, package.PackageId);
            if (!result.Success)
            {
                MedievalDialog.Show(owner, result.Message, "Could not uninstall edits", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            RefreshInstalledPackages();
            _installedPackagesChanged();
        }
        catch (Exception exception) when (Expected(exception))
        {
            MedievalDialog.Show(owner, exception.Message, "Could not uninstall edits", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddMessage(string message)
    {
        var label = new Label { Text = message, AutoSize = true, ForeColor = MedievalTheme.TextMuted, Margin = new Padding(8) };
        _entries.Controls.Add(label);
    }

    private void ResizeRows()
    {
        var width = Math.Max(1, _entries.ClientSize.Width - (_entries.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
        foreach (Control control in _entries.Controls)
        {
            if (control is DashboardMapRow) control.Width = width;
            else control.MaximumSize = new Size(Math.Max(1, width - control.Margin.Horizontal), 0);
        }
    }

    internal static string BuildDetails(CivilizationInstalledPackage package)
    {
        var details = new StringBuilder();
        details.AppendLine("Status: " + package.Status);
        if (package.HasRuntimeEffects)
        {
            details.AppendLine("Runtime effects: " + (package.RuntimeEffectsEnabled ? "Enabled" : "Disabled"));
            details.AppendLine("Launch through Mod Studio. Every multiplayer participant needs the same enabled effects. Multiplayer validation is pending.");
            details.AppendLine("Changes take effect next launch. Proximity bonuses are recalculated; research resources already awarded remain in saved games.");
            details.AppendLine("Activated bonuses are session-only and reset on load. Disabling the runtime leaves their installed buttons inactive; uninstall removes them.");
        }
        details.AppendLine();
        foreach (var change in package.Changes)
        {
            details.AppendLine(change.CivilizationName + (string.IsNullOrWhiteSpace(change.EntityName) ? "" : " · " + change.EntityName));
            details.AppendLine(change.Label + ": " + change.Before + " → " + change.After);
            if (!string.IsNullOrWhiteSpace(change.Scope)) details.AppendLine(change.Scope);
            details.AppendLine();
        }
        return details.ToString();
    }

    private static bool Expected(Exception exception) => exception is IOException or
        UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException;

    private sealed class BufferedEntriesPanel : FlowLayoutPanel
    {
        public BufferedEntriesPanel() => DoubleBuffered = true;
    }
}

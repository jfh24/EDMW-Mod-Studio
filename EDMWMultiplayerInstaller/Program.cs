using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace EDMWMultiplayerInstaller;

internal static class Program
{
    private static readonly byte[] FooterMagic = Encoding.ASCII.GetBytes("EDMWMAPINSTALL01");
    private const int FooterSize = 64;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var package = ReadPackage(Environment.ProcessPath
                ?? throw new InvalidOperationException("The installer path could not be determined."));
            Application.Run(new InstallerForm(package));
        }
        catch (Exception)
        {
            ThemedDialog.Show(null, "Installer Unavailable", "This installer could not be opened.");
        }
    }

    private static byte[] ReadPackage(string executablePath)
    {
        using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < FooterSize) throw new InvalidDataException("This installer does not contain a multiplayer package.");
        stream.Position = stream.Length - FooterSize;
        Span<byte> footer = stackalloc byte[FooterSize];
        stream.ReadExactly(footer);
        if (!footer[..16].SequenceEqual(FooterMagic)) throw new InvalidDataException("The embedded multiplayer package marker is missing.");
        var offset = BitConverter.ToInt64(footer[16..24]);
        var length = BitConverter.ToInt64(footer[24..32]);
        if (offset < 0 || length <= 0 || length > int.MaxValue || length != stream.Length - FooterSize - offset)
            throw new InvalidDataException("The embedded multiplayer package has invalid boundaries.");
        var package = new byte[(int)length];
        stream.Position = offset;
        stream.ReadExactly(package);
        if (!SHA256.HashData(package).AsSpan().SequenceEqual(footer[32..64]))
            throw new InvalidDataException("The embedded multiplayer package failed integrity verification.");
        return package;
    }
}

internal sealed class InstallerForm : Form
{
    private const string GameExecutable = "Empires_DMW.exe";
    private static readonly Color Shell = Color.FromArgb(10, 9, 8);
    private static readonly Color Surface = Color.FromArgb(28, 23, 19);
    private static readonly Color Raised = Color.FromArgb(45, 34, 27);
    private static readonly Color Border = Color.FromArgb(151, 103, 43);
    private static readonly Color Gold = Color.FromArgb(247, 197, 96);
    private static readonly Color TextColor = Color.FromArgb(245, 224, 184);
    private static readonly Color Muted = Color.FromArgb(194, 164, 116);
    private readonly byte[] _package;
    private readonly ZipFileDescriptor[] _installFiles;
    private readonly TextBox _path;

    public InstallerForm(byte[] package)
    {
        _package = package;
        (_installFiles, var preview) = ReadContents(package);
        Text = "EDMW Mod Studio Multiplayer Map Installer";
        ClientSize = new Size(1280, 960);
        MinimumSize = new Size(1080, 810);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Shell;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9.25f);
        DoubleBuffered = true;
        BackgroundImage = LoadResourceImage("EDMWInstaller.Assets.installer-harbor-frame-v2.png");
        BackgroundImageLayout = ImageLayout.Stretch;

        var surface = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var previewFrame = new Panel { BackColor = Color.Black, Padding = new Padding(2), Margin = Padding.Empty };
        previewFrame.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = preview, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black });

        var side = new Panel { BackColor = Color.Transparent, Margin = Padding.Empty };
        var sideLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 16, 18, 16), RowCount = 9, BackColor = Color.Transparent, Margin = Padding.Empty };
        sideLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        sideLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        sideLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        sideLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        sideLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        sideLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        sideLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        sideLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        sideLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sideLayout.Controls.Add(new Label { Text = "MULTIPLAYER MAP INSTALLER", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Georgia", 12.5f, FontStyle.Bold), ForeColor = Gold, Margin = Padding.Empty }, 0, 0);
        sideLayout.Controls.Add(new InstallerDivider { Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 1);
        sideLayout.Controls.Add(new Label { Text = "GAME INSTALLATION", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Georgia", 10.5f, FontStyle.Bold), ForeColor = Gold, Margin = Padding.Empty }, 0, 2);
        var locatedGame = LocateGame();
        _path = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, TabStop = false, BackColor = Color.FromArgb(9, 10, 10), ForeColor = TextColor, BorderStyle = BorderStyle.None, Text = locatedGame ?? "No installation selected", Margin = new Padding(10, 5, 10, 5) };
        var pathFrame = new InstallerInputFrame { Dock = DockStyle.Fill, Padding = new Padding(2), Margin = new Padding(0, 0, 0, 4) };
        pathFrame.Controls.Add(_path);
        sideLayout.Controls.Add(pathFrame, 0, 3);
        var browse = MakeButton("Choose Game Folder…"); browse.Click += (_, _) => Browse(); sideLayout.Controls.Add(browse, 0, 4);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var install = MakeButton("Install Map"); install.Click += (_, _) => Install();
        var uninstall = MakeButton("Uninstall Map"); uninstall.Click += (_, _) => Uninstall();
        actions.Controls.Add(install, 0, 0); actions.Controls.Add(uninstall, 1, 0); sideLayout.Controls.Add(actions, 0, 5);
        var launch = MakeButton("Launch EDMW", primary: true); launch.Click += (_, _) => LaunchEdmw(); sideLayout.Controls.Add(launch, 0, 7);
        side.Controls.Add(sideLayout);
        surface.Controls.Add(previewFrame);
        surface.Controls.Add(side);
        Controls.Add(surface);

        void ArrangeSurface()
        {
            var width = Math.Max(1, surface.ClientSize.Width);
            var height = Math.Max(1, surface.ClientSize.Height);
            Rectangle Region(float x, float y, float w, float h) => new(
                (int)Math.Round(width * x), (int)Math.Round(height * y),
                (int)Math.Round(width * w), (int)Math.Round(height * h));

            previewFrame.Bounds = Region(0.103f, 0.123f, 0.477f, 0.704f);
            side.Bounds = Region(0.645f, 0.125f, 0.266f, 0.690f);
        }

        surface.Resize += (_, _) => ArrangeSurface();
        surface.HandleCreated += (_, _) => ArrangeSurface();
        InstallerChrome.Apply(this, "EDMW Mod Studio", true, integratedHeader: true);
        if (locatedGame is null) Shown += (_, _) => BeginInvoke(Browse);
    }

    private static Button MakeButton(string text, bool primary = false)
    {
        return new InstallerPlaqueButton { Text = text, Dock = DockStyle.Fill, Margin = new Padding(4), Primary = primary };
    }

    private void Browse()
    {
        if (!ThemedFolderDialog.Choose(this, "Locate Empires DMW", $"Select the folder containing {GameExecutable}.", out var selectedPath)) return;
        if (!IsValidGamePath(selectedPath))
        {
            ThemedDialog.Show(this, "Game Folder Required", $"Select the folder containing {GameExecutable}.");
            return;
        }
        _path.Text = Path.GetFullPath(selectedPath);
    }

    private void Install()
    {
        if (!TryGameRoot(out var root)) return;
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExecutable)).Length > 0)
        {
            ThemedDialog.Show(this, "Close EDMW", "Close EDMW, then try again.");
            return;
        }
        var existing = _installFiles.Select(file => SafeTarget(root, file.ArchivePath)).Where(File.Exists).ToArray();
        if (existing.Length > 0)
        {
            if (!ThemedDialog.Confirm(this, "Replace Existing Files?",
                    "Existing map files will be replaced.")) return;
        }
        else if (!ThemedDialog.Confirm(this, "Install Map?", "Install this map to the selected game folder?")) return;
        try
        {
            // Validate every destination before writing any file. This prevents a locked
            // destination from leaving a partially installed map.
            foreach (var file in _installFiles)
            {
                var target = SafeTarget(root, file.ArchivePath);
                if (!File.Exists(target)) continue;
                using var probe = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            using var memory = new MemoryStream(_package, writable: false);
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
            foreach (var file in _installFiles)
            {
                var entry = archive.GetEntry(file.ArchivePath) ?? throw new InvalidDataException($"Missing {file.ArchivePath}.");
                var target = SafeTarget(root, file.ArchivePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output); output.Flush(true);
            }
            ThemedDialog.Show(this, "Installed", "Map installed.");
        }
        catch (Exception)
        {
            ThemedDialog.Show(this, "Installation Failed", "The map could not be installed. Close EDMW and try again.");
        }
    }

    private void Uninstall()
    {
        if (!TryGameRoot(out var root)) return;
        if (!ThemedDialog.Confirm(this, "Remove Map?", "Remove this map from the selected game folder?")) return;
        try
        {
            var removed = 0;
            foreach (var file in _installFiles)
            {
                var target = SafeTarget(root, file.ArchivePath);
                if (!File.Exists(target)) continue;
                File.Delete(target); removed++;
            }
            var anchors = Path.Combine(root, "Data", "Random Map Scripts", "EDMW Anchors");
            if (Directory.Exists(anchors) && !Directory.EnumerateFileSystemEntries(anchors).Any()) Directory.Delete(anchors);
            var result = removed == 0 ? "This map is not installed." : "Map removed.";
            ThemedDialog.Show(this, removed == 0 ? "Not Installed" : "Removed", result);
        }
        catch (Exception)
        {
            ThemedDialog.Show(this, "Removal Failed", "The map could not be removed. Close EDMW and try again.");
        }
    }

    private bool TryGameRoot(out string root)
    {
        root = _path.Text;
        if (IsValidGamePath(root)) { root = Path.GetFullPath(root); return true; }
        ThemedDialog.Show(this, "Game Folder Required", $"Select the folder containing {GameExecutable}.");
        Browse();
        return false;
    }

    private void LaunchEdmw()
    {
        if (!TryGameRoot(out var root)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(root, GameExecutable),
                WorkingDirectory = root,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            ThemedDialog.Show(this, "EDMW Unavailable", "EDMW could not be started.");
        }
    }

    private static string SafeTarget(string root, string relative)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(boundary, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe package path.");
        return target;
    }

    private static (ZipFileDescriptor[] Files, Image Preview) ReadContents(byte[] package)
    {
        using var memory = new MemoryStream(package, writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        var previewEntry = entries.SingleOrDefault(entry =>
            entry.FullName.Equals("EDMW Installer/map-preview.png", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The package map preview is missing.");
        var files = entries.Where(entry => !entry.FullName.StartsWith("EDMW Installer/", StringComparison.OrdinalIgnoreCase)).Select(entry => new ZipFileDescriptor(entry.FullName)).ToArray();
        var generator = files.SingleOrDefault(file => !file.ArchivePath.Contains('/') &&
            file.ArchivePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        var traceMarkers = files.Where(file => !file.ArchivePath.Contains('/') &&
            file.ArchivePath.EndsWith(".trace", StringComparison.OrdinalIgnoreCase)).ToArray();
        var diagnosticLayout = files.Length == 4 && generator is not null &&
            traceMarkers.Length == 1 &&
            Path.GetFileNameWithoutExtension(traceMarkers[0].ArchivePath).Equals(
                Path.GetFileNameWithoutExtension(generator.ArchivePath), StringComparison.OrdinalIgnoreCase);
        if ((files.Length != 3 && !diagnosticLayout) || generator is null ||
            files.Count(file => file.ArchivePath.EndsWith(".rmv", StringComparison.OrdinalIgnoreCase)) != 1 ||
            files.Count(file => file.ArchivePath.EndsWith(".edmwa", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("The package file set is invalid.");
        using var input = previewEntry.Open();
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        copy.Position = 0;
        using var image = Image.FromStream(copy);
        return (files, new Bitmap(image));
    }

    internal static Image LoadResourceImage(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException("Installer artwork is missing.");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    private static string? LocateGame()
    {
        var candidates = new List<string>();
        try { if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\SSSI\Empires", "Install Directory", null) is string value) candidates.Add(value); } catch { }
        candidates.AddRange([@"C:\GAMES\EMPIRES DAWN OF THE MODERN WORLD", @"C:\GOG Games\Empires - Dawn of the Modern World", @"C:\Program Files (x86)\Steam\steamapps\common\Empires Dawn of the Modern World"]);
        return candidates.FirstOrDefault(IsValidGamePath);
    }

    private static bool IsValidGamePath(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(Path.GetFullPath(path), GameExecutable)); } catch { return false; }
    }

    private sealed record ZipFileDescriptor(string ArchivePath);
}

internal sealed class InstallerInputFrame : Panel
{
    public InstallerInputFrame()
    {
        BackColor = Color.FromArgb(8, 9, 9);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        if (bounds.Width <= 1 || bounds.Height <= 1) return;
        using var outer = new Pen(Color.FromArgb(183, 123, 49));
        using var inner = new Pen(Color.FromArgb(46, 33, 21));
        e.Graphics.DrawRectangle(outer, bounds);
        e.Graphics.DrawRectangle(inner, Rectangle.Inflate(bounds, -3, -3));
    }
}

internal sealed class InstallerDivider : Control
{
    public InstallerDivider()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var middle = Height / 2;
        using var line = new Pen(Color.FromArgb(161, 111, 43));
        e.Graphics.DrawLine(line, 8, middle, Math.Max(8, Width - 8), middle);
        using var fill = new SolidBrush(Color.FromArgb(19, 15, 11));
        using var edge = new Pen(Color.FromArgb(214, 152, 58));
        foreach (var offset in new[] { -17, 0, 17 })
        {
            var centerX = Width / 2 + offset;
            var diamond = new[]
            {
                new Point(centerX, middle - 5), new Point(centerX + 5, middle),
                new Point(centerX, middle + 5), new Point(centerX - 5, middle)
            };
            e.Graphics.FillPolygon(fill, diamond);
            e.Graphics.DrawPolygon(edge, diamond);
        }
    }
}

internal sealed class InstallerPlaqueButton : Button
{
    private bool _hovered;
    private bool _pressed;
    [System.ComponentModel.DefaultValue(false)]
    public bool Primary { get; set; }

    public InstallerPlaqueButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
        ForeColor = Color.FromArgb(246, 223, 181);
        Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 12 || Height < 12) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var shift = _pressed ? 1 : 0;
        var outer = Rectangle.FromLTRB(1, 1 + shift, Width - 2, Height - 2 + shift);
        var inner = Rectangle.Inflate(outer, -4, -4);
        if (inner.Width < 4 || inner.Height < 4) return;

        using (var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
            e.Graphics.FillRectangle(shadow, new Rectangle(outer.X, outer.Y + 2, outer.Width, outer.Height));
        using (var outerBrush = new LinearGradientBrush(outer, Color.FromArgb(92, 58, 23), Color.FromArgb(22, 16, 11), LinearGradientMode.Vertical))
        using (var outerEdge = new Pen(Color.FromArgb(217, 155, 55), 1.25f))
        {
            e.Graphics.FillRectangle(outerBrush, outer);
            e.Graphics.DrawRectangle(outerEdge, outer);
        }
        var top = Primary ? Color.FromArgb(30, 77, 115) : Color.FromArgb(59, 54, 47);
        var bottom = Primary ? Color.FromArgb(12, 37, 63) : Color.FromArgb(28, 25, 21);
        using (var innerBrush = new LinearGradientBrush(inner, top, bottom, LinearGradientMode.Vertical))
        using (var innerEdge = new Pen(_hovered ? Color.FromArgb(255, 206, 106) : Color.FromArgb(118, 86, 43)))
        {
            e.Graphics.FillRectangle(innerBrush, inner);
            e.Graphics.DrawRectangle(innerEdge, inner);
        }

        var font = Primary ? new Font(Font.FontFamily, 15f, FontStyle.Bold) : Font;
        try
        {
            var textBounds = Rectangle.Inflate(inner, -6, -2);
            TextRenderer.DrawText(e.Graphics, Text, font, new Rectangle(textBounds.X + 1, textBounds.Y + 1, textBounds.Width, textBounds.Height), Color.Black,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, Text, font, textBounds, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
        finally
        {
            if (Primary) font.Dispose();
        }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }
}

internal sealed class ThemedDialog : Form
{
    private static readonly Color Shell = Color.FromArgb(10, 9, 8);
    private static readonly Color Surface = Color.FromArgb(28, 23, 19);
    private static readonly Color Raised = Color.FromArgb(45, 34, 27);
    private static readonly Color Border = Color.FromArgb(151, 103, 43);
    private static readonly Color Gold = Color.FromArgb(247, 197, 96);
    private static readonly Color TextColor = Color.FromArgb(245, 224, 184);

    private ThemedDialog(string title, string message, bool confirm)
    {
        Text = title;
        ClientSize = new Size(540, 230);
        MinimumSize = new Size(460, 210);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Shell;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9.5f);

        var frame = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 3, BackColor = Surface, Margin = Padding.Empty, BackgroundImage = InstallerForm.LoadResourceImage("EDMWInstaller.Assets.dark-cartography-texture.png"), BackgroundImageLayout = ImageLayout.Stretch };
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        frame.Controls.Add(new Label { Text = title.ToUpperInvariant(), AutoSize = true, Font = new Font("Georgia", 12, FontStyle.Bold), ForeColor = Gold, Margin = new Padding(0, 0, 0, 14) }, 0, 0);
        frame.Controls.Add(new Label { Text = message, Dock = DockStyle.Fill, ForeColor = TextColor, AutoEllipsis = true }, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.Transparent, WrapContents = false };
        var accept = MakeButton(confirm ? "Yes" : "OK"); accept.DialogResult = DialogResult.OK; buttons.Controls.Add(accept); AcceptButton = accept;
        if (confirm)
        {
            var cancel = MakeButton("No"); cancel.DialogResult = DialogResult.Cancel; buttons.Controls.Add(cancel); CancelButton = cancel;
        }
        frame.Controls.Add(buttons, 0, 2);
        Controls.Add(frame);
        InstallerChrome.Apply(this, title.ToUpperInvariant(), false);
    }

    public static void Show(IWin32Window? owner, string title, string message)
    {
        using var dialog = new ThemedDialog(title, message, false);
        if (owner is null) dialog.ShowDialog(); else dialog.ShowDialog(owner);
    }

    public static bool Confirm(IWin32Window owner, string title, string message)
    {
        using var dialog = new ThemedDialog(title, message, true);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    private static Button MakeButton(string text)
    {
        var button = new Button { Text = text, Width = 112, Height = 34, Margin = new Padding(8, 2, 0, 2), FlatStyle = FlatStyle.Flat, BackColor = Raised, ForeColor = TextColor, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = Border;
        return button;
    }
}

internal sealed class ThemedFolderDialog : Form
{
    private static readonly Color Shell = Color.FromArgb(10, 9, 8);
    private static readonly Color Surface = Color.FromArgb(28, 23, 19);
    private static readonly Color Input = Color.FromArgb(24, 20, 16);
    private static readonly Color Raised = Color.FromArgb(45, 34, 27);
    private static readonly Color Border = Color.FromArgb(151, 103, 43);
    private static readonly Color Gold = Color.FromArgb(247, 197, 96);
    private static readonly Color TextColor = Color.FromArgb(245, 224, 184);
    private static readonly Color Muted = Color.FromArgb(194, 164, 116);
    private readonly TreeView _folders;
    private readonly TextBox _selectedPath;

    private ThemedFolderDialog(string title, string prompt)
    {
        Text = title;
        ClientSize = new Size(680, 560);
        MinimumSize = new Size(560, 460);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Shell;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9.5f);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), RowCount = 5, BackColor = Surface, BackgroundImage = InstallerForm.LoadResourceImage("EDMWInstaller.Assets.dark-cartography-texture.png"), BackgroundImageLayout = ImageLayout.Stretch };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.Controls.Add(new Label { Text = title.ToUpperInvariant(), AutoSize = true, Font = new Font("Georgia", 13, FontStyle.Bold), ForeColor = Gold, Margin = new Padding(0, 0, 0, 8) }, 0, 0);
        layout.Controls.Add(new Label { Text = prompt, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 0, 0, 12) }, 0, 1);

        _selectedPath = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Input, ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle };
        _folders = new TreeView { Dock = DockStyle.Fill, BackColor = Input, ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle, HideSelection = false, ShowLines = true, ShowPlusMinus = true, ShowRootLines = true };
        _folders.BeforeExpand += (_, e) => { if (e.Node is not null) Populate(e.Node); };
        _folders.AfterSelect += (_, e) => _selectedPath.Text = e.Node?.Tag as string ?? "";
        _folders.NodeMouseDoubleClick += (_, e) => { if (e.Node is not null && !e.Node.IsExpanded) e.Node.Expand(); };
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            var node = new TreeNode($"{drive.Name}  {drive.VolumeLabel}") { Tag = drive.RootDirectory.FullName };
            node.Nodes.Add(new TreeNode("Loading…"));
            _folders.Nodes.Add(node);
        }
        layout.Controls.Add(_folders, 0, 2);
        layout.Controls.Add(_selectedPath, 0, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.Transparent, WrapContents = false };
        var select = MakeButton("Select Folder"); select.Click += (_, _) =>
        {
            if (Directory.Exists(_selectedPath.Text)) { DialogResult = DialogResult.OK; Close(); }
        };
        var cancel = MakeButton("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(select); buttons.Controls.Add(cancel);
        AcceptButton = select; CancelButton = cancel;
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        InstallerChrome.Apply(this, title.ToUpperInvariant(), false);
    }

    public static bool Choose(IWin32Window owner, string title, string prompt, out string selectedPath)
    {
        using var dialog = new ThemedFolderDialog(title, prompt);
        var accepted = dialog.ShowDialog(owner) == DialogResult.OK;
        selectedPath = accepted ? dialog._selectedPath.Text : "";
        return accepted;
    }

    private static void Populate(TreeNode node)
    {
        var path = node.Tag as string;
        if (string.IsNullOrWhiteSpace(path)) return;
        node.Nodes.Clear();
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var child = new TreeNode(Path.GetFileName(folder)) { Tag = folder };
                try { if (Directory.EnumerateDirectories(folder).Any()) child.Nodes.Add(new TreeNode("Loading…")); } catch { }
                node.Nodes.Add(child);
            }
        }
        catch { }
    }

    private static Button MakeButton(string text)
    {
        var button = new Button { Text = text, Width = 122, Height = 34, Margin = new Padding(8, 2, 0, 2), FlatStyle = FlatStyle.Flat, BackColor = Raised, ForeColor = TextColor, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = Border;
        return button;
    }
}

internal static class InstallerChrome
{
    private const int NonClientLeftButtonDown = 0x00A1;
    private const int CaptionHit = 2;
    private static readonly Color Shell = Color.FromArgb(10, 9, 8);
    private static readonly Color Header = Color.FromArgb(20, 17, 14);
    private static readonly Color Raised = Color.FromArgb(45, 34, 27);
    private static readonly Color Border = Color.FromArgb(151, 103, 43);
    private static readonly Color Gold = Color.FromArgb(247, 197, 96);
    private static readonly Color TextColor = Color.FromArgb(245, 224, 184);

    public static void Apply(
        Form form, string title, bool allowMaximize, bool integratedHeader = false)
    {
        var headerHeight = integratedHeader ? 56 : 38;
        form.FormBorderStyle = FormBorderStyle.None;
        form.Padding = integratedHeader ? Padding.Empty : new Padding(1, headerHeight + 1, 1, 1);
        form.BackColor = Border;
        form.Icon = CreateShieldIcon();

        var bar = new Panel
        {
            Location = new Point(1, 1),
            Size = new Size(form.ClientSize.Width - 2, headerHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = integratedHeader ? Color.Transparent : Header,
            Padding = integratedHeader ? new Padding(34, 0, 12, 0) : new Padding(10, 0, 4, 0)
        };
        var shield = new PictureBox { Dock = DockStyle.Left, Width = integratedHeader ? 44 : 28, Image = CreateShieldBitmap(integratedHeader ? 26 : 18), SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.Transparent };
        var caption = new Label { Dock = DockStyle.Fill, Text = title, ForeColor = TextColor, Font = new Font("Georgia", integratedHeader ? 14.5f : 8.75f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
        var close = CaptionButton("×", integratedHeader ? 52 : 42); close.Dock = DockStyle.Right; close.Click += (_, _) => form.Close();
        Button? maximize = null;
        if (allowMaximize)
        {
            maximize = CaptionButton("□", integratedHeader ? 52 : 42); maximize.Dock = DockStyle.Right;
            maximize.Click += (_, _) => form.WindowState = form.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }
        Button? minimize = null;
        if (allowMaximize)
        {
            minimize = CaptionButton("—", integratedHeader ? 52 : 42); minimize.Dock = DockStyle.Right;
            minimize.Click += (_, _) => form.WindowState = FormWindowState.Minimized;
        }
        bar.Controls.Add(caption); bar.Controls.Add(shield);
        if (minimize is not null) bar.Controls.Add(minimize);
        if (maximize is not null) bar.Controls.Add(maximize);
        bar.Controls.Add(close);
        foreach (Control control in new Control[] { bar, caption, shield }) control.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginDrag(form); };
        form.Controls.Add(bar);
        bar.BringToFront();
        form.PerformLayout();
        form.Paint += (_, e) => { using var pen = new Pen(Border); e.Graphics.DrawRectangle(pen, 0, 0, form.ClientSize.Width - 1, form.ClientSize.Height - 1); };
    }

    private static Button CaptionButton(string text, int width)
    {
        var button = new Button { Text = text, Width = width, Dock = DockStyle.Right, FlatStyle = FlatStyle.Flat, BackColor = Header, ForeColor = Gold, Font = new Font("Segoe UI", 11), TabStop = false, Margin = Padding.Empty };
        button.FlatAppearance.BorderSize = 0;
        button.MouseEnter += (_, _) => button.BackColor = Raised;
        button.MouseLeave += (_, _) => button.BackColor = Header;
        return button;
    }

    private static Bitmap CreateShieldBitmap(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var pen = new Pen(Gold, Math.Max(1.4f, size / 10f));
        var points = new[] { new PointF(size * .5f, size * .08f), new PointF(size * .86f, size * .23f), new PointF(size * .8f, size * .68f), new PointF(size * .5f, size * .92f), new PointF(size * .2f, size * .68f), new PointF(size * .14f, size * .23f) };
        graphics.DrawPolygon(pen, points);
        graphics.DrawLine(pen, size * .5f, size * .14f, size * .5f, size * .82f);
        return bitmap;
    }

    private static Icon CreateShieldIcon()
    {
        using var bitmap = CreateShieldBitmap(32);
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { _ = DestroyIcon(handle); }
    }

    private static void BeginDrag(Form form)
    {
        _ = ReleaseCapture();
        _ = SendMessage(form.Handle, NonClientLeftButtonDown, CaptionHit, 0);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, int wordParameter, int longParameter);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

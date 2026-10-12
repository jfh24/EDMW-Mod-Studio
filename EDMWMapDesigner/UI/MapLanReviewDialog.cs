using EDMWMapDesigner.Civilizations;
using EDMWMapDesigner.Generation;

namespace EDMWMapDesigner.UI;

internal sealed class MapLanReviewDialog : Form
{
    private readonly MapLanOffer _offer;
    private readonly bool _verificationMode;
    private readonly CancellationToken _offerCancellation;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private readonly TextBox _path;
    private readonly Label _status;
    private readonly Button _choose;
    private readonly Button _accept;
    private readonly Button _deny;
    private readonly TableLayoutPanel _actions;
    private readonly PictureBox _preview;
    private bool _reviewing;
    private bool _installing;

    public MapLanReviewDialog(MapLanOffer offer, string? gamePath = null,
        bool verificationMode = false, CancellationToken offerCancellation = default)
    {
        _offer = offer ?? throw new ArgumentNullException(nameof(offer));
        _verificationMode = verificationMode;
        _offerCancellation = offerCancellation;
        Text = "Incoming map";
        ClientSize = new Size(660, 570);
        MinimumSize = new Size(550, 450);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = MedievalTheme.Sidebar;
        ForeColor = MedievalTheme.Text;
        Font = MedievalTheme.UiFont();
        DoubleBuffered = true;

        var layout = new MedievalBufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 7,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(new Label
        {
            Text = offer.Package.MapName, Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.DisplayFont(19), AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "From " + offer.SenderAddress, Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
            AccessibleName = "Sending computer IP address"
        }, 0, 1);
        _preview = new PictureBox
        {
            Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = MedievalTheme.Input,
            Margin = new Padding(0, 7, 0, 5), AccessibleName = "Incoming map preview"
        };
        // The exporter supplies a neutral image when preview visibility is disabled.
        // Show exactly that exported preview, without reconstructing hidden terrain.
        using (var stream = new MemoryStream(offer.Package.PreviewBytes, writable: false))
        using (var image = Image.FromStream(stream)) _preview.Image = new Bitmap(image);
        layout.Controls.Add(_preview, 0, 2);
        layout.Controls.Add(new Label
        {
            Text = "Accept to install this map into the selected game folder.", Dock = DockStyle.Fill,
            ForeColor = MedievalTheme.TextMuted, TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true, Margin = Padding.Empty
        }, 0, 3);

        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        pathRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _path = new TextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, Text = gamePath ?? "",
            AccessibleName = "Game installation folder"
        };
        MedievalTheme.StyleTextInput(_path);
        var input = MedievalTheme.FrameInput(_path);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 5, 8, 5);
        _choose = ActionButton("Game folder…");
        _choose.Click += async (_, _) => await ChooseFolderAsync();
        pathRow.Controls.Add(input, 0, 0);
        pathRow.Controls.Add(_choose, 1, 0);
        layout.Controls.Add(pathRow, 0, 4);
        _status = new Label
        {
            Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextMuted, AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
            AccessibleName = "Incoming map validation status", Text = "Checking…"
        };
        layout.Controls.Add(_status, 0, 5);
        _actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty
        };
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        _actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _deny = ActionButton("Deny");
        _deny.Click += (_, _) => Deny();
        _accept = ActionButton("Accept");
        _accept.Enabled = false;
        _accept.Click += async (_, _) => await AcceptAsync();
        _actions.Controls.Add(_deny, 1, 0);
        _actions.Controls.Add(_accept, 2, 0);
        layout.Controls.Add(_actions, 0, 6);
        MedievalDialogFrame.Apply(this, layout);
        CancelButton = _deny;
        Shown += async (_, _) =>
        {
            if (_offerCancellation.IsCancellationRequested) { WithdrawAndClose(); return; }
            if (!_verificationMode) await RefreshReviewAsync();
        };
        Activated += async (_, _) =>
        {
            if (!_verificationMode && !_offerCancellation.IsCancellationRequested && !_reviewing && !_installing)
                await RefreshReviewAsync();
        };
        FormClosing += (_, args) => { if (_installing) args.Cancel = true; };
        _cancellationRegistration = offerCancellation.Register(WithdrawOffer);
    }

    public CivilizationLanDecision Decision { get; private set; } = new(CivilizationLanStatus.Denied, "Declined.");
    public string GamePath => _path.Text;
    public string? InstalledGamePath { get; private set; }
    protected override bool ShowWithoutActivation => _verificationMode || base.ShowWithoutActivation;
    internal IReadOnlyList<Button> ActionButtonsForVerification => [_deny, _accept];
    internal Control ActionPanelForVerification => _actions;
    internal Control StatusForVerification => _status;
    internal PictureBox PreviewForVerification => _preview;
    internal Task RefreshForVerificationAsync() => RefreshReviewAsync();
    internal Task AcceptForVerificationAsync() => AcceptAsync();
    internal bool EnterForVerification()
    {
        var message = new Message();
        return ProcessCmdKey(ref message, Keys.Enter);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Enter) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        // Approval always requires the explicit button action, even when that
        // button has focus after tabbing through the review.
        if ((keyData & Keys.KeyCode) == Keys.Enter) return true;
        return base.ProcessDialogKey(keyData);
    }

    private async Task ChooseFolderAsync()
    {
        if (_reviewing || _installing) return;
        if (!MedievalGameFolderDialog.Choose(this, _path.Text, out var selectedPath)) return;
        _path.Text = Path.GetFullPath(selectedPath);
        await RefreshReviewAsync();
    }

    private async Task RefreshReviewAsync()
    {
        if (_reviewing || _installing || IsDisposed) return;
        _accept.Enabled = false;
        if (_offerCancellation.IsCancellationRequested) { WithdrawAndClose(); return; }
        if (!Directory.Exists(_path.Text))
        {
            _status.Text = "Choose a game folder.";
            return;
        }
        _reviewing = true;
        _choose.Enabled = false;
        _status.Text = "Checking…";
        try
        {
            var path = _path.Text;
            var validatedPath = await Task.Run(() => _offer.Package.ValidateDestination(path));
            if (IsDisposed || _offerCancellation.IsCancellationRequested) return;
            _path.Text = validatedPath;
            _accept.Enabled = true;
            _status.ForeColor = MedievalTheme.TextMuted;
            _status.Text = "Ready to install.";
            Decision = new(CivilizationLanStatus.Denied, "Declined.");
            _deny.Text = "Deny";
        }
        catch (Exception error)
        {
            if (!IsDisposed) { _status.ForeColor = MedievalTheme.Warning; _status.Text = error.Message; }
        }
        finally
        {
            _reviewing = false;
            if (!IsDisposed) _choose.Enabled = true;
            if (!IsDisposed && _offerCancellation.IsCancellationRequested) WithdrawAndClose();
        }
    }

    private async Task AcceptAsync()
    {
        if (_reviewing || _installing || !_accept.Enabled || IsDisposed) return;
        if (_offerCancellation.IsCancellationRequested) { WithdrawAndClose(); return; }
        _installing = true;
        _accept.Enabled = false;
        _deny.Enabled = false;
        _choose.Enabled = false;
        _status.Text = "Installing…";
        var path = _path.Text;
        try
        {
            var installedPath = await Task.Run(() =>
            {
                _offerCancellation.ThrowIfCancellationRequested();
                var validatedPath = _offer.Package.ValidateDestination(path);
                _offerCancellation.ThrowIfCancellationRequested();
                return _offer.Package.Install(validatedPath);
            });
            Decision = new(CivilizationLanStatus.Installed, "Installed.");
            InstalledGamePath = installedPath;
            _installing = false;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) when (_offerCancellation.IsCancellationRequested)
        {
            Decision = new(CivilizationLanStatus.Denied, "Offer withdrawn.");
        }
        catch (Exception error)
        {
            Decision = new(CivilizationLanStatus.Failed, error.Message);
            if (!IsDisposed) { _status.ForeColor = MedievalTheme.Warning; _status.Text = error.Message; _deny.Text = "Close"; }
        }
        finally
        {
            _installing = false;
            if (!IsDisposed) { _deny.Enabled = true; _choose.Enabled = true; }
            if (!IsDisposed && _offerCancellation.IsCancellationRequested) Close();
        }
    }

    private void Deny()
    {
        if (_installing) return;
        if (Decision.Status != CivilizationLanStatus.Failed) Decision = new(CivilizationLanStatus.Denied, "Declined.");
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void WithdrawAndClose()
    {
        if (IsDisposed || _installing) return;
        Decision = new(CivilizationLanStatus.Denied, "Offer withdrawn.");
        Close();
    }

    private void WithdrawOffer()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke((Action)WithdrawAndClose); }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }

    private static Button ActionButton(string text)
    {
        var button = new Button
        {
            Text = text, Dock = DockStyle.Fill, AutoSize = false, Margin = new Padding(6, 4, 0, 4),
            MinimumSize = new Size(76, 30), AccessibleName = text
        };
        MedievalTheme.StyleSecondaryButton(button);
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancellationRegistration.Dispose();
            _preview.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}

using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

internal sealed class CivilizationLanReviewDialog : Form
{
    private readonly CivilizationLanOffer _offer;
    private readonly bool _verificationMode;
    private readonly CancellationToken _offerCancellation;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private readonly TextBox _path;
    private readonly Label _status;
    private readonly Button _choose;
    private readonly Button _accept;
    private readonly Button _deny;
    private readonly TableLayoutPanel _actions;
    private readonly ListView _changes;
    private readonly Label _title;
    private bool _reviewing;
    private bool _installing;

    public CivilizationLanReviewDialog(CivilizationLanOffer offer, string? gamePath = null,
        bool verificationMode = false, CancellationToken offerCancellation = default)
    {
        _offer = offer ?? throw new ArgumentNullException(nameof(offer));
        _verificationMode = verificationMode;
        _offerCancellation = offerCancellation;
        Text = "Incoming civilization edits";
        ClientSize = new Size(950, 640);
        MinimumSize = new Size(740, 470);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = MedievalTheme.Sidebar;
        ForeColor = MedievalTheme.Text;
        Font = MedievalTheme.UiFont();
        DoubleBuffered = true;

        var layout = new MedievalBufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        _title = new Label
        {
            Text = offer.Package.ProjectName, Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.DisplayFont(19), AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
        };
        layout.Controls.Add(_title, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "From " + offer.SenderAddress, Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
            AccessibleName = "Sending computer IP address"
        }, 0, 1);

        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        pathRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _path = new TextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, Text = gamePath ?? "", AccessibleName = "Game installation folder"
        };
        MedievalTheme.StyleTextInput(_path);
        var input = MedievalTheme.FrameInput(_path);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 5, 8, 5);
        _choose = ActionButton("Game folder…");
        _choose.Click += async (_, _) => await ChooseFolderAsync();
        pathRow.Controls.Add(input, 0, 0);
        pathRow.Controls.Add(_choose, 1, 0);
        layout.Controls.Add(pathRow, 0, 2);

        _changes = new BufferedChangesList
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            HideSelection = false, ShowItemToolTips = true, BackColor = MedievalTheme.Input,
            ForeColor = MedievalTheme.Text, BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = "Civilization changes requiring approval"
        };
        _changes.Columns.Add("Item", 250);
        _changes.Columns.Add("Field", 160);
        _changes.Columns.Add("Change", 220);
        _changes.Columns.Add("Scope", 320);
        MedievalDialogFrame.StyleListView(_changes);
        layout.Controls.Add(_changes, 0, 3);
        _status = new Label
        {
            Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextMuted, AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
            AccessibleName = "Incoming package validation status",
            Text = "Checking…"
        };
        layout.Controls.Add(_status, 0, 4);

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
        layout.Controls.Add(_actions, 0, 5);
        MedievalDialogFrame.Apply(this, layout);
        CancelButton = _deny;
        Shown += async (_, _) =>
        {
            if (_offerCancellation.IsCancellationRequested) { Close(); return; }
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
    internal ListView ChangesForVerification => _changes;
    internal Task RefreshForVerificationAsync() => RefreshReviewAsync();
    internal Task AcceptForVerificationAsync() => AcceptAsync();

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
        if (!Directory.Exists(_path.Text))
        {
            _status.Text = "Choose a game folder.";
            return;
        }
        _reviewing = true;
        _choose.Enabled = false;
        _status.Text = "Checking…";
        _changes.Items.Clear();
        try
        {
            var path = _path.Text;
            var review = await Task.Run(() => CivilizationLanPackageReview.Prepare(_offer.Package, path));
            if (IsDisposed) return;
            var ready = review.Package is not null && review.Issues.Count == 0;
            _accept.Enabled = ready;
            _status.ForeColor = ready ? MedievalTheme.TextMuted : MedievalTheme.Warning;
            _status.Text = ready ? $"{review.Package!.Changes.Count} {(review.Package.Changes.Count == 1 ? "change" : "changes")}" :
                review.Issues.FirstOrDefault() ?? "Cannot verify these edits.";
            if (ready)
            {
                PopulateChanges(review.Package!);
                Decision = new(CivilizationLanStatus.Denied, "Declined.");
                _deny.Text = "Deny";
            }
        }
        catch (Exception error)
        {
            if (!IsDisposed) { _status.ForeColor = MedievalTheme.Warning; _status.Text = error.Message; }
        }
        finally
        {
            _reviewing = false;
            if (!IsDisposed) _choose.Enabled = true;
        }
    }

    private async Task AcceptAsync()
    {
        if (_reviewing || _installing || !_accept.Enabled || IsDisposed) return;
        if (_offerCancellation.IsCancellationRequested) { Close(); return; }
        _installing = true;
        _accept.Enabled = false;
        _deny.Enabled = false;
        _choose.Enabled = false;
        _status.Text = "Installing…";
        var path = _path.Text;
        try
        {
            // Acceptance is the only write path. The manager checks compatibility
            // again under its transaction lock and records protected backups.
            var result = await Task.Run(() =>
            {
                _offerCancellation.ThrowIfCancellationRequested();
                var review = CivilizationLanPackageReview.Prepare(_offer.Package, path);
                if (review.Package is null || review.Issues.Count > 0)
                    return new CivilizationInstallResult(false, review.Issues.FirstOrDefault() ?? "Cannot verify these edits.");
                _offerCancellation.ThrowIfCancellationRequested();
                return CivilizationPackageManager.Install(review.Package, path);
            });
            Decision = new(result.Success ? CivilizationLanStatus.Installed : CivilizationLanStatus.Failed,
                result.Success ? "Installed." : result.Message);
            if (result.Success)
            {
                InstalledGamePath = path;
                _installing = false;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            _status.ForeColor = MedievalTheme.Warning;
            _status.Text = result.Message;
            _deny.Text = "Close";
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

    private void WithdrawOffer()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed || _installing) return;
                Decision = new(CivilizationLanStatus.Denied, "Offer withdrawn.");
                Close();
            }));
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }

    private void PopulateChanges(CivilizationInstallerPackage package)
    {
        _title.Text = package.ProjectName;
        _changes.BeginUpdate();
        try
        {
            _changes.Items.Clear();
            foreach (var change in package.Changes)
            {
                var row = new ListViewItem(change.CivilizationName + (change.EntityName is null ? "" : " / " + change.EntityName))
                { ToolTipText = change.Description };
                row.SubItems.Add(change.Label);
                row.SubItems.Add(change.Before + " → " + change.After);
                row.SubItems.Add(DisplayScope(change));
                _changes.Items.Add(row);
            }
        }
        finally { _changes.EndUpdate(); }
    }

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

    private sealed class BufferedChangesList : ListView
    {
        public BufferedChangesList() => DoubleBuffered = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cancellationRegistration.Dispose();
        base.Dispose(disposing);
    }
}

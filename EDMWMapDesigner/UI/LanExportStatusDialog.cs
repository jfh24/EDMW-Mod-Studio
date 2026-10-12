using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Discovers recipients and immediately offers an export to every discovered endpoint.</summary>
internal class LanExportStatusDialog : Form
{
    private const string LocalEndpoint = "local:0";
    private readonly Func<CancellationToken, Task<IReadOnlyList<CivilizationLanPeer>>> _discover;
    private readonly Func<CivilizationLanPeer, Action<CivilizationLanProgress>, CancellationToken, Task<CivilizationLanResult>> _send;
    private readonly Func<CancellationToken, Task<CivilizationLanResult>>? _installLocal;
    private readonly bool _verificationMode;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _cancellationToken;
    private readonly Label _status;
    private readonly ListView _peers;
    private readonly Button _close;
    private readonly TableLayoutPanel _actions;
    private readonly Dictionary<string, ListViewItem> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CivilizationLanResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private Task? _workflow;
    private bool _lifetimeDisposed;
    private bool _discoveryComplete;
    private string? _discoveryMessage;

    public LanExportStatusDialog(string title, string packageName,
        Func<CancellationToken, Task<IReadOnlyList<CivilizationLanPeer>>> discover,
        Func<CivilizationLanPeer, Action<CivilizationLanProgress>, CancellationToken, Task<CivilizationLanResult>> send,
        bool verificationMode = false, Func<CancellationToken, Task<CivilizationLanResult>>? installLocal = null)
    {
        _discover = discover ?? throw new ArgumentNullException(nameof(discover));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _installLocal = installLocal;
        _verificationMode = verificationMode;
        _cancellationToken = _lifetime.Token;
        Text = title;
        ClientSize = new Size(510, 280);
        MinimumSize = new Size(440, 265);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = MedievalTheme.Sidebar;
        ForeColor = MedievalTheme.Text;
        Font = MedievalTheme.UiFont();
        DoubleBuffered = true;

        var layout = new MedievalBufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label
        {
            Text = packageName, Dock = DockStyle.Fill, ForeColor = MedievalTheme.GoldBright,
            Font = MedievalTheme.DisplayFont(15), AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
        }, 0, 0);

        _peers = new BufferedPeerList
        {
            Dock = DockStyle.Fill, View = View.Details, CheckBoxes = false, FullRowSelect = true,
            MultiSelect = false, HideSelection = true, ShowItemToolTips = true,
            BackColor = MedievalTheme.Input, ForeColor = MedievalTheme.Text,
            BorderStyle = BorderStyle.FixedSingle, AccessibleName = "LAN recipient decisions"
        };
        _peers.Columns.Add("Computer", 240);
        _peers.Columns.Add("Status", 200);
        MedievalDialogFrame.StyleListView(_peers);
        _peers.Resize += (_, _) => ResizeColumns();
        layout.Controls.Add(_peers, 0, 1);
        _status = new Label
        {
            Dock = DockStyle.Fill, ForeColor = MedievalTheme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty,
            AccessibleName = "LAN transfer status", Text = "Searching for recipients…"
        };
        layout.Controls.Add(_status, 0, 2);

        _actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        _actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _close = new Button
        {
            Text = "Cancel", Dock = DockStyle.Fill, AutoSize = false, Margin = new Padding(6, 4, 0, 4),
            MinimumSize = new Size(68, 30), AccessibleName = "Cancel"
        };
        MedievalTheme.StyleSecondaryButton(_close);
        _close.Click += (_, _) => Close();
        _actions.Controls.Add(_close, 1, 0);
        layout.Controls.Add(_actions, 0, 3);
        CancelButton = _close;
        MedievalDialogFrame.Apply(this, layout);
        Shown += async (_, _) => { if (!_verificationMode) await StartTransferAsync(); };
        FormClosing += (_, _) => CancelTransfer();
    }

    public IReadOnlyDictionary<string, CivilizationLanResult> Results => _results;
    protected override bool ShowWithoutActivation => _verificationMode || base.ShowWithoutActivation;
    internal IReadOnlyList<Button> ActionButtonsForVerification => [_close];
    internal Control ActionPanelForVerification => _actions;
    internal Control StatusForVerification => _status;
    internal ListView PeerListForVerification => _peers;
    internal Task StartForVerificationAsync() => StartTransferAsync();
    internal void AddPeerForVerification(CivilizationLanPeer peer) => AddPeer(peer);

    private Task StartTransferAsync() => _workflow ??= ExportAsync();

    private async Task ExportAsync()
    {
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            ListViewItem? localRow = null;
            if (_installLocal is not null)
            {
                await OnUiAsync(() =>
                {
                    AddPeer(new("You (this computer)", "local", 0));
                    localRow = _rows[LocalEndpoint];
                    localRow.SubItems[1].Text = "Installing…";
                    UpdateStatus();
                });
            }
            if (!CanUpdate) return;
            var local = localRow is null ? Task.CompletedTask : InstallLocalAsync(localRow);
            await Task.WhenAll(local, SendRemotesAsync());
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            await OnUiAsync(() =>
            {
                _discoveryComplete = true;
                _discoveryMessage = "Export failed: " + error.Message;
                UpdateStatus();
            });
        }
        finally
        {
            await OnUiAsync(() =>
            {
                UpdateStatus();
                _close.Text = "Close";
                _close.AccessibleName = "Close";
            });
        }
    }

    private async Task InstallLocalAsync(ListViewItem row)
    {
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var result = await _installLocal!(_cancellationToken);
            if (result.Status is not (CivilizationLanStatus.Installed or CivilizationLanStatus.Failed))
                result = new(CivilizationLanStatus.Failed, "Local installation did not complete.");
            await OnUiAsync(() =>
            {
                _results[LocalEndpoint] = result;
                SetRowStatus(row, result.Status, result.Message, local: true);
                UpdateStatus();
            });
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            await OnUiAsync(() =>
            {
                var result = new CivilizationLanResult(CivilizationLanStatus.Failed, error.Message);
                _results[LocalEndpoint] = result;
                SetRowStatus(row, result.Status, result.Message, local: true);
                UpdateStatus();
            });
        }
    }

    private async Task SendRemotesAsync()
    {
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var peers = await _discover(_cancellationToken);
            if (!CanUpdate) return;
            (ListViewItem Row, CivilizationLanPeer Peer)[] recipients = [];
            await OnUiAsync(() =>
            {
                foreach (var peer in peers.OrderBy(peer => peer.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(peer => peer.Address, StringComparer.OrdinalIgnoreCase)) AddPeer(peer);
                recipients = _rows.Where(pair => pair.Key != LocalEndpoint).Select(pair =>
                    (Row: pair.Value, Peer: (CivilizationLanPeer)pair.Value.Tag!)).ToArray();
                _discoveryComplete = true;
                _discoveryMessage = recipients.Length == 0 ? "No recipients found on this LAN." : null;
                UpdateStatus();
            });
            if (recipients.Length == 0 || !CanUpdate) return;
            using var sendSlots = new SemaphoreSlim(4);
            async Task SendOneAsync(ListViewItem row, CivilizationLanPeer peer)
            {
                var slotOwned = 0;
                var key = Endpoint(peer);
                void ReleaseSendSlot()
                {
                    if (Interlocked.Exchange(ref slotOwned, 0) != 0) sendSlots.Release();
                }
                try
                {
                    await sendSlots.WaitAsync(_cancellationToken);
                    Interlocked.Exchange(ref slotOwned, 1);
                    _cancellationToken.ThrowIfCancellationRequested();
                    var result = await _send(peer, progress =>
                    {
                        // Approval can take minutes. Once upload finishes, let the
                        // next recipient receive its offer while this one reviews.
                        if (progress.Status == CivilizationLanStatus.Awaiting) ReleaseSendSlot();
                        OnUi(() =>
                        {
                            if (!_results.ContainsKey(key)) SetRowStatus(row, progress.Status, progress.Message);
                        });
                    }, _cancellationToken);
                    if (!CanUpdate) return;
                    if (result.Status is not (CivilizationLanStatus.Installed or CivilizationLanStatus.Denied or CivilizationLanStatus.Failed))
                        result = new(CivilizationLanStatus.Failed, "The recipient did not return a final decision.");
                    await OnUiAsync(() =>
                    {
                        _results[key] = result;
                        SetRowStatus(row, result.Status, result.Message);
                        UpdateStatus();
                    });
                }
                catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
                catch (Exception error)
                {
                    if (!CanUpdate) return;
                    var result = new CivilizationLanResult(CivilizationLanStatus.Failed, error.Message);
                    await OnUiAsync(() =>
                    {
                        _results[key] = result;
                        SetRowStatus(row, result.Status, result.Message);
                        UpdateStatus();
                    });
                }
                finally
                {
                    ReleaseSendSlot();
                }
            }
            await Task.WhenAll(recipients.Select(item => SendOneAsync(item.Row, item.Peer)));
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            await OnUiAsync(() =>
            {
                _discoveryComplete = true;
                _discoveryMessage = "Export failed: " + error.Message;
                UpdateStatus();
            });
        }
    }

    private void AddPeer(CivilizationLanPeer peer)
    {
        var key = Endpoint(peer);
        if (_rows.TryGetValue(key, out var existing))
        {
            if (!string.Equals(peer.Name, peer.Address, StringComparison.Ordinal)) existing.Text = peer.Name;
            return;
        }
        var row = new ListViewItem(peer.Name) { Tag = peer, ToolTipText = key };
        row.SubItems.Add("Awaiting");
        _rows.Add(key, row);
        _peers.Items.Add(row);
    }

    private string Summary()
    {
        var remote = _results.Where(pair => pair.Key != LocalEndpoint).Select(pair => pair.Value).ToArray();
        var accepted = remote.Count(result => result.Status == CivilizationLanStatus.Installed);
        var denied = remote.Count(result => result.Status == CivilizationLanStatus.Denied);
        var failed = remote.Count(result => result.Status == CivilizationLanStatus.Failed);
        var local = _results.TryGetValue(LocalEndpoint, out var result)
            ? result.Status == CivilizationLanStatus.Installed ? "Installed locally" : "Local install failed" : null;
        return string.Join(" · ", new[]
        {
            local,
            accepted > 0 ? $"{accepted} accepted" : null,
            denied > 0 ? $"{denied} denied" : null,
            failed > 0 ? $"{failed} failed" : null
        }.Where(text => text is not null));
    }

    private void UpdateStatus()
    {
        var pending = !_discoveryComplete ? "Searching for recipients…" :
            _results.Count < _rows.Count ? $"Waiting · {_results.Count}/{_rows.Count} complete" : null;
        _status.Text = string.Join(" · ", new[] { Summary(), pending, _discoveryMessage }.Where(text => !string.IsNullOrEmpty(text)));
    }

    private static void SetRowStatus(ListViewItem row, CivilizationLanStatus status, string message, bool local = false)
    {
        row.SubItems[1].Text = status switch
        {
            CivilizationLanStatus.Submitted => "Sending…",
            CivilizationLanStatus.Awaiting => "Awaiting approval",
            CivilizationLanStatus.Installed => local ? "Installed" : "Accepted (installed)",
            CivilizationLanStatus.Denied => "Denied",
            _ => "Failed"
        };
        row.ForeColor = status switch
        {
            CivilizationLanStatus.Installed => MedievalTheme.Success,
            CivilizationLanStatus.Failed => MedievalTheme.Warning,
            _ => MedievalTheme.Text
        };
        row.ToolTipText = message;
    }

    private void ResizeColumns()
    {
        if (_peers.Columns.Count != 2) return;
        var available = Math.Max(0, _peers.ClientSize.Width);
        _peers.Columns[0].Width = available / 2;
        _peers.Columns[1].Width = available - _peers.Columns[0].Width;
    }

    private bool CanUpdate => !IsDisposed && !Disposing && !_cancellationToken.IsCancellationRequested;

    private void OnUi(Action action)
    {
        if (!CanUpdate) return;
        try
        {
            if (InvokeRequired) BeginInvoke((Action)(() => { if (CanUpdate) action(); }));
            else action();
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }

    private async Task OnUiAsync(Action action)
    {
        if (!CanUpdate) return;
        if (!InvokeRequired)
        {
            if (CanUpdate) action();
            return;
        }
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = _cancellationToken.Register(() => completed.TrySetResult());
        try
        {
            BeginInvoke((Action)(() =>
            {
                try
                {
                    if (CanUpdate) action();
                    completed.TrySetResult();
                }
                catch (Exception error) { completed.TrySetException(error); }
            }));
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { completed.TrySetResult(); }
        await completed.Task.ConfigureAwait(false);
    }

    private void CancelTransfer()
    {
        if (!_lifetimeDisposed) _lifetime.Cancel();
    }

    private static string Endpoint(CivilizationLanPeer peer) => $"{peer.Address}:{peer.Port}";

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_lifetimeDisposed)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            _lifetimeDisposed = true;
        }
        base.Dispose(disposing);
    }

    private sealed class BufferedPeerList : ListView
    {
        public BufferedPeerList() => DoubleBuffered = true;
    }
}

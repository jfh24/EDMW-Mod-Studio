using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Checks automatic sending, offscreen layout and approval defaults without real network transfers.</summary>
internal static class CivilizationLanDialogVerification
{
    private const string ObjectFile = @"Data\db\dbobjects.dat";
    private static readonly string[] FixtureDependencies =
    [
        "Empires_DMW.exe", ObjectFile, @"Data\db\dbtechtree.dat", @"Data\db\dbphoenixcivtable.dat",
        @"Data\db\dbgraphics.dat", @"Data\db\dbbuttons.dat", @"Data\db\dbweapontohit.dat",
        @"Data\db\dbfamily.dat", @"Data\db\dbeffects.dat"
    ];

    public static IReadOnlyList<string> Verify(CivilizationInstallerPackage package, string gamePath)
    {
        var checks = new List<string>();
        using var transfer = new CivilizationLanTransfer((_, _) => Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied)));
        foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            using (var send = new CivilizationLanSendDialog(package, transfer, verificationMode: true))
            {
                PrepareOffscreen(send);
                Require(send.ActionButtonsForVerification.Count == 1 && !send.PeerListForVerification.CheckBoxes,
                    "The compact sender has only Cancel/Close and no recipient selection.");
                Require(send.ClientSize.Width <= 512 && send.ClientSize.Height <= 314,
                    "The sender opens as a small status popup.");
                Require(send.FormBorderStyle == FormBorderStyle.None && send.PeerListForVerification.OwnerDraw,
                    "Sender chrome and list headings use the shared medieval theme.");
                send.AddPeerForVerification(new("Test computer", "127.0.0.1"));
                send.AddPeerForVerification(new("Test computer", "127.0.0.1"));
                Require(send.PeerListForVerification.Items.Count == 1, "Discovery preserves one row per endpoint.");
                send.StatusForVerification.Text = new string('W', 500);
                VerifySizes(send, send.ActionPanelForVerification, send.ActionButtonsForVerification,
                    send.StatusForVerification, scale, "send", checks);
                send.Hide();
            }
            using (var review = new CivilizationLanReviewDialog(new(package, "192.168.1.20", DateTimeOffset.UtcNow),
                gamePath, verificationMode: true))
            {
                PrepareOffscreen(review);
                Require(review.Decision.Status == CivilizationLanStatus.Denied, "Incoming edits default to Denied.");
                Require(review.AcceptButton is null && !review.ActionButtonsForVerification[1].Enabled,
                    "Unverified edits cannot be accepted or installed by Enter.");
                Require(review.InstalledGamePath is null && review.ChangesForVerification.Items.Count == 0,
                    "Incoming claims are withheld until source verification.");
                review.StatusForVerification.Text = new string('W', 500);
                VerifySizes(review, review.ActionPanelForVerification, review.ActionButtonsForVerification,
                    review.StatusForVerification, scale, "review", checks);
                review.Hide();
            }
        }
        checks.AddRange(VerifyAutomaticSending());
        using (var cancelled = new CancellationTokenSource())
        using (var review = new CivilizationLanReviewDialog(new(package, "192.168.1.20", DateTimeOffset.UtcNow),
            gamePath, verificationMode: true, offerCancellation: cancelled.Token))
        {
            PrepareOffscreen(review);
            cancelled.Cancel();
            Application.DoEvents();
            Require(!review.Visible && review.Decision.Status == CivilizationLanStatus.Denied && review.InstalledGamePath is null,
                "Withdrawing an unaccepted offer closes review without installing.");
            checks.Add("Unaccepted offer cancellation closes review and stays Denied.");
        }
        using (var review = new CivilizationLanReviewDialog(new(package, "192.168.1.20", DateTimeOffset.UtcNow),
            gamePath, verificationMode: true))
        {
            PrepareOffscreen(review);
            review.Close();
            Require(review.Decision.Status == CivilizationLanStatus.Denied && review.InstalledGamePath is null,
                "Closing review denies an unaccepted offer.");
            checks.Add("Closing review stays Denied; no automatic approval action is assigned.");
        }
        return checks;
    }

    public static IReadOnlyList<string> VerifyAutomaticSending(string? outputDirectory = null)
    {
        // Offscreen layout probes may dispose the last form and uninstall the
        // automatic context. Keep this workflow on the UI thread while pumping.
        var previousContext = SynchronizationContext.Current;
        using var context = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try { return VerifyAutomaticSendingCore(outputDirectory); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static IReadOnlyList<string> VerifyAutomaticSendingCore(string? outputDirectory)
    {
        var checks = new List<string>();
        var peers = Enumerable.Range(1, 8).Select(index =>
            new CivilizationLanPeer("Computer " + index, "127.0.0.1", 43000 + index)).ToArray();
        var sent = new ConcurrentBag<int>();
        var uploads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvals = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allAwaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var awaiting = 0;
        var discoveries = 0;
        Action<CivilizationLanProgress>? lateProgress = null;
        async Task<CivilizationLanResult> SendAsync(CivilizationLanPeer peer,
            Action<CivilizationLanProgress> progress, CancellationToken cancellationToken)
        {
            sent.Add(peer.Port);
            var uploading = Interlocked.Increment(ref active);
            var observedPeak = Volatile.Read(ref peak);
            while (uploading > observedPeak)
            {
                var prior = Interlocked.CompareExchange(ref peak, uploading, observedPeak);
                if (prior == observedPeak) break;
                observedPeak = prior;
            }
            progress(new(CivilizationLanStatus.Submitted, "Sending fixture export"));
            if (peer.Port == 43001) lateProgress = progress;
            try { await uploads.Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref active); }
            await Task.Run(() =>
            {
                progress(new(CivilizationLanStatus.Awaiting, "Fixture recipient reviewing"));
                progress(new(CivilizationLanStatus.Awaiting, "Repeated asynchronous approval progress"));
            }, cancellationToken);
            if (Interlocked.Increment(ref awaiting) == peers.Length) allAwaiting.SetResult();
            await approvals.Task.WaitAsync(cancellationToken);
            if (peer.Port == 43004) throw new IOException("Fixture recipient disconnected.");
            var status = peer.Port switch
            {
                43001 or 43005 or 43007 => CivilizationLanStatus.Installed,
                43002 or 43006 => CivilizationLanStatus.Denied,
                43008 => CivilizationLanStatus.Submitted,
                _ => CivilizationLanStatus.Failed
            };
            return new(status, "Fixture recipient decision.");
        }
        using (var send = new LanExportStatusDialog("Export over LAN", "LAN export verification", _ =>
        {
            discoveries++;
            return Task.FromResult<IReadOnlyList<CivilizationLanPeer>>(peers.Append(peers[0] with { Name = "Duplicate endpoint" }).ToArray());
        }, SendAsync, verificationMode: true))
        {
            PrepareOffscreen(send);
            var task = send.StartForVerificationAsync();
            Require(ReferenceEquals(task, send.StartForVerificationAsync()) && discoveries == 1,
                "Opening an export starts only one discovery/send workflow.");
            Require(sent.Count == 4 && peak == 4 && active == 4 && !task.IsCompleted,
                "Automatic uploads are bounded to four concurrent recipients.");
            Require(send.ActionButtonsForVerification.Count == 1 && send.ActionButtonsForVerification[0].Text == "Cancel" &&
                !send.PeerListForVerification.CheckBoxes && send.PeerListForVerification.Columns.Count == 2,
                "Automatic export presents only recipient statuses and Cancel.");
            Require(send.PeerListForVerification.Items.Count == peers.Length &&
                send.PeerListForVerification.Items.Cast<ListViewItem>().Count(row => row.SubItems[1].Text == "Sending…") == 4,
                "Active uploads are visible while other recipients wait for an upload slot.");
            uploads.SetResult();
            Complete(allAwaiting.Task, "Upload to all recipients before any approval");
            WaitFor(() => send.PeerListForVerification.Items.Cast<ListViewItem>()
                .All(row => row.SubItems[1].Text == "Awaiting approval"), "Posted recipient approval statuses");
            Require(sent.Count == peers.Length && send.Results.Count == 0 && active == 0 && !task.IsCompleted &&
                send.PeerListForVerification.Items.Cast<ListViewItem>().All(row => row.SubItems[1].Text == "Awaiting approval"),
                "Every recipient receives the export without waiting for an earlier acceptance or denial.");
            approvals.SetResult();
            Complete(task, "Automatic export to all recipients");
            Require(sent.Count == peers.Length && sent.Distinct().Count() == peers.Length && peak == 4 && active == 0,
                "Every discovered endpoint is offered exactly once, including queued recipients.");
            Require(send.Results.Count == peers.Length &&
                send.Results.Values.Count(result => result.Status == CivilizationLanStatus.Installed) == 3 &&
                send.Results.Values.Count(result => result.Status == CivilizationLanStatus.Denied) == 2 &&
                send.Results.Values.Count(result => result.Status == CivilizationLanStatus.Failed) == 3,
                "Mixed approvals, denials, exceptions and invalid final replies are recorded independently: " +
                string.Join("; ", send.Results.Select(pair => $"{pair.Key}={pair.Value.Status} ({pair.Value.Message})")));
            Require(send.StatusForVerification.Text == "3 accepted · 2 denied · 3 failed" &&
                send.ActionButtonsForVerification[0].Text == "Close",
                "The popup summarizes completed decisions and switches Cancel to Close.");
            lateProgress!(new(CivilizationLanStatus.Awaiting, "A delayed progress callback."));
            Application.DoEvents();
            var accepted = send.PeerListForVerification.Items.Cast<ListViewItem>().Single(row =>
                ((CivilizationLanPeer)row.Tag!).Port == 43001);
            Require(accepted.SubItems[1].Text == "Accepted (installed)",
                "Late progress cannot replace a completed recipient decision.");
            if (outputDirectory is not null)
            {
                Directory.CreateDirectory(outputDirectory);
                using var screenshot = new Bitmap(send.Width, send.Height);
                send.DrawToBitmap(screenshot, new Rectangle(Point.Empty, send.Size));
                screenshot.Save(Path.Combine(outputDirectory, "compact-lan-export-status.png"));
            }
            send.Hide();
        }
        checks.Add("One automatic workflow uploads to all eight unique recipients with four concurrent uploads before any approval, preserving independent accepted/denied/failed decisions and delayed-progress ordering.");

        var unexpectedSends = 0;
        Task<CivilizationLanResult> UnexpectedSend(CivilizationLanPeer _, Action<CivilizationLanProgress> __,
            CancellationToken ___)
        {
            Interlocked.Increment(ref unexpectedSends);
            throw new InvalidOperationException("An empty or canceled discovery attempted to send.");
        }
        using (var empty = new LanExportStatusDialog("Export over LAN", "Empty LAN", _ =>
            Task.FromResult<IReadOnlyList<CivilizationLanPeer>>([]), UnexpectedSend, verificationMode: true))
        {
            PrepareOffscreen(empty);
            Complete(empty.StartForVerificationAsync(), "Empty LAN discovery");
            Require(unexpectedSends == 0 && empty.Results.Count == 0 && empty.StatusForVerification.Text == "No recipients found on this LAN." &&
                empty.ActionButtonsForVerification[0].Text == "Close", "Empty discovery completes without sends or recipient inputs.");
            empty.Hide();
        }
        using (var failed = new LanExportStatusDialog("Export over LAN", "Discovery failure", _ =>
            Task.FromException<IReadOnlyList<CivilizationLanPeer>>(new IOException("Fixture discovery failed.")),
            UnexpectedSend, verificationMode: true))
        {
            PrepareOffscreen(failed);
            Complete(failed.StartForVerificationAsync(), "Failed LAN discovery");
            Require(unexpectedSends == 0 && failed.StatusForVerification.Text == "Export failed: Fixture discovery failed." &&
                failed.ActionButtonsForVerification[0].Text == "Close", "A discovery error is visible and finishes the popup workflow.");
            failed.Hide();
        }
        checks.Add("Empty and failed discoveries finish with a compact status message and no sends.");

        CancellationToken discoveryToken = default;
        async Task<IReadOnlyList<CivilizationLanPeer>> CancellableDiscovery(CancellationToken cancellationToken)
        {
            discoveryToken = cancellationToken;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return peers;
        }
        using (var cancel = new LanExportStatusDialog("Export over LAN", "Cancel discovery", CancellableDiscovery,
            UnexpectedSend, verificationMode: true))
        {
            PrepareOffscreen(cancel);
            var task = cancel.StartForVerificationAsync();
            cancel.ActionButtonsForVerification[0].PerformClick();
            Complete(task, "Cancel LAN discovery");
            Require(discoveryToken.IsCancellationRequested && unexpectedSends == 0 && !cancel.Visible,
                "Cancel closes the popup and cancels discovery before any send.");
        }
        var started = 0;
        var canceledSends = 0;
        async Task<CivilizationLanResult> CancellableSend(CivilizationLanPeer _,
            Action<CivilizationLanProgress> __, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref started);
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Interlocked.Increment(ref canceledSends); throw; }
            return new(CivilizationLanStatus.Failed, "Unexpected fixture completion.");
        }
        using (var cancel = new LanExportStatusDialog("Export over LAN", "Cancel transfers", _ =>
            Task.FromResult<IReadOnlyList<CivilizationLanPeer>>(peers), CancellableSend, verificationMode: true))
        {
            PrepareOffscreen(cancel);
            var task = cancel.StartForVerificationAsync();
            Require(started == 4 && !task.IsCompleted, "Cancel fixture has four active sends and four queued recipients.");
            cancel.ActionButtonsForVerification[0].PerformClick();
            Complete(task, "Cancel LAN transfers");
            Require(started == 4 && canceledSends == 4 && cancel.Results.Count == 0 && !cancel.Visible,
                "Closing cancels active sends and prevents queued offers from starting.");
        }
        checks.Add("Cancel withdraws discovery or active sends and prevents queued recipients from receiving a new offer.");
        VerifyLocalInstallation(peers.Take(2).ToArray(), outputDirectory, checks);
        return checks;
    }

    private static void VerifyLocalInstallation(IReadOnlyList<CivilizationLanPeer> peers, string? outputDirectory,
        List<string> checks)
    {
        var localCalls = 0;
        var remoteCalls = 0;
        var localRelease = new TaskCompletionSource<CivilizationLanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CivilizationLanResult> Local(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref localCalls);
            return localRelease.Task;
        }
        Task<CivilizationLanResult> NoRemote(CivilizationLanPeer _, Action<CivilizationLanProgress> __,
            CancellationToken ___)
        {
            Interlocked.Increment(ref remoteCalls);
            throw new InvalidOperationException("Local installation was incorrectly sent to the network.");
        }
        using (var send = new LanExportStatusDialog("Export over LAN", "Install on this computer", _ =>
            Task.FromResult<IReadOnlyList<CivilizationLanPeer>>([]), NoRemote, verificationMode: true, installLocal: Local))
        {
            PrepareOffscreen(send);
            var task = send.StartForVerificationAsync();
            Require(localCalls == 1 && remoteCalls == 0 && send.PeerListForVerification.Items.Count == 1 &&
                send.PeerListForVerification.Items[0].Text == "You (this computer)" && !task.IsCompleted &&
                send.ActionButtonsForVerification[0].Text == "Cancel", "Local installation starts once and runs even with no LAN recipients.");
            localRelease.SetResult(new(CivilizationLanStatus.Installed, "Fixture installed locally."));
            Complete(task, "Local installation without recipients");
            Require(send.Results.Count == 1 && send.Results["local:0"].Status == CivilizationLanStatus.Installed &&
                send.PeerListForVerification.Items[0].SubItems[1].Text == "Installed" &&
                send.StatusForVerification.Text == "Installed locally · No recipients found on this LAN." &&
                send.ActionButtonsForVerification[0].Text == "Close", "Local completion is recorded and shown independently from empty discovery.");
            send.Hide();
        }
        using (var send = new LanExportStatusDialog("Export over LAN", "Discovery unavailable", _ =>
            Task.FromException<IReadOnlyList<CivilizationLanPeer>>(new IOException("LAN unavailable.")), NoRemote,
            verificationMode: true, installLocal: _ =>
            {
                Interlocked.Increment(ref localCalls);
                return Task.FromResult(new CivilizationLanResult(CivilizationLanStatus.Installed, "Installed despite discovery failure."));
            }))
        {
            PrepareOffscreen(send);
            Complete(send.StartForVerificationAsync(), "Local installation with failed discovery");
            Require(localCalls == 2 && remoteCalls == 0 && send.Results["local:0"].Status == CivilizationLanStatus.Installed &&
                send.StatusForVerification.Text == "Installed locally · Export failed: LAN unavailable.",
                "A failed LAN discovery does not prevent the sharer's local installation.");
            send.Hide();
        }
        using (var send = new LanExportStatusDialog("Export over LAN", "Local and LAN decisions", _ =>
            Task.FromResult(peers), (peer, _, _) =>
            {
                Interlocked.Increment(ref remoteCalls);
                return Task.FromResult(new CivilizationLanResult(peer == peers[0] ? CivilizationLanStatus.Installed :
                    CivilizationLanStatus.Denied, "Independent remote decision."));
            }, verificationMode: true, installLocal: _ =>
            {
                Interlocked.Increment(ref localCalls);
                return Task.FromException<CivilizationLanResult>(new IOException("Fixture local installation failed."));
            }))
        {
            PrepareOffscreen(send);
            Complete(send.StartForVerificationAsync(), "Independent local and remote decisions");
            Require(localCalls == 3 && remoteCalls == 2 && send.Results.Count == 3 &&
                send.Results["local:0"].Status == CivilizationLanStatus.Failed &&
                send.Results.Values.Count(result => result.Status == CivilizationLanStatus.Installed) == 1 &&
                send.Results.Values.Count(result => result.Status == CivilizationLanStatus.Denied) == 1 &&
                send.StatusForVerification.Text == "Local install failed · 1 accepted · 1 denied",
                "A local failure preserves remote acceptance and denial results and never creates a local network offer.");
            if (outputDirectory is not null)
            {
                using var screenshot = new Bitmap(send.Width, send.Height);
                send.DrawToBitmap(screenshot, new Rectangle(Point.Empty, send.Size));
                screenshot.Save(Path.Combine(outputDirectory, "compact-lan-export-local-status.png"));
            }
            send.Hide();
        }
        using (var canceled = new LanExportStatusDialog("Export over LAN", "Canceled before installation", _ =>
            Task.FromResult<IReadOnlyList<CivilizationLanPeer>>([]), NoRemote, verificationMode: true, installLocal: Local))
        {
            PrepareOffscreen(canceled);
            canceled.Close();
            Complete(canceled.StartForVerificationAsync(), "Cancellation before local installation");
            Require(localCalls == 3 && remoteCalls == 2, "Cancellation before the workflow starts prevents local and remote work.");
        }
        var committed = false;
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<CivilizationLanResult> AcceptedTransaction(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref localCalls);
            await commit.Task;
            committed = true;
            return new(CivilizationLanStatus.Installed, "Accepted local transaction finished.");
        }
        using (var canceled = new LanExportStatusDialog("Export over LAN", "Accepted local installation", async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return peers;
        }, NoRemote, verificationMode: true, installLocal: AcceptedTransaction))
        {
            PrepareOffscreen(canceled);
            var task = canceled.StartForVerificationAsync();
            Require(localCalls == 4 && !task.IsCompleted, "An explicitly initiated local transaction runs alongside discovery.");
            canceled.ActionButtonsForVerification[0].PerformClick();
            Application.DoEvents();
            Require(!task.IsCompleted && !committed && remoteCalls == 2, "Closing the popup withdraws discovery while an accepted transaction can finish.");
            commit.SetResult();
            Complete(task, "Accepted local transaction after popup cancellation");
            Require(committed && localCalls == 4 && remoteCalls == 2,
                "An already accepted local transaction completes once after popup cancellation.");
        }
        checks.Add("The sharer installs locally exactly once even with empty/failed discovery; local failures remain independent of LAN decisions, and accepted local transactions can finish after closing.");
    }

    public static IReadOnlyList<string> VerifyApproval(string gamePath, string outputDirectory)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (output.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LAN dialog verification must run outside the source game folder.");
        var sourceHashes = FixtureDependencies.ToDictionary(relative => relative,
            relative => HashFile(Path.Combine(source, relative)), StringComparer.OrdinalIgnoreCase);
        var fixture = Path.Combine(output, "lan-dialog-fixtures", Guid.NewGuid().ToString("N"));
        if (Directory.Exists(fixture)) throw new InvalidOperationException("LAN dialog verification requires a new fixture.");
        Directory.CreateDirectory(fixture);
        foreach (var relative in FixtureDependencies)
        {
            var destination = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(source, relative), destination);
            File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
        }
        File.WriteAllText(Path.Combine(fixture, "VERIFICATION_ONLY.txt"), "Isolated LAN approval fixture. Never executed.\n");
        var checks = new List<string>();
        try
        {
            var originalObjects = File.ReadAllBytes(Path.Combine(fixture, ObjectFile));
            var health = BinaryPrimitives.ReadUInt32LittleEndian(originalObjects.AsSpan(4 + 833 * 1936 + 120, 4));
            var project = CivilizationEditProject.CreateDefault();
            project.Name = "LAN dialog approval fixture";
            project.Civilizations.Single(civ => civ.SourceRowIndex == 10).Entities.Add(new()
            {
                Name = "Citizen (English)", Category = CivilizationCategory.Units, SourceObjectId = 833,
                Properties = new(StringComparer.Ordinal)
                {
                    ["unit.health"] = checked(health + 13).ToString(CultureInfo.InvariantCulture)
                }
            });
            var before = Snapshot(fixture);
            var package = CivilizationInstallerPackage.Create(project, fixture);
            RequireSame(before, Snapshot(fixture), "Preparing the real approval fixture is read-only.");

            using (var denied = NewReview(package, fixture))
            {
                PrepareOffscreen(denied);
                Complete(denied.RefreshForVerificationAsync(), "Read-only recipient review");
                RequireReady(denied, package);
                RequireSame(before, Snapshot(fixture), "Verified review does not change the fixture.");
                denied.ActionButtonsForVerification[0].PerformClick();
                Require(denied.Decision.Status == CivilizationLanStatus.Denied && denied.InstalledGamePath is null,
                    "The actual Deny handler keeps an offer uninstalled.");
            }
            RequireSame(before, Snapshot(fixture), "The actual Deny handler creates no package or game writes.");
            checks.Add("Production recipient review populates source-verified changes; the actual Deny action leaves all files and directories unchanged.");

            using (var withdrawn = new CancellationTokenSource())
            using (var cancelled = NewReview(package, fixture, withdrawn.Token))
            {
                PrepareOffscreen(cancelled);
                Complete(cancelled.RefreshForVerificationAsync(), "Review before withdrawal");
                RequireReady(cancelled, package);
                withdrawn.Cancel();
                // Invoke the production accept method before pumping the queued
                // withdrawal callback: its own token guard must prevent writes.
                Complete(cancelled.AcceptForVerificationAsync(), "Withdrawn offer acceptance guard");
                Require(cancelled.Decision.Status == CivilizationLanStatus.Denied && cancelled.InstalledGamePath is null,
                    "An offer withdrawn after successful review cannot enter installation.");
            }
            RequireSame(before, Snapshot(fixture), "Withdrawing a ready offer creates no package or game writes.");
            checks.Add("Withdrawal after a successful review is checked again by the production Accept handler and makes no writes.");

            using (var accepted = NewReview(package, fixture))
            {
                PrepareOffscreen(accepted);
                Complete(accepted.RefreshForVerificationAsync(), "Review before explicit acceptance");
                RequireReady(accepted, package);
                Complete(accepted.AcceptForVerificationAsync(), "Explicit recipient acceptance");
                Require(accepted.Decision.Status == CivilizationLanStatus.Installed && accepted.InstalledGamePath == fixture,
                    "The production Accept handler reports successful fixture installation.");
            }
            var expected = originalObjects.ToArray();
            foreach (var patch in package.Files.Single(file => file.RelativePath == ObjectFile).Patches)
                patch.Replacement.CopyTo(expected, patch.Offset);
            Require(File.ReadAllBytes(Path.Combine(fixture, ObjectFile)).AsSpan().SequenceEqual(expected),
                "The production Accept handler writes exactly the reviewed semantic field.");
            var inventory = CivilizationPackageManager.ReadInstalled(fixture);
            Require(inventory.Count == 1 && inventory[0].PackageId == package.PackageId &&
                inventory[0].ProjectName == package.ProjectName && inventory[0].Changes.Count == 1 &&
                inventory[0].Changes[0].Before == package.Changes[0].Before && inventory[0].Changes[0].After == package.Changes[0].After,
                "The accepted dialog package records its real before/after values in the tracked inventory.");
            using var home = new CivilizationInstalledEditsPanel(() => fixture,
                () => throw new InvalidOperationException("The verification inventory attempted a user action."));
            home.RefreshInstalledPackages();
            Require(home.InstalledProjectNamesForVerification.SequenceEqual(new[] { package.ProjectName }),
                "The Home panel lists the package installed through the production Accept handler.");
            checks.Add("The production Accept handler revalidates, writes exactly the reviewed field and records the package in Home.");

            var removed = CivilizationPackageManager.Uninstall(fixture, package.PackageId);
            Require(removed.Success, "The package installed by the production Accept handler uninstalls: " + removed.Message);
            home.RefreshInstalledPackages();
            Require(home.InstalledProjectNamesForVerification.Count == 0 && CivilizationPackageManager.ReadInstalled(fixture).Count == 0,
                "Uninstall clears the accepted package from Home and the tracked inventory.");
            Require(FixtureDependencies.All(relative => HashFile(Path.Combine(fixture, relative)) == sourceHashes[relative]),
                "The approval fixture restores every original game-file hash.");
            checks.Add("Uninstall clears Home and restores all copied game-file hashes after a production UI acceptance.");
            File.WriteAllLines(Path.Combine(output, "civilization-lan-dialog-approval-verification.txt"),
                checks.Append("Fixture: " + fixture));
            return checks.AsReadOnly();
        }
        finally
        {
            foreach (var pair in sourceHashes)
                Require(HashFile(Path.Combine(source, pair.Key)) == pair.Value,
                    "The approval verifier preserves the source game file: " + pair.Key);
        }
    }

    private static CivilizationLanReviewDialog NewReview(CivilizationInstallerPackage package, string fixture,
        CancellationToken cancellationToken = default) =>
        new(new(package, "127.0.0.1", DateTimeOffset.UtcNow), fixture, verificationMode: true,
            offerCancellation: cancellationToken);

    private static void RequireReady(CivilizationLanReviewDialog dialog, CivilizationInstallerPackage package)
    {
        Require(dialog.ActionButtonsForVerification[1].Enabled && dialog.ChangesForVerification.Items.Count == package.Changes.Count,
            "Source verification populates changes and enables explicit acceptance: " + dialog.StatusForVerification.Text);
        var row = dialog.ChangesForVerification.Items[0];
        Require(row.SubItems[1].Text == package.Changes[0].Label &&
            row.SubItems[2].Text == package.Changes[0].Before + " → " + package.Changes[0].After,
            "The real review rows contain the verified field and before/after values.");
        Require(dialog.Decision.Status == CivilizationLanStatus.Denied && dialog.InstalledGamePath is null,
            "A successful source review alone does not approve installation.");
    }

    private static void Complete(Task task, string operation)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("LAN dialog verification timed out: " + operation);
            Application.DoEvents();
            Thread.Sleep(2);
        }
        task.GetAwaiter().GetResult();
        Application.DoEvents();
    }

    private static void WaitFor(Func<bool> condition, string operation)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("LAN dialog verification timed out: " + operation);
            Application.DoEvents();
            Thread.Sleep(2);
        }
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), HashFile, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(root, directory) + "\\"] = "directory";
        return files;
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void RequireSame(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual,
        string message) => Require(expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var hash) && hash == pair.Value), message);

    private static void PrepareOffscreen(Form form)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.Show();
        Application.DoEvents();
        form.AutoScaleMode = AutoScaleMode.None;
    }

    private static void VerifySizes(Form form, Control panel, IReadOnlyList<Button> buttons, Control status,
        float scale, string name, List<string> checks)
    {
        var defaultSize = form.ClientSize;
        form.Size = form.MinimumSize;
        var minimumSize = form.ClientSize;
        form.ClientSize = defaultSize;
        if (scale != 1f) form.Scale(new SizeF(scale, scale));
        foreach (var size in new[]
        {
            (Name: "default", Client: Scaled(defaultSize, scale)),
            (Name: "minimum", Client: Scaled(minimumSize, scale)),
            (Name: "large", Client: Scaled(new Size(1920, 1080), scale))
        })
        {
            form.ClientSize = size.Client;
            PerformLayouts(form);
            Application.DoEvents();
            PerformLayouts(form);
            var context = $"{name} {size.Name} at {scale:0.##} layout scale";
            Require(panel.Visible && panel.Height > 0, context + ": the action panel is visible.");
            foreach (var button in buttons)
            {
                Require(button.Visible && button.Width > 0 && button.Height >= 28,
                    context + ": " + button.Text + " has visible bounds.");
                Require(panel.ClientRectangle.Contains(button.Bounds) && form.ClientRectangle.Contains(BoundsInForm(form, button)),
                    context + ": " + button.Text + " remains inside its action row.");
            }
            for (var index = 0; index < buttons.Count; index++)
                for (var next = index + 1; next < buttons.Count; next++)
                    Require(!buttons[index].Bounds.IntersectsWith(buttons[next].Bounds), context + ": actions do not overlap.");
            Require(BoundsInForm(form, panel).Top >= BoundsInForm(form, status).Bottom,
                context + ": action row stays below status, including long errors.");
            checks.Add(context + ": action geometry and long error layout passed.");
        }
    }

    private static Size Scaled(Size size, float scale) => new(
        (int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));

    private static void PerformLayouts(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) PerformLayouts(child);
    }

    private static Rectangle BoundsInForm(Form form, Control control)
    {
        var parent = control.Parent ?? throw new InvalidOperationException("A verified control has no parent.");
        return new Rectangle(form.PointToClient(parent.PointToScreen(control.Location)), control.Size);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("LAN dialog verification failed: " + message);
    }
}

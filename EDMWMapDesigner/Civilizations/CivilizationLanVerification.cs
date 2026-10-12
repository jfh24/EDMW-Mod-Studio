using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EDMWMapDesigner.Civilizations;

/// <summary>
/// Bounded, real loopback transfers against a copied game-data fixture. This
/// verifier never starts a process or installs against the source game path.
/// </summary>
public static class CivilizationLanVerification
{
    private const string ObjectFile = @"Data\db\dbobjects.dat";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly string[] Dependencies =
    [
        "Empires_DMW.exe", ObjectFile, @"Data\db\dbtechtree.dat", @"Data\db\dbphoenixcivtable.dat",
        @"Data\db\dbgraphics.dat", @"Data\db\dbbuttons.dat", @"Data\db\dbweapontohit.dat",
        @"Data\db\dbfamily.dat", @"Data\db\dbeffects.dat"
    ];

    public static async Task<IReadOnlyList<string>> Run(string gamePath, string outputDirectory)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (output.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LAN verification must run outside the live game folder.");
        Directory.CreateDirectory(output);
        var originals = Dependencies.ToDictionary(relative => relative,
            relative => HashFile(Path.Combine(source, relative)), StringComparer.OrdinalIgnoreCase);
        var fixture = NewFixture(source, output);
        var checks = new List<string>();
        try
        {
            var originalObjects = File.ReadAllBytes(Path.Combine(fixture, ObjectFile));
            var health = BinaryPrimitives.ReadUInt32LittleEndian(originalObjects.AsSpan(4 + 833 * 1936 + 120, 4));
            var project = CivilizationEditProject.CreateDefault();
            project.Name = "LAN acceptance fixture";
            project.Civilizations.Single(civ => civ.SourceRowIndex == 10).Entities.Add(new()
            {
                Name = "Citizen (English)", Category = CivilizationCategory.Units, SourceObjectId = 833,
                Properties = new(StringComparer.Ordinal)
                {
                    ["unit.health"] = checked(health + 17).ToString(CultureInfo.InvariantCulture)
                }
            });
            var beforeExport = Snapshot(fixture);
            var package = CivilizationInstallerPackage.Create(project, fixture);
            RequireSame(beforeExport, Snapshot(fixture), "Preparing a LAN export changed the fixture.");

            VerifyReviewMetadata(fixture, package, originalObjects, checks);
            await VerifyDecisions(fixture, package, originalObjects, checks).ConfigureAwait(false);
            await VerifyMalformedTransfers(fixture, package, checks).ConfigureAwait(false);
            await VerifyPendingLimit(fixture, package, checks).ConfigureAwait(false);
            await VerifyCancellationAndLifecycle(fixture, package, checks).ConfigureAwait(false);
            VerifyLocalSender(fixture, project, originalObjects, checks);

            Require(CivilizationPackageManager.ReadInstalled(fixture).Count == 0,
                "LAN verification left an installed package in the fixture.");
            Require(Dependencies.All(relative => HashFile(Path.Combine(fixture, relative)) == originals[relative]),
                "A LAN installation round trip left game fixture bytes changed.");
            checks.Add("Accepted LAN edits use the same tracked package inventory and uninstall restores every original game-file hash.");
            File.WriteAllLines(Path.Combine(output, "civilization-lan-verification.txt"), checks.Append("Fixture: " + fixture));
            return checks.AsReadOnly();
        }
        finally
        {
            foreach (var original in originals)
                Require(HashFile(Path.Combine(source, original.Key)) == original.Value,
                    "LAN verification changed a source game file: " + original.Key);
        }
    }

    private static void VerifyLocalSender(string fixture, CivilizationEditProject project, byte[] originalObjects, List<string> checks)
    {
        var before = Snapshot(fixture);
        var outgoing = CivilizationLanLocalExport.Prepare(project, fixture);
        Require(!CivilizationLanLocalExport.IsInstalled(outgoing, fixture), "An absent export was acknowledged as installed.");
        RequireSame(before, Snapshot(fixture), "Preparing the sender's export wrote to its game fixture.");
        var encoded = CivilizationInstallerPackageCodec.Encode(outgoing);
        var first = CivilizationLanLocalExport.Install(outgoing, fixture);
        Require(first.Status == CivilizationLanStatus.Installed, "The LAN sender's own export did not install: " + first.Message);
        var installed = Snapshot(fixture);
        var restartedProject = CivilizationEditProject.Deserialize(project.Serialize());
        restartedProject.GamePath = "A different remembered sender folder is ignored in the portable document";
        var reused = CivilizationLanLocalExport.Prepare(restartedProject, fixture);
        Require(reused.PackageId == outgoing.PackageId && CivilizationInstallerPackageCodec.Encode(reused).AsSpan().SequenceEqual(encoded),
            "A reloaded identical sender project did not reuse its persisted package identity, baselines, and encoded bytes.");
        RequireSame(installed, Snapshot(fixture), "Reusing an installed sender export wrote to the game or tracking files.");
        var second = CivilizationLanLocalExport.Install(reused, fixture);
        Require(second.Status == CivilizationLanStatus.Installed && CivilizationPackageManager.ReadInstalled(fixture).Count == 1,
            "The idempotent sender export created another installation or did not report its verified installed state.");
        RequireSame(installed, Snapshot(fixture), "An idempotent sender install changed existing bytes or tracking data.");
        Require(CivilizationLanLocalExport.IsInstalled(reused, fixture), "A verified repeated remote export was not acknowledged as already installed.");
        RequireSame(installed, Snapshot(fixture), "Read-only acknowledgement of a prior export changed game files or tracking data.");

        var differing = CivilizationInstallerPackageCodec.Decode(encoded);
        differing.Files.Single(file => file.RelativePath == ObjectFile).Patches.First().Replacement[0] ^= 1;
        var wrongContentsRejected = false;
        try { _ = CivilizationLanLocalExport.IsInstalled(differing, fixture); }
        catch (InvalidDataException) { wrongContentsRejected = true; }
        Require(wrongContentsRejected, "A different packet with the same installed identity was falsely acknowledged.");
        RequireSame(installed, Snapshot(fixture), "Rejecting different contents under an installed identity wrote to the fixture.");

        var changedProject = restartedProject.Clone();
        var entity = changedProject.Civilizations.Single(civ => civ.SourceRowIndex == 10).Entities.Single();
        entity.Properties["unit.health"] = (uint.Parse(entity.Properties["unit.health"], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
        var changed = CivilizationLanLocalExport.Prepare(changedProject, fixture);
        var conflict = CivilizationLanLocalExport.Install(changed, fixture);
        Require(conflict.Status == CivilizationLanStatus.Failed, "A changed overlapping sender project replaced a package without removal.");
        RequireSame(installed, Snapshot(fixture), "A changed overlapping sender export silently removed or changed the installed package.");

        var target = Path.Combine(fixture, ObjectFile);
        var installedBytes = File.ReadAllBytes(target);
        var patch = outgoing.Files.Single(file => file.RelativePath == ObjectFile).Patches.First();
        foreach (var offset in new[] { patch.Offset, 4 + 833 * 1936 + CivilizationObjectBindings.RecordIdentityOffset })
        {
            var tampered = installedBytes.ToArray(); tampered[offset] ^= 1; File.WriteAllBytes(target, tampered);
            var tamperedSnapshot = Snapshot(fixture);
            try
            {
                var result = CivilizationLanLocalExport.Install(outgoing, fixture);
                Require(result.Status == CivilizationLanStatus.Failed, "A tampered installed field or structural byte falsely reported Installed.");
                var failed = false;
                try { _ = CivilizationLanLocalExport.Prepare(restartedProject, fixture); }
                catch (InvalidDataException) { failed = true; }
                Require(failed, "A sender re-export trusted tracking metadata after its game bytes changed externally.");
                var acknowledgmentRejected = false;
                try { _ = CivilizationLanLocalExport.IsInstalled(outgoing, fixture); }
                catch (InvalidDataException) { acknowledgmentRejected = true; }
                Require(acknowledgmentRejected, "A read-only recipient acknowledgement trusted an externally changed prior installation.");
                RequireSame(tamperedSnapshot, Snapshot(fixture), "Rejecting an externally changed sender package wrote or repaired files without authorization.");
            }
            finally { File.WriteAllBytes(target, installedBytes); }
        }
        var removed = CivilizationPackageManager.Uninstall(fixture, outgoing.PackageId);
        Require(removed.Success && File.ReadAllBytes(target).AsSpan().SequenceEqual(originalObjects),
            "Removing the sender's reused package did not restore its original game bytes.");
        VerifyCanonicalAcknowledgement(fixture, outgoing, originalObjects, checks);
        checks.Add("Export installs the sender once; an unchanged project after reload reuses its persisted ID, original baselines, and exact package bytes; repeated installation and recipient acknowledgement verify current fields/assets/backups without writes, differing packet contents and overlapping edits conflict, and external field/structure changes never falsely report Installed.");
    }

    private static void VerifyCanonicalAcknowledgement(string fixture, CivilizationInstallerPackage original, byte[] originalObjects, List<string> checks)
    {
        var incoming = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(original));
        var authored = CivilizationEditProject.Deserialize(incoming.ProjectJson);
        var civilization = authored.Civilizations.Single(civ => civ.SourceRowIndex == 10);
        civilization.Name = "Authored England label";
        civilization.Entities.Single().Name = "Authored citizen label";
        incoming.ProjectJson = authored.Serialize();
        var review = CivilizationLanPackageReview.Prepare(incoming, fixture);
        Require(review.Package is not null && review.Issues.Count == 0 && review.Package.ProjectJson != incoming.ProjectJson,
            "The recipient acknowledgement fixture did not rebuild authored labels locally.");
        var accepted = CivilizationPackageManager.Install(review.Package!, fixture);
        Require(accepted.Success, "The locally rebuilt recipient fixture failed to install: " + accepted.Message);
        var installed = Snapshot(fixture);
        Require(CivilizationLanLocalExport.IsInstalled(incoming, fixture), "The recipient rejected a repeated export whose exact effects and authored intent were already installed with locally rebuilt labels.");
        RequireSame(installed, Snapshot(fixture), "Acknowledging a recipient's canonical prior installation wrote files or tracking data.");

        var forged = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(incoming));
        forged.Files.Single(file => file.RelativePath == ObjectFile).Patches.First().Replacement[0] ^= 1;
        var rejected = false;
        try { _ = CivilizationLanLocalExport.IsInstalled(forged, fixture); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Recipient acknowledgement ignored changed installation bytes under a prior package identity.");
        var differentIntent = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(incoming));
        var changedProject = CivilizationEditProject.Deserialize(differentIntent.ProjectJson);
        var entity = changedProject.Civilizations.Single(civ => civ.SourceRowIndex == 10).Entities.Single();
        entity.Properties["unit.health"] = (uint.Parse(entity.Properties["unit.health"], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
        differentIntent.ProjectJson = changedProject.Serialize();
        rejected = false;
        try { _ = CivilizationLanLocalExport.IsInstalled(differentIntent, fixture); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Recipient acknowledgement ignored changed authored properties under a prior identity.");
        RequireSame(installed, Snapshot(fixture), "Rejecting a forged repeated offer changed the recipient fixture.");

        var target = Path.Combine(fixture, ObjectFile);
        var current = File.ReadAllBytes(target);
        var patch = incoming.Files.Single(file => file.RelativePath == ObjectFile).Patches.First();
        var tampered = current.ToArray(); tampered[patch.Offset] ^= 1; File.WriteAllBytes(target, tampered);
        var tamperedSnapshot = Snapshot(fixture);
        try
        {
            rejected = false;
            try { _ = CivilizationLanLocalExport.IsInstalled(incoming, fixture); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "A recipient falsely acknowledged an installed field that changed externally.");
            RequireSame(tamperedSnapshot, Snapshot(fixture), "A repeated recipient offer repaired changed files without approval.");
        }
        finally { File.WriteAllBytes(target, current); }
        var removed = CivilizationPackageManager.Uninstall(fixture, incoming.PackageId);
        Require(removed.Success && File.ReadAllBytes(target).AsSpan().SequenceEqual(originalObjects), "Canonical recipient acknowledgement lost the original uninstall baselines.");
        checks.Add("Recipients acknowledge unchanged repeated exports even after rebuilding civilization/entity labels locally; exact file effects, authored properties, source identities, dependencies, and protected originals remain verified, with forged effects or changed active fields rejected without writes.");
    }

    private static async Task VerifyDecisions(string fixture, CivilizationInstallerPackage package,
        byte[] originalObjects, List<string> checks)
    {
        var received = NewCompletion<CivilizationLanOffer>();
        var decision = NewCompletion<CivilizationLanDecision>();
        var callbackCount = 0;
        using var receiver = NewReceiver(async (offer, token) =>
        {
            Interlocked.Increment(ref callbackCount);
            received.TrySetResult(offer);
            return await decision.Task.WaitAsync(token).ConfigureAwait(false);
        });
        receiver.Start();
        Require(receiver.IsListening && receiver.Port > 0 && receiver.DiscoveryPort > 0,
            "LAN receive listeners did not bind their ephemeral test ports.");
        using var sender = new CivilizationLanTransfer((_, _) => Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied)),
            "Verification sender", port: 0, discoveryPort: receiver.DiscoveryPort, bindAddress: IPAddress.Loopback);
        var peers = await sender.DiscoverAsync(TimeSpan.FromMilliseconds(500)).WaitAsync(Timeout).ConfigureAwait(false);
        Require(peers.Any(peer => peer.Port == receiver.Port && IPAddress.Parse(peer.Address).Equals(IPAddress.Loopback)),
            "UDP discovery did not find the independent loopback receiver.");
        var selfDiscovery = await receiver.DiscoverAsync(TimeSpan.FromMilliseconds(300)).WaitAsync(Timeout).ConfigureAwait(false);
        Require(!selfDiscovery.Any(found => found.Port == receiver.Port && found.Address == "127.0.0.1"),
            "Discovery included the sending Designer as its own recipient.");
        checks.Add("UDP discovery finds a listening Designer without a pre-established connection.");

        var before = Snapshot(fixture);
        var peer = new CivilizationLanPeer("Unverified peer display name", "127.0.0.1", receiver.Port);
        var progress = new System.Collections.Concurrent.ConcurrentQueue<CivilizationLanStatus>();
        var sending = sender.SendAsync(package, peer, update => progress.Enqueue(update.Status));
        var offered = await received.Task.WaitAsync(Timeout).ConfigureAwait(false);
        Require(IPAddress.Parse(offered.SenderAddress).Equals(IPAddress.Loopback),
            "An offer did not retain its real remote IP address.");
        Require(offered.Package.PackageId == package.PackageId && offered.Package.ProjectJson == package.ProjectJson &&
            offered.Package.Changes.SequenceEqual(package.Changes), "The offer lost its reviewable package or changes.");
        var reviewed = CivilizationLanPackageReview.Prepare(offered.Package, fixture);
        Require(reviewed.Package is not null && reviewed.Issues.Count == 0,
            "A transferred package failed its read-only local project and compatibility review.");
        Require(!sending.IsCompleted && callbackCount == 1,
            "Sending completed before the recipient made a decision.");
        RequireSame(before, Snapshot(fixture), "Receiving or reviewing an undecided package changed game files or tracking data.");
        Require(CivilizationPackageManager.ReadInstalled(fixture).Count == 0,
            "An undecided package became installed.");
        decision.TrySetResult(new(CivilizationLanStatus.Denied));
        var denied = await sending.WaitAsync(Timeout).ConfigureAwait(false);
        Require(denied.Status == CivilizationLanStatus.Denied, "The sender did not receive the recipient's denial.");
        Require(progress.SequenceEqual(new[] { CivilizationLanStatus.Submitted, CivilizationLanStatus.Awaiting, CivilizationLanStatus.Denied }),
            "The send progress omitted or reordered submission, recipient review and denial.");
        RequireSame(before, Snapshot(fixture), "Denying a LAN edit changed a file or created package tracking data.");
        receiver.Stop();
        checks.Add("A verified offer waits for the recipient; read-only review and Deny leave all fixture files and directories untouched.");

        var acceptedOffer = NewCompletion<CivilizationLanOffer>();
        var approve = NewCompletion<bool>();
        using var accepting = NewReceiver(async (offer, token) =>
        {
            var snapshot = CivilizationLanPackageReview.Prepare(offer.Package, fixture);
            if (snapshot.Package is null || snapshot.Issues.Count > 0)
                return new(CivilizationLanStatus.Failed, string.Join(Environment.NewLine, snapshot.Issues));
            acceptedOffer.TrySetResult(offer);
            await approve.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var installed = CivilizationPackageManager.Install(snapshot.Package, fixture);
            return new(installed.Success ? CivilizationLanStatus.Installed : CivilizationLanStatus.Failed, installed.Message);
        });
        accepting.Start();
        var acceptingSend = sender.SendAsync(package, new("Fixture recipient", "127.0.0.1", accepting.Port));
        await acceptedOffer.Task.WaitAsync(Timeout).ConfigureAwait(false);
        RequireSame(before, Snapshot(fixture), "A received package installed before explicit fixture approval.");
        approve.TrySetResult(true); // This is the verifier's explicit recipient decision, never an automatic production approval.
        var installedResult = await acceptingSend.WaitAsync(Timeout).ConfigureAwait(false);
        Require(installedResult.Status == CivilizationLanStatus.Installed,
            "The sender did not receive the approved installation result: " + installedResult.Message);
        var expected = originalObjects.ToArray();
        foreach (var patch in package.Files.Single(file => file.RelativePath == ObjectFile).Patches)
            patch.Replacement.CopyTo(expected, patch.Offset);
        Require(File.ReadAllBytes(Path.Combine(fixture, ObjectFile)).AsSpan().SequenceEqual(expected),
            "Accept did not install exactly the reviewed semantic fields.");
        var inventory = CivilizationPackageManager.ReadInstalled(fixture);
        Require(inventory.Count == 1 && inventory[0].PackageId == package.PackageId &&
            inventory[0].ProjectName == package.ProjectName && inventory[0].Changes.Count == package.Changes.Count &&
            inventory[0].Changes.Select(change => (change.Key, change.Before, change.After, change.Scope))
                .SequenceEqual(package.Changes.Select(change => (change.Key, change.Before, change.After, change.Scope))),
            "The accepted package did not enter the Home panel's tracked package inventory.");
        var removed = CivilizationPackageManager.Uninstall(fixture, package.PackageId);
        Require(removed.Success, "The accepted LAN fixture could not be uninstalled: " + removed.Message);
        Require(File.ReadAllBytes(Path.Combine(fixture, ObjectFile)).AsSpan().SequenceEqual(originalObjects),
            "Uninstall did not restore the original object table after a LAN transfer.");
        checks.Add("Explicit recipient approval installs only the reviewed fields and returns Installed to the sender.");
    }

    private static void VerifyReviewMetadata(string fixture, CivilizationInstallerPackage package,
        byte[] originalObjects, List<string> checks)
    {
        var before = Snapshot(fixture);
        var misleading = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(package));
        misleading.Changes[0] = misleading.Changes[0] with
        {
            CivilizationName = "Untrusted civilization label", EntityName = "Untrusted entity label",
            Label = "Claims no gameplay change", Before = "A harmless label", After = "Another harmless label",
            Scope = "Claims local-only", TargetFile = "Claims no database edit"
        };
        var corrected = CivilizationLanPackageReview.Prepare(misleading, fixture);
        Require(corrected.Package is not null && corrected.Issues.Count == 0 &&
            corrected.Package.PackageId == package.PackageId &&
            corrected.Package.Changes.Single().Key == package.Changes.Single().Key &&
            corrected.Package.Changes.Single().Before == package.Changes.Single().Before &&
            corrected.Package.Changes.Single().After == package.Changes.Single().After &&
            corrected.Package.Changes.Single().Scope == package.Changes.Single().Scope &&
            corrected.Package.Changes.Single().Label != "Claims no gameplay change",
            "LAN review trusted a sender's change descriptions instead of rebuilding the real changes locally.");

        var extra = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(package));
        var offset = 4 + 833 * 1936 + 136;
        var identity = CivilizationInstallerPackage.IdentityGuard(ObjectFile, originalObjects, offset);
        var sight = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(originalObjects.AsSpan(offset, 4)));
        var replacement = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(replacement, BitConverter.SingleToInt32Bits(sight + 1));
        extra.Files.Single(file => file.RelativePath == ObjectFile).Patches.Add(new()
        {
            Offset = offset, Length = 4, SemanticTarget = $"{ObjectFile.ToLowerInvariant()}:{offset}:4",
            Expected = originalObjects.AsSpan(offset, 4).ToArray(), Replacement = replacement,
            IdentityOffset = identity.Offset, IdentityBytes = identity.Bytes
        });
        var blocked = CivilizationLanPackageReview.Prepare(extra, fixture);
        Require(blocked.Package is null && blocked.Issues.Count > 0,
            "An undisclosed extra field patch survived comparison with its embedded project.");
        var differingProject = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(package));
        var project = CivilizationEditProject.Deserialize(differingProject.ProjectJson);
        project.Civilizations.Single(civ => civ.SourceRowIndex == 10).Entities.Single().SourceObjectId = 851;
        differingProject.ProjectJson = project.Serialize();
        var wrongSource = CivilizationLanPackageReview.Prepare(differingProject, fixture);
        Require(wrongSource.Package is null && wrongSource.Issues.Count > 0,
            "A mismatched project source object survived recipient review.");
        RequireSame(before, Snapshot(fixture), "Preparing safe or rejected network reviews changed fixture files.");
        checks.Add("Recipient review reconstructs accurate field changes locally, ignores forged descriptions and rejects undisclosed ranges or mismatched project source records.");
    }

    private static async Task VerifyMalformedTransfers(string fixture, CivilizationInstallerPackage package, List<string> checks)
    {
        var calls = 0;
        using var receiver = NewReceiver((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied));
        });
        receiver.Start();
        var before = Snapshot(fixture);
        var payload = CivilizationInstallerPackageCodec.Encode(package);
        var magic = Header(payload);
        magic[0] ^= 1;
        await RequireRejected(receiver.Port, magic, null, "Wrong transfer marker").ConfigureAwait(false);
        var version = Header(payload);
        BinaryPrimitives.WriteInt32BigEndian(version.AsSpan(8, 4), 2);
        await RequireRejected(receiver.Port, version, null, "Unsupported protocol version").ConfigureAwait(false);
        foreach (var length in new[] { 0, -1, CivilizationLanTransfer.MaxPayloadBytes + 1 })
        {
            var header = Header(payload);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), length);
            await RequireRejected(receiver.Port, header, null, "Invalid declared payload length").ConfigureAwait(false);
        }
        var wrongHash = Header(payload);
        wrongHash[16] ^= 1;
        await RequireRejected(receiver.Port, wrongHash, payload, "Wrong transfer hash").ConfigureAwait(false);
        byte[] invalidArchive = "Not a civilization package"u8.ToArray();
        await RequireRejected(receiver.Port, Header(invalidArchive), invalidArchive, "Malformed archive").ConfigureAwait(false);
        var malicious = CivilizationInstallerPackageCodec.Decode(payload);
        malicious.Files[0].RelativePath = @"..\outside-package-target.dat";
        var maliciousPayload = EncodeUnchecked(malicious);
        await RequireRejected(receiver.Port, Header(maliciousPayload), maliciousPayload, "Unsafe target path").ConfigureAwait(false);
        await RequireRejected(receiver.Port, Header(payload), payload[..(payload.Length / 2)],
            "Truncated payload", halfClose: true).ConfigureAwait(false);
        Require(calls == 0, "Malformed network data reached the recipient decision callback.");
        RequireSame(before, Snapshot(fixture), "Rejecting malformed network data changed fixture files or directories.");
        checks.Add("Wrong markers, versions, zero/negative/oversized lengths, hashes, archives, unsafe paths and truncated bodies are rejected before prompting or writing.");

        using var sender = NewReceiver((_, _) => Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied)));
        var valid = await sender.SendAsync(package, new("Healthy receiver", "127.0.0.1", receiver.Port))
            .WaitAsync(Timeout).ConfigureAwait(false);
        Require(valid.Status == CivilizationLanStatus.Denied && calls == 1,
            "Malformed requests left the receiver unable to handle a later valid package.");
        checks.Add("A receiver remains usable after rejected requests and releases each failed transfer reservation.");
        Require(CivilizationLanTransfer.IsLanAddress(IPAddress.Loopback) &&
            !CivilizationLanTransfer.IsLanAddress(IPAddress.IPv6Loopback) &&
            !CivilizationLanTransfer.IsLanAddress(IPAddress.Parse("8.8.8.8")),
            "The LAN address filter admitted a public or unsupported IPv6 address.");
        foreach (var peer in new[] { new CivilizationLanPeer("Public", "8.8.8.8", receiver.Port),
            new CivilizationLanPeer("Invalid", "127.0.0.1", 0), new CivilizationLanPeer("Invalid", "127.0.0.1", 65536) })
            Require((await sender.SendAsync(package, peer).WaitAsync(Timeout).ConfigureAwait(false)).Status == CivilizationLanStatus.Failed,
                "An invalid or non-LAN recipient was admitted.");
        checks.Add("Sending rejects public IPv4, unsupported IPv6 and invalid recipient ports before opening a transfer.");
    }

    private static async Task VerifyPendingLimit(string fixture, CivilizationInstallerPackage package, List<string> checks)
    {
        var firstEntered = NewCompletion<bool>();
        var secondEntered = NewCompletion<bool>();
        var decision = NewCompletion<CivilizationLanDecision>();
        var count = 0;
        using var receiver = NewReceiver(async (_, token) =>
        {
            var current = Interlocked.Increment(ref count);
            if (current == 1) firstEntered.TrySetResult(true);
            if (current == 2) secondEntered.TrySetResult(true);
            return await decision.Task.WaitAsync(token).ConfigureAwait(false);
        });
        using var sender = NewReceiver((_, _) => Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied)));
        receiver.Start();
        var before = Snapshot(fixture);
        var peer = new CivilizationLanPeer("Recipient", "127.0.0.1", receiver.Port);
        var first = sender.SendAsync(package, peer);
        await firstEntered.Task.WaitAsync(Timeout).ConfigureAwait(false);
        var second = sender.SendAsync(package, peer);
        await secondEntered.Task.WaitAsync(Timeout).ConfigureAwait(false);
        var excess = await sender.SendAsync(package, peer).WaitAsync(Timeout).ConfigureAwait(false);
        Require(excess.Status == CivilizationLanStatus.Failed && count == 2,
            "The bounded recipient review queue admitted a third pending offer.");
        RequireSame(before, Snapshot(fixture), "Pending or busy offers changed fixture files or directories.");
        decision.TrySetResult(new(CivilizationLanStatus.Denied));
        var completed = await Task.WhenAll(first, second).WaitAsync(Timeout).ConfigureAwait(false);
        Require(completed.All(result => result.Status == CivilizationLanStatus.Denied),
            "A bounded pending offer lost its final recipient decision.");
        RequireSame(before, Snapshot(fixture), "Denying the pending queue changed fixture files or directories.");
        checks.Add("Only two offers may await recipient decisions; excess requests fail without creating another prompt or writing files.");
    }

    private static async Task VerifyCancellationAndLifecycle(string fixture, CivilizationInstallerPackage package, List<string> checks)
    {
        var entered = NewCompletion<bool>();
        var withdrawn = NewCompletion<bool>();
        using var receiver = NewReceiver(async (_, token) =>
        {
            entered.TrySetResult(true);
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return new(CivilizationLanStatus.Denied);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                withdrawn.TrySetResult(true);
                throw;
            }
        });
        using var sender = NewReceiver((_, _) => Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied)));
        receiver.Start();
        var before = Snapshot(fixture);
        using var cancel = new CancellationTokenSource();
        var pending = sender.SendAsync(package, new("Recipient", "127.0.0.1", receiver.Port), cancellationToken: cancel.Token);
        await entered.Task.WaitAsync(Timeout).ConfigureAwait(false);
        cancel.Cancel();
        await ObserveDisconnected(pending).ConfigureAwait(false);
        await withdrawn.Task.WaitAsync(Timeout).ConfigureAwait(false);
        RequireSame(before, Snapshot(fixture), "Withdrawing a pending offer changed fixture files or directories.");
        checks.Add("Sender cancellation cancels a pending recipient decision and leaves every fixture file untouched.");

        receiver.Stop();
        entered = NewCompletion<bool>();
        withdrawn = NewCompletion<bool>();
        receiver.Start();
        var senderStopping = sender.SendAsync(package, new("Recipient", "127.0.0.1", receiver.Port));
        await entered.Task.WaitAsync(Timeout).ConfigureAwait(false);
        sender.Stop();
        await ObserveDisconnected(senderStopping).ConfigureAwait(false);
        await withdrawn.Task.WaitAsync(Timeout).ConfigureAwait(false);
        RequireSame(before, Snapshot(fixture), "Stopping an outgoing sender changed fixture files.");
        checks.Add("Stopping the sender also withdraws its outgoing offer and cancels the recipient decision.");

        receiver.Stop();
        entered = NewCompletion<bool>();
        withdrawn = NewCompletion<bool>();
        receiver.Start();
        var tcpPort = receiver.Port;
        var udpPort = receiver.DiscoveryPort;
        var stopping = sender.SendAsync(package, new("Recipient", "127.0.0.1", tcpPort));
        await entered.Task.WaitAsync(Timeout).ConfigureAwait(false);
        receiver.Stop();
        await withdrawn.Task.WaitAsync(Timeout).ConfigureAwait(false);
        await ObserveDisconnected(stopping).ConfigureAwait(false);
        Require(!receiver.IsListening, "Stop retained the listening state.");
        RequirePortsReleased(tcpPort, udpPort);
        RequireSame(before, Snapshot(fixture), "Stopping during a pending decision changed fixture files.");
        for (var index = 0; index < 3; index++)
        {
            receiver.Start();
            Require(receiver.IsListening, "A stopped LAN receiver could not restart.");
            tcpPort = receiver.Port;
            udpPort = receiver.DiscoveryPort;
            receiver.Stop();
            RequirePortsReleased(tcpPort, udpPort);
        }
        receiver.Start();
        tcpPort = receiver.Port;
        udpPort = receiver.DiscoveryPort;
        receiver.Dispose();
        Require(!receiver.IsListening, "Dispose retained the listening state.");
        RequirePortsReleased(tcpPort, udpPort);
        checks.Add("Stop withdraws pending decisions; repeated starts, stops and disposal release both TCP and discovery ports.");
    }

    private static CivilizationLanTransfer NewReceiver(Func<CivilizationLanOffer, CancellationToken, Task<CivilizationLanDecision>> handler) =>
        new(handler, "LAN verification recipient", port: 0, discoveryPort: 0, bindAddress: IPAddress.Loopback);

    private static async Task RequireRejected(int port, byte[] header, byte[]? body, string scenario, bool halfClose = false)
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellation.Token).ConfigureAwait(false);
        using var stream = client.GetStream();
        await stream.WriteAsync(header, cancellation.Token).ConfigureAwait(false);
        var response = await ReadResponse(stream, cancellation.Token).ConfigureAwait(false);
        if (response.Status == CivilizationLanStatus.Submitted)
        {
            Require(body is not null, scenario + " was admitted before its invalid header was rejected.");
            await stream.WriteAsync(body!, cancellation.Token).ConfigureAwait(false);
            if (halfClose) client.Client.Shutdown(SocketShutdown.Send);
            response = await ReadResponse(stream, cancellation.Token).ConfigureAwait(false);
        }
        Require(response.Status == CivilizationLanStatus.Failed, scenario + " reached a non-rejection network status.");
    }

    private static async Task<CivilizationLanResult> ReadResponse(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        Require(length is > 0 and <= 4096, "A LAN response exceeded its bounded framing limit.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<CivilizationLanResult>(bytes, CivilizationInstallerPackageCodec.JsonOptions)
            ?? throw new InvalidDataException("A LAN verification response was empty.");
    }

    private static byte[] Header(byte[] payload)
    {
        var header = new byte[CivilizationLanTransfer.HeaderSize];
        Encoding.ASCII.GetBytes(CivilizationLanTransfer.ProtocolMagic).CopyTo(header, 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8, 4), CivilizationLanTransfer.ProtocolVersion);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), payload.Length);
        SHA256.HashData(payload).CopyTo(header, 16);
        return header;
    }

    private static byte[] EncodeUnchecked(CivilizationInstallerPackage package)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using var manifest = archive.CreateEntry("civilization-package.json").Open();
            JsonSerializer.Serialize(manifest, package, CivilizationInstallerPackageCodec.JsonOptions);
        }
        return output.ToArray();
    }

    private static async Task ObserveDisconnected(Task<CivilizationLanResult> sending)
    {
        try
        {
            var result = await sending.WaitAsync(Timeout).ConfigureAwait(false);
            Require(result.Status is CivilizationLanStatus.Failed or CivilizationLanStatus.Denied,
                "A withdrawn transfer reported an installed package.");
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
    }

    private static void RequirePortsReleased(int tcpPort, int udpPort)
    {
        var probe = new TcpListener(IPAddress.Loopback, tcpPort);
        try { probe.Start(); }
        finally { probe.Stop(); }
        using var discovery = new UdpClient(new IPEndPoint(IPAddress.Loopback, udpPort));
    }

    private static string NewFixture(string source, string output)
    {
        var fixture = Path.Combine(output, "lan-fixtures", Guid.NewGuid().ToString("N"));
        if (Directory.Exists(fixture)) throw new InvalidOperationException("A LAN fixture must be new.");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "VERIFICATION_ONLY.txt"), "Offline LAN fixture. Never launch this copied game executable.\n");
        foreach (var relative in Dependencies)
        {
            var destination = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(source, relative), destination);
            File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
        }
        return fixture;
    }

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static IReadOnlyDictionary<string, string> Snapshot(string root)
    {
        var snapshot = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            path => "file:" + Path.GetRelativePath(root, path), HashFile, StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            snapshot.Add("dir:" + Path.GetRelativePath(root, path), "");
        return snapshot;
    }
    private static void RequireSame(IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after, string message) =>
        Require(before.Count == after.Count && before.All(entry =>
            after.TryGetValue(entry.Key, out var value) && entry.Value == value), message);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

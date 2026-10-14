using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EDMWMapDesigner.Civilizations;
using EDMWMapDesigner.Generation;

namespace EDMWMapDesigner.UI;

/// <summary>Project-free sharing checks using isolated, non-playable game folders.</summary>
internal static class InstalledMapSharingVerification
{
    internal static IReadOnlyList<string> Verify(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        var fixture = Path.Combine(output, "installed-map-sharing", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(fixture, "source");
        var recipient = Path.Combine(fixture, "recipient");
        CreateGame(source);
        CreateGame(recipient);
        var exported = MapLanVerification.CreatePackage(fixture);
        var exportedFiles = ArchiveFiles(exported);
        foreach (var file in exported.Files)
        {
            var target = Target(source, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, exportedFiles[file.RelativePath]);
        }

        var installed = CustomMapInstallationCatalog.Discover(source).Single();
        // Preserve a legitimate installed RMV revision instead of regenerating its script.
        File.AppendAllText(installed.ScriptPath!, "\n// Installed map sharing retains this authored RMV revision.\n",
            new UTF8Encoding(false));
        var before = Snapshot(source);
        var shared = CustomMapInstallationCatalog.CreateSharePackage(installed);
        var sharedFiles = ArchiveFiles(MapLanPackageCodec.Decode(MapLanPackageCodec.Encode(shared)));
        Require(shared.MapId == exported.MapId && shared.MapName == installed.Name,
            "Installed map sharing changed the map identity or name.");
        Require(shared.PreviewBytes.AsSpan().SequenceEqual(exported.PreviewBytes),
            "The installed terrain did not reconstruct the exported map preview.");
        foreach (var file in shared.Files)
            Require(File.ReadAllBytes(Target(source, file.RelativePath)).AsSpan().SequenceEqual(sharedFiles[file.RelativePath]),
                "Installed map sharing changed " + file.RelativePath + ".");
        RequireSame(before, Snapshot(source), "Installed map sharing changed the source game folder.");
        Require(!Directory.EnumerateFiles(source, "*.edmwmap", SearchOption.AllDirectories).Any(),
            "The installed-map fixture unexpectedly requires a project file.");

        shared.Install(recipient);
        foreach (var file in shared.Files)
            Require(File.ReadAllBytes(Target(recipient, file.RelativePath)).AsSpan().SequenceEqual(sharedFiles[file.RelativePath]),
                "Receiving a project-free map changed " + file.RelativePath + ".");
        Require(!File.Exists(Path.Combine(recipient, "EDMW Installer", "map-preview.png")),
            "Receiving a project-free map installed review metadata into the game.");
        var checks = new List<string>
        {
            "Home map sharing preserves installed RMV, generator, terrain bytes, map name, and identity without a project; codec and recipient installation roundtrip successfully."
        };

        var hidden = CustomMapInstallationCatalog.CreateSharePackage(installed, hidePreview: true);
        Require(hidden.MapId == shared.MapId && !hidden.PreviewBytes.AsSpan().SequenceEqual(shared.PreviewBytes),
            "Hiding an installed map preview changed its identity or failed to redact the preview.");
        var hiddenFiles = ArchiveFiles(hidden);
        foreach (var file in shared.Files)
            Require(hiddenFiles[file.RelativePath].AsSpan().SequenceEqual(sharedFiles[file.RelativePath]),
                "Hiding an installed map preview changed installed game bytes.");
        RequireSame(before, Snapshot(source), "Preview redaction changed the source game folder.");
        checks.Add("Preview redaction preserves all installed map bytes and identity.");

        ExpectFailureWithoutWrites(source, () => CustomMapInstallationCatalog.CreateSharePackage(installed with { ScriptPath = null }),
            "An orphaned installation was shared.");
        ExpectFailureWithoutWrites(source, () => CustomMapInstallationCatalog.CreateSharePackage(installed with { MapId = "000000" }),
            "An invalid map identity was shared.");
        ExpectFailureWithoutWrites(source, () => CustomMapInstallationCatalog.CreateSharePackage(installed with
        {
            ScriptPath = Path.Combine(source, Path.GetFileName(installed.ScriptPath!))
        }), "A script outside the map scripts folder was shared.");
        ExpectFailureWithoutWrites(source, () => CustomMapInstallationCatalog.CreateSharePackage(installed with
        {
            PayloadPath = Path.Combine(source, Path.GetFileName(installed.PayloadPath))
        }), "A terrain payload outside its expected folder was shared.");
        ExpectFailureWithoutWrites(source, () => CustomMapInstallationCatalog.CreateSharePackage(installed with
        {
            GeneratorPath = Path.Combine(source, "unidentified.dll")
        }), "A generator outside its identity path was shared.");
        foreach (var path in new[] { installed.ScriptPath!, installed.PayloadPath, installed.GeneratorPath })
        {
            var original = File.ReadAllBytes(path);
            File.Delete(path);
            try
            {
                ExpectFailureWithoutWrites(source, () => CustomMapInstallationCatalog.CreateSharePackage(installed),
                    "An incomplete installed map was shared.");
            }
            finally { File.WriteAllBytes(path, original); }
        }
        checks.Add("Orphaned, incomplete, misidentified, and misplaced installed maps are rejected before sharing.");

        VerifyGeneratorNormalization(source, recipient, installed, sharedFiles);
        checks.Add("A nonmatching installed x86 generator is normalized to the current verified generator in the shared package; RMV, terrain, identity, and source files stay unchanged, and the receiver safely upgrades its matching prior generator.");
        VerifyChangedFile(source, installed.GeneratorPath, bytes => bytes[0] = (byte)'N', installed,
            "An invalid installed DLL was accepted for sharing.");
        VerifyChangedFile(source, installed.GeneratorPath, bytes =>
        {
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C, 4));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(peOffset + 4, 2), 0x8664);
        }, installed, "An installed generator for a different architecture was accepted for sharing.");
        VerifyChangedFile(source, installed.PayloadPath, bytes => bytes[6] = (byte)'6', installed,
            "An incompatible terrain payload version was shared.");
        VerifyChangedFile(source, installed.PayloadPath, bytes => bytes[8] ^= 1, installed,
            "A terrain payload with a different identity was shared.");
        VerifyChangedFile(source, installed.PayloadPath, bytes => bytes[AnchorPackage.HeaderSize] ^= 1, installed,
            "A terrain payload with invalid integrity was shared.");
        var scriptBytes = File.ReadAllBytes(installed.ScriptPath!);
        File.WriteAllText(installed.ScriptPath!, Encoding.UTF8.GetString(scriptBytes).Replace(
            AnchorPackage.GeneratorName(shared.MapId), "EDMW_000001", StringComparison.Ordinal), new UTF8Encoding(false));
        try
        {
            ExpectFailureWithoutWrites(source, () => CustomMapInstallationCatalog.CreateSharePackage(installed),
                "An RMV with a different generator identity was shared.");
        }
        finally { File.WriteAllBytes(installed.ScriptPath!, scriptBytes); }
        var generatorBytes = File.ReadAllBytes(installed.GeneratorPath);
        File.WriteAllBytes(installed.GeneratorPath, new byte[16 * 1024 * 1024 + 1]);
        try
        {
            ExpectFailureWithoutWrites(source, () => CustomMapInstallationCatalog.CreateSharePackage(installed),
                "An oversized installed map file was shared.");
        }
        finally { File.WriteAllBytes(installed.GeneratorPath, generatorBytes); }
        RequireSame(before, Snapshot(source), "Installed map verification changed source fixture contents.");
        checks.Add("Existing package validators reject invalid DLLs, non-x86 generators, incompatible or damaged terrain, mismatched RMVs, and oversized files; sharing never writes source game data.");
        Task.WhenAll(VerifyMapShareDiscoveryAsync(), VerifyCivilizationShareDiscoveryAsync()).GetAwaiter().GetResult();
        checks.Add("Map and civilization Home discovery exclude the current editor's receiver by instance identity while preserving another editor on the same computer, without sending offers.");
        File.WriteAllLines(Path.Combine(fixture, "verification.txt"), checks);
        return checks.AsReadOnly();
    }

    private static void VerifyGeneratorNormalization(string source, string recipient,
        CustomMapInstallation installed, Dictionary<string, byte[]> sharedFiles)
    {
        var generatorPath = sharedFiles.Keys.Single(path => path.EndsWith(".dll", StringComparison.Ordinal));
        var currentGenerator = File.ReadAllBytes(installed.GeneratorPath);
        var nonmatchingGenerator = (byte[])currentGenerator.Clone();
        nonmatchingGenerator[^1] ^= 1;
        EmbeddedGenerator.ValidateX86Dll(nonmatchingGenerator);
        var recipientGeneratorPath = Target(recipient, generatorPath);
        var priorRecipient = File.ReadAllBytes(recipientGeneratorPath);
        File.WriteAllBytes(installed.GeneratorPath, nonmatchingGenerator);
        File.WriteAllBytes(recipientGeneratorPath, nonmatchingGenerator);
        try
        {
            var beforeShare = Snapshot(source);
            var normalized = MapLanPackageCodec.Decode(MapLanPackageCodec.Encode(
                CustomMapInstallationCatalog.CreateSharePackage(installed)));
            var normalizedFiles = ArchiveFiles(normalized);
            Require(normalizedFiles[generatorPath].AsSpan().SequenceEqual(EmbeddedGenerator.Read()) &&
                    !normalizedFiles[generatorPath].AsSpan().SequenceEqual(nonmatchingGenerator),
                "Sharing a prior x86 generator distributed unverified installed native bytes.");
            foreach (var file in normalized.Files.Where(file => !file.RelativePath.EndsWith(".dll", StringComparison.Ordinal)))
                Require(normalizedFiles[file.RelativePath].AsSpan().SequenceEqual(sharedFiles[file.RelativePath]),
                    "Generator normalization changed the installed script or terrain bytes.");
            Require(normalized.MapId.ToString("X6") == installed.MapId && normalized.MapName == installed.Name,
                "Generator normalization changed the installed map identity or name.");
            normalized.Install(recipient);
            foreach (var file in normalized.Files)
                Require(File.ReadAllBytes(Target(recipient, file.RelativePath)).AsSpan().SequenceEqual(normalizedFiles[file.RelativePath]),
                    "The receiver did not install the normalized map package exactly.");
            RequireSame(beforeShare, Snapshot(source), "Generator normalization changed the source game files.");
        }
        finally
        {
            File.WriteAllBytes(installed.GeneratorPath, currentGenerator);
            File.WriteAllBytes(recipientGeneratorPath, priorRecipient);
        }
    }

    private static async Task VerifyMapShareDiscoveryAsync()
    {
        var offers = 0;
        Task<CivilizationLanDecision> Reject(MapLanOffer _, CancellationToken __)
        {
            Interlocked.Increment(ref offers);
            return Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied));
        }
        using var currentReceiver = new MapLanTransfer(Reject, "Current map editor",
            port: 0, discoveryPort: 0, bindAddress: IPAddress.Loopback);
        using var otherReceiver = new MapLanTransfer(Reject, "Other map editor",
            port: 0, discoveryPort: 0, bindAddress: IPAddress.Loopback);
        currentReceiver.Start();
        otherReceiver.Start();
        using var selfSender = new MapLanTransfer(Reject, "Home map sender",
            port: 0, discoveryPort: currentReceiver.DiscoveryPort, bindAddress: IPAddress.Loopback);
        using var otherSender = new MapLanTransfer(Reject, "Home map sender",
            port: 0, discoveryPort: otherReceiver.DiscoveryPort, bindAddress: IPAddress.Loopback);
        var baseline = await Task.WhenAll(
            selfSender.DiscoverAsync(TimeSpan.FromMilliseconds(350)),
            otherSender.DiscoverAsync(TimeSpan.FromMilliseconds(350))).ConfigureAwait(false);
        RequireLoopbackPeer(baseline[0], currentReceiver.Port, "Map discovery did not find the current editor before exclusion.");
        RequireLoopbackPeer(baseline[1], otherReceiver.Port, "Map discovery did not find the other editor on the same computer.");
        var excluded = await Task.WhenAll(
            selfSender.DiscoverForShareAsync(currentReceiver.InstanceId, CancellationToken.None),
            otherSender.DiscoverForShareAsync(currentReceiver.InstanceId, CancellationToken.None)).ConfigureAwait(false);
        Require(excluded[0].Count == 0, "Home map discovery included its current editor receiver.");
        RequireLoopbackPeer(excluded[1], otherReceiver.Port, "Home map discovery excluded another editor on the same computer.");
        Require(!selfSender.IsListening && !otherSender.IsListening && offers == 0,
            "Map discovery started a sender listener or submitted an offer.");
    }

    private static async Task VerifyCivilizationShareDiscoveryAsync()
    {
        var offers = 0;
        Task<CivilizationLanDecision> Reject(CivilizationLanOffer _, CancellationToken __)
        {
            Interlocked.Increment(ref offers);
            return Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied));
        }
        using var currentReceiver = new CivilizationLanTransfer(Reject, "Current civilization editor",
            port: 0, discoveryPort: 0, bindAddress: IPAddress.Loopback);
        using var otherReceiver = new CivilizationLanTransfer(Reject, "Other civilization editor",
            port: 0, discoveryPort: 0, bindAddress: IPAddress.Loopback);
        currentReceiver.Start();
        otherReceiver.Start();
        using var selfSender = new CivilizationLanTransfer(Reject, "Home civilization sender",
            port: 0, discoveryPort: currentReceiver.DiscoveryPort, bindAddress: IPAddress.Loopback);
        using var otherSender = new CivilizationLanTransfer(Reject, "Home civilization sender",
            port: 0, discoveryPort: otherReceiver.DiscoveryPort, bindAddress: IPAddress.Loopback);
        var baseline = await Task.WhenAll(
            selfSender.DiscoverAsync(TimeSpan.FromMilliseconds(350)),
            otherSender.DiscoverAsync(TimeSpan.FromMilliseconds(350))).ConfigureAwait(false);
        RequireLoopbackPeer(baseline[0], currentReceiver.Port, "Civilization discovery did not find the current editor before exclusion.");
        RequireLoopbackPeer(baseline[1], otherReceiver.Port, "Civilization discovery did not find the other editor on the same computer.");
        var excluded = await Task.WhenAll(
            selfSender.DiscoverForShareAsync(currentReceiver.InstanceId, CancellationToken.None),
            otherSender.DiscoverForShareAsync(currentReceiver.InstanceId, CancellationToken.None)).ConfigureAwait(false);
        Require(excluded[0].Count == 0, "Home civilization discovery included its current editor receiver.");
        RequireLoopbackPeer(excluded[1], otherReceiver.Port, "Home civilization discovery excluded another editor on the same computer.");
        Require(!selfSender.IsListening && !otherSender.IsListening && offers == 0,
            "Civilization discovery started a sender listener or submitted an offer.");
    }

    private static void RequireLoopbackPeer(IReadOnlyList<CivilizationLanPeer> peers, int port, string message) =>
        Require(peers.Count == 1 && peers[0].Address == IPAddress.Loopback.ToString() && peers[0].Port == port, message);

    private static void VerifyChangedFile(string source, string path, Action<byte[]> corrupt,
        CustomMapInstallation installed, string message)
    {
        var original = File.ReadAllBytes(path);
        var changed = (byte[])original.Clone();
        corrupt(changed);
        File.WriteAllBytes(path, changed);
        try { ExpectFailureWithoutWrites(source, () => CustomMapInstallationCatalog.CreateSharePackage(installed), message); }
        finally { File.WriteAllBytes(path, original); }
    }

    private static void CreateGame(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Data", "Random Map Scripts"));
        File.WriteAllBytes(Path.Combine(root, GameInstallationLocator.GameExecutableName), []);
    }

    private static Dictionary<string, byte[]> ArchiveFiles(MapLanPackage package)
    {
        using var stream = new MemoryStream(MapLanPackageCodec.Encode(package), writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var input = entry.Open();
            using var bytes = new MemoryStream();
            input.CopyTo(bytes);
            return bytes.ToArray();
        }, StringComparer.Ordinal);
    }

    private static string Target(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static Dictionary<string, string> Snapshot(string root)
    {
        var result = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            path => "file:" + Path.GetRelativePath(root, path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            result.Add("dir:" + Path.GetRelativePath(root, directory), "");
        return result;
    }

    private static void ExpectFailureWithoutWrites(string source, Action action, string message)
    {
        var before = Snapshot(source);
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
        {
            RequireSame(before, Snapshot(source), "A rejected installed map share changed game files.");
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void RequireSame(Dictionary<string, string> before, Dictionary<string, string> after, string message) =>
        Require(before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value), message);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

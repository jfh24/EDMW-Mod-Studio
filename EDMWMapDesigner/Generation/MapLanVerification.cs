using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EDMWMapDesigner.Civilizations;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Generation;

/// <summary>Real bounded loopback offers and transactions using fresh, non-playable recipient fixtures.</summary>
public static class MapLanVerification
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static MapLanPackage CreatePackage(string outputDirectory)
    {
        var map = MapProject.CreateDefault(64);
        map.Name = "LAN map fixture";
        for (var y = 8; y < 56; y++)
            for (var x = 8; x < 56; x++) map.SetCell(x, y, TerrainKind.Grassland);
        map.PreserveCoastline = true;
        map.SetCell(20, 20, TerrainKind.Forest);
        map.SetCell(21, 20, TerrainKind.Hills);
        map.SetCell(22, 20, TerrainKind.Mountain);
        map.SetCell(23, 20, TerrainKind.Mountain);
        map.ElevationMask[20 * map.GridSize + 21] = 25;
        map.ElevationMask[20 * map.GridSize + 22] = 30;
        map.CliffMask[20 * map.GridSize + 22] = 1;
        map.ElevationMask[20 * map.GridSize + 23] = MapProject.CliffElevation;
        map.RampMask[20 * map.GridSize + 21] = 1;
        map.ManualResources.AddRange([new() { Kind = ManualResourceKind.Gold, X = 25, Y = 25 },
            new() { Kind = ManualResourceKind.Deer, X = 26, Y = 26 }, new() { Kind = ManualResourceKind.Fish, X = 2, Y = 2 }]);
        map.ManualPlayerStarts.Add(new() { PlayerSlot = 1, X = 30, Y = 30 });
        return PackageFromMap(map, outputDirectory);
    }

    private static MapLanPackage PackageFromMap(MapProject map, string outputDirectory, bool hidePreview = false)
    {
        var output = Path.GetFullPath(outputDirectory);
        var staging = Path.Combine(output, "map-lan-package", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var generated = GeneratedMapPackage.WriteForSelfTest(map, Path.Combine(staging, map.Name + ".rmv"));
        // The offline renderer uses the legacy generator name. Give this synthetic fixture
        // the same per-map generator identity used by real stock-template exports.
        var script = File.ReadAllText(generated.ScriptPath).Replace("\"EDMWAnchored\"", $"\"{AnchorPackage.GeneratorName(AnchorPackage.MapId(map))}\"", StringComparison.Ordinal);
        File.WriteAllText(generated.ScriptPath, script, new UTF8Encoding(false));
        var archive = MultiplayerPackageExporter.Export(map, generated, Path.Combine(staging, "map.zip"), hidePreview);
        return MapLanPackage.FromArchive(File.ReadAllBytes(archive));
    }

    public static async Task<IReadOnlyList<string>> Run(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var fixture = CreateFixture(output);
        var package = CreatePackage(output);
        var checks = new List<string>();
        Require(MapLanPackageCodec.Encode(MapLanPackageCodec.Decode(MapLanPackageCodec.Encode(package))).AsSpan()
            .SequenceEqual(MapLanPackageCodec.Encode(package)), "The LAN codec changed the existing multiplayer archive.");
        Require(package.Files.Count == 3 && package.PreviewBytes.Length > 0, "The map review metadata is incomplete.");
        checks.Add("The LAN payload is the exact existing multiplayer archive, with three install files and its PNG preview.");
        VerifyBrandingCompatibility(fixture, package, checks);
        var minimum = MapProject.CreateDefault(MapProject.MinGridSize);
        minimum.Name = "Minimum payload fixture";
        for (var index = 0; index < minimum.Cells.Length; index++) minimum.Cells[index] = (int)TerrainCatalog.All[index % TerrainCatalog.All.Length];
        for (var index = 0; index < minimum.ComponentRoles.Length; index++) minimum.ComponentRoles[index] = (byte)(index % 5);
        Require(PackageFromMap(minimum, output).MapId == AnchorPackage.MapId(minimum), "A valid minimum-size mixed-terrain payload was rejected.");
        var maximum = MapProject.CreateDefault(MapProject.MaxGridSize);
        maximum.Name = "Maximum hidden-preview fixture";
        Require(PackageFromMap(maximum, output, hidePreview: true).MapId == AnchorPackage.MapId(maximum), "A valid maximum-size payload or redacted preview was rejected.");
        checks.Add("Payload validation accepts minimum and maximum grids, all terrain and anchor roles, automatic and authored elevations, coastlines, cliffs, ramps, resources, player starts, and redacted previews.");
        VerifyReadOnlyAndRollback(fixture, package, checks);
        await VerifyDecisions(fixture, package, checks).ConfigureAwait(false);
        await VerifyDiscovery(package, checks).ConfigureAwait(false);
        await VerifyMalformed(fixture, package, checks).ConfigureAwait(false);
        await VerifyCancellation(fixture, package, checks).ConfigureAwait(false);
        File.WriteAllLines(Path.Combine(output, "map-lan-verification.txt"), checks.Append("Fixture: " + fixture));
        return checks.AsReadOnly();
    }

    private static void VerifyBrandingCompatibility(string fixture, MapLanPackage package, List<string> checks)
    {
        var before = Snapshot(fixture);
        var brandedScriptFound = false;
        var legacyArchive = RewriteArchive(package, (name, bytes) =>
        {
            if (!name.EndsWith(".rmv", StringComparison.Ordinal)) return (name, bytes);
            var script = Encoding.UTF8.GetString(bytes);
            Require(script.Contains("// Generated by EDMW Mod Studio", StringComparison.Ordinal),
                "New map exports have no EDMW Mod Studio attribution.");
            brandedScriptFound = true;
            return (name, Encoding.UTF8.GetBytes(script.Replace("// Generated by EDMW Mod Studio",
                "// Generated by EDMW Map Designer", StringComparison.Ordinal)));
        });
        Require(brandedScriptFound, "The exported archive contains no RMV script.");
        var legacyPackage = MapLanPackage.FromArchive(legacyArchive);
        Require(legacyPackage.MapId == package.MapId, "A legacy script attribution changed the map identity.");
        legacyPackage.Install(fixture);
        _ = package.ValidateDestination(fixture);
        package.Install(fixture);
        RemoveMap(fixture, package);
        RequireSame(before, Snapshot(fixture), "The branding compatibility check left map files in the fixture.");
        checks.Add("New exports use EDMW Mod Studio attribution; legacy EDMW Map Designer exports still import and can be updated in place.");
    }

    private static void VerifyReadOnlyAndRollback(string fixture, MapLanPackage package, List<string> checks)
    {
        var before = Snapshot(fixture);
        _ = package.ValidateDestination(fixture);
        RequireSame(before, Snapshot(fixture), "Review validation wrote to the recipient folder.");
        var exposed = package.PreviewBytes; exposed[0] ^= 1;
        Require(package.PreviewBytes[0] != exposed[0], "The reviewed preview bytes were mutable.");
        var archiveCopy = MapLanPackageCodec.Encode(package); archiveCopy[0] ^= 1;
        Require(MapLanPackageCodec.Encode(package)[0] != archiveCopy[0], "The reviewed archive bytes were mutable.");
        ExpectFailure(() => package.InstallForVerification(fixture, index =>
        {
            if (index == 1) throw new IOException("Intentional verification failure after the first committed map file.");
        }), "A failed transaction unexpectedly succeeded.");
        RequireSame(before, Snapshot(fixture), "A failed fresh installation did not roll back files and created directories.");
        package.Install(fixture);
        var priorGenerator = package.Files.Single(file => file.RelativePath.EndsWith(".dll", StringComparison.Ordinal));
        var priorGeneratorPath = Target(fixture, priorGenerator.RelativePath);
        var previousGeneratorBytes = File.ReadAllBytes(priorGeneratorPath); previousGeneratorBytes[^1] ^= 1;
        File.WriteAllBytes(priorGeneratorPath, previousGeneratorBytes);
        var existingScript = package.Files.Single(file => file.RelativePath.EndsWith(".rmv", StringComparison.Ordinal));
        File.AppendAllText(Target(fixture, existingScript.RelativePath), "\n// Previous custom-map script revision retained by rollback.\n");
        var installed = Snapshot(fixture);
        ExpectFailure(() => package.InstallForVerification(fixture, index =>
        {
            if (index == 3) throw new IOException("Intentional verification failure while replacing existing custom files.");
        }), "A failed replacement transaction unexpectedly succeeded.");
        RequireSame(installed, Snapshot(fixture), "A failed custom-map replacement did not restore original bytes.");
        RemoveMap(fixture, package);
        RequireSame(before, Snapshot(fixture), "A test install left map files in the fresh fixture.");

        var script = package.Files.Single(file => file.RelativePath.EndsWith(".rmv", StringComparison.Ordinal));
        var stockTarget = Target(fixture, script.RelativePath);
        File.WriteAllText(stockTarget, "UseMapGenerator \"Default\"\n// Original game script\n");
        var stock = Snapshot(fixture);
        ExpectFailure(() => package.ValidateDestination(fixture), "An original stock map could be overwritten during review.");
        ExpectFailure(() => package.Install(fixture), "An original stock map could be overwritten during acceptance.");
        RequireSame(stock, Snapshot(fixture), "Rejecting a stock-map collision changed the fixture.");
        File.Delete(stockTarget);
        checks.Add("Recipient review is read-only; stock-script conflicts are blocked; failed fresh and replacement transactions restore every original byte and remove staged files and empty created directories.");
    }

    private static async Task VerifyDecisions(string fixture, MapLanPackage package, List<string> checks)
    {
        var offered = Completion<MapLanOffer>(); var decision = Completion<CivilizationLanDecision>();
        using var receiver = NewReceiver(async (offer, token) =>
        {
            offered.TrySetResult(offer);
            return await decision.Task.WaitAsync(token).ConfigureAwait(false);
        });
        using var sender = NewReceiver((_, _) => Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied)));
        receiver.Start();
        var peer = new CivilizationLanPeer("Map recipient", "127.0.0.1", receiver.Port);
        var before = Snapshot(fixture);
        var progress = new List<CivilizationLanStatus>();
        var sending = sender.SendAsync(package, peer, update => { lock (progress) progress.Add(update.Status); });
        var offer = await offered.Task.WaitAsync(Timeout).ConfigureAwait(false);
        Require(offer.Package.MapId == package.MapId && offer.Package.MapName == package.MapName && offer.SenderAddress == "127.0.0.1",
            "The receiver lost exported map metadata.");
        RequireSame(before, Snapshot(fixture), "An undecided LAN offer wrote map files.");
        decision.TrySetResult(new(CivilizationLanStatus.Denied, "Denied by recipient."));
        var denial = await sending.WaitAsync(Timeout).ConfigureAwait(false);
        Require(denial.Status == CivilizationLanStatus.Denied, "The sender did not receive the recipient denial.");
        RequireSame(before, Snapshot(fixture), "A denied LAN map wrote files.");

        offered = Completion<MapLanOffer>(); decision = Completion<CivilizationLanDecision>();
        sending = sender.SendAsync(package, peer, update => { lock (progress) progress.Add(update.Status); });
        offer = await offered.Task.WaitAsync(Timeout).ConfigureAwait(false);
        offer.Package.Install(fixture); // The explicit recipient decision authorizes the local transaction.
        decision.TrySetResult(new(CivilizationLanStatus.Installed, "Installed."));
        var accepted = await sending.WaitAsync(Timeout).ConfigureAwait(false);
        Require(accepted.Status == CivilizationLanStatus.Installed, "The sender did not receive the recipient acceptance.");
        foreach (var file in package.Files) Require(new FileInfo(Target(fixture, file.RelativePath)).Length == file.Length, "Acceptance missed an expected map file.");
        Require(!File.Exists(Path.Combine(fixture, "EDMW Installer", "map-preview.png")), "The review-only preview was installed into the game.");
        lock (progress) Require(progress.Contains(CivilizationLanStatus.Submitted) && progress.Contains(CivilizationLanStatus.Awaiting) &&
            progress.Contains(CivilizationLanStatus.Denied) && progress.Contains(CivilizationLanStatus.Installed), "The sender lost transfer or decision statuses.");
        RemoveMap(fixture, package);
        RequireSame(before, Snapshot(fixture), "The accepted test left unexpected fixture changes.");
        checks.Add("Standalone map offers remain pending without writes; denial leaves files untouched; explicit acceptance directly installs exactly the DLL, payload, and RMV and reports the final decision to the sender.");
    }

    private static async Task VerifyDiscovery(MapLanPackage package, List<string> checks)
    {
        using var receiver = NewReceiver((_, _) => Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied)));
        receiver.Start();
        using var sender = new MapLanTransfer((_, _) => Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied)),
            "Map sender", port: 0, discoveryPort: receiver.DiscoveryPort, bindAddress: IPAddress.Loopback);
        var peers = await sender.DiscoverAsync(TimeSpan.FromMilliseconds(350)).WaitAsync(Timeout).ConfigureAwait(false);
        Require(peers.Any(peer => peer.Address == "127.0.0.1" && peer.Port == receiver.Port), "Standalone map UDP discovery did not find its receiver.");
        var self = await receiver.DiscoverAsync(TimeSpan.FromMilliseconds(250)).WaitAsync(Timeout).ConfigureAwait(false);
        Require(self.Count == 0, "Map discovery included the sender's own listener.");
        using var civilization = new CivilizationLanTransfer((_, _) => Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied)),
            "Civilization sender", port: 0, discoveryPort: receiver.DiscoveryPort, bindAddress: IPAddress.Loopback);
        Require((await civilization.DiscoverAsync(TimeSpan.FromMilliseconds(250)).WaitAsync(Timeout).ConfigureAwait(false)).Count == 0,
            "Civilization discovery admitted the distinct map protocol.");
        var invalid = await sender.SendAsync(package, new("Public", "8.8.8.8", receiver.Port)).WaitAsync(Timeout).ConfigureAwait(false);
        Require(invalid.Status == CivilizationLanStatus.Failed, "A public IPv4 address was accepted as a LAN map recipient.");
        checks.Add("Map UDP discovery finds peers, excludes itself, uses a separate protocol from civilization exports, and rejects public recipients.");
    }

    private static async Task VerifyMalformed(string fixture, MapLanPackage package, List<string> checks)
    {
        var calls = 0;
        using var receiver = NewReceiver((_, _) =>
        {
            Interlocked.Increment(ref calls); return Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied));
        });
        receiver.Start(); var before = Snapshot(fixture); var payload = MapLanPackageCodec.Encode(package);
        var wrongMagic = Header(payload); wrongMagic[0] ^= 1;
        await Reject(receiver.Port, wrongMagic, null).ConfigureAwait(false);
        var wrongVersion = Header(payload); BinaryPrimitives.WriteInt32BigEndian(wrongVersion.AsSpan(8, 4), 2);
        await Reject(receiver.Port, wrongVersion, null).ConfigureAwait(false);
        foreach (var length in new[] { 0, -1, MapLanTransfer.MaxPayloadBytes + 1 })
        {
            var header = Header(payload); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), length);
            await Reject(receiver.Port, header, null).ConfigureAwait(false);
        }
        var wrongHash = Header(payload); wrongHash[16] ^= 1;
        await Reject(receiver.Port, wrongHash, payload).ConfigureAwait(false);
        byte[] invalidArchive = "Not a map archive"u8.ToArray();
        await Reject(receiver.Port, Header(invalidArchive), invalidArchive).ConfigureAwait(false);
        var unsafePath = RewriteArchive(package, (name, bytes) => name.EndsWith(".rmv", StringComparison.Ordinal) ? ("../outside.rmv", bytes) : (name, bytes));
        await Reject(receiver.Port, Header(unsafePath), unsafePath).ConfigureAwait(false);
        var executable = RewriteArchive(package, (name, bytes) => name.EndsWith(".dll", StringComparison.Ordinal) ? ("received.exe", bytes) : (name, bytes));
        await Reject(receiver.Port, Header(executable), executable).ConfigureAwait(false);
        var alteredGenerator = RewriteArchive(package, (name, bytes) =>
        {
            if (name.EndsWith(".dll", StringComparison.Ordinal)) bytes[^1] ^= 1; return (name, bytes);
        });
        await Reject(receiver.Port, Header(alteredGenerator), alteredGenerator).ConfigureAwait(false);
        var alteredTerrain = RewriteArchive(package, (name, bytes) =>
        {
            if (name.EndsWith(".edmwa", StringComparison.Ordinal)) bytes[32] ^= 1; return (name, bytes);
        });
        await Reject(receiver.Port, Header(alteredTerrain), alteredTerrain).ConfigureAwait(false);
        var wrongScript = RewriteArchive(package, (name, bytes) => name.EndsWith(".rmv", StringComparison.Ordinal)
            ? (name, Encoding.UTF8.GetBytes("// Generated by EDMW Map Designer\nUseMapGenerator \"EDMW_000001\"\n")) : (name, bytes));
        await Reject(receiver.Port, Header(wrongScript), wrongScript).ConfigureAwait(false);
        await Reject(receiver.Port, Header(payload), payload[..(payload.Length / 2)], truncate: true).ConfigureAwait(false);
        Require(calls == 0, "Malformed map data reached recipient approval.");
        RequireSame(before, Snapshot(fixture), "Malformed map requests changed the recipient folder.");
        using var sender = NewReceiver((_, _) => Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied)));
        Require((await sender.SendAsync(package, new("Recovered", "127.0.0.1", receiver.Port)).WaitAsync(Timeout).ConfigureAwait(false)).Status == CivilizationLanStatus.Denied && calls == 1,
            "Rejected map transfers left the receiver unusable.");
        checks.Add("Malformed headers, hashes, archives, unsafe paths, executables, modified DLLs, altered terrain, mismatched script identities, and truncated transfers fail before recipient approval or any write; a later valid offer still works.");
    }

    private static async Task VerifyCancellation(string fixture, MapLanPackage package, List<string> checks)
    {
        var entered = Completion<bool>(); var withdrawn = Completion<bool>();
        using var receiver = NewReceiver(async (_, token) =>
        {
            entered.TrySetResult(true);
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { withdrawn.TrySetResult(true); throw; }
            return new(CivilizationLanStatus.Denied);
        });
        using var sender = NewReceiver((_, _) => Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied)));
        receiver.Start(); var before = Snapshot(fixture);
        using var cancellation = new CancellationTokenSource();
        var pending = sender.SendAsync(package, new("Recipient", "127.0.0.1", receiver.Port), cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(Timeout).ConfigureAwait(false); cancellation.Cancel();
        await Withdrawn(pending).ConfigureAwait(false); await withdrawn.Task.WaitAsync(Timeout).ConfigureAwait(false);
        RequireSame(before, Snapshot(fixture), "Canceling a map offer wrote files.");
        receiver.Stop(); receiver.Start(); entered = Completion<bool>(); withdrawn = Completion<bool>();
        pending = sender.SendAsync(package, new("Recipient", "127.0.0.1", receiver.Port));
        await entered.Task.WaitAsync(Timeout).ConfigureAwait(false);
        var tcpPort = receiver.Port; var discoveryPort = receiver.DiscoveryPort; receiver.Stop();
        await Withdrawn(pending).ConfigureAwait(false); await withdrawn.Task.WaitAsync(Timeout).ConfigureAwait(false);
        RequireSame(before, Snapshot(fixture), "Stopping the map receiver wrote files.");
        var tcpProbe = new TcpListener(IPAddress.Loopback, tcpPort);
        try { tcpProbe.Start(); } finally { tcpProbe.Stop(); }
        using var udpProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, discoveryPort));
        receiver.Start(); Require(receiver.IsListening, "The map receiver could not restart after stopping.");
        receiver.Dispose(); Require(!receiver.IsListening, "Map receiver disposal retained a listening state.");
        checks.Add("Sender cancellation and listener shutdown withdraw recipient approval without writes; map listeners restart and release both TCP and UDP ports.");
    }

    private static MapLanTransfer NewReceiver(Func<MapLanOffer, CancellationToken, Task<CivilizationLanDecision>> handler) =>
        new(handler, "LAN map verification recipient", port: 0, discoveryPort: 0, bindAddress: IPAddress.Loopback);
    private static async Task Reject(int port, byte[] header, byte[]? body, bool truncate = false)
    {
        using var cancellation = new CancellationTokenSource(Timeout); using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellation.Token).ConfigureAwait(false);
        using var stream = client.GetStream(); await stream.WriteAsync(header, cancellation.Token).ConfigureAwait(false);
        var result = await ReadResponse(stream, cancellation.Token).ConfigureAwait(false);
        if (result.Status == CivilizationLanStatus.Submitted)
        {
            Require(body is not null, "A malformed map header was admitted.");
            await stream.WriteAsync(body!, cancellation.Token).ConfigureAwait(false);
            if (truncate) client.Client.Shutdown(SocketShutdown.Send);
            result = await ReadResponse(stream, cancellation.Token).ConfigureAwait(false);
        }
        Require(result.Status == CivilizationLanStatus.Failed, "A malformed map request was not rejected.");
    }
    private static async Task<CivilizationLanResult> ReadResponse(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix); Require(length is > 0 and <= 4096, "An invalid response frame was returned.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<CivilizationLanResult>(bytes, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new InvalidDataException("An empty map response was returned.");
    }
    private static byte[] Header(byte[] payload)
    {
        var bytes = new byte[MapLanTransfer.HeaderSize]; Encoding.ASCII.GetBytes(MapLanTransfer.ProtocolMagic).CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), MapLanTransfer.ProtocolVersion);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12, 4), payload.Length); SHA256.HashData(payload).CopyTo(bytes, 16); return bytes;
    }
    private static byte[] RewriteArchive(MapLanPackage package, Func<string, byte[], (string Name, byte[] Bytes)> transform)
    {
        using var original = new MemoryStream(MapLanPackageCodec.Encode(package)); using var input = new ZipArchive(original, ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in input.Entries)
            {
                using var source = entry.Open(); using var copy = new MemoryStream(); source.CopyTo(copy);
                var transformed = transform(entry.FullName, copy.ToArray());
                using var destination = archive.CreateEntry(transformed.Name).Open(); destination.Write(transformed.Bytes);
            }
        return output.ToArray();
    }
    private static async Task Withdrawn(Task<CivilizationLanResult> pending)
    {
        try { Require((await pending.WaitAsync(Timeout).ConfigureAwait(false)).Status == CivilizationLanStatus.Failed, "A withdrawn map offer reported installation."); }
        catch (OperationCanceledException) { }
    }
    private static string CreateFixture(string output)
    {
        var fixture = Path.Combine(output, "map-lan-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "Data", "Random Map Scripts"));
        File.WriteAllText(Path.Combine(fixture, "Empires_DMW.exe"), "Non-playable verification fixture. Never execute.\n");
        return fixture;
    }
    private static string Target(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static void RemoveMap(string fixture, MapLanPackage package)
    {
        foreach (var file in package.Files) File.Delete(Target(fixture, file.RelativePath));
        var anchors = Path.Combine(fixture, "Data", "Random Map Scripts", AnchorPackage.DataFolderName);
        if (Directory.Exists(anchors) && !Directory.EnumerateFileSystemEntries(anchors).Any()) Directory.Delete(anchors);
    }
    private static Dictionary<string, string> Snapshot(string root)
    {
        var result = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => "file:" + Path.GetRelativePath(root, path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) result.Add("dir:" + Path.GetRelativePath(root, directory), "");
        return result;
    }
    private static void RequireSame(Dictionary<string, string> before, Dictionary<string, string> after, string message) =>
        Require(before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value), message);
    private static void ExpectFailure(Action action, string message)
    {
        try { action(); } catch (Exception error) when (error is IOException or InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

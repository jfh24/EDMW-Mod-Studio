using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace EDMWMapDesigner.Civilizations;

/// <summary>Nonvisual sharing checks that write only into new isolated fixtures.</summary>
public static class CivilizationInstalledShareVerification
{
    private const string ObjectFile = "Data\\db\\dbobjects.dat";
    private static readonly string[] SourceFiles = ["Empires_DMW.exe", ObjectFile,
        "Data\\db\\dbphoenixcivtable.dat", "Data\\db\\dbtechtree.dat",
        "Data\\db\\dbgraphics.dat", "Data\\db\\dbbuttons.dat"];

    public static IReadOnlyList<string> Run(string gamePath, string outputDirectory)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (output.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installed-sharing verification must run outside the live game folder.");
        var work = Path.Combine(output, "installed-civ-share-fixtures", Guid.NewGuid().ToString("N"));
        var sender = Path.Combine(work, "sender");
        var recipient = Path.Combine(work, "recipient");
        var sourceHashes = SourceFiles.ToDictionary(relative => relative,
            relative => FileHash(Path.Combine(source, relative)), StringComparer.OrdinalIgnoreCase);
        CopyFixture(source, sender);
        CopyFixture(source, recipient);
        var checks = new List<string>();
        try
        {
            var originalObjects = File.ReadAllBytes(Path.Combine(sender, ObjectFile));
            var healthOffset = 4 + 833 * CivilizationObjectBindings.RecordSize + 120;
            var sightOffset = 4 + 833 * CivilizationObjectBindings.RecordSize + 136;
            var health = BinaryPrimitives.ReadUInt32LittleEndian(originalObjects.AsSpan(healthOffset, 4));
            var sight = BinaryPrimitives.ReadSingleLittleEndian(originalObjects.AsSpan(sightOffset, 4));
            var catalog = CivilizationGameCatalog.Load(sender);
            var a = CivilizationInstallerPackage.Create(ObjectProject(sender, catalog, "Selected health edit", 833,
                "unit.health", checked(health + 1).ToString(CultureInfo.InvariantCulture)), sender);
            var b = CivilizationInstallerPackage.Create(ObjectProject(sender, catalog, "Independent sight edit", 833,
                "unit.sight", (sight + .5f).ToString("R", CultureInfo.InvariantCulture)), sender);
            RequireSuccess(CivilizationPackageManager.Install(a, sender));
            RequireSuccess(CivilizationPackageManager.Install(b, sender));
            foreach (var package in new[] { a, b })
            {
                var shared = ReadUnchanged(sender, package.PackageId);
                Require(SamePackage(package, shared), "Sharing changed the selected package's identity, intent, baselines or payload.");
                Require(SamePackage(shared, ReadUnchanged(sender, package.PackageId)), "Repeated shares changed the installed identity.");
                var received = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(shared));
                var reviewed = CivilizationLanPackageReview.Prepare(received, recipient);
                Require(reviewed.Issues.Count == 0 && reviewed.Package is not null,
                    "The selected installed package failed recipient review: " + string.Join("; ", reviewed.Issues));
                RequireSuccess(CivilizationPackageManager.Install(reviewed.Package!, recipient));
                var actual = File.ReadAllBytes(Path.Combine(recipient, ObjectFile));
                var otherOffset = package.PackageId == a.PackageId ? sightOffset : healthOffset;
                Require(actual.AsSpan(otherOffset, 4).SequenceEqual(originalObjects.AsSpan(otherOffset, 4)),
                    "Sharing one selected item included the neighboring installed edit.");
                RequireSuccess(CivilizationPackageManager.Uninstall(recipient, package.PackageId));
                Require(File.ReadAllBytes(Path.Combine(recipient, ObjectFile)).AsSpan().SequenceEqual(originalObjects),
                    "The recipient round trip did not restore its original object table.");
            }
            checks.Add("Selected installed edits retain exact payloads and stable identities; each independently reviews, installs and restores on a clean recipient without including neighboring edits.");

            ExpectInvalid(() => ReadUnchanged(sender, Guid.NewGuid().ToString("N")), "A removed or missing item was shared.");
            ExpectInvalid(() => ReadUnchanged(sender, "../invalid"), "An invalid package identity was accepted.");
            ExpectInvalid(() => ReadUnchanged(sender, "legacy"), "An earlier installation without authored package metadata was silently reconstructed.");
            checks.Add("Missing items, invalid identities and legacy installations without a saved package fail explicitly.");

            var objectPath = Path.Combine(sender, ObjectFile);
            var installedObjects = File.ReadAllBytes(objectPath);
            var external = installedObjects.ToArray();
            external[healthOffset] ^= 1;
            File.WriteAllBytes(objectPath, external);
            ExpectInvalid(() => ReadUnchanged(sender, a.PackageId), "An externally changed installed field was shared.");
            File.WriteAllBytes(objectPath, installedObjects);

            var backupPath = Path.Combine(sender, ".edmw-civilization-packages", "backups", a.PackageId + ".json");
            var backup = JsonNode.Parse(File.ReadAllText(backupPath))!;
            var originalPath = Path.Combine(backup["directory"]!.GetValue<string>(),
                backup["files"]!.AsArray()[0]!["backupName"]!.GetValue<string>());
            var protectedOriginal = File.ReadAllBytes(originalPath);
            var damagedOriginal = protectedOriginal.ToArray();
            damagedOriginal[healthOffset] ^= 1;
            File.WriteAllBytes(originalPath, damagedOriginal);
            ExpectInvalid(() => ReadUnchanged(sender, a.PackageId), "A damaged original backup was accepted for sharing.");
            File.WriteAllBytes(originalPath, protectedOriginal);

            var manifestPath = Path.Combine(sender, ".edmw-civilization-packages", "installed", a.PackageId + ".json");
            var manifestBytes = File.ReadAllBytes(manifestPath);
            var manifest = JsonNode.Parse(manifestBytes)!;
            var changedFile = manifest["package"]!["files"]!.AsArray().Single(file =>
                file!["relativePath"]!.GetValue<string>() == ObjectFile);
            var expected = a.Files.Single(file => file.RelativePath == ObjectFile).Patches.Single().Expected.ToArray();
            expected[0] ^= 1;
            changedFile!["patches"]!.AsArray()[0]!["expected"] = Convert.ToBase64String(expected);
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            ExpectInvalid(() => ReadUnchanged(sender, a.PackageId), "An altered expected field baseline was shared despite an intact protected original.");
            File.WriteAllBytes(manifestPath, manifestBytes);

            var transactionPath = Path.Combine(Path.GetDirectoryName(originalPath)!, "transaction.json");
            var transactionBytes = File.ReadAllBytes(transactionPath);
            var transaction = JsonNode.Parse(transactionBytes)!;
            transaction["state"] = "Prepared";
            File.WriteAllText(transactionPath, transaction.ToJsonString());
            ExpectInvalid(() => ReadUnchanged(sender, a.PackageId), "An interrupted installation was shared.");
            File.WriteAllBytes(transactionPath, transactionBytes);
            checks.Add("Externally edited fields, damaged originals, altered expected baselines and interrupted transactions reject sharing without writing to the selected game or its protected inventory.");
            RequireSuccess(CivilizationPackageManager.Uninstall(sender, b.PackageId));
            RequireSuccess(CivilizationPackageManager.Uninstall(sender, a.PackageId));

            VerifyModels(source, sender, recipient, catalog, checks);
            var ages = CivilizationEditProject.CreateDefault();
            ages.Name = "Installed age progression";
            var england = ages.Civilizations.Single(civ => civ.Name == "England");
            var values = CivilizationCivilizationBindings.ReadValues(
                File.ReadAllBytes(Path.Combine(sender, "Data\\db\\dbphoenixcivtable.dat")), england.SourceRowIndex!.Value);
            england.Properties["age.finalAge"] = (int.Parse(values["age.finalAge"], CultureInfo.InvariantCulture) + 1)
                .ToString(CultureInfo.InvariantCulture);
            var agePackage = CivilizationInstallerPackage.Create(ages, sender);
            RequireSuccess(CivilizationPackageManager.Install(agePackage, sender));
            Require(SamePackage(agePackage, ReadUnchanged(sender, agePackage.PackageId)), "Sharing lost the installed progression fields.");
            Require(CivilizationLanPackageReview.Prepare(ReadUnchanged(sender, agePackage.PackageId), recipient).Issues.Count == 0,
                "The shared installed progression failed canonical recipient review.");
            RequireSuccess(CivilizationPackageManager.Uninstall(sender, agePackage.PackageId));
            checks.Add("Installed age progression retains its authored fields and original expected values without a project file.");
            File.WriteAllLines(Path.Combine(output, "civilization-installed-share-verification.txt"), checks.Append("Fixtures: " + work));
            return checks.AsReadOnly();
        }
        finally
        {
            foreach (var relative in SourceFiles)
                Require(FileHash(Path.Combine(source, relative)) == sourceHashes[relative],
                    "Installed-sharing verification changed a source game file: " + relative);
        }
    }

    private static void VerifyModels(string source, string sender, string recipient,
        CivilizationGameCatalog catalog, List<string> checks)
    {
        var assets = new CivilizationGameAssets(source);
        var target = assets.FindModelName(1094) ?? throw new InvalidDataException("The verified target model could not be resolved.");
        var replacementName = assets.FindModelName(1095) ?? throw new InvalidDataException("The verified replacement model could not be resolved.");
        var original = assets.ReadModel(target);
        var replacement = assets.ReadModel(replacementName);
        foreach (var fixture in new[] { sender, recipient })
        {
            // The graphics reader verifies adjacent row identities, so retain
            // the neighboring named model as well as the replacement target.
            foreach (var (name, bytes) in new[] { (target, original), (replacementName, replacement) })
            {
                var targetPath = Path.Combine(fixture, "Data", name);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.WriteAllBytes(targetPath, bytes);
            }
        }
        var asset = new CivilizationModelAsset { Name = "Installed share model", OriginalFileName = "share.cem",
            Format = "cem", MimeType = "application/octet-stream", FileLength = replacement.Length,
            Data = replacement.ToArray(), Sha256 = CivilizationInstallerPackage.Hash(replacement) };
        var project = ObjectProject(sender, catalog, "Installed model edit", 1078, "building.modelAsset", asset.Id);
        project.ModelAssets.Add(asset);
        var package = CivilizationInstallerPackage.Create(project, sender);
        RequireSuccess(CivilizationPackageManager.Install(package, sender));
        var shared = ReadUnchanged(sender, package.PackageId);
        Require(SamePackage(package, shared), "Sharing lost the installed embedded model, references or ownership.");
        var reviewed = CivilizationLanPackageReview.Prepare(
            CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(shared)), recipient);
        Require(reviewed.Issues.Count == 0 && reviewed.Package is not null,
            "An installed embedded model failed recipient review: " + string.Join("; ", reviewed.Issues));
        RequireSuccess(CivilizationPackageManager.Install(reviewed.Package!, recipient));
        Require(File.ReadAllBytes(Path.Combine(recipient, "Data", target)).AsSpan().SequenceEqual(replacement),
            "The shared installed model did not reproduce its embedded payload on the recipient.");
        RequireSuccess(CivilizationPackageManager.Uninstall(recipient, package.PackageId));
        RequireSuccess(CivilizationPackageManager.Uninstall(sender, package.PackageId));
        checks.Add("Installed model edits retain complete embedded bytes, source references and ownership; the recipient reviews and installs the selected model without its original project file.");
    }

    private static CivilizationInstallerPackage ReadUnchanged(string fixture, string id)
    {
        var before = Snapshot(fixture);
        try { return CivilizationPackageManager.CreateSharePackage(fixture, id); }
        finally
        {
            var after = Snapshot(fixture);
            Require(before.Count == after.Count && before.All(item => after.GetValueOrDefault(item.Key) == item.Value),
                "Creating or rejecting an installed share package wrote to its game fixture.");
        }
    }

    private static CivilizationEditProject ObjectProject(string fixture, CivilizationGameCatalog catalog,
        string name, int row, string key, string value)
    {
        var item = catalog.Items.Single(item => item.SourceId == row);
        var project = CivilizationEditProject.CreateDefault();
        project.Name = name; project.GamePath = fixture;
        project.Civilizations.Single(civ => civ.Name == "England").Entities.Add(new()
        {
            Name = item.Name, Category = item.Category, SourceObjectId = row,
            Properties = new(StringComparer.Ordinal) { [key] = value }
        });
        return project;
    }

    private static void CopyFixture(string source, string fixture)
    {
        if (Directory.Exists(fixture)) throw new InvalidOperationException("Sharing verification requires a new fixture.");
        foreach (var relative in SourceFiles)
        {
            var path = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(source, relative), path);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
        File.WriteAllText(Path.Combine(fixture, "VERIFICATION_ONLY.txt"), "Isolated installed-sharing fixture.\n");
    }

    private static Dictionary<string, string> Snapshot(string fixture) => Directory.GetFiles(fixture, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(fixture, path), FileHash, StringComparer.OrdinalIgnoreCase);
    private static bool SamePackage(CivilizationInstallerPackage first, CivilizationInstallerPackage second) =>
        CivilizationInstallerPackageCodec.Encode(first).AsSpan().SequenceEqual(CivilizationInstallerPackageCodec.Encode(second));
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void RequireSuccess(CivilizationInstallResult result) => Require(result.Success, result.Message);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void ExpectInvalid(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }
}

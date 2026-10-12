using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EDMWMapDesigner.Civilizations;

/// <summary>
/// Recovery and malformed-package checks in new isolated fixtures. No processes
/// are started and the source game installation is only read and hashed.
/// </summary>
public static class CivilizationPackageAdversarialVerification
{
    private const string ObjectFile = @"Data\db\dbobjects.dat";
    private const string TechFile = @"Data\db\dbtechtree.dat";
    private const string EffectFile = @"Data\db\dbeffects.dat";
    private const string Storage = ".edmw-civilization-packages";
    private static readonly string[] Dependencies = ["Empires_DMW.exe", ObjectFile, TechFile, EffectFile,
        @"Data\db\dbphoenixcivtable.dat", @"Data\db\dbgraphics.dat", @"Data\db\dbbuttons.dat",
        @"Data\db\dbevents.dat", @"Data\db\dbcalamity.dat"];

    public static IReadOnlyList<string> Run(string gamePath, string outputDirectory)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (output.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Adversarial verification must run outside the live game folder.");
        Directory.CreateDirectory(output);
        var originals = Dependencies.ToDictionary(relative => relative, relative => HashFile(Path.Combine(source, relative)),
            StringComparer.OrdinalIgnoreCase);
        var checks = new List<string>();
        try
        {
            VerifyPathsAndIdentities(NewFixture(source, output, "identity"), output, checks);
            VerifyEffectOperands(NewFixture(source, output, "effects"), checks);
            VerifyMergedObjectRanges(NewFixture(source, output, "merged-object-ranges"), checks);
            VerifyPreparedInstall(NewFixture(source, output, "recovery-install"), checks);
            foreach (var inlineManifest in new[] { true, false })
                VerifyPreparedUninstall(NewFixture(source, output,
                    inlineManifest ? "recovery-uninstall-inline" : "recovery-uninstall-file"), inlineManifest, checks);
            VerifyRecoveryRefusal(NewFixture(source, output, "recovery-refusal"), checks);
            File.WriteAllLines(Path.Combine(output, "civilization-package-adversarial-verification.txt"), checks);
            return checks.AsReadOnly();
        }
        finally
        {
            foreach (var original in originals)
                Require(HashFile(Path.Combine(source, original.Key)) == original.Value,
                    "Adversarial verification changed a source game file: " + original.Key);
        }
    }

    private static void VerifyPathsAndIdentities(string fixture, string output, List<string> checks)
    {
        var package = ObjectPackage(fixture, "Path and identity fixture", "unit.health", "117");
        var before = ReadData(fixture);
        var sentinelPath = Path.Combine(output, "outside-package-target.txt");
        File.WriteAllText(sentinelPath, "This file must never be a package target.");
        var sentinelHash = HashFile(sentinelPath);
        foreach (var path in new[] { @"..\outside-package-target.txt", @"Data\db\..\dbobjects.dat",
            Path.Combine(fixture, ObjectFile), "Data/db/dbobjects.dat", @"Data\db\dbobjects.dat:stream", "Empires_DMW.exe" })
        {
            var malformed = Copy(package);
            malformed.Files.First(file => file.Patches.Count > 0).RelativePath = path;
            Require(CivilizationPackageManager.Review(malformed, fixture).Count > 0 &&
                !CivilizationPackageManager.Install(malformed, fixture).Success,
                "A malformed or executable-target package path was accepted: " + path);
            RequireData(fixture, before, "A rejected package path changed fixture data.");
            Require(HashFile(sentinelPath) == sentinelHash, "A package path escaped its fixture.");
        }
        foreach (var id in new[] { "../invalid", "", Guid.NewGuid().ToString("D") })
        {
            var malformed = Copy(package);
            malformed.PackageId = id;
            Require(CivilizationPackageManager.Review(malformed, fixture).Count > 0 &&
                !CivilizationPackageManager.Install(malformed, fixture).Success,
                "A malformed package identity was accepted.");
            RequireData(fixture, before, "A rejected package identity changed fixture data.");
        }
        var installed = CivilizationPackageManager.Install(package, fixture);
        RequireSuccess(installed, "Install manifest-identity fixture");
        var manifestPath = Path.Combine(fixture, Storage, "installed", package.PackageId + ".json");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifest = JsonNode.Parse(manifestBytes)!.AsObject();
        manifest["package"]!["packageId"] = Guid.NewGuid().ToString("N");
        WriteJson(manifestPath, manifest);
        var afterInstall = ReadData(fixture);
        ExpectInvalid(() => CivilizationPackageManager.ReadInstalled(fixture),
            "A protected manifest identity different from its filename was accepted.");
        Require(!CivilizationPackageManager.Uninstall(fixture, package.PackageId).Success,
            "A mismatched protected manifest could be used to remove data.");
        RequireData(fixture, afterInstall, "A corrupt manifest changed installed data during rejection.");
        File.WriteAllBytes(manifestPath, manifestBytes);
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, package.PackageId), "Remove repaired identity fixture");
        RequireData(fixture, before, "The manifest-identity fixture did not restore exactly.");
        checks.Add("Escaping/rooted/stream/executable paths, malformed package IDs and mismatched protected manifest identities are rejected without data writes.");
    }

    private static void VerifyEffectOperands(string fixture, List<string> checks)
    {
        var before = ReadData(fixture);
        var relative = EffectPackage(fixture, "Stone relative operand", "technology.effect.1660.relative", "-0.25");
        var additive = EffectPackage(fixture, "Stone additive operand", "technology.effect.1660.additive", "1");
        var relativePatch = relative.Files.Single(file => file.RelativePath == EffectFile).Patches.Single();
        var additivePatch = additive.Files.Single(file => file.RelativePath == EffectFile).Patches.Single();
        Require(relativePatch.Offset == 1660 * 88 + 16 && additivePatch.Offset == 1660 * 88 + 12 &&
            relativePatch.Length == 4 && additivePatch.Length == 4,
            "Effect operands did not export their exact four-byte fields.");
        RequireSuccess(CivilizationPackageManager.Install(relative, fixture), "Install relative effect operand");
        Require(CivilizationPackageManager.Review(additive, fixture).Count == 0,
            "A distinct operand in the same effect record conflicted with its identity guard.");
        RequireSuccess(CivilizationPackageManager.Install(additive, fixture), "Install independent additive effect operand");
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, relative.PackageId), "Remove the older relative operand");
        var expected = Apply(before[EffectFile], additive, EffectFile);
        Require(File.ReadAllBytes(Path.Combine(fixture, EffectFile)).AsSpan().SequenceEqual(expected),
            "Effect operand removal restored over the remaining independent operand.");
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, additive.PackageId), "Remove additive effect operand");
        RequireData(fixture, before, "Removing effect operands did not restore the exact baseline.");

        var unsupported = Copy(relative);
        var changedFile = unsupported.Files.Single(file => file.RelativePath == EffectFile);
        var patch = changedFile.Patches.Single();
        patch.Offset = 1660 * 88 + 20; // Target/reference word, not a modifier operand.
        patch.Expected = before[EffectFile].AsSpan(patch.Offset, 4).ToArray();
        patch.Replacement = [1, 0, 0, 0];
        patch.SemanticTarget = $"{EffectFile.ToLowerInvariant()}:{patch.Offset}:4";
        RequireRejected(unsupported, fixture, before, "A patch to an effect target/reference escaped the operand whitelist.");
        var wrongGuard = Copy(relative);
        wrongGuard.Files.Single(file => file.RelativePath == EffectFile).Patches.Single().IdentityBytes[4] ^= 1;
        RequireRejected(wrongGuard, fixture, before, "An altered effect identity guard was accepted.");

        var opcodeChanged = before[EffectFile].ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(opcodeChanged.AsSpan(1660 * 88 + 32, 4), 1);
        File.WriteAllBytes(Path.Combine(fixture, EffectFile), opcodeChanged);
        var evasion = Copy(relative);
        // Recomputing the structure hash cannot turn a dedicated opcode's bytes
        // into a verified generic modifier field.
        evasion.Files.Single(file => file.RelativePath == EffectFile).StructuralHash =
            CivilizationInstallerPackage.StructuralHash(EffectFile, opcodeChanged);
        var changedBaseline = ReadData(fixture);
        RequireRejected(evasion, fixture, changedBaseline,
            "A changed effect opcode was accepted after replacing the structure fingerprint.");
        File.WriteAllBytes(Path.Combine(fixture, EffectFile), before[EffectFile]);
        checks.Add("Independent operands in one effect record coexist and uninstall independently; target/reference, identity and opcode bypasses fail without data writes.");
    }

    private static void VerifyMergedObjectRanges(string fixture, List<string> checks)
    {
        var before = ReadData(fixture);
        var source = CivilizationObjectBindings.ReadValues(before[ObjectFile], 833, CivilizationCategory.Units);
        var minimum = float.Parse(source["unit.minimumRange"], CultureInfo.InvariantCulture);
        var maximum = float.Parse(source["unit.attackRange"], CultureInfo.InvariantCulture);
        Require(float.IsFinite(minimum) && float.IsFinite(maximum) && minimum >= 0 && maximum > minimum,
            "The stock Citizen833 source needs a valid nonzero range interval for this fixture.");
        var width = maximum - minimum;
        var raisedMinimum = minimum + width * 0.75f;
        var loweredMaximum = minimum + width * 0.25f;
        // Each endpoint change is valid against the stock interval. Combining
        // them in either order must fail before any file or inventory mutation.
        var highMinimum = ObjectPackage(fixture, "Independent raised minimum", "unit.minimumRange",
            raisedMinimum.ToString("G9", CultureInfo.InvariantCulture));
        var lowMaximum = ObjectPackage(fixture, "Independent lowered maximum", "unit.attackRange",
            loweredMaximum.ToString("G9", CultureInfo.InvariantCulture));
        foreach (var (first, second) in new[] { (highMinimum, lowMaximum), (lowMaximum, highMinimum) })
        {
            RequireSuccess(CivilizationPackageManager.Install(first, fixture), "Install first individually valid range endpoint");
            var installed = ReadData(fixture);
            RequireRejected(second, fixture, installed,
                "Individually valid range endpoint packages combined into a reversed native interval.");
            Require(CivilizationPackageManager.ReadInstalled(fixture).Select(item => item.PackageId).SequenceEqual([first.PackageId]),
                "A rejected merged range endpoint changed active package ownership.");
            RequireSuccess(CivilizationPackageManager.Uninstall(fixture, first.PackageId), "Remove valid range endpoint owner");
            RequireData(fixture, before, "The rejected merged range fixture did not restore its original interval.");
        }

        // Package B is authored against A's expanded interval. Removing A
        // first would restore a maximum below B's minimum, including when A
        // owns another scalar in the same object file.
        var expandedMaximum = maximum + width;
        var dependentMinimum = maximum + width * 0.5f;
        var health = uint.Parse(source["unit.health"], CultureInfo.InvariantCulture);
        var expansionProject = CivilizationEditProject.CreateDefault();
        expansionProject.Name = "Range expansion with independent health";
        expansionProject.Civilizations.Single(civ => civ.SourceRowIndex == 10).Entities.Add(new()
        {
            Name = "Citizen (English)", Category = CivilizationCategory.Units, SourceObjectId = 833,
            Properties = new(StringComparer.Ordinal)
            {
                ["unit.attackRange"] = expandedMaximum.ToString("G9", CultureInfo.InvariantCulture),
                ["unit.health"] = checked(health + 17).ToString(CultureInfo.InvariantCulture)
            }
        });
        var expansion = CivilizationInstallerPackage.Create(expansionProject, fixture);
        RequireSuccess(CivilizationPackageManager.Install(expansion, fixture), "Install valid range expansion");
        var dependent = ObjectPackage(fixture, "Minimum using installed expansion", "unit.minimumRange",
            dependentMinimum.ToString("G9", CultureInfo.InvariantCulture));
        RequireSuccess(CivilizationPackageManager.Install(dependent, fixture), "Install valid dependent minimum");
        var bothInstalled = ReadData(fixture);
        Require(!CivilizationPackageManager.Uninstall(fixture, expansion.PackageId).Success,
            "Uninstall restored a maximum below another active package's minimum.");
        RequireData(fixture, bothInstalled, "Unsafe endpoint uninstall partially restored range or unrelated health data.");
        Require(CivilizationPackageManager.ReadInstalled(fixture).Select(item => item.PackageId).ToHashSet()
            .SetEquals([expansion.PackageId, dependent.PackageId]), "Unsafe endpoint uninstall changed active ownership.");
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, dependent.PackageId), "Remove dependent minimum first");
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, expansion.PackageId), "Remove expansion after dependent endpoint");
        RequireData(fixture, before, "Safe endpoint uninstall order did not restore exact baseline data.");
        checks.Add("Separate range endpoints reject reversed merged intervals in both install orders; unsafe uninstall preserves all data/ownership, and safe order restores exact originals.");
    }

    private static void VerifyPreparedInstall(string fixture, List<string> checks)
    {
        var before = ReadData(fixture);
        var neighbor = ObjectPackage(fixture, "Existing independent sight", "unit.sight", "7.5");
        var interrupted = MultiFilePackage(fixture, "Interrupted two-file install");
        RequireSuccess(CivilizationPackageManager.Install(neighbor, fixture), "Install existing recovery neighbor");
        var withNeighbor = ReadData(fixture);
        var result = CivilizationPackageManager.Install(interrupted, fixture);
        RequireSuccess(result, "Create protected two-file install journal");
        var prepared = PrepareInstallJournal(fixture, interrupted, result);
        var files = prepared["files"]!.AsArray();
        Require(files.Count == 2, "The recovery fixture must change two files.");
        // Emulate a crash after the first target replacement but before the
        // second. Both protected originals came from a real successful install.
        var secondPath = files[1]!["relativePath"]!.GetValue<string>();
        File.WriteAllBytes(Path.Combine(fixture, secondPath), withNeighbor[secondPath]);
        Require(CivilizationPackageManager.ReadInstalled(fixture).Any(item => item.PackageId == interrupted.PackageId &&
            item.Status.Contains("recovery", StringComparison.OrdinalIgnoreCase)),
            "A prepared partial installation was not visible as interrupted.");
        RequireSuccess(CivilizationPackageManager.Recover(fixture), "Recover prepared partial install");
        RequireData(fixture, withNeighbor, "Interrupted install recovery overwrote the existing independent package.");
        Require(CivilizationPackageManager.ReadInstalled(fixture).Select(item => item.PackageId)
            .SequenceEqual([neighbor.PackageId]), "Recovery did not remove only the interrupted installation.");
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, neighbor.PackageId), "Remove recovery neighbor");
        RequireData(fixture, before, "Prepared-install recovery did not finish at original hashes.");
        checks.Add("A prepared two-file installation recovers after only its first replacement and preserves an already installed independent package.");
    }

    private static void VerifyPreparedUninstall(string fixture, bool inlineManifest, List<string> checks)
    {
        var before = ReadData(fixture);
        var owner = MultiFilePackage(fixture, "Interrupted uninstall owner");
        var independent = ObjectPackage(fixture, "Recovery-triggering sight", "unit.sight", "7.5");
        RequireSuccess(CivilizationPackageManager.Install(owner, fixture), "Install uninstall-recovery owner");
        var installed = ReadData(fixture);
        var manifestPath = Path.Combine(fixture, Storage, "installed", owner.PackageId + ".json");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var result = CivilizationPackageManager.Uninstall(fixture, owner.PackageId);
        RequireSuccess(result, "Create protected uninstall journal");
        var journalPath = JournalPath(result);
        var journal = ReadJson(journalPath);
        journal["state"] = "Prepared";
        if (inlineManifest)
        {
            journal["manifestBeforeHash"] = null;
            journal["manifestBefore"] = Convert.ToBase64String(manifestBytes);
        }
        else
        {
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(journalPath)!, "manifest.original"), manifestBytes);
            journal["manifestBefore"] = null;
            journal["manifestBeforeHash"] = Hash(manifestBytes);
        }
        WriteJson(journalPath, journal);
        if (!inlineManifest)
            File.WriteAllBytes(manifestPath, manifestBytes); // Crash before deleting active ownership.
        Require(CivilizationPackageManager.ReadInstalled(fixture).Any(item => item.PackageId == owner.PackageId &&
            item.Status.Contains("recovery", StringComparison.OrdinalIgnoreCase)),
            "A prepared uninstall was not detected independently of whether its active manifest still exists.");
        RequireSuccess(CivilizationPackageManager.Recover(fixture), "Recover prepared uninstall directly");
        RequireData(fixture, installed, "Direct uninstall recovery did not restore the installed owner.");
        RequireSuccess(CivilizationPackageManager.Install(independent, fixture), "Recover uninstall before a new independent install");
        var expected = Apply(installed[ObjectFile], independent, ObjectFile);
        Require(File.ReadAllBytes(Path.Combine(fixture, ObjectFile)).AsSpan().SequenceEqual(expected) &&
            File.ReadAllBytes(Path.Combine(fixture, TechFile)).AsSpan().SequenceEqual(installed[TechFile]),
            "Uninstall recovery did not restore the installed owner before the independent install.");
        Require(CivilizationPackageManager.ReadInstalled(fixture).Select(item => item.PackageId).ToHashSet()
            .SetEquals([owner.PackageId, independent.PackageId]), "Uninstall recovery lost the protected original active manifest.");
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, owner.PackageId), "Remove recovered owner");
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, independent.PackageId), "Remove independent recovery trigger");
        RequireData(fixture, before, "Prepared-uninstall recovery did not finish at original hashes.");
        checks.Add($"Prepared uninstall recovery restores both data and active ownership using {(inlineManifest ? "the compatible inline" : "the hashed separate")} prior manifest.");
    }

    private static void VerifyRecoveryRefusal(string fixture, List<string> checks)
    {
        var before = ReadData(fixture);
        var package = MultiFilePackage(fixture, "Protected recovery refusal");
        var result = CivilizationPackageManager.Install(package, fixture);
        RequireSuccess(result, "Create recovery-refusal journal");
        var installed = ReadData(fixture);
        var journalPath = JournalPath(result);
        var journal = PrepareInstallJournal(fixture, package, result);
        var pristineJournal = File.ReadAllBytes(journalPath);
        var files = journal["files"]!.AsArray();
        var first = files[0]!.AsObject();
        var backupPath = Path.Combine(Path.GetDirectoryName(journalPath)!, first["backupName"]!.GetValue<string>());
        var backup = File.ReadAllBytes(backupPath);
        var alteredBackup = backup.ToArray();
        alteredBackup[0] ^= 1;
        File.WriteAllBytes(backupPath, alteredBackup);
        Require(!CivilizationPackageManager.Recover(fixture).Success,
            "An altered protected backup was trusted during recovery.");
        RequireData(fixture, installed, "A rejected backup recovery partially wrote another target before preflight completed.");
        File.WriteAllBytes(backupPath, backup);

        var firstRelative = first["relativePath"]!.GetValue<string>();
        var changed = installed[firstRelative].ToArray();
        changed[^1] ^= 1; // Neither the journal's before nor after hash.
        File.WriteAllBytes(Path.Combine(fixture, firstRelative), changed);
        var external = ReadData(fixture);
        Require(!CivilizationPackageManager.Recover(fixture).Success,
            "An externally changed transaction target was overwritten by recovery.");
        RequireData(fixture, external, "Recovery of an externally changed target partially wrote another target.");
        File.WriteAllBytes(Path.Combine(fixture, firstRelative), installed[firstRelative]);

        first["backupName"] = @"..\outside.original";
        WriteJson(journalPath, journal);
        Require(!CivilizationPackageManager.Recover(fixture).Success,
            "A transaction backup path escaped protected storage.");
        RequireData(fixture, installed, "A rejected transaction backup path changed data.");
        File.WriteAllBytes(journalPath, pristineJournal);
        RequireSuccess(CivilizationPackageManager.Recover(fixture), "Recover repaired protected journal");
        RequireData(fixture, before, "Repairing protected recovery inputs did not restore exact originals.");
        checks.Add("Altered protected backups, external transaction changes and backup-path escapes stop recovery before any target write; repaired inputs recover exactly.");
    }

    private static JsonObject PrepareInstallJournal(string fixture, CivilizationInstallerPackage package,
        CivilizationInstallResult result)
    {
        var path = JournalPath(result);
        var journal = ReadJson(path);
        journal["state"] = "Prepared";
        journal["installBackup"] = false;
        journal["manifestBefore"] = null;
        journal["manifestBeforeHash"] = null;
        WriteJson(path, journal);
        File.Delete(Path.Combine(fixture, Storage, "installed", package.PackageId + ".json"));
        File.Delete(Path.Combine(fixture, Storage, "backups", package.PackageId + ".json"));
        return journal;
    }

    private static string NewFixture(string source, string output, string name)
    {
        var fixture = Path.Combine(output, "adversarial-fixtures", name + "-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(fixture)) throw new InvalidOperationException("A recovery fixture must be new.");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "VERIFICATION_ONLY.txt"), "Offline data fixture. Never launch this copied game executable.\n");
        foreach (var relative in Dependencies)
        {
            var destination = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(source, relative), destination);
            File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
        }
        return fixture;
    }

    private static CivilizationInstallerPackage ObjectPackage(string fixture, string name, string key, string value)
    {
        var project = CivilizationEditProject.CreateDefault();
        project.Name = name;
        project.Civilizations.Single(civ => civ.SourceRowIndex == 10).Entities.Add(new()
        {
            Name = "Citizen (English)", Category = CivilizationCategory.Units, SourceObjectId = 833,
            Properties = new(StringComparer.Ordinal) { [key] = value }
        });
        return CivilizationInstallerPackage.Create(project, fixture);
    }

    private static CivilizationInstallerPackage MultiFilePackage(string fixture, string name)
    {
        var project = CivilizationEditProject.CreateDefault();
        project.Name = name;
        var objects = File.ReadAllBytes(Path.Combine(fixture, ObjectFile));
        var health = BinaryPrimitives.ReadUInt32LittleEndian(objects.AsSpan(4 + 833 * 1936 + 120, 4));
        var technologyId = checked((int)CivilizationObjectBindings.ReadTechnologyId(objects, 833, CivilizationCategory.Units));
        var production = CivilizationTechnologyBindings.ReadRecords(File.ReadAllBytes(Path.Combine(fixture, TechFile)))
            .Single(record => record.TechId == technologyId);
        project.Civilizations.Single(civ => civ.SourceRowIndex == 10).Entities.Add(new()
        {
            Name = "Citizen (English)", Category = CivilizationCategory.Units, SourceObjectId = 833,
            Properties = new(StringComparer.Ordinal)
            {
                ["unit.health"] = checked(health + 17).ToString(CultureInfo.InvariantCulture),
                ["unit.foodCost"] = checked(production.FoodCost + 9).ToString(CultureInfo.InvariantCulture)
            }
        });
        return CivilizationInstallerPackage.Create(project, fixture);
    }

    private static CivilizationInstallerPackage EffectPackage(string fixture, string name, string key, string value)
    {
        var project = CivilizationEditProject.CreateDefault();
        project.Name = name;
        project.Civilizations.Single(civ => civ.SourceRowIndex == 9).Entities.Add(new()
        {
            Name = "Stone gathering research", Category = CivilizationCategory.Technologies, SourceTechnologyId = 9404,
            Properties = new(StringComparer.Ordinal) { [key] = value }
        });
        return CivilizationInstallerPackage.Create(project, fixture);
    }

    private static CivilizationInstallerPackage Copy(CivilizationInstallerPackage package) =>
        CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(package));
    private static Dictionary<string, byte[]> ReadData(string fixture) => Dependencies.ToDictionary(relative => relative,
        relative => File.ReadAllBytes(Path.Combine(fixture, relative)), StringComparer.OrdinalIgnoreCase);
    private static void RequireData(string fixture, IReadOnlyDictionary<string, byte[]> expected, string message) =>
        Require(expected.All(file => File.ReadAllBytes(Path.Combine(fixture, file.Key)).AsSpan().SequenceEqual(file.Value)), message);
    private static byte[] Apply(byte[] original, CivilizationInstallerPackage package, string relative)
    {
        var result = original.ToArray();
        foreach (var patch in package.Files.Single(file => file.RelativePath == relative).Patches)
            patch.Replacement.CopyTo(result, patch.Offset);
        return result;
    }
    private static void RequireRejected(CivilizationInstallerPackage package, string fixture,
        IReadOnlyDictionary<string, byte[]> before, string message)
    {
        Require(CivilizationPackageManager.Review(package, fixture).Count > 0 &&
            !CivilizationPackageManager.Install(package, fixture).Success, message);
        RequireData(fixture, before, "A rejected malformed package changed fixture data.");
    }
    private static string JournalPath(CivilizationInstallResult result) => Path.Combine(result.BackupPath
        ?? throw new InvalidOperationException("A completed fixture transaction did not retain its journal."), "transaction.json");
    private static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
    private static void WriteJson(string path, JsonObject value) =>
        File.WriteAllText(path, value.ToJsonString(CivilizationInstallerPackageCodec.JsonOptions));
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void RequireSuccess(CivilizationInstallResult result, string action) =>
        Require(result.Success, action + " failed: " + result.Message);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void ExpectInvalid(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
}

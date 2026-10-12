using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EDMWMapDesigner.UI;

namespace EDMWMapDesigner.Civilizations;

/// <summary>
/// Package acceptance checks in a new game-data fixture. This class never starts
/// an executable and never installs or uninstalls against the source game path.
/// </summary>
public static class CivilizationPackageVerification
{
    private const string ObjectFile = "Data\\db\\dbobjects.dat";
    private const string TechnologyFile = "Data\\db\\dbtechtree.dat";
    private const string CivilizationFile = "Data\\db\\dbphoenixcivtable.dat";

    public static IReadOnlyList<string> Run(string gamePath, string outputDirectory)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (output.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Package verification must run outside the live game folder.");
        var fixture = Path.Combine(output, "package-fixtures", Guid.NewGuid().ToString("N"));
        if (Directory.Exists(fixture)) throw new InvalidOperationException("Package verification requires a new fixture.");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "VERIFICATION_ONLY.txt"),
            "Isolated package fixture. No source game files are installed, removed, or changed.\n");
        string[] dependencies = ["Empires_DMW.exe", ObjectFile, "Data\\db\\dbphoenixcivtable.dat",
            TechnologyFile, "Data\\db\\dbgraphics.dat", "Data\\db\\dbbuttons.dat",
            "Data\\db\\dbweapontohit.dat", "Data\\db\\dbfamily.dat", "Data\\db\\dbeffects.dat"];
        var sourceHashes = dependencies.ToDictionary(relative => relative,
            relative => FileHash(Path.Combine(source, relative)), StringComparer.OrdinalIgnoreCase);
        foreach (var relative in dependencies)
        {
            var destination = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(source, relative), destination);
            File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
        }
        var checks = new List<string>();
        try
        {
            var objectsPath = Path.Combine(fixture, ObjectFile);
            var technologiesPath = Path.Combine(fixture, TechnologyFile);
            var originalObjects = File.ReadAllBytes(objectsPath);
            var originalTechnologies = File.ReadAllBytes(technologiesPath);
            var catalog = CivilizationGameCatalog.Load(fixture);
            var health = ReadUInt(originalObjects, 833, 120);
            var sight = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                originalObjects.AsSpan(ObjectOffset(833, 136), 4)));
            var packageA = CreateObjectPackage(fixture, catalog, "Package A — citizen health", 833,
                "unit.health", checked(health + 1).ToString(CultureInfo.InvariantCulture));
            var packageB = CreateObjectPackage(fixture, catalog, "Package B — citizen sight", 833,
                "unit.sight", (sight + .5f).ToString("R", CultureInfo.InvariantCulture));
            Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(originalObjects) &&
                File.ReadAllBytes(technologiesPath).AsSpan().SequenceEqual(originalTechnologies) &&
                CivilizationPackageManager.ReadInstalled(fixture).Count == 0,
                "Creating portable packages changed game files or installed a package.");
            Require(CivilizationEditProject.Deserialize(packageA.ProjectJson).GamePath == "",
                "The portable package retained the source machine's game path.");
            var healthPatch = ChangedFile(packageA, ObjectFile).Patches.Single();
            Require(healthPatch.Offset == ObjectOffset(833, 120) && healthPatch.Length == 4,
                "A uint object stat was exported as changed bytes instead of its complete semantic field.");
            VerifyCodec(packageA, output, checks);
            var expectedA = ApplyPatches(originalObjects, packageA, ObjectFile);
            var expectedB = ApplyPatches(originalObjects, packageB, ObjectFile);
            var expectedBoth = ApplyPatches(expectedA, packageB, ObjectFile);

            foreach (var removeAFirst in new[] { true, false })
            {
                Require(CivilizationPackageManager.Review(packageA, fixture).Count == 0,
                    "The first independent package failed its read-only review.");
                RequireSuccess(CivilizationPackageManager.Install(packageA, fixture), "Install package A");
                RequireSuccess(CivilizationPackageManager.Install(packageB, fixture), "Install package B alongside A");
                Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(expectedBoth),
                    "Two disjoint fields in the same object table did not coexist exactly.");
                RequireInstalled(fixture, packageA.PackageId, packageB.PackageId);
                using var homeInventory = new CivilizationInstalledEditsPanel(() => fixture,
                    () => throw new InvalidOperationException("An inventory-only UI assertion triggered uninstall."));
                homeInventory.RefreshInstalledPackages();
                Require(homeInventory.InstalledProjectNamesForVerification.ToHashSet(StringComparer.Ordinal)
                        .SetEquals(new[] { packageA.ProjectName, packageB.ProjectName }),
                    "The Home inventory did not display both fixture-installed civilization projects.");
                var installedA = CivilizationPackageManager.ReadInstalled(fixture)
                    .Single(package => package.PackageId == packageA.PackageId);
                var detail = CivilizationInstalledEditsPanel.BuildDetails(installedA);
                Require(detail.Contains("Health: " + health.ToString(CultureInfo.InvariantCulture) + " → " +
                    checked(health + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
                    "Home package details omitted the actual before-and-after change values.");
                var interrupted = installedA with { Status = "Interrupted installation — recovery required" };
                Require(CivilizationInstalledEditsPanel.BuildDetails(interrupted).Contains(interrupted.Status,
                    StringComparison.Ordinal), "Home package details concealed an interrupted transaction status.");
                var first = removeAFirst ? packageA : packageB;
                var second = removeAFirst ? packageB : packageA;
                RequireSuccess(CivilizationPackageManager.Uninstall(fixture, first.PackageId), "Remove the first selected package");
                Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(removeAFirst ? expectedB : expectedA),
                    "Removing one package restored over the other package's field.");
                RequireInstalled(fixture, second.PackageId);
                homeInventory.RefreshInstalledPackages();
                Require(homeInventory.InstalledProjectNamesForVerification.SequenceEqual(new[] { second.ProjectName }),
                    "The Home inventory retained an uninstalled project or lost its installed neighbor.");
                RequireSuccess(CivilizationPackageManager.Uninstall(fixture, second.PackageId), "Remove the remaining package");
                Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(originalObjects),
                    "Removing both packages did not restore the original object table.");
                RequireInstalled(fixture);
                homeInventory.RefreshInstalledPackages();
                Require(homeInventory.InstalledProjectNamesForVerification.Count == 0,
                    "The Home inventory did not clear after all fixture packages were removed.");
            }
            checks.Add("Independent mods share an object table and can be removed in either order without losing the remaining mod.");
            checks.Add("Home inventory refresh shows installed projects, change details and arbitrary removals without invoking a user dialog.");

            RequireSuccess(CivilizationPackageManager.Install(packageA, fixture), "Install the semantic-overlap owner");
            // Export against A's actual installed value. The new change alters a
            // different byte of the same uint, so stale-baseline checks alone
            // would allow it. Ownership must cover the entire four-byte field.
            var packageC = CreateObjectPackage(fixture, catalog, "Package C — conflicting high byte", 833,
                "unit.health", checked(health + 1 + 256).ToString(CultureInfo.InvariantCulture));
            var conflictPatch = ChangedFile(packageC, ObjectFile).Patches.Single();
            Require(healthPatch.SemanticTarget == conflictPatch.SemanticTarget &&
                !ChangedPositions(healthPatch).Intersect(ChangedPositions(conflictPatch)).Any(),
                "The conflict fixture does not change separate bytes of the same semantic field.");
            Require(CivilizationPackageManager.Review(packageC, fixture).Count > 0 &&
                !CivilizationPackageManager.Install(packageC, fixture).Success,
                "An overlapping field was admitted because its changed bytes did not overlap.");
            Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(expectedA),
                "Rejecting a semantic overlap changed the installed owner.");
            RequireInstalled(fixture, packageA.PackageId);
            RequireSuccess(CivilizationPackageManager.Uninstall(fixture, packageA.PackageId), "Remove the semantic-overlap owner");
            checks.Add("Same-field ownership blocks packages even when they change different bytes and their expected baseline is current.");

            var wrongBase = (byte[])originalObjects.Clone();
            CivilizationObjectBindings.WriteValue(wrongBase, 833, CivilizationCategory.Units, "unit.health",
                checked(health + 23).ToString(CultureInfo.InvariantCulture));
            File.WriteAllBytes(objectsPath, wrongBase);
            Require(CivilizationPackageManager.Review(packageA, fixture).Count > 0 &&
                !CivilizationPackageManager.Install(packageA, fixture).Success,
                "A package with an incompatible current field baseline was accepted.");
            Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(wrongBase),
                "Rejecting a wrong field baseline overwrote existing data.");
            RequireInstalled(fixture);
            File.WriteAllBytes(objectsPath, originalObjects);
            checks.Add("Read-only review and installation reject an incompatible field baseline without changing game data.");

            VerifyExternalChanges(fixture, catalog, originalObjects, originalTechnologies, packageB, checks);
            VerifyCombinedAges(fixture, checks);
            VerifyModels(source, fixture, catalog, originalObjects, checks);
            VerifyExport(fixture, output, catalog, originalObjects, checks);
            RequireInstalled(fixture);
            Require(dependencies.All(relative => FileHash(Path.Combine(fixture, relative)) == sourceHashes[relative]),
                "Package round trips left a database or executable dependency modified.");
            checks.Add("All fixture databases and the executable finish with their original hashes.");
            File.WriteAllLines(Path.Combine(output, "civilization-package-verification.txt"),
                checks.Append("Fixture: " + fixture));
            return checks.AsReadOnly();
        }
        finally
        {
            foreach (var relative in dependencies)
                if (FileHash(Path.Combine(source, relative)) != sourceHashes[relative])
                    throw new InvalidOperationException("Package verification changed a source game file: " + relative);
        }
    }

    private static void VerifyCodec(CivilizationInstallerPackage package, string output, List<string> checks)
    {
        var payload = CivilizationInstallerPackageCodec.Encode(package);
        var decoded = CivilizationInstallerPackageCodec.Decode(payload);
        Require(decoded.PackageId == package.PackageId && decoded.ProjectJson == package.ProjectJson &&
            decoded.Changes.SequenceEqual(package.Changes) &&
            ChangedFile(decoded, ObjectFile).Patches.Single().Expected.AsSpan()
                .SequenceEqual(ChangedFile(package, ObjectFile).Patches.Single().Expected) &&
            ChangedFile(decoded, ObjectFile).Patches.Single().Replacement.AsSpan()
                .SequenceEqual(ChangedFile(package, ObjectFile).Patches.Single().Replacement),
            "Package payload round trip lost project metadata, review changes, or field bytes.");
        var brokenPayload = (byte[])payload.Clone();
        Array.Clear(brokenPayload);
        ExpectInvalid(() => CivilizationInstallerPackageCodec.Decode(brokenPayload),
            "A damaged archive payload was accepted.");
        var containerPath = Path.Combine(output, "package-container-fixture.bin");
        using (var stream = File.Create(containerPath))
        {
            stream.Write("VERIFICATION CONTAINER — NEVER EXECUTED"u8);
            CivilizationInstallerPackageCodec.Append(stream, payload);
        }
        Require(CivilizationInstallerPackageCodec.ReadExecutable(containerPath).PackageId == package.PackageId,
            "An appended package footer did not recover the portable payload.");
        var brokenContainer = File.ReadAllBytes(containerPath);
        brokenContainer[^1] ^= 1;
        var tamperedPath = Path.Combine(output, "tampered-package-container.bin");
        File.WriteAllBytes(tamperedPath, brokenContainer);
        ExpectInvalid(() => CivilizationInstallerPackageCodec.ReadExecutable(tamperedPath),
            "A changed embedded payload hash was accepted.");
        checks.Add("Portable package payloads and appended footers round-trip; damaged archives and integrity hashes are rejected.");
    }

    private static void VerifyExternalChanges(string fixture, CivilizationGameCatalog catalog,
        byte[] originalObjects, byte[] originalTechnologies, CivilizationInstallerPackage other,
        List<string> checks)
    {
        var project = ObjectProject(fixture, catalog, "Package D — multi-file edit", 833,
            "unit.health", checked(ReadUInt(originalObjects, 833, 120) + 7).ToString(CultureInfo.InvariantCulture));
        var entity = project.Civilizations.Single(civilization => civilization.Name == "England").Entities.Single();
        var techId = checked((int)CivilizationObjectBindings.ReadTechnologyId(originalObjects, 833, CivilizationCategory.Units));
        var technology = CivilizationTechnologyBindings.ReadRecords(originalTechnologies).Single(record => record.TechId == techId);
        entity.Properties["unit.foodCost"] = checked(technology.FoodCost + 3).ToString(CultureInfo.InvariantCulture);
        var package = CivilizationInstallerPackage.Create(project, fixture);
        RequireSuccess(CivilizationPackageManager.Install(package, fixture), "Install the external-change test package");
        RequireSuccess(CivilizationPackageManager.Install(other, fixture), "Install an independent package for preservation checks");
        var objectsPath = Path.Combine(fixture, ObjectFile);
        var technologiesPath = Path.Combine(fixture, TechnologyFile);
        var installedObjects = File.ReadAllBytes(objectsPath);
        var installedTechnologies = File.ReadAllBytes(technologiesPath);
        var external = (byte[])installedTechnologies.Clone();
        CivilizationTechnologyBindings.WriteObjectValue(external, techId, CivilizationCategory.Units,
            "unit.foodCost", checked(technology.FoodCost + 4).ToString(CultureInfo.InvariantCulture));
        File.WriteAllBytes(technologiesPath, external);
        Require(!CivilizationPackageManager.Uninstall(fixture, package.PackageId).Success,
            "Uninstall overwrote an externally changed owned field.");
        Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(installedObjects) &&
            File.ReadAllBytes(technologiesPath).AsSpan().SequenceEqual(external),
            "A rejected multi-file uninstall changed either owned or independent data.");
        RequireInstalled(fixture, package.PackageId, other.PackageId);
        File.WriteAllBytes(technologiesPath, installedTechnologies);
        var externalObjects = (byte[])installedObjects.Clone();
        externalObjects[ObjectOffset(833, 1800)] ^= 1;
        File.WriteAllBytes(objectsPath, externalObjects);
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, package.PackageId),
            "Remove the multi-file package while preserving an unrelated external change");
        var otherOnly = ApplyPatches(originalObjects, other, ObjectFile);
        var expectedWithExternal = (byte[])otherOnly.Clone();
        expectedWithExternal[ObjectOffset(833, 1800)] ^= 1;
        Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(expectedWithExternal) &&
            File.ReadAllBytes(technologiesPath).AsSpan().SequenceEqual(originalTechnologies),
            "Removing the multi-file package did not exactly restore its owned fields while preserving unrelated edits.");
        RequireInstalled(fixture, other.PackageId);
        // Remove only this test's independent external mutation so the final
        // fixture round-trip assertion can compare against its original bytes.
        File.WriteAllBytes(objectsPath, otherOnly);
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, other.PackageId), "Remove the independent neighbor");
        checks.Add("Owned external changes refuse uninstall atomically; range-scoped removal preserves unrelated external edits and independent packages.");
    }

    private static void VerifyModels(string source, string fixture, CivilizationGameCatalog catalog,
        byte[] originalObjects, List<string> checks)
    {
        var sourceAssets = new CivilizationGameAssets(source);
        var modelName = sourceAssets.FindModelName(1094) ?? throw new InvalidOperationException("The source Town Centre model did not resolve.");
        var alternateName = sourceAssets.FindModelName(1095) ?? throw new InvalidOperationException("The alternate Town Centre model did not resolve.");
        var stock = sourceAssets.ReadModel(modelName);
        var alternate = sourceAssets.ReadModel(alternateName);
        Require(!stock.AsSpan().SequenceEqual(alternate), "Native replacement fixtures must contain different geometry bytes.");
        WriteModelArchive(Path.Combine(fixture, "Data", "data.ssa"), [(modelName, stock), (alternateName, alternate)]);
        var modelPath = Path.Combine(fixture, "Data", modelName);
        Require(!File.Exists(modelPath), "The absent-original model fixture already has a loose override.");
        var first = CivilizationInstallerPackage.Create(ModelProject(fixture, catalog,
            "Model package A", alternate), fixture);
        var wholeFile = first.Files.Single(file => file.WholeFile);
        Require(wholeFile.ExpectedHash is null && wholeFile.Patches.Count == 0 &&
            wholeFile.Replacement!.AsSpan().SequenceEqual(alternate),
            "An archived native model did not export a whole-file patch with an absent original.");
        RequireSuccess(CivilizationPackageManager.Install(first, fixture), "Install native model package A");
        Require(File.ReadAllBytes(modelPath).AsSpan().SequenceEqual(alternate), "Native model bytes changed during package installation.");
        var second = CivilizationInstallerPackage.Create(ModelProject(fixture, catalog,
            "Model package B", stock), fixture);
        Require(CivilizationPackageManager.Review(second, fixture).Count > 0 &&
            !CivilizationPackageManager.Install(second, fixture).Success &&
            File.ReadAllBytes(modelPath).AsSpan().SequenceEqual(alternate),
            "A second package took ownership of an already installed whole model file.");
        RequireInstalled(fixture, first.PackageId);
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, first.PackageId), "Remove an absent-original native replacement");
        Require(!File.Exists(modelPath) && new CivilizationGameAssets(fixture).ReadModel(modelName).AsSpan().SequenceEqual(stock),
            "Uninstall did not remove a newly created loose model and reveal its archive original.");

        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllBytes(modelPath, stock);
        var withOriginal = CivilizationInstallerPackage.Create(ModelProject(fixture, catalog,
            "Model package with loose original", alternate), fixture);
        Require(withOriginal.Files.Single(file => file.WholeFile).ExpectedHash == CivilizationInstallerPackage.Hash(stock),
            "A model package omitted its original loose-file fingerprint.");
        RequireSuccess(CivilizationPackageManager.Install(withOriginal, fixture), "Replace an existing loose model");
        var externalModel = (byte[])alternate.Clone();
        externalModel[^1] ^= 1;
        File.WriteAllBytes(modelPath, externalModel);
        Require(!CivilizationPackageManager.Uninstall(fixture, withOriginal.PackageId).Success &&
            File.ReadAllBytes(modelPath).AsSpan().SequenceEqual(externalModel),
            "Uninstall overwrote an externally changed model file.");
        File.WriteAllBytes(modelPath, alternate);
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, withOriginal.PackageId), "Restore the protected original loose model");
        Require(File.ReadAllBytes(modelPath).AsSpan().SequenceEqual(stock) &&
            File.ReadAllBytes(Path.Combine(fixture, ObjectFile)).AsSpan().SequenceEqual(originalObjects),
            "Model uninstall did not restore its prior bytes or changed an unrelated object table.");
        checks.Add("Native models have whole-file ownership; uninstall restores an existing original or removes an absent-original override, and refuses external model edits.");
    }

    private static void VerifyCombinedAges(string fixture, List<string> checks)
    {
        var path = Path.Combine(fixture, CivilizationFile);
        var original = File.ReadAllBytes(path);
        var baseline = original.ToArray();
        const int row = 10; // England's verified Phoenix row.
        var record = checked(4 + row * 344);
        BinaryPrimitives.WriteInt32LittleEndian(baseline.AsSpan(record + 212, 4), 5);
        BinaryPrimitives.WriteInt32LittleEndian(baseline.AsSpan(record + 216, 4), 7);
        File.WriteAllBytes(path, baseline);
        var initial = CreateAgePackage(fixture, "Initial age package", "age.initialAge", "7");
        var final = CreateAgePackage(fixture, "Final age package", "age.finalAge", "6");
        foreach (var initialFirst in new[] { true, false })
        {
            var first = initialFirst ? initial : final;
            var second = initialFirst ? final : initial;
            RequireSuccess(CivilizationPackageManager.Install(first, fixture), "Install the individually valid age endpoint");
            Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(ApplyPatches(baseline, first, CivilizationFile)),
                "The first age package changed more than its intended endpoint.");
            var beforeRejectedInstall = SnapshotFiles(fixture);
            Require(CivilizationPackageManager.Review(second, fixture).Count > 0 &&
                !CivilizationPackageManager.Install(second, fixture).Success,
                "Disjoint endpoint packages combined into an invalid 7..6 civilization interval.");
            RequireSameSnapshot(beforeRejectedInstall, SnapshotFiles(fixture),
                "Rejecting a combined invalid age interval wrote game data, backups, or package metadata.");
            RequireInstalled(fixture, first.PackageId);
            RequireSuccess(CivilizationPackageManager.Uninstall(fixture, first.PackageId), "Remove the single age-endpoint package");
            Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(baseline),
                "Removing the valid endpoint package did not restore the age baseline.");
            RequireInstalled(fixture);
        }

        // Both installed packages are valid together, but removing the widened
        // upper endpoint first would leave the other package's 8..7 interval.
        var widenedFinal = CreateAgePackage(fixture, "Widened final age", "age.finalAge", "9");
        RequireSuccess(CivilizationPackageManager.Install(widenedFinal, fixture), "Widen the fixture's final age to 9");
        var raisedInitial = CreateAgePackage(fixture, "Raised initial age", "age.initialAge", "8");
        RequireSuccess(CivilizationPackageManager.Install(raisedInitial, fixture), "Raise the fixture's initial age to 8");
        var both = ApplyPatches(ApplyPatches(baseline, widenedFinal, CivilizationFile), raisedInitial, CivilizationFile);
        Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(both), "The valid combined 8..9 interval did not install exactly.");
        var beforeRejectedRemoval = SnapshotFiles(fixture);
        Require(!CivilizationPackageManager.Uninstall(fixture, widenedFinal.PackageId).Success,
            "Uninstall left a remaining package with an invalid 8..7 civilization interval.");
        RequireSameSnapshot(beforeRejectedRemoval, SnapshotFiles(fixture),
            "Rejecting an invalid age uninstall wrote game data, backups, or package metadata.");
        RequireInstalled(fixture, widenedFinal.PackageId, raisedInitial.PackageId);
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, raisedInitial.PackageId), "Remove the raised lower endpoint first");
        Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(ApplyPatches(baseline, widenedFinal, CivilizationFile)),
            "Removing the lower endpoint lost the remaining widened final age.");
        RequireSuccess(CivilizationPackageManager.Uninstall(fixture, widenedFinal.PackageId), "Remove the widened final endpoint");
        Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(baseline), "Age package removal did not restore 5..7 exactly.");
        RequireInstalled(fixture);
        File.WriteAllBytes(path, original);
        checks.Add("Disjoint age endpoints cannot combine or uninstall into an invalid interval; refusals occur before any data or metadata write.");
    }

    private static CivilizationInstallerPackage CreateAgePackage(string fixture,
        string name, string key, string value)
    {
        var project = CivilizationEditProject.CreateDefault();
        project.Name = name;
        project.GamePath = fixture;
        project.Civilizations.Single(civilization => civilization.Name == "England").Properties[key] = value;
        return CivilizationInstallerPackage.Create(project, fixture);
    }

    private static void VerifyExport(string fixture, string output, CivilizationGameCatalog catalog,
        byte[] originalObjects, List<string> checks)
    {
        var project = ObjectProject(fixture, catalog, "Shared Test", 833, "unit.health",
            checked(ReadUInt(originalObjects, 833, 120) + 19).ToString(CultureInfo.InvariantCulture));
        var before = SnapshotFiles(fixture);
        var exportFolder = Path.Combine(output, "export-fixtures", Guid.NewGuid().ToString("N"));
        var fileName = CivilizationInstallerBuilder.SuggestedFileName(project.Name);
        Require(fileName == "EDMW_Shared Testciv_installer.exe" &&
            CivilizationInstallerBuilder.SuggestedFileName(" invalid/:name. ")
                .IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
            "The civilization exporter did not use a safe project-specific installer filename.");
        var path = CivilizationInstallerBuilder.Export(project, fixture, Path.Combine(exportFolder, fileName));
        Require(Directory.GetFiles(exportFolder).Length == 1 &&
            Path.GetFileName(path) == fileName, "Standalone export produced dependencies or left temporary files beside its EXE.");
        var after = SnapshotFiles(fixture);
        Require(before.Count == after.Count && before.All(entry => after.TryGetValue(entry.Key, out var hash) && hash == entry.Value),
            "Exporting a civilization installer changed files in the game fixture.");
        RequireInstalled(fixture);
        var package = CivilizationInstallerPackageCodec.ReadExecutable(path);
        Require(package.ProjectName == project.Name && package.Changes.Count == 1 &&
            package.Changes[0].Label == "Health" &&
            package.Changes[0].After == project.Civilizations.Single(civilization => civilization.Name == "England")
                .Entities.Single().Properties["unit.health"] &&
            CivilizationEditProject.Deserialize(package.ProjectJson).GamePath == "",
            "The exported installer did not preserve its reviewable changes and portable project.");
        using (var template = Assembly.GetExecutingAssembly().GetManifestResourceStream("EDMWMapDesigner.Tools.EDMWCivilizationInstaller.exe")
            ?? throw new InvalidOperationException("The self-contained civilization installer template was not embedded."))
        using (var exported = File.OpenRead(path))
        {
            Span<byte> footer = stackalloc byte[CivilizationInstallerPackageCodec.FooterSize];
            exported.Position = exported.Length - footer.Length;
            exported.ReadExactly(footer);
            var templateLength = BinaryPrimitives.ReadInt64LittleEndian(footer[16..24]);
            exported.Position = 0;
            Span<byte> header = stackalloc byte[64];
            exported.ReadExactly(header);
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(header[60..64]);
            Require(header[..2].SequenceEqual("MZ"u8) && peOffset >= 64 && peOffset < templateLength - 4,
                "The exported installer is not a native Windows executable container.");
            exported.Position = peOffset;
            Span<byte> signature = stackalloc byte[4];
            exported.ReadExactly(signature);
            Require(signature.SequenceEqual("PE\0\0"u8), "The exported installer has an invalid PE header.");
            exported.Position = 0;
            Require(HashPrefix(exported, templateLength) == Convert.ToHexString(SHA256.HashData(template)),
                "The exporter modified the standalone runtime while attaching its payload.");
            var trailerLength = (int)Math.Min(templateLength, 256 * 1024);
            var trailer = new byte[trailerLength];
            exported.Position = templateLength - trailerLength;
            exported.ReadExactly(trailer);
            var bundleNames = Encoding.ASCII.GetString(trailer);
            Require(bundleNames.Contains("System.Private.CoreLib.dll", StringComparison.Ordinal) &&
                bundleNames.Contains("System.Windows.Forms.dll", StringComparison.Ordinal) &&
                bundleNames.Contains("EDMWCivilizationInstaller.dll", StringComparison.Ordinal) &&
                bundleNames.Contains("EDMWCivilizationInstaller.runtimeconfig.json", StringComparison.Ordinal),
                "The exported EXE did not retain the bundled managed runtime, UI, and installer components.");
        }
        var damagedPath = Path.Combine(output, "tampered-export-fixture.exe");
        File.Copy(path, damagedPath, true);
        using (var damaged = new FileStream(damagedPath, FileMode.Open, FileAccess.ReadWrite))
        {
            damaged.Position = damaged.Length - 1;
            var original = damaged.ReadByte();
            damaged.Position--;
            damaged.WriteByte((byte)(original ^ 1));
        }
        ExpectInvalid(() => CivilizationInstallerPackageCodec.ReadExecutable(damagedPath),
            "An altered exported installer was accepted by the recipient's package reader.");
        ExpectInvalid(() => CivilizationInstallerBuilder.Export(project, fixture,
            Path.Combine(fixture, "Empires_DMW.exe")), "Export was allowed to overwrite the game executable.");
        ExpectInvalid(() => CivilizationInstallerBuilder.Export(project, fixture,
            Path.Combine(fixture, "Data", "unsafe-installer.exe")), "Export was allowed inside live game-data paths.");
        var final = SnapshotFiles(fixture);
        Require(before.Count == final.Count && before.All(entry => final.TryGetValue(entry.Key, out var hash) && hash == entry.Value),
            "An accepted or rejected installer export changed the game fixture.");
        checks.Add("One project-named Windows EXE retains its bundled runtime and reviewable payload; export never changes game files, and tampered installers are rejected without execution.");
    }

    private static CivilizationInstallerPackage CreateObjectPackage(string fixture,
        CivilizationGameCatalog catalog, string name, int row, string key, string value) =>
        CivilizationInstallerPackage.Create(ObjectProject(fixture, catalog, name, row, key, value), fixture);

    private static CivilizationEditProject ObjectProject(string fixture,
        CivilizationGameCatalog catalog, string name, int row, string key, string value)
    {
        var source = catalog.Items.Single(item => item.SourceId == row);
        var project = CivilizationEditProject.CreateDefault();
        project.Name = name;
        project.GamePath = fixture;
        project.Civilizations.Single(civilization => civilization.Name == "England").Entities.Add(new()
        {
            Name = source.Name, Category = source.Category, SourceObjectId = row,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value }
        });
        return project;
    }

    private static CivilizationEditProject ModelProject(string fixture,
        CivilizationGameCatalog catalog, string name, byte[] replacement)
    {
        var asset = new CivilizationModelAsset
        {
            Name = "Verification native model", OriginalFileName = "verification.cem", Format = "cem",
            MimeType = "application/octet-stream", FileLength = replacement.Length,
            Data = replacement.ToArray(), Sha256 = CivilizationInstallerPackage.Hash(replacement)
        };
        var project = ObjectProject(fixture, catalog, name, 1078, "building.modelAsset", asset.Id);
        project.ModelAssets.Add(asset);
        return project;
    }

    private static CivilizationPackageFile ChangedFile(CivilizationInstallerPackage package, string relative) =>
        package.Files.Single(file => file.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase) && file.Patches.Count > 0);

    private static byte[] ApplyPatches(byte[] original, CivilizationInstallerPackage package, string relative)
    {
        var result = original.ToArray();
        foreach (var patch in ChangedFile(package, relative).Patches) patch.Replacement.CopyTo(result, patch.Offset);
        return result;
    }

    private static IEnumerable<int> ChangedPositions(CivilizationPackagePatch patch) =>
        Enumerable.Range(0, patch.Length).Where(index => patch.Expected[index] != patch.Replacement[index])
            .Select(index => patch.Offset + index);

    private static void RequireInstalled(string fixture, params string[] ids)
    {
        var installed = CivilizationPackageManager.ReadInstalled(fixture);
        Require(installed.Select(package => package.PackageId).ToHashSet(StringComparer.Ordinal).SetEquals(ids) &&
            installed.Count == ids.Length && installed.All(package => package.Status == "Installed"),
            "The installed package inventory differs from the expected active packages.");
    }

    private static uint ReadUInt(byte[] objects, int row, int field) =>
        BinaryPrimitives.ReadUInt32LittleEndian(objects.AsSpan(ObjectOffset(row, field), 4));
    private static int ObjectOffset(int row, int field) => checked(4 + row * 0x790 + field);
    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static IReadOnlyDictionary<string, string> SnapshotFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), FileHash, StringComparer.OrdinalIgnoreCase);
    private static void RequireSameSnapshot(IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after, string message) =>
        Require(before.Count == after.Count && before.All(entry =>
            after.TryGetValue(entry.Key, out var hash) && hash == entry.Value), message);
    private static string HashPrefix(Stream stream, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (length > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
            if (read == 0) throw new InvalidDataException("The exported runtime prefix was truncated.");
            hash.AppendData(buffer, 0, read);
            length -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
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
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void WriteModelArchive(string path, (string Name, byte[] Data)[] models)
    {
        var names = models.Select(model => Encoding.ASCII.GetBytes(model.Name + "\0\0")).ToArray();
        var indexLength = names.Sum(name => 4 + name.Length + 12);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, false);
        writer.Write("rass"u8); writer.Write(1u); writer.Write(0u); writer.Write((uint)indexLength);
        var start = checked((uint)(16 + indexLength));
        for (var index = 0; index < models.Length; index++)
        {
            writer.Write((uint)names[index].Length); writer.Write(names[index]);
            writer.Write(start); writer.Write(checked(start + (uint)models[index].Data.Length - 1));
            writer.Write((uint)models[index].Data.Length);
            start = checked(start + (uint)models[index].Data.Length);
        }
        foreach (var model in models) writer.Write(model.Data);
    }
}

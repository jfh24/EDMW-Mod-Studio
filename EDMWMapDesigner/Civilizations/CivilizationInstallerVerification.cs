using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace EDMWMapDesigner.Civilizations;

/// <summary>
/// Installer acceptance checks in a new, isolated data fixture. This never
/// installs into the source game folder and never starts a process.
/// </summary>
public static class CivilizationInstallerVerification
{
    public static IReadOnlyList<string> Run(string gamePath, string outputDirectory)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (output.Equals(source, StringComparison.OrdinalIgnoreCase) || output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installer verification output must be outside the live game folder.");
        var fixture = Path.Combine(output, "installer-fixtures", Guid.NewGuid().ToString("N"));
        if (Directory.Exists(fixture)) throw new InvalidOperationException("The installer fixture must be a new directory.");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "VERIFICATION_ONLY.txt"), "Isolated fixture. No live game files were installed or changed.\n");
        string[] dependencies = ["Empires_DMW.exe", "Data\\db\\dbobjects.dat", "Data\\db\\dbphoenixcivtable.dat", "Data\\db\\dbtechtree.dat", "Data\\db\\dbgraphics.dat", "Data\\db\\dbbuttons.dat"];
        var sourceHashes = dependencies.ToDictionary(relative => relative, relative => FileHash(Path.Combine(source, relative)));
        foreach (var relative in dependencies)
        {
            var target = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, relative), target);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }
        var assertions = new List<string>();
        try
        {
            var objectsPath = Path.Combine(fixture, "Data", "db", "dbobjects.dat");
            var techPath = Path.Combine(fixture, "Data", "db", "dbtechtree.dat");
            var originalObjects = File.ReadAllBytes(objectsPath);
            var originalTechnologies = File.ReadAllBytes(techPath);
            var catalog = CivilizationGameCatalog.Load(fixture);
            var project = CivilizationEditProject.CreateDefault();
            project.GamePath = fixture;
            project.Name = "Isolated installer verification";
            var england = project.Civilizations.Single(civilization => civilization.Name == "England");
            var citizen = catalog.Items.Single(item => item.SourceId == 833);
            var unit = new CivilizationEntityDraft { Name = citizen.Name, Category = CivilizationCategory.Units, SourceObjectId = citizen.SourceId };
            england.Entities.Add(unit);
            var health = BinaryPrimitives.ReadUInt32LittleEndian(originalObjects.AsSpan(4 + 833 * 0x790 + 120, 4));
            var techId = checked((int)CivilizationObjectBindings.ReadTechnologyId(originalObjects, 833, CivilizationCategory.Units));
            var production = CivilizationTechnologyBindings.ReadRecords(originalTechnologies).Single(record => record.TechId == techId);
            unit.Properties["unit.health"] = checked(health + 17).ToString(CultureInfo.InvariantCulture);
            unit.Properties["unit.foodCost"] = checked(production.FoodCost + 9).ToString(CultureInfo.InvariantCulture);
            var expectedObjects = (byte[])originalObjects.Clone();
            CivilizationObjectBindings.WriteValue(expectedObjects, 833, unit.Category, "unit.health", unit.Properties["unit.health"]);
            var expectedTechnologies = (byte[])originalTechnologies.Clone();
            CivilizationTechnologyBindings.WriteObjectValue(expectedTechnologies, techId, unit.Category, "unit.foodCost", unit.Properties["unit.foodCost"]);

            var beforeFiles = Directory.GetFiles(fixture, "*", SearchOption.AllDirectories).Order().ToArray();
            var plan = CivilizationGameInstaller.BuildPlan(project, fixture);
            Require(plan.CanInstall, "The mapped HP/food-cost plan was blocked: " + string.Join("; ", plan.Issues));
            Require(plan.FileCount == 2 && plan.Changes.Count == 2, "The plan did not contain exactly the two intended database changes.");
            Require(beforeFiles.SequenceEqual(Directory.GetFiles(fixture, "*", SearchOption.AllDirectories).Order()), "BuildPlan wrote files into the game fixture.");
            Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(originalObjects) && File.ReadAllBytes(techPath).AsSpan().SequenceEqual(originalTechnologies), "BuildPlan changed game data.");
            Require(plan.Files.Single(file => file.RelativePath.EndsWith("dbobjects.dat", StringComparison.Ordinal)).Patched.AsSpan().SequenceEqual(expectedObjects), "HP changed bytes outside the intended object field.");
            Require(plan.Files.Single(file => file.RelativePath.EndsWith("dbtechtree.dat", StringComparison.Ordinal)).Patched.AsSpan().SequenceEqual(expectedTechnologies), "Food cost changed bytes outside the intended production field.");
            assertions.Add("BuildPlan is read-only; HP and cost patches preserve every unrelated byte.");

            var installed = CivilizationGameInstaller.Install(plan);
            Require(installed.Success, "Fixture installation failed: " + installed.Message);
            Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(expectedObjects) && File.ReadAllBytes(techPath).AsSpan().SequenceEqual(expectedTechnologies), "Installed fixture values differ from the reviewed bytes.");
            Require(installed.BackupPath is not null && installed.BackupPath.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.GetFiles(installed.BackupPath, "*.original").Length == 2, "The installation did not preserve its original files.");
            Require(!CivilizationGameInstaller.BuildPlan(project, fixture).CanInstall, "A second package bypassed the active-installation guard.");
            Require(CivilizationGameInstaller.Restore(fixture).Success, "Fixture Restore failed.");
            Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(originalObjects) && File.ReadAllBytes(techPath).AsSpan().SequenceEqual(originalTechnologies), "Restore did not recover the exact original database bytes.");
            assertions.Add("Install writes the reviewed values; backups and Restore recover exact original hashes.");

            var partialPlan = CivilizationGameInstaller.BuildPlan(project, fixture);
            using (var lockedTechnology = File.Open(techPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failedInstall = CivilizationGameInstaller.Install(partialPlan);
                Require(!failedInstall.Success, "An installation unexpectedly replaced a target held without delete sharing.");
                Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(originalObjects) && File.ReadAllBytes(techPath).AsSpan().SequenceEqual(originalTechnologies), "A partially applied installation did not roll back its first database file.");
                Require(!File.Exists(Path.Combine(fixture, ".edmw-civilization-editor", "current.json")), "A completed rollback left an active installation manifest.");
            }
            assertions.Add("A deterministic second-file lock triggers rollback of the already applied first file.");

            var civilizationsPath = Path.Combine(fixture, "Data", "db", "dbphoenixcivtable.dat");
            var originalCivilizations = File.ReadAllBytes(civilizationsPath);
            foreach (var (lower, upper) in new[] { (8, 9), (5, 6) })
            {
                var ageProject = CivilizationEditProject.CreateDefault();
                var ageOwner = ageProject.Civilizations.Single(civilization => civilization.Name == "England");
                if (lower == 5)
                {
                    // The downgrade starts from 8..9 in the isolated fixture,
                    // so writing the upper endpoint first would be invalid if
                    // the installer treated the two writes independently.
                    var laterAgeBaseline = (byte[])originalCivilizations.Clone();
                    var offset = 4 + ageOwner.SourceRowIndex!.Value * 344;
                    BinaryPrimitives.WriteInt32LittleEndian(laterAgeBaseline.AsSpan(offset + 212, 4), 8);
                    BinaryPrimitives.WriteInt32LittleEndian(laterAgeBaseline.AsSpan(offset + 216, 4), 9);
                    File.WriteAllBytes(civilizationsPath, laterAgeBaseline);
                }
                // Insert lower first for the upgrade and upper first for the
                // downgrade; both endpoint changes must be validated together.
                if (lower == 8) { ageOwner.Properties["age.initialAge"] = "8"; ageOwner.Properties["age.finalAge"] = "9"; }
                else { ageOwner.Properties["age.finalAge"] = "6"; ageOwner.Properties["age.initialAge"] = "5"; }
                var agePlan = CivilizationGameInstaller.BuildPlan(ageProject, fixture);
                Require(agePlan.CanInstall && agePlan.FileCount == 1, "A valid two-endpoint age edit was rejected: " + string.Join("; ", agePlan.Issues));
                var ageValues = CivilizationCivilizationBindings.ReadValues(agePlan.Files.Single().Patched, ageOwner.SourceRowIndex!.Value);
                Require(ageValues["age.initialAge"] == lower.ToString(CultureInfo.InvariantCulture) && ageValues["age.finalAge"] == upper.ToString(CultureInfo.InvariantCulture), "The age interval differs from the requested pair.");
                Require(CivilizationGameInstaller.Install(agePlan).Success && CivilizationGameInstaller.Restore(fixture).Success, "The two-endpoint age patch did not install and restore.");
                File.WriteAllBytes(civilizationsPath, originalCivilizations);
            }
            var conflictingAges = CivilizationEditProject.CreateDefault();
            var conflictingAgeOwner = conflictingAges.Civilizations.Single(civilization => civilization.Name == "England");
            conflictingAgeOwner.Properties["age.supportedAges"] = "5, 6, 7";
            conflictingAgeOwner.Properties["age.initialAge"] = "6";
            Require(!CivilizationGameInstaller.BuildPlan(conflictingAges, fixture).CanInstall, "Overlapping age aliases silently discarded an authored endpoint.");
            assertions.Add("Two age endpoints install together in either insertion order; disagreeing age aliases block installation.");

            var stale = CivilizationGameInstaller.BuildPlan(project, fixture);
            var external = (byte[])originalObjects.Clone();
            external[4 + 833 * 0x790 + 1800] ^= 1;
            File.WriteAllBytes(objectsPath, external);
            Require(!CivilizationGameInstaller.Install(stale).Success, "A stale installation plan overwrote changed source data.");
            Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(external) && !File.Exists(Path.Combine(fixture, ".edmw-civilization-editor", "current.json")), "Rejecting a stale plan changed files or created an active package.");
            File.WriteAllBytes(objectsPath, originalObjects);
            assertions.Add("Stale plans reject changed inputs without overwriting them.");

            installed = CivilizationGameInstaller.Install(CivilizationGameInstaller.BuildPlan(project, fixture));
            Require(installed.Success, "External-edit test installation failed: " + installed.Message);
            external = (byte[])expectedObjects.Clone();
            external[4 + 833 * 0x790 + 1800] ^= 1;
            File.WriteAllBytes(objectsPath, external);
            Require(!CivilizationGameInstaller.Restore(fixture).Success, "Restore overwrote an externally edited installed file.");
            Require(File.ReadAllBytes(objectsPath).AsSpan().SequenceEqual(external), "Failed Restore changed the external edit.");
            File.WriteAllBytes(objectsPath, expectedObjects);
            Require(CivilizationGameInstaller.Restore(fixture).Success, "Restore could not proceed once the expected installed bytes were recovered.");
            assertions.Add("Restore refuses external edits and remains usable after the conflict is resolved.");

            var unknown = project.Clone();
            unknown.Civilizations.Single(civilization => civilization.Id == england.Id).Entities.Single(entity => entity.Id == unit.Id).Properties["unit.verificationUnmapped"] = "1";
            var blocked = CivilizationGameInstaller.BuildPlan(unknown, fixture);
            Require(!blocked.CanInstall && blocked.Issues.Any(issue => issue.Contains("verificationUnmapped", StringComparison.Ordinal)), "An unmapped authored override was silently omitted.");
            var conflict = project.Clone();
            var copy = conflict.Civilizations.Single(civilization => civilization.Id == england.Id).Entities.Single(entity => entity.Id == unit.Id).Clone();
            copy.Id = Guid.NewGuid().ToString("D"); copy.Name += " conflicting copy"; copy.Properties["unit.health"] = checked(health + 18).ToString(CultureInfo.InvariantCulture);
            conflict.Civilizations.Single(civilization => civilization.Id == england.Id).Entities.Add(copy);
            Require(!CivilizationGameInstaller.BuildPlan(conflict, fixture).CanInstall, "Conflicting shared-object edits were accepted.");
            assertions.Add("Unmapped overrides and conflicting shared-record edits block the entire installation.");

            var data = new CivilizationGameData(fixture, catalog);
            var china = project.Civilizations.Single(civilization => civilization.Name == "China");
            var chineseResearch = data.GetRoster(china, CivilizationCategory.Technologies).Select(entity => entity.SourceTechnologyId).ToHashSet();
            var research = data.GetRoster(england, CivilizationCategory.Technologies).FirstOrDefault(entity => !chineseResearch.Contains(entity.SourceTechnologyId))
                ?? throw new InvalidOperationException("The verified English technology roster is empty.");
            var technologyProject = CivilizationEditProject.CreateDefault();
            technologyProject.GamePath = fixture;
            var technologyOwner = technologyProject.Civilizations.Single(civilization => civilization.Name == "England");
            technologyOwner.Entities.Add(research);
            Require(research.SourceTechnologyId is not null, "A real technology entry lost its source ID.");
            var researchValues = data.GetValues(research);
            var woodCost = uint.Parse(researchValues["technology.woodCost"], CultureInfo.InvariantCulture);
            research.Properties["technology.woodCost"] = checked(woodCost + 23).ToString(CultureInfo.InvariantCulture);
            var technologyPlan = CivilizationGameInstaller.BuildPlan(technologyProject, fixture);
            Require(technologyPlan.CanInstall && technologyPlan.FileCount == 1, "A mapped source technology edit was rejected: " + string.Join("; ", technologyPlan.Issues));
            var expectedResearch = (byte[])originalTechnologies.Clone();
            CivilizationTechnologyBindings.WriteObjectValue(expectedResearch, research.SourceTechnologyId!.Value, research.Category, "technology.woodCost", research.Properties["technology.woodCost"]);
            Require(technologyPlan.Files.Single().Patched.AsSpan().SequenceEqual(expectedResearch), "Technology editing changed unrelated database bytes.");
            Require(CivilizationGameInstaller.Install(technologyPlan).Success && File.ReadAllBytes(techPath).AsSpan().SequenceEqual(expectedResearch), "The source technology was not installed correctly.");
            Require(CivilizationGameInstaller.Restore(fixture).Success && File.ReadAllBytes(techPath).AsSpan().SequenceEqual(originalTechnologies), "Source technology Restore did not recover the original table.");
            var foreignTechnology = technologyProject.Clone();
            foreignTechnology.Civilizations.Single(civilization => civilization.Name == "England").Entities.Clear();
            foreignTechnology.Civilizations.Single(civilization => civilization.Name == "China").Entities.Add(research.Clone());
            Require(!CivilizationGameInstaller.BuildPlan(foreignTechnology, fixture).CanInstall, "An unrelated civilization accepted a source technology it does not own.");
            assertions.Add("Real technology entities install only their intended field bytes and enforce civilization ownership.");

            var sourceAssets = new CivilizationGameAssets(source);
            var targetModel = sourceAssets.FindModelName(1094) ?? throw new InvalidOperationException("The English Town Centre mesh did not resolve.");
            var alternateModel = sourceAssets.FindModelName(1095) ?? throw new InvalidOperationException("The adjacent Town Centre mesh did not resolve.");
            var stockBytes = sourceAssets.ReadModel(targetModel);
            var alternateBytes = sourceAssets.ReadModel(alternateModel);
            foreach (var (name, bytes) in new[] { (targetModel, stockBytes), (alternateModel, alternateBytes) })
            {
                var path = Path.Combine(fixture, "Data", name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
            var modelProject = CivilizationEditProject.CreateDefault();
            modelProject.GamePath = fixture;
            var modelOwner = modelProject.Civilizations.Single(civilization => civilization.Name == "England");
            var building = catalog.Items.Single(item => item.SourceId == 1078);
            var asset = new CivilizationModelAsset { Name = "Verification native CEM", OriginalFileName = "replacement.cem", Format = "cem", MimeType = "application/octet-stream", FileLength = alternateBytes.Length, Data = alternateBytes, Sha256 = Convert.ToHexString(SHA256.HashData(alternateBytes)) };
            modelProject.ModelAssets.Add(asset);
            modelOwner.Entities.Add(new CivilizationEntityDraft { Name = building.Name, Category = CivilizationCategory.Buildings, SourceObjectId = building.SourceId, Properties = new Dictionary<string, string> { ["building.modelAsset"] = asset.Id } });
            var modelPlan = CivilizationGameInstaller.BuildPlan(modelProject, fixture);
            Require(modelPlan.CanInstall && modelPlan.FileCount == 1 && modelPlan.Changes.Single().Scope.Contains("object records", StringComparison.Ordinal), "The replacement model plan did not resolve a single shared stock target: " + string.Join("; ", modelPlan.Issues));
            var targetModelPath = Path.Combine(fixture, "Data", targetModel);
            Require(CivilizationGameInstaller.Install(modelPlan).Success && File.ReadAllBytes(targetModelPath).AsSpan().SequenceEqual(alternateBytes), "CEM installation changed or misplaced the uploaded bytes.");
            Require(CivilizationGameInstaller.Restore(fixture).Success && File.ReadAllBytes(targetModelPath).AsSpan().SequenceEqual(stockBytes), "CEM Restore did not recover the stock model bytes.");
            assertions.Add("Native CEM replacements preserve uploaded bytes, review their shared scope, and restore the stock mesh.");

            File.Delete(targetModelPath);
            File.Delete(Path.Combine(fixture, "Data", alternateModel));
            WriteModelArchive(Path.Combine(fixture, "Data", "data.ssa"), [(targetModel, stockBytes), (alternateModel, alternateBytes)]);
            var looseCreationPlan = CivilizationGameInstaller.BuildPlan(modelProject, fixture);
            Require(looseCreationPlan.CanInstall && looseCreationPlan.Files.Single().Original is null, "An archived model replacement did not plan a new loose override: " + string.Join("; ", looseCreationPlan.Issues));
            Require(CivilizationGameInstaller.Install(looseCreationPlan).Success && File.ReadAllBytes(targetModelPath).AsSpan().SequenceEqual(alternateBytes), "Installing over an archived model did not create the intended loose file.");
            Require(CivilizationGameInstaller.Restore(fixture).Success && !File.Exists(targetModelPath), "Restore failed to remove a newly created loose model override.");
            Require(new CivilizationGameAssets(fixture).ReadModel(targetModel).AsSpan().SequenceEqual(stockBytes), "Deleting the loose replacement did not recover archive model precedence.");
            assertions.Add("Restore deletes newly created loose model overrides and reveals the original archive asset.");

            Require(dependencies.All(relative => FileHash(Path.Combine(fixture, relative)) == sourceHashes[relative]), "A completed fixture round trip left changed database or executable dependencies.");
            assertions.Add("All fixture dependencies and the executable end with their original hashes.");
            File.WriteAllLines(Path.Combine(output, "installer-verification.txt"), assertions.Append("Fixture: " + fixture));
            return assertions.AsReadOnly();
        }
        finally
        {
            foreach (var relative in dependencies)
                if (FileHash(Path.Combine(source, relative)) != sourceHashes[relative]) throw new InvalidOperationException("The source game file changed during installer verification: " + relative);
        }
    }

    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void WriteModelArchive(string path, (string Name, byte[] Data)[] models)
    {
        var names = models.Select(model => System.Text.Encoding.ASCII.GetBytes(model.Name + "\0\0")).ToArray();
        var indexLength = names.Sum(name => 4 + name.Length + 12);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, false);
        writer.Write("rass"u8); writer.Write(1u); writer.Write(0u); writer.Write((uint)indexLength);
        var start = checked((uint)(16 + indexLength));
        for (var index = 0; index < models.Length; index++)
        {
            writer.Write((uint)names[index].Length); writer.Write(names[index]);
            writer.Write(start); writer.Write(checked(start + (uint)models[index].Data.Length - 1)); writer.Write((uint)models[index].Data.Length);
            start = checked(start + (uint)models[index].Data.Length);
        }
        foreach (var model in models) writer.Write(model.Data);
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Controlled offline acceptance checks for civilization data and workspace behavior.</summary>
internal static class CivilizationEditorVerification
{
    public static int RunNavigation()
    {
        try
        {
            VerifyCatalogKeyboard();
            VerifyCatalogResize();
            Console.WriteLine("CIVILIZATION NAVIGATION VERIFICATION PASS: selection shortcuts and viewport shrinking retain the selected card and the browsed page.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("CIVILIZATION NAVIGATION VERIFICATION FAIL: " + error);
            return 1;
        }
    }

    public static int Run(string gamePath, string outputDirectory)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            VerifyCatalogKeyboard();
            VerifyCatalogResize();
            var objectsPath = Path.Combine(gamePath, "Data", "db", "dbobjects.dat");
            var civilizationsPath = Path.Combine(gamePath, "Data", "db", "dbphoenixcivtable.dat");
            var technologiesPath = Path.Combine(gamePath, "Data", "db", "dbtechtree.dat");
            var originalObjectsHash = Hash(objectsPath);
            var originalCivilizationsHash = Hash(civilizationsPath);
            var originalTechnologiesHash = Hash(technologiesPath);
            var catalog = CivilizationGameCatalog.Load(gamePath);
            VerifyLargeCatalog(catalog, outputDirectory);
            var gameData = new CivilizationGameData(gamePath, catalog);
            var gameAssets = new CivilizationGameAssets(gamePath);
            var importedCivilizations = CivilizationGameCatalog.ImportCivilizations(gamePath);
            Require(importedCivilizations.Count == 9 && catalog.Items.Count > 0,
                "Existing civilization identities and object catalog did not load.");
            Require(importedCivilizations.Select(civ => civ.SourceCivilizationId).Distinct().Count() == 9,
                "Civilization source identities were conflated.");
            VerifySourceRosters(gameData, catalog, importedCivilizations,
                File.ReadAllBytes(objectsPath), File.ReadAllBytes(technologiesPath),
                File.ReadAllBytes(civilizationsPath));
            CivilizationSourcesVerification.Verify(gamePath, gameData, catalog, importedCivilizations,
                Path.Combine(outputDirectory, "sources"));
            var stockModelName = gameAssets.FindModelName(1094)
                ?? throw new InvalidOperationException("The stock English Town Centre model did not resolve.");
            var stockModel = CivilizationModelReader.Read(gameAssets.ReadModel(stockModelName), stockModelName);
            Require(stockModel.Vertices.Count > 0 && stockModel.TriangleIndices.Count > 0,
                "The archive model did not contain previewable geometry.");
            foreach (var row in new[] { 919, 916 })
            {
                var modelName = gameAssets.FindModelName(row)
                    ?? throw new InvalidOperationException($"The unit model for graphics row {row} did not resolve.");
                Require(CivilizationModelReader.Read(gameAssets.ReadModel(modelName), modelName).TriangleIndices.Count > 0,
                    "A unit's explicit UDF model reference did not produce geometry.");
            }

            var project = CivilizationEditProject.CreateDefault();
            project.GamePath = gamePath;
            project.Name = "Civilization editor acceptance";
            var england = project.Civilizations.Single(civ => civ.Name == "England");
            var sourceUnit = catalog.Items.Single(item => item.SourceId == 818 &&
                item.Category == CivilizationCategory.Units);
            var unit = new CivilizationEntityDraft
            {
                Name = sourceUnit.Name,
                Category = CivilizationCategory.Units,
                SourceObjectId = sourceUnit.SourceId
            };
            england.Entities.Add(unit);
            var history = new CivilizationEditHistory(project);
            var pristine = history.Current.Serialize();
            history.Change(next => next.Civilizations.Single(civ => civ.Id == england.Id)
                .Entities.Single(entity => entity.Id == unit.Id).Properties["unit.health"] = "125");
            Require(history.IsDirty && history.CanUndo, "A stat edit was not tracked.");
            history.Undo();
            Require(!history.IsDirty && history.Current.Serialize() == pristine,
                "Undo did not restore the saved baseline.");
            history.Redo();
            var afterHealth = history.Current.Serialize();
            ExpectInvalid(() => history.Change(next => next.Civilizations.Single(civ => civ.Id == england.Id)
                .Entities.Single(entity => entity.Id == unit.Id).Properties["unit.health"] = "NaN"));
            Require(history.Current.Serialize() == afterHealth,
                "A rejected stat edit changed the document.");
            history.Change(next =>
            {
                var entity = next.Civilizations.Single(civ => civ.Id == england.Id)
                    .Entities.Single(entity => entity.Id == unit.Id);
                entity.Properties["unit.attackProfiles"] = "Primary: melee\nSecondary: ranged";
                entity.Properties["future.mapping"] = "Preserve this extension field";
                var copy = entity.Clone();
                copy.Id = Guid.NewGuid().ToString("D");
                copy.Name += " copy";
                copy.Properties["unit.health"] = "200";
                next.Civilizations.Single(civ => civ.Id == england.Id).Entities.Add(copy);
            });
            var projectPath = Path.Combine(outputDirectory, "acceptance.edmwciv");
            history.Current.Save(projectPath);
            history.MarkSaved();
            var reopened = CivilizationEditProject.Load(projectPath);
            Require(reopened.Serialize() == history.Current.Serialize() && !history.IsDirty,
                "Project save/reopen lost authored data.");
            Require(reopened.Civilizations.Single(civ => civ.Id == england.Id).Entities
                .Single(entity => entity.Id == unit.Id).Properties["unit.health"] == "125",
                "Duplicating an entry changed the original override.");
            Require(reopened.Civilizations.Single(civ => civ.Name == "China").Entities.Count == 0,
                "Civilization-specific edits leaked into another civilization.");
            ExpectInvalid(() => CivilizationEditProject.Deserialize("{\"formatVersion\":99}"));
            var invalidIdentity = reopened.Clone();
            invalidIdentity.Civilizations[1].Id = invalidIdentity.Civilizations[0].Id;
            ExpectInvalid(invalidIdentity.Validate);

            using var shell = new MainForm(verificationMode: true)
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-30000, -30000)
            };
            shell.Show();
            var view = shell.OpenCivilizationEditorForVerification();
            var viewSnapshot = view.SnapshotProject;
            var selectedCivilization = viewSnapshot.Civilizations.Single(civ => civ.Name == "England");
            Console.WriteLine("EDITOR UI: checking all native civilization and source-roster selections.");
            VerifyVirtualRosters(view, gameData, viewSnapshot);
            Console.WriteLine("EDITOR UI: native selections passed; checking field edits, undo, save and portable models.");
            view.SelectCivilization(selectedCivilization.Id);
            view.SelectCategory(CivilizationCategory.Overview);
            CheckLayout(shell, new Size(1500, 900));
            view.SelectCategory(CivilizationCategory.Units);
            var virtualUnit = view.DisplayedEntriesForVerification.Single(entity =>
                entity.SourceObjectId == sourceUnit.SourceId);
            view.SelectEntityForVerification(virtualUnit.Id);
            var baseline = view.SnapshotProject.Serialize();
            var currentHealth = gameData.GetValues(virtualUnit)["unit.health"];
            Require(view.GetFieldForVerification("unit.health") == currentHealth,
                "The virtual unit's current game health was not shown.");
            view.SetFieldForVerification("unit.health", currentHealth + ".0");
            Require(!view.IsDirty && view.SnapshotProject.Serialize() == baseline,
                "A numerically equivalent edit materialized a virtual entry or dirtied the project.");
            view.SetFieldForVerification("unit.health", "");
            Require(!view.IsDirty && view.SnapshotProject.Serialize() == baseline &&
                view.GetFieldForVerification("unit.health") == currentHealth,
                "Clearing an untouched field did not remain inherited and display its baseline.");
            view.ResetCategoryForVerification();
            Require(!view.IsDirty && view.SnapshotProject.Serialize() == baseline,
                "Resetting an untouched virtual category materialized a draft.");
            view.SelectGameModelForVerification(stockModelName);
            Require(view.SnapshotProject.ModelAssets.Count == 1 &&
                view.SnapshotProject.Civilizations.Single(civ => civ.Id == selectedCivilization.Id)
                    .Entities.Single(entity => entity.Id == virtualUnit.Id).Properties.ContainsKey("unit.modelAsset"),
                "Selecting a game model on a virtual entry did not safely materialize its replacement.");
            view.HandleShortcut(Keys.Control | Keys.Z);
            Require(!view.IsDirty && view.SnapshotProject.Serialize() == baseline,
                "Undoing a virtual game-model selection did not restore the clean baseline.");
            view.SetFieldForVerification("unit.health", "125");
            var materialized = view.SnapshotProject.Civilizations.Single(civ => civ.Id == selectedCivilization.Id)
                .Entities.Single(entity => entity.Id == virtualUnit.Id);
            Require(materialized.SourceObjectId == sourceUnit.SourceId &&
                materialized.Properties.GetValueOrDefault("unit.health") == "125",
                "A changed virtual entry did not preserve its source identity and stat override.");
            view.ResetFieldForVerification("unit.health");
            Require(!view.IsDirty && view.SnapshotProject.Serialize() == baseline &&
                view.GetFieldForVerification("unit.health") == currentHealth,
                "Resetting the last virtual-entry override left a materialized empty draft.");
            view.SetFieldForVerification("unit.health", "125");
            Require(view.HandleShortcut(Keys.Control | Keys.Z), "Civilization undo shortcut was not handled.");
            Require(!view.IsDirty && view.SnapshotProject.Serialize() == baseline &&
                view.DisplayedEntriesForVerification.Any(entity => entity.Id == virtualUnit.Id),
                "Undo did not restore the virtual source entry without losing the roster.");
            view.HandleShortcut(Keys.Control | Keys.Y);
            Require(view.SnapshotProject.Civilizations.Single(civ => civ.Id == selectedCivilization.Id)
                .Entities.Single(entity => entity.Id == virtualUnit.Id).Properties["unit.health"] == "125",
                "Redo did not restore the edited virtual source entry.");
            view.SetFieldForVerification("unit.attackRange", "5");
            ExpectInvalid(() => view.SetFieldForVerification("unit.minimumRange", "6"));
            view.SetFieldForVerification("unit.minimumRange", "2");
            var projectNameInput = Descendants(view).OfType<TextBox>()
                .Single(input => input.AccessibleName == "Civilization project name");
            projectNameInput.Text = " UI acceptance ";
            var uiProjectPath = Path.Combine(outputDirectory, "ui-acceptance.edmwciv");
            view.SaveProjectFile(uiProjectPath);
            Require(!view.IsDirty && view.ProjectName == "UI acceptance",
                "Saving a project name with surrounding whitespace left a false dirty state.");
            var uiSaved = view.SnapshotProject.Serialize();
            view.SetFieldForVerification("unit.health", "175");
            view.OpenProjectFile(uiProjectPath);
            Require(!view.IsDirty && view.SnapshotProject.Serialize() == uiSaved,
                "The UI save/reopen workflow did not restore the saved draft.");
            view.SelectCivilization(selectedCivilization.Id);
            view.SelectCategory(CivilizationCategory.Units);
            view.SelectEntityForVerification(virtualUnit.Id);
            Require(view.GetFieldForVerification("unit.health") == "125" &&
                view.SnapshotProject.Civilizations.Single(civ => civ.Id == selectedCivilization.Id)
                    .Entities.Single(entity => entity.Id == virtualUnit.Id).SourceObjectId == sourceUnit.SourceId,
                "An edited virtual source entry did not survive save/reopen.");
            var uploadPath = Path.Combine(outputDirectory, "upload-validation.obj");
            File.WriteAllText(uploadPath, "v 0 0 0\nv 1 0 0\nv 0 1 0\nv 0 0 1\nf 1 2 3\nf 1 4 2\nf 1 3 4\nf 2 4 3\n");
            view.UploadModelForVerification(uploadPath);
            var replacementProject = view.SnapshotProject;
            Require(replacementProject.ModelAssets.Count == 1 &&
                replacementProject.Civilizations.Single(civ => civ.Id == selectedCivilization.Id)
                    .Entities.Any(entity => replacementProject.GetModelAsset(entity.Properties.GetValueOrDefault("unit.modelAsset")) is not null),
                "Replacement upload did not attach embedded model bytes to the selected entry.");
            var replacementHash = replacementProject.ModelAssets.Single().Sha256;
            view.HandleShortcut(Keys.Control | Keys.Z);
            Require(view.SnapshotProject.ModelAssets.Count == 0,
                "Undo did not remove the replacement import and its reference atomically.");
            view.HandleShortcut(Keys.Control | Keys.Y);
            Require(view.SnapshotProject.ModelAssets.Single().Sha256 == replacementHash,
                "Redo did not restore embedded replacement bytes.");
            var clone = replacementProject.Clone();
            clone.ModelAssets.Single().Data[0] ^= 1;
            Require(clone.ModelAssets.Single().Data[0] != replacementProject.ModelAssets.Single().Data[0],
                "Project cloning shared mutable replacement-model bytes.");
            view.SaveProjectFile(uiProjectPath);
            File.Delete(uploadPath);
            view.OpenProjectFile(uiProjectPath);
            Require(CivilizationModelReader.Read(view.SnapshotProject.ModelAssets.Single().Data,
                view.SnapshotProject.ModelAssets.Single().OriginalFileName).TriangleIndices.Count == 12,
                "The replacement model depended on the original upload file after reopening.");
            view.SelectCivilization(selectedCivilization.Id);
            view.SelectCategory(CivilizationCategory.Units);
            view.SelectEntityForVerification(virtualUnit.Id);
            CheckLayout(shell, new Size(1500, 900));
            foreach (var category in Enum.GetValues<CivilizationCategory>())
            {
                view.SelectCategory(category);
                shell.PerformLayout();
                Application.DoEvents();
            }
            Console.WriteLine("EDITOR UI: document workflows passed; checking visual choices, model library and every category layout.");
            VerifyVisualWorkspace(shell, view, gameData, selectedCivilization.Id, virtualUnit.Id, stockModelName, outputDirectory);
            Console.WriteLine("EDITOR UI: visual workspace passed; checking retained navigation and isolated installer fixtures.");
            view.SelectCategory(CivilizationCategory.Buildings);
            view.AddCatalogEntry(catalog.Items.First(item => item.Category == CivilizationCategory.Buildings));
            CheckLayout(shell, new Size(1200, 760));
            var retained = view.SnapshotProject.Serialize();
            shell.ShowWorkspaceForVerification(mapDesigner: true);
            CheckLayout(shell, new Size(1500, 900));
            shell.ShowWorkspaceForVerification(mapDesigner: false);
            CheckLayout(shell, new Size(1500, 900));
            Require(ReferenceEquals(view, shell.OpenCivilizationEditorForVerification()) &&
                view.SnapshotProject.Serialize() == retained,
                "Workspace navigation replaced or discarded the civilization document.");
            var installerChecks = CivilizationInstallerVerification.Run(gamePath,
                Path.Combine(outputDirectory, "installer"));
            Require(installerChecks.Count > 0, "The installer acceptance suite did not run.");
            Require(Hash(objectsPath) == originalObjectsHash && Hash(civilizationsPath) == originalCivilizationsHash &&
                Hash(technologiesPath) == originalTechnologiesHash,
                "Editor acceptance checks modified the game databases.");
            Console.WriteLine($"CIVILIZATION EDITOR VERIFICATION PASS ({catalog.Items.Count} source objects; " +
                "9 civilizations with current owned/shared rosters; clean virtual selection, numeric no-op/reset, " +
                "virtual edit persistence/undo, save/reopen, invalid edit rollback, independent cloning, " +
                "all editing categories, retained workspace navigation, native archive geometry, portable replacement upload, " +
                "visual civilization/category/roster selection, named choice buttons and retained source edits, " +
                $"every category at 1200/1500/1920px; {installerChecks.Count} installer checks; game databases unchanged)");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("CIVILIZATION EDITOR VERIFICATION FAIL: " + exception);
            return 1;
        }
    }

    private static void VerifySourceRosters(CivilizationGameData gameData, CivilizationGameCatalog catalog,
        IReadOnlyList<CivilizationDraft> civilizations, byte[] objects, byte[] technologies, byte[] civilizationTable)
    {
        var records = CivilizationTechnologyBindings.ReadRecords(technologies)
            .ToDictionary(record => record.TechId);
        var allIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var civilization in civilizations)
        {
            var row = civilization.SourceRowIndex ?? throw new InvalidOperationException("A source civilization has no row.");
            var civilizationOffset = 4 + row * 344;
            var lower = BinaryPrimitives.ReadInt32LittleEndian(civilizationTable.AsSpan(civilizationOffset + 212, 4));
            var upper = BinaryPrimitives.ReadInt32LittleEndian(civilizationTable.AsSpan(civilizationOffset + 216, 4));
            var availableObjects = new[] { CivilizationCategory.Units, CivilizationCategory.Buildings }
                .SelectMany(category => gameData.GetRoster(civilization, category))
                .Select(entity => entity.SourceObjectId!.Value).ToHashSet();
            var availableBuildings = gameData.GetRoster(civilization, CivilizationCategory.Buildings)
                .Select(entity => entity.SourceObjectId!.Value).ToHashSet();
            foreach (var category in new[] { CivilizationCategory.Units, CivilizationCategory.Buildings })
            {
                var roster = gameData.GetRoster(civilization, category);
                Require(roster.Count > 0, $"{civilization.Name} has no automatic {category} roster.");
                var ids = roster.Select(entity => entity.SourceObjectId ?? -1).ToHashSet();
                Require(ids.Count == roster.Count && !ids.Contains(-1),
                    $"{civilization.Name}/{category} has duplicate or missing source object identities.");
                var owned = 0;
                foreach (var entity in roster)
                {
                    Require(entity.Category == category && entity.Properties.Count == 0 &&
                        allIdentities.Add(entity.Id), "A virtual roster entry contains overrides or reuses a project identity.");
                    var id = entity.SourceObjectId!.Value;
                    Require(catalog.Items.Any(item => item.SourceId == id && item.Category == category),
                        "A source roster includes an unavailable catalog object.");
                    var techId = CivilizationObjectBindings.ReadTechnologyId(objects, id, category);
                    Require(techId <= int.MaxValue && records.ContainsKey((int)techId),
                        "A source roster entry has no corresponding production technology.");
                    var technology = records[(int)techId];
                    Require(!technology.EditorOnly && !technology.IsEpochDefinition &&
                        technology.StartAge <= upper && technology.EndAge >= lower,
                        $"{civilization.Name}/{entity.Name} lies outside the civilization's supported ages.");
                    Require(technology.AllowsCivilizationRow(row),
                        $"{civilization.Name}/{entity.Name} is excluded by the game's civilization availability mask.");
                    if (technology.CivilizationRowIndex == row) owned++;
                    else Require(technology.ProducerObjectIds.Any(availableObjects.Contains),
                        "A source object with a shared or legacy civilization tag has no available producer.");
                }
                var expectedOwned = catalog.Items.Where(item => item.Category == category)
                    .Where(item =>
                    {
                        var techId = CivilizationObjectBindings.ReadTechnologyId(objects, item.SourceId, category);
                        return techId <= int.MaxValue && records.TryGetValue((int)techId, out var technology) &&
                            technology.CivilizationRowIndex == row && !technology.EditorOnly &&
                            !technology.IsEpochDefinition && technology.AllowsCivilizationRow(row) &&
                            technology.StartAge <= upper && technology.EndAge >= lower;
                    }).Select(item => item.SourceId).ToHashSet();
                Require(expectedOwned.IsSubsetOf(ids) && owned == expectedOwned.Count,
                    $"{civilization.Name}/{category} omitted an age-available civilization-owned entry.");
                Console.WriteLine($"ROSTER {civilization.Name}/{category}: {roster.Count} entries " +
                    $"({owned} owned, {roster.Count - owned} shared)");
            }
            var objectTechnologyIds = Enumerable.Range(0, BinaryPrimitives.ReadInt32LittleEndian(objects))
                .Select(objectRow => BinaryPrimitives.ReadInt32LittleEndian(
                    objects.AsSpan(4 + objectRow * 0x790 + 900, 4))).ToHashSet();
            var technologyRoster = gameData.GetRoster(civilization, CivilizationCategory.Technologies);
            Require(technologyRoster.Count > 0 && technologyRoster.Select(entity => entity.SourceTechnologyId)
                .Distinct().Count() == technologyRoster.Count,
                $"{civilization.Name} has an empty or duplicated automatic research roster.");
            foreach (var entity in technologyRoster)
            {
                Require(entity.Category == CivilizationCategory.Technologies && entity.Properties.Count == 0 &&
                    entity.SourceObjectId is null && entity.SourceTechnologyId is int && allIdentities.Add(entity.Id),
                    "A virtual research entry has incorrect source identity or authored overrides.");
                var technology = records[entity.SourceTechnologyId!.Value];
                Require(!technology.EditorOnly && !technology.IsEpochDefinition &&
                    technology.AllowsCivilizationRow(row) && technology.StartAge <= upper && technology.EndAge >= lower &&
                    !objectTechnologyIds.Contains(technology.TechId) &&
                    CivilizationTechnologyBindings.IsResearchRecord(technology),
                    $"{civilization.Name}/{entity.Name} is not an available research technology.");
                Require(technology.CivilizationRowIndex == row ||
                    technology.ProducerObjectIds.Any(availableBuildings.Contains),
                    "A research roster borrowed an unavailable civilization or producer.");
            }
            Console.WriteLine($"ROSTER {civilization.Name}/Technologies: {technologyRoster.Count} research entries");
        }
    }

    private static void VerifyVirtualRosters(CivilizationEditorView view, CivilizationGameData gameData,
        CivilizationEditProject baseline)
    {
        var pristine = baseline.Serialize();
        Require(!view.IsDirty && baseline.Civilizations.All(civilization => civilization.Entities.Count == 0),
            "Automatic catalog loading materialized source entries or dirtied a new project.");
        Require(view.CivilizationListNamesForVerification.SequenceEqual(baseline.Civilizations.Select(gameData.GetDisplayName)),
            "The civilization picker displays a private record/debug representation instead of localized civilization names.");
        foreach (var civilization in baseline.Civilizations)
        {
            view.SelectCivilization(civilization.Id);
            foreach (var category in new[] { CivilizationCategory.Units, CivilizationCategory.Buildings,
                CivilizationCategory.Technologies, CivilizationCategory.Abilities })
            {
                view.SelectCategory(category);
                var expected = gameData.GetRoster(civilization, category);
                var displayed = view.DisplayedEntriesForVerification;
                Require(displayed.Select(entity => entity.Id).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(expected.Select(entity => entity.Id)),
                    $"The {civilization.Name}/{category} picker did not display its current source roster.");
                Require(view.EntityListNamesForVerification.SequenceEqual(expected.Select(gameData.GetDisplayName)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)),
                    $"The {civilization.Name}/{category} picker labels are not the actual localized source names.");
                if (category == CivilizationCategory.Abilities)
                {
                    foreach (var power in displayed.Where(entity => entity.SourcePowerId is not null))
                        Require(gameData.GetValues(power).TryGetValue("available", out var availability) &&
                            bool.TryParse(availability, out var available) && available,
                            $"{civilization.Name}/{power.Name} did not expose its configured power availability.");
                    var native = displayed.FirstOrDefault(entity => entity.SourcePowerId is not null &&
                        entity.SourceTechnologyId is null);
                    if (native is not null)
                    {
                        view.SelectEntityForVerification(native.Id);
                        Require(!gameData.CanWrite(native, "available") && !view.IsFieldWritableForVerification("available") &&
                            bool.TryParse(view.GetFieldForVerification("available"), out var available) && available,
                            "A native-only civilization power's configured availability is blank or editable without a verified mapping.");
                        Require(!view.IsDirty && view.SnapshotProject.Serialize() == pristine,
                            "Selecting a native-only power authored a source override.");
                    }
                    var command = displayed.FirstOrDefault(entity => entity.SourcePowerId is null &&
                        !gameData.CanWrite(entity, "available"));
                    Require(command is not null, "The native command availability check has no representative source command.");
                    view.SelectEntityForVerification(command!.Id);
                    Require(!view.IsFieldWritableForVerification("available") &&
                        bool.TryParse(view.GetFieldForVerification("available"), out var commandAvailable) && commandAvailable &&
                        !view.IsDirty && view.SnapshotProject.Serialize() == pristine,
                        "Selecting a native command changed or exposed writable unmapped availability.");
                }
                var representative = category == CivilizationCategory.Abilities
                    ? displayed.First(entity => entity.SourcePowerId is null) : displayed.First();
                view.SelectEntityForVerification(representative.Id);
                var prefix = category switch
                {
                    CivilizationCategory.Units => "unit",
                    CivilizationCategory.Buildings => "building",
                    CivilizationCategory.Abilities => "ability",
                    _ => "technology"
                };
                var values = gameData.GetValues(representative);
                var suffixes = category is CivilizationCategory.Technologies or CivilizationCategory.Abilities
                    ? new[] { "foodCost", "woodCost", "goldCost", "stoneCost", "productionCoefficient", "internalName" }
                    : new[] { "health", "attack", "attackRange", "sight", "graphicsId", "foodCost", "woodCost", "goldCost", "stoneCost" };
                foreach (var suffix in suffixes)
                {
                    var key = prefix + "." + suffix;
                    Require(values.TryGetValue(key, out var current) && view.GetFieldForVerification(key) == current,
                        $"The {civilization.Name}/{representative.Name} editor did not load current {suffix}.");
                }
                Require(!view.IsDirty && view.SnapshotProject.Serialize() == pristine,
                    "Selecting a civilization, category, or source entry materialized an unchanged draft.");
            }
        }
    }

    private static void CheckLayout(Form form, Size size)
    {
        form.WindowState = FormWindowState.Normal;
        form.ClientSize = size;
        form.Opacity = 1;
        form.PerformLayout();
        Application.DoEvents();
        Require(form.ClientSize == size && Descendants(form).Any(control => control.Visible && control.Width > 0),
            "The selected workspace did not lay out its controls.");
    }

    private static void VerifyVisualWorkspace(Form shell, CivilizationEditorView view, CivilizationGameData gameData,
        string civilizationId, string entityId, string stockModelName, string outputDirectory)
    {
        var pristine = view.SnapshotProject.Serialize();
        var wasDirty = view.IsDirty;
        var civilizations = view.SnapshotProject.Civilizations;
        var germany = civilizations.Single(civilization => civilization.Name == "Germany");
        var england = civilizations.Single(civilization => civilization.Id == civilizationId);
        CheckLayout(shell, new Size(1500, 900));
        ClickSelectionCard(view, "Select civilization: " + gameData.GetDisplayName(germany));
        ClickSelectionCard(view, "Select civilization category: Units");
        var expected = gameData.GetRoster(germany, CivilizationCategory.Units);
        Require(view.DisplayedEntriesForVerification.Select(entity => entity.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(expected.Select(entity => entity.Id)),
            "Clicking a civilization card did not select its actual source roster.");
        var civilizationSearch = Descendants(view).OfType<TextBox>().Single(input => input.AccessibleName == "Find civilization");
        civilizationSearch.Text = gameData.GetDisplayName(germany);
        Require(view.CivilizationListNamesForVerification.SequenceEqual([gameData.GetDisplayName(germany)]),
            "Civilization search did not filter the visual selection cards.");
        civilizationSearch.Text = "";
        ClickSelectionCard(view, "Select civilization: " + gameData.GetDisplayName(england));
        var unit = view.DisplayedEntriesForVerification.Single(entity => entity.Id == entityId);
        ClickSelectionCard(view, "Select civilization roster item: " + gameData.GetDisplayName(unit));
        Require(view.GetFieldForVerification("unit.health") == "125" &&
            view.DisplayedEntriesForVerification.Any(entity => entity.Id == entityId),
            "Clicking a source roster card lost its source identity or retained health override.");
        Require(view.SnapshotProject.Serialize() == pristine && view.IsDirty == wasDirty,
            "Visual civilization/category/roster selection authored a document change.");
        VerifyPendingCardNavigation(view, gameData, england, entityId);
        VerifyModelPicker(view, stockModelName, outputDirectory);

        // Exercise a real writable native choice rather than merely checking selector colors.
        ClickSelectionCard(view, "Select civilization category: Ages");
        var lower = int.Parse(view.GetFieldForVerification("age.initialAge"), System.Globalization.CultureInfo.InvariantCulture);
        var upper = int.Parse(view.GetFieldForVerification("age.finalAge"), System.Globalization.CultureInfo.InvariantCulture);
        var nextAge = Math.Min(lower + 1, upper);
        Require(nextAge != lower && view.IsFieldWritableForVerification("age.initialAge"),
            "The choice-button acceptance check has no editable native age interval.");
        var definition = CivilizationSchema.GetFields(CivilizationCategory.Ages)
            .Single(field => field.Key == "age.initialAge");
        var selector = Descendants(view).OfType<MedievalChoiceSelector>().Single(input => input.AccessibleName == definition.Label);
        if (!selector.Visible)
            Descendants(view).OfType<Button>().Single(button => button.AccessibleName == definition.Section + " properties").PerformClick();
        Require(selector.Visible && selector.Enabled, "The writable age choice is hidden or disabled.");
        var choice = Descendants(selector).OfType<Button>().Single(button => button.Text == gameData.GetAgeName(nextAge));
        choice.PerformClick();
        var changed = view.SnapshotProject.Serialize();
        Require(changed != pristine && view.SnapshotProject.Civilizations.Single(civilization => civilization.Id == civilizationId)
                .Properties.GetValueOrDefault("age.initialAge") == nextAge.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Clicking a named age choice did not author its verified native age value.");
        Require(view.HandleShortcut(Keys.Control | Keys.Z) && view.SnapshotProject.Serialize() == pristine &&
            view.HandleShortcut(Keys.Control | Keys.Y) && view.SnapshotProject.Serialize() == changed &&
            view.HandleShortcut(Keys.Control | Keys.Z) && view.SnapshotProject.Serialize() == pristine,
            "A named native choice edit is not independently undoable/redoable.");

        // A derived boolean keeps its selected presentation while remaining read-only.
        ClickSelectionCard(view, "Select civilization category: Overview");
        var availability = Descendants(view).OfType<MedievalChoiceSelector>().Single(input =>
            input.AccessibleName == "Selectable in new games");
        Require(!availability.Enabled && !view.IsFieldWritableForVerification("overview.selectable"),
            "Changing boolean presentation enabled an unverified native availability flag.");

        var screenshots = Path.Combine(outputDirectory, "workspace-style");
        Directory.CreateDirectory(screenshots);
        var categories = Enum.GetValues<CivilizationCategory>().Select(category => category.ToString()).Append("Changes");
        foreach (var category in categories)
        {
            ClickSelectionCard(view, "Select civilization category: " + category);
            foreach (var size in new[] { new Size(1200, 760), new Size(1500, 900), new Size(1920, 1080) })
            {
                CheckLayout(shell, size);
                PerformLayouts(view);
                Application.DoEvents();
                PerformLayouts(view);
                SaveScreenshot(view, Path.Combine(screenshots, $"civilization-{category.ToLowerInvariant()}-{size.Width}.png"));
                VerifyWorkspaceControls(view, category, size.Width);
                Require(view.SnapshotProject.Serialize() == pristine && view.IsDirty == wasDirty,
                    $"Rendering {category} at {size.Width}px changed the retained civilization document.");
            }
            Console.WriteLine($"EDITOR UI: {category} choice/cards and 1200/1500/1920px layouts passed.");
        }
        ClickSelectionCard(view, "Select civilization category: Units");
        ClickSelectionCard(view, "Select civilization roster item: " + gameData.GetDisplayName(unit));
        Require(view.GetFieldForVerification("unit.health") == "125" && view.SnapshotProject.Serialize() == pristine,
            "Navigating every styled category lost the retained source entry or field override.");
    }

    private static void VerifyWorkspaceControls(CivilizationEditorView view, string category, int width)
    {
        var context = $"{category} at {width}px";
        var controls = Descendants(view).ToArray();
        Require(!controls.Any(control => control is ListBox or ListView or ComboBox),
            context + ": a civilization editor picker still uses a plain list or dropdown.");
        foreach (var name in new[] { "Civilizations", "Civilization categories", "Civilization roster" })
        {
            var container = controls.Single(control => control.AccessibleName == name);
            if (!container.Visible) continue;
            var bounds = new Rectangle(view.PointToClient(container.PointToScreen(Point.Empty)), container.Size);
            Require(container.Width >= 150 && container.Height >= 120 && view.ClientRectangle.Contains(bounds),
                context + ": a visual selection rail is clipped or has no usable viewport: " + name +
                    $" ({container.Size}, bounds {bounds} in {view.ClientRectangle}).");
            var cards = Descendants(container).OfType<Button>().Where(button =>
                button.AccessibleName?.StartsWith("Select civilization", StringComparison.Ordinal) == true).ToArray();
            foreach (var card in cards)
                Require(card.Height >= 30 && card.Width >= 140 && card.Left >= 0 &&
                    card.Right <= card.Parent!.ClientSize.Width,
                    context + ": a visual selection card is too small or extends horizontally beyond its rail: " + card.AccessibleName);
            for (var index = 1; index < cards.Length; index++)
                if (ReferenceEquals(cards[index - 1].Parent, cards[index].Parent))
                    Require(!cards[index - 1].Bounds.IntersectsWith(cards[index].Bounds),
                        context + ": visual selection cards overlap.");
        }
        foreach (var input in controls.Where(control => control.Visible && control is TextBox or NumericUpDown))
            Require(input.BackColor == MedievalTheme.Input && input.ForeColor == MedievalTheme.Text,
                context + ": a civilization property input uses system colors: " + input.AccessibleName);
        foreach (var selector in controls.OfType<MedievalChoiceSelector>().Where(selector => selector.Visible))
        {
            Require(selector.Width >= 96 && selector.Height >= 20,
                context + ": a choice selector is too small: " + selector.AccessibleName);
            foreach (var button in Descendants(selector).OfType<Button>())
                Require(selector.ClientRectangle.Contains(button.Bounds),
                    context + ": a native field choice extends beyond its selector: " + button.AccessibleName);
        }
        var form = controls.Single(control => control.AccessibleName == "Civilization editor form");
        Require(category == "Ages" || form.Width <= 722,
            context + ": the property form stretches across the workspace.");
        foreach (var grid in controls.Where(control => control.AccessibleName?.StartsWith("Civilization fields: ", StringComparison.Ordinal) == true))
            Require(category == "Ages" && grid.AccessibleName == "Civilization fields: Progression" || grid.Width <= 682,
                context + ": a property grid stretches across the workspace: " + grid.AccessibleName);
    }

    private static void VerifyPendingCardNavigation(CivilizationEditorView view, CivilizationGameData gameData,
        CivilizationDraft civilization, string entityId)
    {
        var pristine = view.SnapshotProject.Serialize();
        var germany = view.SnapshotProject.Civilizations.Single(owner => owner.Name == "Germany");
        var native = view.DisplayedEntriesForVerification.Single(entity => entity.Id == entityId);
        var health = CivilizationSchema.GetFields(CivilizationCategory.Units).Single(field => field.Key == "unit.health");
        var nextValue = (uint.Parse(view.GetFieldForVerification("unit.health"), System.Globalization.CultureInfo.InvariantCulture) + 1)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var target = view.DisplayedEntriesForVerification.First(entity => entity.Id != entityId && entity.SourceObjectId != native.SourceObjectId &&
            view.DisplayedEntriesForVerification.Count(other => gameData.GetDisplayName(other) == gameData.GetDisplayName(entity)) == 1);
        EditPendingHealth();
        var targetCard = Descendants(view).OfType<Button>().Single(button =>
            button.AccessibleName == "Select civilization roster item: " + gameData.GetDisplayName(target));
        Require(targetCard.Focus() && !targetCard.IsDisposed && targetCard.Enabled,
            "Saving a pending field disposed or disabled its destination roster card before its Click.");
        targetCard.PerformClick();
        Require(Descendants(view).OfType<TextBox>().Single(input => input.AccessibleName == "Roster entry name").Text == target.Name &&
            view.SnapshotProject.Civilizations.Single(owner => owner.Id == civilization.Id).Entities.Single(entity => entity.Id == entityId)
                .Properties.GetValueOrDefault("unit.health") == nextValue,
            "Moving from a pending health field to a roster card failed to save the field and select the actual destination.");
        Require(view.HandleShortcut(Keys.Control | Keys.Z) && view.SnapshotProject.Serialize() == pristine,
            "Undo did not restore the exact project after pending-field roster navigation.");
        ClickSelectionCard(view, "Select civilization roster item: " + gameData.GetDisplayName(native));

        EditPendingHealth();
        var civilizationCard = Descendants(view).OfType<Button>().Single(button =>
            button.AccessibleName == "Select civilization: " + gameData.GetDisplayName(germany));
        Require(civilizationCard.Focus() && !civilizationCard.IsDisposed && civilizationCard.Enabled,
            "Saving a pending field disposed or disabled its destination civilization card before its Click.");
        civilizationCard.PerformClick();
        Require(view.DisplayedEntriesForVerification.Select(entity => entity.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(gameData.GetRoster(germany, CivilizationCategory.Units).Select(entity => entity.Id)) &&
            view.SnapshotProject.Civilizations.Single(owner => owner.Id == civilization.Id).Entities.Single(entity => entity.Id == entityId)
                .Properties.GetValueOrDefault("unit.health") == nextValue,
            "Moving from a pending health field to a civilization card failed to save the field and select its source roster.");
        Require(view.HandleShortcut(Keys.Control | Keys.Z) && view.SnapshotProject.Serialize() == pristine,
            "Undo did not restore the exact project after pending-field civilization navigation.");
        ClickSelectionCard(view, "Select civilization: " + gameData.GetDisplayName(civilization));
        ClickSelectionCard(view, "Select civilization roster item: " + gameData.GetDisplayName(native));

        void EditPendingHealth()
        {
            var input = Descendants(view).OfType<TextBox>().Single(control => control.AccessibleName == health.Label);
            if (!input.Visible)
                Descendants(view).OfType<Button>().Single(button => button.AccessibleName == health.Section + " properties").PerformClick();
            Require(input.Focus(), "The pending navigation check cannot focus its actual health field.");
            input.Text = nextValue;
        }
    }

    private static void VerifyModelPicker(CivilizationEditorView view, string stockModelName, string outputDirectory)
    {
        var pristine = view.SnapshotProject.Serialize();
        var previewPanel = Descendants(view).OfType<CivilizationModelPreviewPanel>().Single();
        using var picker = previewPanel.CreateGameModelPickerForVerification();
        picker.ShowInTaskbar = false;
        picker.StartPosition = FormStartPosition.Manual;
        picker.Location = new Point(-30000, -30000);
        picker.Show();
        Application.DoEvents();
        var controls = Descendants(picker).ToArray();
        var search = controls.OfType<TextBox>().Single(control => control.Name == "GameModelsSearch");
        var cards = controls.OfType<FlowLayoutPanel>().Single(control => control.Name == "GameModelCards");
        var next = controls.OfType<Button>().Single(control => control.Name == "GameModelsNextPage");
        var previous = controls.OfType<Button>().Single(control => control.Name == "GameModelsPreviousPage");
        var use = controls.OfType<Button>().Single(control => control.Name == "GameModelsUseSelection");
        search.Text = "";
        var firstPage = cards.Controls.OfType<Button>().Select(card => (string)card.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Require(firstPage.Count == 18 && next.Enabled, "The game model library no longer bounds a page to 18 native model cards.");
        next.PerformClick();
        var secondPage = cards.Controls.OfType<Button>().Select(card => (string)card.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Require(secondPage.Count is > 0 and <= 18 && !secondPage.Overlaps(firstPage) && previous.Enabled,
            "The game model page action did not show a separate bounded model page.");
        previous.PerformClick();
        Require(cards.Controls.OfType<Button>().Select(card => (string)card.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(firstPage),
            "Returning to the previous model page lost its native model paths.");
        search.Text = stockModelName;
        var model = cards.Controls.OfType<Button>().Single(card => Equals(card.Tag, stockModelName));
        model.PerformClick();
        var preview = controls.OfType<CivilizationModelViewport>().Single(control => control.Name == "GameModelPickerPreview");
        Require(use.Enabled && preview.Mesh is { TriangleIndices.Count: > 0 } &&
            preview.RenderFrame(200, 150).Statistics.RasterizedTriangles > 0,
            "Clicking a native model card failed to validate and render its geometry before enabling Use.");
        var screenshots = Path.Combine(outputDirectory, "workspace-style");
        Directory.CreateDirectory(screenshots);
        foreach (var size in new[] { new Size(760, 470), new Size(1000, 620), new Size(1280, 760) })
        {
            CheckLayout(picker, size);
            PerformLayouts(picker);
            Application.DoEvents();
            SaveScreenshot(picker, Path.Combine(screenshots, $"game-model-library-{size.Width}.png"));
            foreach (var control in new Control[] { search, cards, next, previous, use, preview })
            {
                var bounds = new Rectangle(picker.PointToClient(control.PointToScreen(Point.Empty)), control.Size);
                Require(control.Visible && control.Width > 0 && control.Height > 0 && picker.ClientRectangle.Contains(bounds),
                    $"The model library clips its {control.Name} control at {size.Width}px.");
            }
        }
        use.PerformClick();
        Require(picker.DialogResult == DialogResult.OK && view.SnapshotProject.Serialize() == pristine,
            "The model picker failed its explicit Use action or changed the project while browsing.");
        picker.Close();
    }

    private static void VerifyCatalogKeyboard()
    {
        var names = Enumerable.Range(0, 20).Select(index => $"Fixture{index:00}").ToArray();
        using var host = new Form
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), ClientSize = new Size(300, 400),
            BackColor = MedievalTheme.Sidebar, ForeColor = MedievalTheme.Text
        };
        using var catalog = CivilizationEditorView.CreateCatalogSelectionForVerification(names);
        catalog.Dock = DockStyle.Fill;
        host.Controls.Add(catalog);
        host.Show();
        Application.DoEvents();
        var cards = Descendants(catalog).OfType<Button>().Where(button =>
            button.AccessibleName?.StartsWith("Select catalog item: ", StringComparison.Ordinal) == true)
            .OrderBy(card => card.Text, StringComparer.Ordinal).ToArray();
        Require(cards.Length == names.Length, "Catalog keyboard acceptance did not create all production selection cards.");
        cards[0].PerformClick();
        Key(cards[0], Keys.Down);
        Require(CivilizationEditorView.CatalogSelectionForVerification(catalog).SequenceEqual([names[1]]),
            "Down in the catalog moved focus without selecting the next actual item.");
        Key(cards[1], Keys.Control | Keys.Down);
        Require(CivilizationEditorView.CatalogSelectionForVerification(catalog).SequenceEqual([names[1]]) && cards[2].Focused,
            "Ctrl+Down changed the catalog's chosen set or failed to move its focus.");
        Key(cards[2], Keys.Shift | Keys.End);
        Require(CivilizationEditorView.CatalogSelectionForVerification(catalog).ToHashSet(StringComparer.Ordinal)
                .SetEquals(names.Skip(1)),
            "Shift+End did not select the catalog range from the last plain selection anchor.");
        Key(cards[19], Keys.Space);
        Require(CivilizationEditorView.CatalogSelectionForVerification(catalog).ToHashSet(StringComparer.Ordinal)
                .SetEquals(names.Skip(1).Take(18)), "Space did not toggle the focused catalog item.");
        Key(cards[19], Keys.Control | Keys.A);
        Require(CivilizationEditorView.CatalogSelectionForVerification(catalog).ToHashSet(StringComparer.Ordinal).SetEquals(names),
            "Ctrl+A did not select the entire catalog's actual chosen set.");
        Key(cards[19], Keys.Home);
        Require(CivilizationEditorView.CatalogSelectionForVerification(catalog).SequenceEqual([names[0]]),
            "Home did not restore a single catalog selection at the first item.");
        Key(cards[0], Keys.PageDown);
        Require(CivilizationEditorView.CatalogSelectionForVerification(catalog).Count == 1 &&
            CivilizationEditorView.CatalogSelectionForVerification(catalog).Single() != names[0],
            "PageDown did not select a visible-page offset in the catalog.");
        Console.WriteLine("EDITOR UI: actual catalog cards accept navigation keys; focus, range, toggle, Select All and paging update the chosen set.");

        static void Key(Button button, Keys key)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            if ((key & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)
                Require((bool)button.GetType().GetMethod("IsInputKey", flags)!.Invoke(button, [key])!,
                    "A catalog navigation key is intercepted by dialog navigation before the card sees it: " + key);
            var args = new KeyEventArgs(key);
            button.GetType().GetMethod("OnKeyDown", flags)!.Invoke(button, [args]);
            Require(args.Handled && args.SuppressKeyPress, "An actual catalog card did not consume its selection shortcut: " + key);
            Application.DoEvents();
        }
    }

    private static void VerifyCatalogResize()
    {
        foreach (var count in new[] { 20, 130 })
        {
            var names = Enumerable.Range(0, count).Select(index => $"Viewport{index:000}").ToArray();
            using var host = new Form
            {
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(-30000, -30000), ClientSize = new Size(300, 600),
                BackColor = MedievalTheme.Sidebar, ForeColor = MedievalTheme.Text
            };
            using var catalog = CivilizationEditorView.CreateCatalogSelectionForVerification(names);
            catalog.Dock = DockStyle.Fill;
            host.Controls.Add(catalog);
            host.Show();
            Application.DoEvents();
            var first = Cards().First();
            first.PerformClick();
            if (count == 20)
            {
                Key(first, Keys.End);
                var selected = Cards().Last();
                Require(CivilizationEditorView.CatalogSelectionForVerification(catalog).SequenceEqual([names[^1]]),
                    "End failed to choose the production rail's last item before resizing.");
                host.ClientSize = new Size(300, 180);
                PerformLayouts(host);
                Application.DoEvents();
                Require(!selected.IsDisposed && selected.Parent!.ClientRectangle.Contains(selected.Bounds) &&
                    CivilizationEditorView.CatalogSelectionForVerification(catalog).SequenceEqual([names[^1]]),
                    "Shrinking a navigation rail hides its selected card or changes the chosen item: " +
                    $"card={selected.Bounds}, viewport={selected.Parent?.ClientRectangle}.");
            }
            else
            {
                var next = Descendants(catalog).OfType<Button>().Single(button => button.AccessibleName == "Next items page");
                next.PerformClick();
                Application.DoEvents();
                Require(Cards().First().Text == names[64], "Next failed to open the second production catalog page.");
                host.ClientSize = new Size(300, 180);
                PerformLayouts(host);
                Application.DoEvents();
                Require(Cards().First().Text == names[64] &&
                    CivilizationEditorView.CatalogSelectionForVerification(catalog).SequenceEqual([names[0]]),
                    "Shrinking a catalog while browsing another page changes that page or its existing selection.");
                var previous = Descendants(catalog).OfType<Button>().Single(button => button.AccessibleName == "Previous items page");
                previous.PerformClick();
                Application.DoEvents();
                Require(Cards().First().Text == names[0] && Cards().First().Parent!.ClientRectangle.Contains(Cards().First().Bounds),
                    "Returning to the selected catalog page fails to reveal its chosen card.");
            }
            host.Close();

            Button[] Cards() => Descendants(catalog).OfType<Button>().Where(button =>
                button.AccessibleName?.StartsWith("Select catalog item: ", StringComparison.Ordinal) == true).ToArray();
        }
        Console.WriteLine("EDITOR UI: a shrinking rail retains its visible selected card; browsing another catalog page stays independent of selection.");

        static void Key(Button button, Keys key)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            Require((bool)button.GetType().GetMethod("IsInputKey", flags)!.Invoke(button, [key])!,
                "A navigation rail intercepts End before its actual card can select the final item.");
            var args = new KeyEventArgs(key);
            button.GetType().GetMethod("OnKeyDown", flags)!.Invoke(button, [args]);
            Require(args.Handled && args.SuppressKeyPress, "The production rail failed to handle End selection.");
            Application.DoEvents();
        }
    }

    private static void VerifyLargeCatalog(CivilizationGameCatalog catalog, string outputDirectory)
    {
        var names = catalog.Items.Where(item => item.Category == CivilizationCategory.Units)
            .Select(item => $"{item.SourceId}: {item.Name}").ToArray();
        Require(names.Length > 64, "The native unit catalog does not exercise a bounded page.");
        using var host = new Form
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), ClientSize = new Size(340, 600),
            BackColor = MedievalTheme.Sidebar, ForeColor = MedievalTheme.Text
        };
        using var picker = CivilizationEditorView.CreateCatalogSelectionForVerification(names);
        picker.Dock = DockStyle.Fill;
        host.Controls.Add(picker);
        host.Show();
        Application.DoEvents();
        host.AutoScaleMode = AutoScaleMode.None;
        host.Scale(new SizeF(2f, 2f));
        host.ClientSize = new Size(680, 1200);
        PerformLayouts(host);
        var all = new HashSet<string>(StringComparer.Ordinal);
        var next = Descendants(picker).OfType<Button>().Single(button => button.AccessibleName == "Next items page");
        var previous = Descendants(picker).OfType<Button>().Single(button => button.AccessibleName == "Previous items page");
        var pages = 0;
        while (true)
        {
            var cards = Cards();
            Require(cards.Length is > 0 and <= 64, "A native unit catalog page exceeds its 64-card bound.");
            foreach (var card in cards)
            {
                Require(card.Height >= 128 && card.Top >= 0 && card.Right <= card.Parent!.ClientSize.Width,
                    "A paged native unit card extends outside its usable horizontal range.");
                Require(all.Add(card.Text), "The native unit catalog repeats an item on another page.");
            }
            for (var index = 1; index < cards.Length; index++)
                Require(!cards[index - 1].Bounds.IntersectsWith(cards[index].Bounds),
                    "Native unit catalog cards overlap at 200% equivalent layout scale.");
            pages++;
            foreach (var pager in new[] { previous, next })
            {
                var bounds = new Rectangle(picker.PointToClient(pager.PointToScreen(Point.Empty)), pager.Size);
                Require(pager.Visible && pager.Height >= 48 && picker.ClientRectangle.Contains(bounds),
                    "A scaled native unit catalog pager is clipped or too small.");
            }
            if (!next.Enabled) break;
            next.PerformClick();
            Application.DoEvents();
            Require(pages <= (names.Length + 63) / 64, "The native unit catalog does not reach its last page.");
        }
        Require(all.SetEquals(names) && pages == (names.Length + 63) / 64,
            "Bounded catalog pages lose native unit identities or omit a page.");
        while (previous.Enabled) { previous.PerformClick(); Application.DoEvents(); }
        Cards().First().PerformClick();
        Key(Cards().First(), Keys.Control | Keys.End);
        var last = Cards().Last();
        Require(last.Text == names[^1] && CivilizationEditorView.CatalogSelectionForVerification(picker).SequenceEqual([names[0]]),
            "Ctrl+End did not open the last catalog page while preserving the first chosen item.");
        Key(last, Keys.Space);
        Require(CivilizationEditorView.CatalogSelectionForVerification(picker).ToHashSet(StringComparer.Ordinal)
                .SetEquals([names[0], names[^1]]), "Catalog selection does not persist across its first and last pages.");
        Key(Cards().Last(), Keys.Home);
        Key(Cards().First(), Keys.Shift | Keys.End);
        Require(CivilizationEditorView.CatalogSelectionForVerification(picker).ToHashSet(StringComparer.Ordinal).SetEquals(names),
            "Shift+End omits native catalog selections outside the rendered page.");
        Key(Cards().Last(), Keys.Control | Keys.A);
        Require(CivilizationEditorView.CatalogSelectionForVerification(picker).ToHashSet(StringComparer.Ordinal).SetEquals(names),
            "Ctrl+A no longer selects the entire native catalog across all pages.");
        Directory.CreateDirectory(Path.Combine(outputDirectory, "workspace-style"));
        SaveScreenshot(picker, Path.Combine(outputDirectory, "workspace-style", "native-unit-catalog-paged-2x.png"));
        Console.WriteLine($"EDITOR UI: {names.Length} native units across {pages} bounded pages retain selection, range, Select All and input routing without card overlap at 200% equivalent scale.");

        Button[] Cards() => Descendants(picker).OfType<Button>().Where(button =>
            button.AccessibleName?.StartsWith("Select catalog item: ", StringComparison.Ordinal) == true).ToArray();

        static void Key(Button button, Keys key)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            if ((key & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End)
                Require((bool)button.GetType().GetMethod("IsInputKey", flags)!.Invoke(button, [key])!,
                    "A native catalog navigation shortcut is intercepted before the actual card receives it.");
            var args = new KeyEventArgs(key);
            button.GetType().GetMethod("OnKeyDown", flags)!.Invoke(button, [args]);
            Require(args.Handled && args.SuppressKeyPress, "A native catalog card failed to handle a selection shortcut.");
            Application.DoEvents();
        }
    }

    private static void ClickSelectionCard(Control view, string name)
    {
        var card = Descendants(view).OfType<Button>().Single(button => button.AccessibleName == name);
        Require(card.Visible && card.Enabled, "A visual civilization editor choice is unavailable: " + name);
        card.PerformClick();
        Application.DoEvents();
    }

    private static void PerformLayouts(Control root)
    {
        root.PerformLayout();
        foreach (Control child in root.Controls) PerformLayouts(child);
    }

    private static void SaveScreenshot(Control control, string path)
    {
        var size = control is Form form ? form.ClientSize : control.Size;
        using var image = new Bitmap(size.Width, size.Height);
        control.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        image.Save(path);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid project data was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

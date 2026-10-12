using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

/// <summary>Production Ages UI and real LAN/package checks against isolated copies.</summary>
internal static class AgeProgressionVerification
{
    private const string TechnologyFile = @"Data\db\dbtechtree.dat";
    private static readonly string[] RequiredFiles =
    [
        "Empires_DMW.exe", @"Data\db\dbobjects.dat", TechnologyFile,
        @"Data\db\dbphoenixcivtable.dat", @"Data\db\dbgraphics.dat",
        @"Data\db\dbbuttons.dat", @"Data\db\dbweapontohit.dat",
        @"Data\db\dbfamily.dat", @"Data\db\dbeffects.dat", @"Data\db\dbevents.dat"
    ];

    public static int Run(string gamePath, string outputDirectory)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Require(!output.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                !output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Age progression verification must run outside the source game folder.");
            Directory.CreateDirectory(output);
            foreach (var relative in RequiredFiles) originals.Add(relative, Hash(Path.Combine(source, relative)));
            var sender = CopyFixture(source, output, "sender");
            var recipient = CopyFixture(source, output, "recipient");
            var checks = new List<string>();
            var project = VerifyEditor(sender, output, checks);
            VerifyInvalidSources(sender, project, checks);
            VerifyForgedLan(sender, project, checks);
            VerifyLan(sender, recipient, project, checks).GetAwaiter().GetResult();
            VerifyLegacyPackages(sender, checks);
            VerifyAgeEditingPackages(sender, checks);
            VerifyCombinedTechnologyAges(sender, checks);
            VerifyMembershipPackages(sender, checks);
            VerifyExpandedAgeSupport(sender, checks);
            foreach (var relative in RequiredFiles)
            {
                Require(Hash(Path.Combine(sender, relative)) == originals[relative],
                    "Verification left sender game bytes changed: " + relative);
                Require(Hash(Path.Combine(recipient, relative)) == originals[relative],
                    "Verification left recipient game bytes changed: " + relative);
            }
            checks.Add("Every source, sender and recipient game-file hash is unchanged after tracked export removal.");
            File.WriteAllLines(Path.Combine(output, "age-progression-verification.txt"),
                checks.Append("Sender fixture: " + sender).Append("Recipient fixture: " + recipient));
            Console.WriteLine("AGE PROGRESSION VERIFICATION PASS: " + string.Join(" ", checks));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("AGE PROGRESSION VERIFICATION FAIL: " + error);
            return 1;
        }
        finally
        {
            foreach (var original in originals)
                Require(Hash(Path.Combine(source, original.Key)) == original.Value,
                    "Age progression verification modified a source game file: " + original.Key);
        }
    }

    public static int RunLayout(string gamePath, string outputDirectory)
    {
        try
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
            var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
            Require(!output.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                !output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Progression layout verification must write captures outside the source game folder.");
            Directory.CreateDirectory(output);
            var originals = RequiredFiles.ToDictionary(relative => relative, relative => Hash(Path.Combine(source, relative)),
                StringComparer.OrdinalIgnoreCase);
            try
            {
                var catalog = CivilizationGameCatalog.Load(source);
                Console.WriteLine("LAYOUT CATALOG COUNTS: " + string.Join(", ", catalog.Items.GroupBy(item => item.Category)
                    .OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Count()}")));
                var gameData = new CivilizationGameData(source, catalog);
                var france = CivilizationGameCatalog.ImportCivilizations(source).Single(civilization => civilization.Name == "France");
                var candidates = gameData.GetAgeProgressionCandidates(france);
                Console.WriteLine($"LAYOUT PROGRESSION COUNTS: France included={candidates.Count(item => item.IncludedByDefault)}, " +
                    $"add pool={candidates.Count(item => !item.IncludedByDefault)}, total={candidates.Count}.");
                var checks = new List<string>();
                VerifyResponsiveLayout(gameData, france, output, checks, onlyLargestAddCatalog: true);
                File.WriteAllLines(Path.Combine(output, "age-progression-layout-verification.txt"), checks);
                Console.WriteLine("AGE PROGRESSION LAYOUT VERIFICATION PASS: " + string.Join(" ", checks));
                return 0;
            }
            finally
            {
                foreach (var original in originals)
                    Require(Hash(Path.Combine(source, original.Key)) == original.Value,
                        "Progression layout verification modified a source game file: " + original.Key);
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("AGE PROGRESSION LAYOUT VERIFICATION FAIL: " + error);
            return 1;
        }
    }

    private static CivilizationEditProject VerifyEditor(string fixture, string output, List<string> checks)
    {
        using var host = new Form
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), Size = new Size(1500, 1000),
            BackColor = MedievalTheme.Shell, Font = MedievalTheme.UiFont()
        };
        using var view = new CivilizationEditorView(() => fixture);
        host.Controls.Add(view);
        host.Show();
        var civilization = view.SnapshotProject.Civilizations.Single(civ => civ.Name == "England");
        view.SelectCivilization(civilization.Id);
        view.SelectCategory(CivilizationCategory.Ages);
        var pristine = view.SnapshotProject.Serialize();
        var gameData = new CivilizationGameData(fixture, CivilizationGameCatalog.Load(fixture));
        var panel = view.ProgressionPanelForVerification;
        Require(!view.IsDirty && panel.ItemsForVerification.Count > 0 &&
            panel.ItemsForVerification.All(item => item.Entity.Category != CivilizationCategory.Ages),
            "The inline progression view is missing verified source items or creates Ages drafts.");
        Require(panel.FindForm() == host && Descendants(view).OfType<AgeProgressionPanel>().Single() == panel,
            "The progression editor is not integrated in the Ages workspace.");
        VerifyThemeAndLayout(panel);
        var native = panel.IncludedItemsForVerification.First(item => item.Entity.Category == CivilizationCategory.Units);
        panel.SelectItemForVerification(native.Entity.Id);
        panel.SetFieldForVerification("unit.initialAge", panel.GetFieldForVerification("unit.initialAge") + ".0");
        Require(panel.ApplyForVerification() && !view.IsDirty && view.SnapshotProject.Serialize() == pristine,
            "Applying an unchanged inline progression field created an override.");

        panel.SetFieldForVerification("unit.finalAge",
            (int.Parse(panel.GetFieldForVerification("unit.finalAge"), CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture));
        panel.CancelPendingForVerification();
        Require(!view.IsDirty && view.SnapshotProject.Serialize() == pristine && !panel.HasPendingChanges,
            "Discarding inline field edits changed the document.");

        var foodInput = Descendants(panel).OfType<NumericUpDown>().Single(input => input.AccessibleName == "Food cost");
        Require(foodInput.Focus(), "The production progression cost field cannot receive keyboard focus.");
        var originalFood = panel.GetFieldForVerification("unit.foodCost");
        panel.SetFieldForVerification("unit.foodCost",
            (uint.Parse(originalFood, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture));
        var discard = Descendants(panel).OfType<Button>().Single(button =>
            button.AccessibleName == "Discard unapplied progression field edits");
        var discardScroll = Descendants(panel).OfType<Panel>().Single(control => control.AccessibleName == "Progression item details");
        discardScroll.ScrollControlIntoView(discard);
        var discardEnabled = discard.Enabled;
        string? discardedCommitStack = null;
        void CaptureDiscardCommit(CivilizationDraft _) => discardedCommitStack = Environment.StackTrace;
        panel.CivilizationChanged += CaptureDiscardCommit;
        var discardFocused = discardEnabled && discard.Focus();
        panel.CivilizationChanged -= CaptureDiscardCommit;
        Require(discardFocused, "The production Discard action is not reachable from a pending field: " +
            $"Enabled={discardEnabled}, Visible={discard.Visible}, CanFocus={discard.CanFocus}, Pending={panel.HasPendingChanges}, " +
            $"value={panel.GetFieldForVerification("unit.foodCost")}, original={originalFood}." +
            (discardedCommitStack is null ? "" : "\nUnexpected commit while focusing Discard:\n" + discardedCommitStack));
        discard.PerformClick();
        Require(!view.IsDirty && !panel.HasPendingChanges && view.SnapshotProject.Serialize() == pristine &&
            panel.GetFieldForVerification("unit.foodCost") == originalFood,
            "Moving keyboard focus from a changed field to Discard committed the edit before it could be discarded.");

        foodInput = Descendants(panel).OfType<NumericUpDown>().Single(input => input.AccessibleName == "Food cost");
        Require(foodInput.Focus(), "The normal focus-exit check cannot focus its cost field.");
        panel.SetFieldForVerification("unit.foodCost",
            (uint.Parse(originalFood, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture));
        var woodInput = Descendants(panel).OfType<NumericUpDown>().Single(input => input.AccessibleName == "Wood cost");
        Require(woodInput.Focus() && !panel.HasPendingChanges && view.IsDirty && view.SnapshotProject.Serialize() != pristine,
            "Leaving a changed cost for an ordinary field stopped saving the native project edit automatically.");
        Require(view.HandleShortcut(Keys.Control | Keys.Z) && !view.IsDirty && view.SnapshotProject.Serialize() == pristine,
            "Undo did not restore the pristine document after an ordinary focus-exit save.");
        panel = view.ProgressionPanelForVerification;
        panel.SelectItemForVerification(native.Entity.Id);

        panel.SetFieldForVerification("unit.initialAge", "10");
        panel.SetFieldForVerification("unit.finalAge", "5");
        Require(!panel.ApplyForVerification(),
            "Inline progression accepted reversed availability bounds.");
        panel.CancelPendingForVerification();
        Require(view.SnapshotProject.Serialize() == pristine, "Rejecting reversed availability bounds changed the document.");
        panel.SetFieldForVerification("unit.foodCost", "1.5");
        Require(!panel.ApplyForVerification(),
            "Inline progression accepted a fractional native integer cost.");
        panel.CancelPendingForVerification();
        Require(view.SnapshotProject.Serialize() == pristine, "Rejecting a fractional progression cost changed the document.");
        var invalidEffect = FirstEffect(panel, gameData);
        panel.SelectItemForVerification(invalidEffect.Item.Entity.Id);
        panel.SetFieldForVerification(invalidEffect.Field.Key, "NaN");
        Require(!panel.ApplyForVerification(),
            "Inline progression accepted a non-finite effect.");
        panel.CancelPendingForVerification();
        Require(view.SnapshotProject.Serialize() == pristine, "Rejecting a non-finite progression effect changed the document.");
        panel.SelectItemForVerification(invalidEffect.Item.Entity.Id);
        var effectGrid = Descendants(panel).Single(control =>
            control.AccessibleName == "Progression fields: " + invalidEffect.Field.Section);
        var effectInput = Descendants(effectGrid).OfType<TextBox>().Single(input => input.AccessibleName == invalidEffect.Field.Label);
        Require(effectInput.Focus(), "The production effect field cannot receive keyboard focus.");
        var originalEffect = panel.GetFieldForVerification(invalidEffect.Field.Key);
        panel.SetFieldForVerification(invalidEffect.Field.Key, "NaN");
        Require(!panel.ApplyForVerification(), "The invalid effect reset check did not start with a rejected native scalar.");
        var resetEffect = Descendants(effectGrid).OfType<Button>().Single(button =>
            button.AccessibleName == $"Reset {invalidEffect.Field.Label} to game value");
        var effectScroll = Descendants(panel).OfType<Panel>().Single(control => control.AccessibleName == "Progression item details");
        effectScroll.ScrollControlIntoView(resetEffect);
        Require(resetEffect.Focus(), "The production effect Reset action cannot receive keyboard focus after invalid input.");
        resetEffect.PerformClick();
        Require(!view.IsDirty && !panel.HasPendingChanges && view.SnapshotProject.Serialize() == pristine &&
            panel.GetFieldForVerification(invalidEffect.Field.Key) == originalEffect,
            "Resetting a focused invalid effect did not restore its game value without authoring another field.");

        var originalObjects = File.ReadAllBytes(Path.Combine(fixture, @"Data\db\dbobjects.dat"));
        var added = panel.ItemsForVerification.FirstOrDefault(item => item.Entity.Category == CivilizationCategory.Units &&
            !panel.IsIncludedForVerification(item.Entity.Id) && item.AddedProducerIds.Count > 0 &&
            !Enumerable.Range(0, CivilizationObjectDetails.ActionTechnologyCount).Any(slot =>
                BinaryPrimitives.ReadInt32LittleEndian(originalObjects.AsSpan(4 + item.AddedProducerIds[0] * 1936 +
                    CivilizationObjectDetails.ActionTechnologyIdsOffset + slot * 4, 4)) == item.TechnologyId));
        Require(added is not null, "The progression editor offers no existing foreign unit to add.");
        panel.AddItemForVerification(added!.Entity.Id);
        panel.RemoveItemForVerification(added.Entity.Id);
        Require(view.SnapshotProject.Serialize() == pristine && !view.IsDirty,
            "Adding then removing an unchanged foreign progression item left redundant overrides.");
        panel.AddItemForVerification(added!.Entity.Id);
        var afterAdd = view.SnapshotProject.Serialize();
        Require(panel.IsIncludedForVerification(added.Entity.Id) && view.IsDirty &&
            view.SnapshotProject.Civilizations.Single(civ => civ.Id == civilization.Id).Entities
                .Any(entity => entity.Properties.GetValueOrDefault(added.MembershipKey) == "true"),
            "Adding a progression item did not author its real civilization membership.");
        Require(view.HandleShortcut(Keys.Control | Keys.Z) && view.SnapshotProject.Serialize() == pristine && !view.IsDirty,
            "Undo did not remove the progression addition.");
        Require(view.HandleShortcut(Keys.Control | Keys.Y) && view.SnapshotProject.Serialize() == afterAdd,
            "Redo did not restore the progression addition.");
        panel = view.ProgressionPanelForVerification;
        panel.RemoveItemForVerification(native.Entity.Id);
        var afterRemove = view.SnapshotProject.Serialize();
        Require(!panel.IsIncludedForVerification(native.Entity.Id) &&
            view.SnapshotProject.Civilizations.Single(civ => civ.Id == civilization.Id).Entities
                .Any(entity => entity.Properties.GetValueOrDefault(native.MembershipKey) == "false"),
            "Removing a stock progression item did not author its exclusion.");
        Require(view.HandleShortcut(Keys.Control | Keys.Z) && view.SnapshotProject.Serialize() == afterAdd,
            "Undo did not restore the removed stock progression item.");
        Require(view.HandleShortcut(Keys.Control | Keys.Y) && view.SnapshotProject.Serialize() == afterRemove,
            "Redo did not restore the stock progression exclusion.");
        panel = view.ProgressionPanelForVerification;
        panel.RestoreItemForVerification(native.Entity.Id);
        Require(panel.IsIncludedForVerification(native.Entity.Id), "Restoring original membership failed.");
        Require(view.HandleShortcut(Keys.Control | Keys.Z) && view.SnapshotProject.Serialize() == afterRemove,
            "Undo of restored original membership failed.");
        panel = view.ProgressionPanelForVerification;

        var changedIdentities = new HashSet<string>(StringComparer.Ordinal) { added.Entity.Id, native.Entity.Id };
        foreach (var category in new[] { CivilizationCategory.Units, CivilizationCategory.Buildings, CivilizationCategory.Technologies })
        {
            var item = panel.IncludedItemsForVerification.First(entry => entry.Entity.Category == category && entry.Kind != "Age advance" &&
                entry.Entity.Id != added.Entity.Id);
            var before = view.SnapshotProject.Serialize();
            panel.SelectItemForVerification(item.Entity.Id);
            var prefix = Prefix(category);
            var lower = int.Parse(panel.GetFieldForVerification(prefix + ".initialAge"), CultureInfo.InvariantCulture);
            var upper = int.Parse(panel.GetFieldForVerification(prefix + ".finalAge"), CultureInfo.InvariantCulture);
            panel.SetFieldForVerification(prefix + ".initialAge", (lower + 1).ToString(CultureInfo.InvariantCulture));
            panel.SetFieldForVerification(prefix + ".finalAge", (upper == 14 ? 14 : upper + 1).ToString(CultureInfo.InvariantCulture));
            Require(panel.ApplyForVerification(), "A valid inline progression interval was rejected.");
            var after = view.SnapshotProject.Serialize();
            Require(view.HandleShortcut(Keys.Control | Keys.Z) && view.SnapshotProject.Serialize() == before,
                "Undo did not restore both progression interval endpoints together.");
            Require(view.HandleShortcut(Keys.Control | Keys.Y) && view.SnapshotProject.Serialize() == after,
                "Redo did not restore both progression interval endpoints together.");
            panel = view.ProgressionPanelForVerification;
            changedIdentities.Add(item.Entity.Id);
        }
        var advance = panel.IncludedItemsForVerification.FirstOrDefault(item => item.Kind == "Age advance");
        Require(advance is not null, "England's age advance is missing from inline progression.");
        panel.SelectItemForVerification(advance!.Entity.Id);
        var cost = uint.Parse(panel.GetFieldForVerification("technology.goldCost"), CultureInfo.InvariantCulture);
        panel.SetFieldForVerification("technology.goldCost", checked(cost + 1).ToString(CultureInfo.InvariantCulture));
        Require(panel.ApplyForVerification(), "An age advance cost edit failed.");
        changedIdentities.Add(advance.Entity.Id);
        var effect = FirstEffect(panel, gameData);
        panel.SelectItemForVerification(effect.Item.Entity.Id);
        var scalar = float.Parse(panel.GetFieldForVerification(effect.Field.Key), CultureInfo.InvariantCulture);
        panel.SetFieldForVerification(effect.Field.Key, (scalar + 0.125f).ToString("G9", CultureInfo.InvariantCulture));
        Require(panel.ApplyForVerification(), "An inline native effect edit failed.");
        changedIdentities.Add(effect.Item.Entity.Id);
        var changed = view.SnapshotProject;
        Require(changed.Civilizations.Single(civ => civ.Id == civilization.Id).Entities.Select(entity => entity.Id)
                .ToHashSet(StringComparer.Ordinal).SetEquals(changedIdentities) &&
            changed.Civilizations.Where(civ => civ.Id != civilization.Id).All(civ => civ.Entities.Count == 0),
            "Inline progression authored unrelated entries or changed another civilization.");

        VerifyThemeAndLayout(panel);
        using (var screenshot = new Bitmap(view.Width, view.Height))
        {
            view.DrawToBitmap(screenshot, new Rectangle(Point.Empty, screenshot.Size));
            screenshot.Save(Path.Combine(output, "age-progression-inline.png"));
        }
        var saved = Path.Combine(output, "age-progression.edmwciv");
        view.SaveProjectFile(saved);
        view.OpenProjectFile(saved);
        Require(!view.IsDirty && view.SnapshotProject.Serialize() == changed.Serialize(),
            "Save/reopen lost progression membership, source identities or edited values.");
        view.SelectCivilization(civilization.Id);
        view.SelectCategory(CivilizationCategory.Ages);
        panel = view.ProgressionPanelForVerification;
        Require(panel.IsIncludedForVerification(added.Entity.Id) && !panel.IsIncludedForVerification(native.Entity.Id),
            "Reopened inline progression lost its addition or removal.");
        panel.SelectItemForVerification(added.Entity.Id);
        var addedCost = uint.Parse(panel.GetFieldForVerification("unit.foodCost"), CultureInfo.InvariantCulture);
        panel.SetFieldForVerification("unit.foodCost", checked(addedCost + 3).ToString(CultureInfo.InvariantCulture));
        Require(panel.ApplyForVerification(), "An added foreign item's retained field edit failed.");
        panel.RemoveItemForVerification(added.Entity.Id);
        var retainedProject = view.SnapshotProject;
        var retained = retainedProject.Civilizations.Single(civ => civ.Id == civilization.Id).Entities.Single(entity => entity.Id == added.Entity.Id);
        Require(retained.Properties.GetValueOrDefault("unit.progressionIncluded") == "false" &&
            !retained.Properties.ContainsKey("unit.progressionProducers") && retained.Properties.ContainsKey("unit.foodCost") &&
            CivilizationGameInstaller.BuildPlan(retainedProject, fixture, forExport: true).CanInstall,
            "Removing an edited foreign item left a producer addition or made its retained shared field edits unexportable.");
        panel.RestoreItemForVerification(added.Entity.Id);
        Require(view.SnapshotProject.Serialize() == retainedProject.Serialize() &&
            CivilizationGameInstaller.BuildPlan(view.SnapshotProject, fixture, forExport: true).CanInstall,
            "Restoring an edited foreign item's original exclusion erased its authorization or retained shared edits.");
        Require(view.HandleShortcut(Keys.Control | Keys.Z) && view.HandleShortcut(Keys.Control | Keys.Z) &&
            view.SnapshotProject.Serialize() == changed.Serialize(), "Undo did not restore the exact project after removing an edited foreign item.");
        VerifyNamedAgeFilters(view, gameData, checks);
        var france = view.SnapshotProject.Civilizations.Single(civ => civ.Name == "France");
        VerifyResponsiveLayout(gameData, france, output, checks);
        view.SelectCivilization(france.Id);
        view.SelectCategory(CivilizationCategory.Ages);
        view.ProgressionPanelForVerification.SetAgeFilterForVerification(8);
        PerformLayouts(view);
        Application.DoEvents();
        SaveScreenshot(view, Path.Combine(output, "age-progression-main-clean.png"));
        VerifyExistingDraftAndReset(view, changed, civilization.Id, output);
        checks.Add("Production Ages embeds a themed progression list and editable detail view; actual additions, stock exclusions and restore are independently undoable/redoable, with no progression popup.");
        checks.Add("Inline edits cover unit/building/research bounds, age advance costs and native effects; discard/no-op stay clean, invalid values reject, and save/reopen preserves membership and values.");
        checks.Add("Editing an existing source draft preserves its custom identity, name and unrelated properties; resetting its progression fields preserves membership and other authored fields.");
        checks.Add("Add then Remove of an unchanged foreign item returns to the pristine project; removing an edited foreign item clears producer additions while preserving valid shared edits.");
        return changed;
    }

    private static (CivilizationAgeProgressionItem Item, CivilizationFieldDefinition Field) FirstEffect(
        AgeProgressionPanel panel, CivilizationGameData gameData)
    {
        var effect = panel.IncludedItemsForVerification.SelectMany(item => gameData.GetEffectFields(item.Entity)
            .Where(field => gameData.CanWrite(item.Entity, field.Key))
            .Select(field => (Item: item, Field: field))).FirstOrDefault();
        Require(effect.Item is not null, "The fixture exposes no editable scalar progression effect.");
        return effect;
    }

    private static void VerifyExistingDraftAndReset(CivilizationEditorView view,
        CivilizationEditProject original, string civilizationId, string output)
    {
        var renamed = original.Clone();
        var civilization = renamed.Civilizations.Single(civ => civ.Id == civilizationId);
        foreach (var entity in civilization.Entities)
        {
            entity.Id = Guid.NewGuid().ToString("D");
            entity.Name += " custom draft";
            entity.Properties["future.progression-verification"] = "Preserve unrelated authored content";
        }
        var path = Path.Combine(output, "existing-progression-drafts.edmwciv");
        renamed.Save(path);
        view.OpenProjectFile(path);
        view.SelectCivilization(civilizationId);
        view.SelectCategory(CivilizationCategory.Ages);
        var existing = civilization.Entities.First(entity => entity.Category == CivilizationCategory.Units &&
            entity.Properties.ContainsKey("unit.finalAge"));
        var panel = view.ProgressionPanelForVerification;
        var source = panel.ItemsForVerification.Single(item => item.Entity.Category == CivilizationCategory.Units &&
            item.Entity.SourceObjectId == existing.SourceObjectId);
        panel.SelectItemForVerification(source.Entity.Id);
        var current = decimal.Parse(panel.GetFieldForVerification("unit.finalAge"), CultureInfo.InvariantCulture);
        panel.SetFieldForVerification("unit.finalAge", (current + 1).ToString(CultureInfo.InvariantCulture));
        Require(panel.ApplyForVerification(), "An existing progression source draft could not be edited.");
        var edited = view.SnapshotProject.Civilizations.Single(civ => civ.Id == civilizationId);
        Require(edited.Entities.Count == civilization.Entities.Count &&
            edited.Entities.Select(entity => entity.Id).ToHashSet(StringComparer.Ordinal).SetEquals(civilization.Entities.Select(entity => entity.Id)) &&
            edited.Entities.Single(entity => entity.Id == existing.Id).Name == existing.Name &&
            edited.Entities.All(entity => entity.Properties.GetValueOrDefault("future.progression-verification") == "Preserve unrelated authored content"),
            "Editing progression duplicated an existing source, changed its identity/name or erased unrelated fields.");
        panel.ResetItemForVerification();
        var restored = view.SnapshotProject.Civilizations.Single(civ => civ.Id == civilizationId).Entities.Single(entity => entity.Id == existing.Id);
        Require(!restored.Properties.ContainsKey("unit.initialAge") && !restored.Properties.ContainsKey("unit.finalAge") &&
            restored.Name == existing.Name && restored.Properties.GetValueOrDefault("future.progression-verification") == "Preserve unrelated authored content",
            "Progression reset did not remove age overrides or erased unrelated draft fields.");
    }

    private static void VerifyNamedAgeFilters(CivilizationEditorView view, CivilizationGameData gameData, List<string> checks)
    {
        Require(gameData.GetAgeName(8) == "WWI" && gameData.GetAgeName(9) == "WWII" && gameData.GetAgeName(14) == "No upper limit",
            "Known native epoch IDs do not have the requested game age names.");
        var pristine = view.SnapshotProject.Serialize();
        var owner = view.SnapshotProject.Civilizations.Single(civ => civ.Name == "Germany");
        view.SelectCivilization(owner.Id);
        view.SelectCategory(CivilizationCategory.Ages);
        var panel = view.ProgressionPanelForVerification;
        foreach (var age in new[] { 8, 9 })
        {
            panel.SetAgeFilterForVerification(age);
            var expected = panel.IncludedItemsForVerification.Where(item =>
            {
                var values = gameData.GetValues(item.Entity);
                var prefix = Prefix(item.Entity.Category);
                return int.Parse(values[prefix + ".initialAge"], CultureInfo.InvariantCulture) <= age &&
                    int.Parse(values[prefix + ".finalAge"], CultureInfo.InvariantCulture) >= age;
            }).Select(item => item.Entity.Id).ToHashSet(StringComparer.Ordinal);
            Require(expected.Count > 0 && panel.FullFilteredItemsForVerification.Select(item => item.Entity.Id).ToHashSet(StringComparer.Ordinal).SetEquals(expected),
                "The named progression age filter does not match native availability: " + age);
            Require(panel.VisibleAgeLabelsForVerification.All(label => !label.Any(char.IsDigit)),
                "A progression age column exposes raw numeric age IDs.");
        }
        panel.SetAgeFilterForVerification(null);
        var moving = panel.IncludedItemsForVerification.First(item => item.Entity.Category == CivilizationCategory.Units &&
            gameData.GetValues(item.Entity).GetValueOrDefault("unit.initialAge") == "8" &&
            int.Parse(gameData.GetValues(item.Entity)["unit.finalAge"], CultureInfo.InvariantCulture) >= 9);
        panel.SelectItemForVerification(moving.Entity.Id);
        Require(Descendants(panel).OfType<MedievalChoiceSelector>().Any(combo => combo.Items.Cast<object>().OfType<CivilizationAgeName>()
            .Any(age => age.Id == 8 && age.Name == "WWI")), "Inline availability edits are missing named age choices.");
        panel.SetAgeFilterForVerification(8);
        panel.SetFieldForVerification("unit.initialAge", "9");
        Require(panel.ApplyForVerification() && panel.FullFilteredItemsForVerification.All(item => item.Entity.Id != moving.Entity.Id),
            "An edited progression interval did not update the active age filter.");
        Require(view.HandleShortcut(Keys.Control | Keys.Z) && view.SnapshotProject.Serialize() == pristine,
            "Undo of a filtered age edit failed to restore the original project.");
        panel = view.ProgressionPanelForVerification;
        panel.SetAgeFilterForVerification(8);
        Require(panel.FullFilteredItemsForVerification.Any(item => item.Entity.Id == moving.Entity.Id),
            "Undo did not restore the item to the WWI filter.");
        checks.Add("Named age dropdowns and filters show WWI/WWII and No upper limit; filtered lists match native intervals, react immediately to edits and restore through Undo.");
    }

    private static void VerifyInvalidSources(string fixture, CivilizationEditProject project, List<string> checks)
    {
        var original = Snapshot(fixture);
        var invalid = project.Clone();
        var owner = invalid.Civilizations.Single(civ => civ.Name == "England");
        var unit = owner.Entities.First(entity => entity.Category == CivilizationCategory.Units && entity.Properties.ContainsKey("unit.finalAge"));
        var gameData = new CivilizationGameData(fixture, CivilizationGameCatalog.Load(fixture));
        var other = invalid.Civilizations.First(civ => civ.Id != owner.Id &&
            !gameData.GetRoster(civ, CivilizationCategory.Units).Any(entity => entity.SourceObjectId == unit.SourceObjectId));
        owner.Entities.Remove(unit);
        other.Entities.Add(unit);
        Require(!CivilizationGameInstaller.BuildPlan(invalid, fixture, forExport: true).CanInstall,
            "A progression source moved to a different civilization survived export ownership review.");
        ExpectInvalid(() => CivilizationInstallerPackage.Create(invalid, fixture),
            "A wrong-civilization progression source was packaged.");
        var reversed = project.Clone();
        var entity = reversed.Civilizations.Single(civ => civ.Name == "England").Entities.First(item => item.Category == CivilizationCategory.Units && item.Properties.ContainsKey("unit.finalAge"));
        entity.Properties["unit.initialAge"] = "10";
        entity.Properties["unit.finalAge"] = "5";
        ExpectInvalid(() => CivilizationInstallerPackage.Create(reversed, fixture),
            "A reversed progression interval survived project/export validation.");
        RequireSame(original, Snapshot(fixture), "Rejecting invalid progression exports wrote fixture files or metadata.");
        checks.Add("Export rejects reversed bounds and source entries belonging to another civilization before any game or package metadata write.");
    }

    private static async Task VerifyLan(string senderFixture, string recipientFixture, CivilizationEditProject project, List<string> checks)
    {
        var senderBefore = Snapshot(senderFixture);
        var recipientBefore = Snapshot(recipientFixture);
        var outgoing = CivilizationLanLocalExport.Prepare(project, senderFixture);
        RequireSame(senderBefore, Snapshot(senderFixture), "Preparing progression LAN export wrote sender files.");
        var originalTables = outgoing.Files.Where(file => file.Patches.Count > 0 || file.WholeFile && CivilizationInstallerPackage.IsDatabasePath(file.RelativePath))
            .ToDictionary(file => file.RelativePath, file => File.ReadAllBytes(Path.Combine(senderFixture, file.RelativePath)), StringComparer.OrdinalIgnoreCase);
        var technologyFile = outgoing.Files.Single(file => file.RelativePath == TechnologyFile);
        var originalTechnologyRecords = CivilizationTechnologyBindings.ReadRecords(originalTables[TechnologyFile]);
        var plannedTechnologyBytes = technologyFile.WholeFile ? technologyFile.Replacement! : originalTables[TechnologyFile].ToArray();
        if (!technologyFile.WholeFile)
            foreach (var patch in technologyFile.Patches) patch.Replacement.CopyTo(plannedTechnologyBytes, patch.Offset);
        var plannedTechnologyRecords = CivilizationTechnologyBindings.ReadRecords(plannedTechnologyBytes);
        Require(plannedTechnologyRecords.Any(record => originalTechnologyRecords.Single(original => original.TechId == record.TechId) is { } original &&
            (record.StartAge != original.StartAge || record.EndAge != original.EndAge)),
            "The progression export contained no native availability endpoint patches.");
        Require(technologyFile.WholeFile && plannedTechnologyBytes.Length > originalTables[TechnologyFile].Length &&
            plannedTechnologyRecords.Any(record => record.ProducerObjectIds.Count > originalTechnologyRecords.Single(original => original.TechId == record.TechId).ProducerObjectIds.Count),
            "Adding the foreign unit did not append a real civilization producer reference.");
        var owner = project.Civilizations.Single(civilization => civilization.Name == "England");
        foreach (var record in plannedTechnologyRecords)
        {
            var original = originalTechnologyRecords.Single(candidate => candidate.TechId == record.TechId);
            Require(Enumerable.Range(3, 9).Where(row => row != owner.SourceRowIndex).All(row => record.AllowsCivilizationRow(row) == original.AllowsCivilizationRow(row)),
                "Progression changed another civilization's membership mask: " + record.TechId);
            Require(record.ProducerObjectIds.Take(original.ProducerObjectIds.Count).SequenceEqual(original.ProducerObjectIds) &&
                record.AdditionalTechnologyIds.SequenceEqual(original.AdditionalTechnologyIds),
                "Progression changed original producer references or unrelated technology vectors.");
            foreach (var row in Enumerable.Range(3, 9).Where(row => row != owner.SourceRowIndex))
            {
                var originalButtonOffset = originalTables[TechnologyFile][original.Offset + 312] != 0 ? original.Offset + 276 : original.Offset + 324 + (row - 1) * 8;
                var plannedButtonOffset = plannedTechnologyBytes[record.Offset + 312] != 0 ? record.Offset + 276 : record.Offset + 324 + (row - 1) * 8;
                var originalButton = BinaryPrimitives.ReadUInt32LittleEndian(originalTables[TechnologyFile].AsSpan(originalButtonOffset, 4));
                var firstButton = BinaryPrimitives.ReadUInt32LittleEndian(plannedTechnologyBytes.AsSpan(plannedButtonOffset, 4));
                var secondButton = plannedTechnologyBytes[record.Offset + 312] != 0 ? firstButton :
                    BinaryPrimitives.ReadUInt32LittleEndian(plannedTechnologyBytes.AsSpan(plannedButtonOffset + 4, 4));
                Require(firstButton == originalButton && secondButton == (originalTables[TechnologyFile][original.Offset + 312] != 0 ? originalButton :
                    BinaryPrimitives.ReadUInt32LittleEndian(originalTables[TechnologyFile].AsSpan(originalButtonOffset + 4, 4))),
                    "Progression changed another civilization's native menu button positions: " + record.TechId);
            }
        }
        var nativeAddition = owner.Entities.First(entity => entity.Category == CivilizationCategory.Units &&
            entity.Properties.GetValueOrDefault("unit.progressionIncluded") == "true");
        var addedTechnologyId = CivilizationObjectBindings.ReadTechnologyId(originalTables[@"Data\db\dbobjects.dat"], nativeAddition.SourceObjectId!.Value, CivilizationCategory.Units);
        var addedRecord = plannedTechnologyRecords.Single(record => record.TechId == addedTechnologyId);
        var producerId = CivilizationTechnologyBindings.ParseProgressionProducerIds(nativeAddition.Properties["unit.progressionProducers"]).First();
        var plannedObjects = outgoing.Files.Single(file => CivilizationInstallerPackage.IsObjectPath(file.RelativePath));
        Require(plannedObjects.WholeFile, "Adding a foreign unit did not own its native producer action table.");
        var catalog = CivilizationGameCatalog.Load(senderFixture);
        var producerCategory = catalog.Items.Single(item => item.SourceId == producerId).Category;
        var originalActions = CivilizationObjectDetails.ReadActionTechnologyIds(originalTables[@"Data\db\dbobjects.dat"], producerId, producerCategory);
        var plannedActions = CivilizationObjectDetails.ReadActionTechnologyIds(plannedObjects.Replacement!, producerId, producerCategory);
        Require(!originalActions.Contains((uint)addedTechnologyId) && plannedActions.Contains((uint)addedTechnologyId) &&
            plannedActions.Where(id => id != (uint)addedTechnologyId).SequenceEqual(originalActions),
            "The foreign unit is missing from its chosen producer's native action menu or changed existing menu actions.");
        var originalActionCount = BinaryPrimitives.ReadUInt32LittleEndian(originalTables[@"Data\db\dbobjects.dat"].AsSpan(4 + producerId * 1936 + CivilizationObjectDetails.ActionTechnologyCountOffset, 4));
        var plannedActionCount = BinaryPrimitives.ReadUInt32LittleEndian(plannedObjects.Replacement!.AsSpan(4 + producerId * 1936 + CivilizationObjectDetails.ActionTechnologyCountOffset, 4));
        Require(plannedActionCount == originalActionCount + 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(plannedObjects.Replacement.AsSpan(4 + producerId * 1936 + CivilizationObjectDetails.ActionTechnologyIdsOffset + checked((int)originalActionCount) * 4, 4)) == addedTechnologyId &&
            plannedObjects.Replacement.AsSpan(4 + producerId * 1936 + CivilizationObjectDetails.ActionTechnologyIdsOffset, checked((int)originalActionCount) * 4)
                .SequenceEqual(originalTables[@"Data\db\dbobjects.dat"].AsSpan(4 + producerId * 1936 + CivilizationObjectDetails.ActionTechnologyIdsOffset, checked((int)originalActionCount) * 4)),
            "The native menu did not increment its active count and append at the original prefix boundary.");
        var addedButtonOffset = plannedTechnologyBytes[addedRecord.Offset + 312] != 0 ? addedRecord.Offset + 276 :
            addedRecord.Offset + 324 + (owner.SourceRowIndex!.Value - 1) * 8;
        Require(BinaryPrimitives.ReadUInt32LittleEndian(plannedTechnologyBytes.AsSpan(addedButtonOffset, 4)) is >= 1 and <= 41,
            "The added foreign unit has no playable native HUD menu button.");
        var civilizations = File.ReadAllBytes(Path.Combine(senderFixture, @"Data\db\dbphoenixcivtable.dat"));
        foreach (var other in project.Civilizations.Where(civ => civ.Id != owner.Id))
        {
            var row = other.SourceRowIndex!.Value;
            var values = CivilizationCivilizationBindings.ReadValues(civilizations, row);
            var lower = int.Parse(values["age.initialAge"], CultureInfo.InvariantCulture);
            var upper = int.Parse(values["age.finalAge"], CultureInfo.InvariantCulture);
            foreach (var category in new[] { CivilizationCategory.Units, CivilizationCategory.Buildings })
                Require(CivilizationTechnologyBindings.GetCivilizationObjectIds(originalTables[@"Data\db\dbobjects.dat"], originalTechnologyRecords, row, category, lower, upper)
                    .ToHashSet().SetEquals(CivilizationTechnologyBindings.GetCivilizationObjectIds(plannedObjects.Replacement!, plannedTechnologyRecords, row, category, lower, upper)),
                    "Progression changed another civilization's native production roster: " + other.Name);
            Require(CivilizationTechnologyBindings.GetCivilizationTechnologyRecords(originalTables[@"Data\db\dbobjects.dat"], originalTechnologyRecords, row, lower, upper).Select(record => record.TechId).ToHashSet()
                .SetEquals(CivilizationTechnologyBindings.GetCivilizationTechnologyRecords(plannedObjects.Replacement!, plannedTechnologyRecords, row, lower, upper).Select(record => record.TechId)),
                "Progression changed another civilization's native research roster: " + other.Name);
        }
        var local = CivilizationLanLocalExport.Install(outgoing, senderFixture);
        Require(local.Status == CivilizationLanStatus.Installed, "The progression export did not load for its sender: " + local.Message);
        RequireExactPatchedTables(senderFixture, outgoing, originalTables);
        var installedGameData = new CivilizationGameData(senderFixture, CivilizationGameCatalog.Load(senderFixture));
        var addedUnit = owner.Entities.First(entity => entity.Category == CivilizationCategory.Units &&
            entity.Properties.GetValueOrDefault("unit.progressionIncluded") == "true");
        var removedUnit = owner.Entities.First(entity => entity.Category == CivilizationCategory.Units &&
            entity.Properties.GetValueOrDefault("unit.progressionIncluded") == "false");
        Require(installedGameData.GetRoster(owner, CivilizationCategory.Units).Any(entity => entity.SourceObjectId == addedUnit.SourceObjectId) &&
            !installedGameData.GetRoster(owner, CivilizationCategory.Units).Any(entity => entity.SourceObjectId == removedUnit.SourceObjectId),
            "Installed native progression does not expose the added foreign unit or still includes the removed stock unit.");
        CivilizationInstallerPackage? reviewed = null;
        using var receiver = new CivilizationLanTransfer((offer, _) =>
        {
            var review = CivilizationLanPackageReview.Prepare(offer.Package, recipientFixture);
            if (review.Package is null || review.Issues.Count > 0)
                return Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Failed, string.Join("; ", review.Issues)));
            reviewed = review.Package;
            var installed = CivilizationPackageManager.Install(review.Package, recipientFixture);
            return Task.FromResult(new CivilizationLanDecision(installed.Success ? CivilizationLanStatus.Installed : CivilizationLanStatus.Failed, installed.Message));
        }, "Progression fixture recipient", port: 0, discoveryPort: 0, bindAddress: IPAddress.Loopback);
        using var sender = new CivilizationLanTransfer((_, _) => Task.FromResult(new CivilizationLanDecision(CivilizationLanStatus.Denied)),
            "Progression fixture sender", port: 0, discoveryPort: 0, bindAddress: IPAddress.Loopback);
        receiver.Start();
        var result = await sender.SendAsync(outgoing, new("Fixture recipient", "127.0.0.1", receiver.Port))
            .WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Require(result.Status == CivilizationLanStatus.Installed && reviewed is not null,
            "The real progression LAN transfer/review/install failed: " + result.Message);
        RequireExactPatchedTables(recipientFixture, reviewed!, originalTables);
        Require(CivilizationPackageManager.ReadInstalled(senderFixture).Count == 1 && CivilizationPackageManager.ReadInstalled(recipientFixture).Count == 1,
            "LAN progression installations were not tracked for both sender and recipient.");
        Require(CivilizationPackageManager.Uninstall(senderFixture, outgoing.PackageId).Success &&
            CivilizationPackageManager.Uninstall(recipientFixture, reviewed!.PackageId).Success,
            "The tracked progression export could not be removed.");
        foreach (var table in originalTables)
        {
            Require(File.ReadAllBytes(Path.Combine(senderFixture, table.Key)).AsSpan().SequenceEqual(table.Value) &&
                File.ReadAllBytes(Path.Combine(recipientFixture, table.Key)).AsSpan().SequenceEqual(table.Value),
                "Removing progression export did not restore the native table exactly: " + table.Key);
        }
        Require(senderBefore.Where(entry => entry.Key.StartsWith("file:", StringComparison.Ordinal)).All(entry =>
            Snapshot(senderFixture).GetValueOrDefault(entry.Key) == entry.Value) &&
            recipientBefore.Where(entry => entry.Key.StartsWith("file:", StringComparison.Ordinal)).All(entry =>
            Snapshot(recipientFixture).GetValueOrDefault(entry.Key) == entry.Value),
            "The LAN round trip changed a pre-existing fixture file outside its patches.");
        checks.Add("A real foreign unit gains the selected civilization's verified producer and native membership; stock removal changes that civilization only. Real loopback LAN rebuild/review installs exact planned bytes for sender and recipient, and tracked uninstall restores both originals.");
    }

    private static void VerifyForgedLan(string fixture, CivilizationEditProject project, List<string> checks)
    {
        var before = Snapshot(fixture);
        var package = CivilizationInstallerPackage.Create(project, fixture);
        var encoded = CivilizationInstallerPackageCodec.Encode(package);
        var forged = CivilizationInstallerPackageCodec.Decode(encoded);
        var technology = forged.Files.Single(file => file.RelativePath == TechnologyFile);
        Require(technology.WholeFile, "Foreign progression export did not retain complete technology-table ownership.");
        var record = CivilizationTechnologyBindings.ReadRecords(technology.Replacement!).First();
        technology.Replacement![record.Offset + 312] = technology.Replacement[record.Offset + 312] == 0 ? (byte)1 : (byte)0;
        Require(CivilizationLanPackageReview.Prepare(forged, fixture).Package is null,
            "LAN review accepted a forged universal civilization mask.");
        forged = CivilizationInstallerPackageCodec.Decode(encoded);
        technology = forged.Files.Single(file => file.RelativePath == TechnologyFile);
        technology.Replacement![record.Offset + 315] = 2;
        Require(CivilizationLanPackageReview.Prepare(forged, fixture).Package is null,
            "LAN review accepted an invalid civilization mask Boolean.");
        forged = CivilizationInstallerPackageCodec.Decode(encoded);
        var wrong = CivilizationEditProject.Deserialize(forged.ProjectJson);
        var owner = wrong.Civilizations.Single(civ => civ.Name == "England");
        var other = wrong.Civilizations.First(civ => civ.Id != owner.Id);
        other.Entities.AddRange(owner.Entities);
        owner.Entities.Clear();
        forged.ProjectJson = wrong.Serialize();
        Require(CivilizationLanPackageReview.Prepare(forged, fixture).Package is null,
            "LAN review accepted progression moved to the wrong civilization.");
        forged = CivilizationInstallerPackageCodec.Decode(encoded);
        var impossible = CivilizationEditProject.Deserialize(forged.ProjectJson);
        var addition = impossible.Civilizations.SelectMany(civ => civ.Entities).First(entity =>
            entity.Properties.GetValueOrDefault("unit.progressionIncluded") == "true");
        addition.Properties["unit.progressionProducers"] = int.MaxValue.ToString(CultureInfo.InvariantCulture);
        forged.ProjectJson = impossible.Serialize();
        Require(CivilizationLanPackageReview.Prepare(forged, fixture).Package is null,
            "LAN review accepted an impossible foreign-unit producer reference.");
        RequireSame(before, Snapshot(fixture), "Rejecting forged LAN progression wrote game files or tracking metadata.");
        checks.Add("Read-only recipient review rejects forged universal/invalid masks, wrong civilization ownership and impossible foreign producers before any game or tracking write.");
    }

    private static void VerifyMembershipPackages(string fixture, List<string> checks)
    {
        var path = Path.Combine(fixture, TechnologyFile);
        var original = File.ReadAllBytes(path);
        var gameData = new CivilizationGameData(fixture, CivilizationGameCatalog.Load(fixture));
        var owner = CivilizationEditProject.CreateDefault().Civilizations.Single(civ => civ.Name == "England");
        var source = gameData.GetRoster(owner, CivilizationCategory.Technologies).First();
        var record = CivilizationTechnologyBindings.ReadRecords(original).Single(item => item.TechId == source.SourceTechnologyId);
        var baseline = original.ToArray();
        baseline[record.Offset + 312] = 1;
        File.WriteAllBytes(path, baseline);
        try
        {
            var removal = EndpointPackage(fixture, source, "Universal stock progression removal", "technology.progressionIncluded", "false");
            var table = removal.Files.Single(file => file.RelativePath == TechnologyFile);
            Require(table.WholeFile || table.Patches.Count(patch => patch.Length == 1 &&
                CivilizationInstallerPackage.ProgressionMaskOffsets.Contains(patch.Offset - record.Offset)) == 10,
                "A universal mask expansion did not atomically own every civilization flag.");
            Require(CivilizationPackageManager.Install(removal, fixture).Success, "Universal stock progression removal failed to install.");
            var installed = File.ReadAllBytes(path);
            var removed = CivilizationTechnologyBindings.ReadRecords(installed).Single(item => item.TechId == record.TechId);
            Require(!removed.AllowsCivilizationRow(owner.SourceRowIndex!.Value) &&
                Enumerable.Range(3, 9).Where(row => row != owner.SourceRowIndex).All(removed.AllowsCivilizationRow),
                "Removing a universal progression item changed another civilization's membership.");
            var readd = EndpointPackage(fixture, source, "Conflicting universal progression restoration", "technology.progressionIncluded", "true");
            var beforeConflict = Snapshot(fixture);
            Require(CivilizationPackageManager.Review(readd, fixture).Count > 0 && !CivilizationPackageManager.Install(readd, fixture).Success,
                "A second mask package overlapped an atomic universal expansion.");
            RequireSame(beforeConflict, Snapshot(fixture), "Rejecting a conflicting universal mask wrote fixture files.");
            CivilizationInstallerPackage? broken = null;
            if (!table.WholeFile)
            {
                broken = CivilizationInstallerPackageCodec.Decode(CivilizationInstallerPackageCodec.Encode(removal));
                broken.PackageId = Guid.NewGuid().ToString("N");
                broken.Files.Single(file => file.RelativePath == TechnologyFile).Patches.RemoveAll(patch =>
                    patch.Length == 1 && patch.Offset == record.Offset + 315);
            }
            Require(CivilizationPackageManager.Uninstall(fixture, removal.PackageId).Success &&
                File.ReadAllBytes(path).AsSpan().SequenceEqual(baseline), "Universal mask removal did not restore its exact native baseline.");
            if (broken is not null)
            {
                var beforeBroken = Snapshot(fixture);
                Require(CivilizationPackageManager.Review(broken, fixture).Count > 0 && !CivilizationPackageManager.Install(broken, fixture).Success,
                    "A partial universal civilization-mask patch was accepted.");
                RequireSame(beforeBroken, Snapshot(fixture), "Rejecting a partial universal mask wrote fixture files.");
            }
        }
        finally { File.WriteAllBytes(path, original); }
        checks.Add("Pure membership edits retain semantic ranges; universal exclusions preserve every other civilization, own all mask flags atomically, reject overlaps/partial packets, and uninstall restores the exact baseline.");
    }

    private static void VerifyCombinedTechnologyAges(string fixture, List<string> checks)
    {
        var path = Path.Combine(fixture, TechnologyFile);
        var original = File.ReadAllBytes(path);
        var gameData = new CivilizationGameData(fixture, CivilizationGameCatalog.Load(fixture));
        var civilization = CivilizationEditProject.CreateDefault().Civilizations.Single(civ => civ.Name == "England");
        var source = gameData.GetRoster(civilization, CivilizationCategory.Technologies).First();
        var record = CivilizationTechnologyBindings.ReadRecords(original).Single(item => item.TechId == source.SourceTechnologyId);
        var baseline = original.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(baseline.AsSpan(record.Offset + 108, 4), 5);
        BinaryPrimitives.WriteInt32LittleEndian(baseline.AsSpan(record.Offset + 112, 4), 7);
        File.WriteAllBytes(path, baseline);
        try
        {
            var lower = EndpointPackage(fixture, source, "Lower endpoint seven", "technology.initialAge", "7");
            var upper = EndpointPackage(fixture, source, "Upper endpoint six", "technology.finalAge", "6");
            foreach (var lowerFirst in new[] { true, false })
            {
                var first = lowerFirst ? lower : upper;
                var second = lowerFirst ? upper : lower;
                Require(CivilizationPackageManager.Install(first, fixture).Success, "An individually valid technology endpoint did not install.");
                var before = Snapshot(fixture);
                Require(CivilizationPackageManager.Review(second, fixture).Count > 0 && !CivilizationPackageManager.Install(second, fixture).Success,
                    "Disjoint progression packages created a reversed 7..6 technology interval.");
                RequireSame(before, Snapshot(fixture), "Rejecting combined invalid technology ages wrote files, backup or tracking metadata.");
                Require(CivilizationPackageManager.Uninstall(fixture, first.PackageId).Success,
                    "A valid endpoint package could not be removed after rejecting a conflict.");
                Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(baseline), "Endpoint uninstall did not restore the 5..7 technology baseline.");
            }
            var widened = EndpointPackage(fixture, source, "Widened technology endpoint", "technology.finalAge", "9");
            Require(CivilizationPackageManager.Install(widened, fixture).Success, "The widened technology endpoint did not install.");
            var raised = EndpointPackage(fixture, source, "Raised technology endpoint", "technology.initialAge", "8");
            Require(CivilizationPackageManager.Install(raised, fixture).Success, "Valid combined technology ages 8..9 did not install.");
            var installed = Snapshot(fixture);
            Require(!CivilizationPackageManager.Uninstall(fixture, widened.PackageId).Success,
                "Uninstall left a remaining package with an invalid 8..7 technology interval.");
            RequireSame(installed, Snapshot(fixture), "Rejecting invalid technology age removal wrote data or tracking metadata.");
            Require(CivilizationPackageManager.Uninstall(fixture, raised.PackageId).Success &&
                CivilizationPackageManager.Uninstall(fixture, widened.PackageId).Success,
                "Removing progression endpoints in a valid order failed.");
            Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(baseline), "Valid endpoint removal did not restore the technology baseline.");
            Require(CivilizationPackageManager.ReadInstalled(fixture).Count == 0, "Endpoint verification left an installed package.");
        }
        finally { File.WriteAllBytes(path, original); }
        checks.Add("Disjoint progression endpoint packages cannot merge or uninstall into invalid technology intervals; both install orders and unsafe removal reject before writes.");
    }

    private static void VerifyLegacyPackages(string fixture, List<string> checks)
    {
        var technologyPath = Path.Combine(fixture, TechnologyFile);
        var original = File.ReadAllBytes(technologyPath);
        var gameData = new CivilizationGameData(fixture, CivilizationGameCatalog.Load(fixture));
        var project = CivilizationEditProject.CreateDefault();
        project.Name = "Earlier LAN export fixture";
        project.GamePath = fixture;
        var civilization = project.Civilizations.Single(civ => civ.Name == "England");
        var technology = gameData.GetRoster(civilization, CivilizationCategory.Technologies).First().Clone();
        var record = CivilizationTechnologyBindings.ReadRecords(original).Single(item => item.TechId == technology.SourceTechnologyId);
        technology.Properties["technology.goldCost"] = checked(record.GoldCost + 11).ToString(CultureInfo.InvariantCulture);
        civilization.Entities.Add(technology);
        var legacy = CivilizationInstallerPackage.Create(project, fixture);
        foreach (var file in legacy.Files.Where(file => !file.WholeFile))
            file.StructuralHash = CivilizationInstallerPackage.LegacyStructuralHash(file.RelativePath, File.ReadAllBytes(Path.Combine(fixture, file.RelativePath)));
        Require(CivilizationPackageManager.Review(legacy, fixture).Count == 0 &&
            CivilizationLanPackageReview.Prepare(legacy, fixture).Package is not null,
            "An earlier LAN packet with its original technology fingerprint was rejected.");
        Require(CivilizationPackageManager.Install(legacy, fixture).Success, "An earlier LAN export could not be installed.");
        var installed = Snapshot(fixture);
        var encoded = CivilizationInstallerPackageCodec.Encode(legacy);
        Require(CivilizationLanLocalExport.IsInstalled(legacy, fixture) &&
            CivilizationInstallerPackageCodec.Encode(CivilizationLanLocalExport.Prepare(project, fixture)).AsSpan().SequenceEqual(encoded),
            "Re-exporting an unchanged earlier LAN project replaced its package identity, fingerprint or original baselines.");
        RequireSame(installed, Snapshot(fixture), "Verifying or reusing an earlier installed export wrote fixture files.");

        var upper = EndpointPackage(fixture, technology, "New progression with earlier LAN export", "technology.finalAge",
            (record.EndAge + 1).ToString(CultureInfo.InvariantCulture));
        Require(CivilizationPackageManager.Install(upper, fixture).Success,
            "A new progression endpoint could not coexist with an earlier export of a different field.");
        var coexist = Snapshot(fixture);
        Require(CivilizationLanLocalExport.IsInstalled(legacy, fixture) &&
            CivilizationInstallerPackageCodec.Encode(CivilizationLanLocalExport.Prepare(project, fixture)).AsSpan().SequenceEqual(encoded),
            "An intact tracked progression endpoint invalidated an earlier installed LAN export's fingerprint.");
        RequireSame(coexist, Snapshot(fixture), "Reusing the earlier export alongside tracked progression wrote files.");

        var current = File.ReadAllBytes(technologyPath);
        var unrelated = CivilizationTechnologyBindings.ReadRecords(current).First(item => item.TechId != record.TechId && item.EndAge < 100);
        var externallyChanged = current.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(externallyChanged.AsSpan(unrelated.Offset + 112, 4), unrelated.EndAge + 1);
        File.WriteAllBytes(technologyPath, externallyChanged);
        try
        {
            var tampered = Snapshot(fixture);
            ExpectInvalid(() => CivilizationLanLocalExport.IsInstalled(legacy, fixture),
                "An earlier LAN fingerprint ignored an unowned external age change.");
            ExpectInvalid(() => CivilizationLanLocalExport.Prepare(project, fixture),
                "Reusing an earlier LAN export masked an external age change.");
            RequireSame(tampered, Snapshot(fixture), "Rejecting an external legacy age mismatch wrote or repaired fixture data.");
        }
        finally { File.WriteAllBytes(technologyPath, current); }
        Require(CivilizationPackageManager.Uninstall(fixture, legacy.PackageId).Success,
            "An earlier export could not be removed alongside a tracked progression package.");
        Require(CivilizationLanLocalExport.IsInstalled(upper, fixture),
            "Removing the earlier export erased an independently tracked progression endpoint.");
        Require(CivilizationPackageManager.Uninstall(fixture, upper.PackageId).Success &&
            File.ReadAllBytes(technologyPath).AsSpan().SequenceEqual(original),
            "Removing coexisting earlier/new exports did not restore the original technology table.");
        checks.Add("Earlier LAN exports retain exact identities/fingerprints on reuse, coexist with intact tracked progression edits, reject unowned external age changes, and remain independently removable.");
    }

    private static void VerifyAgeEditingPackages(string fixture, List<string> checks)
    {
        var path = Path.Combine(fixture, TechnologyFile);
        var original = File.ReadAllBytes(path);
        var gameData = new CivilizationGameData(fixture, CivilizationGameCatalog.Load(fixture));
        var project = CivilizationEditProject.CreateDefault();
        project.Name = "Earlier age-editing LAN export";
        project.GamePath = fixture;
        var owner = project.Civilizations.Single(civ => civ.Name == "England");
        var source = gameData.GetRoster(owner, CivilizationCategory.Technologies).First().Clone();
        var record = CivilizationTechnologyBindings.ReadRecords(original).Single(item => item.TechId == source.SourceTechnologyId);
        source.Properties["technology.goldCost"] = checked(record.GoldCost + 13).ToString(CultureInfo.InvariantCulture);
        source.Properties["technology.finalAge"] = (record.EndAge + 1).ToString(CultureInfo.InvariantCulture);
        owner.Entities.Add(source);
        var earlier = CivilizationInstallerPackage.Create(project, fixture);
        foreach (var file in earlier.Files.Where(file => !file.WholeFile))
            file.StructuralHash = CivilizationInstallerPackage.AgeEditingStructuralHash(file.RelativePath, File.ReadAllBytes(Path.Combine(fixture, file.RelativePath)));
        Require(CivilizationLanPackageReview.Prepare(earlier, fixture).Package is not null && CivilizationPackageManager.Install(earlier, fixture).Success,
            "An earlier age-editing packet was rejected by its mask-preserving fingerprint.");
        var encoded = CivilizationInstallerPackageCodec.Encode(earlier);
        var removal = EndpointPackage(fixture, source, "New membership alongside earlier age editing", "technology.progressionIncluded", "false");
        Require(CivilizationPackageManager.Install(removal, fixture).Success,
            "A disjoint membership package could not coexist with an earlier age-editing packet.");
        Require(CivilizationLanLocalExport.IsInstalled(earlier, fixture) &&
            CivilizationInstallerPackageCodec.Encode(CivilizationLanLocalExport.Prepare(project, fixture)).AsSpan().SequenceEqual(encoded),
            "A tracked membership edit changed an earlier age packet's reusable identity/fingerprint.");
        var intact = File.ReadAllBytes(path);
        var unrelated = CivilizationTechnologyBindings.ReadRecords(intact).First(item => item.TechId != record.TechId);
        var tampered = intact.ToArray();
        tampered[unrelated.Offset + 315] = tampered[unrelated.Offset + 315] == 0 ? (byte)1 : (byte)0;
        File.WriteAllBytes(path, tampered);
        try
        {
            var before = Snapshot(fixture);
            ExpectInvalid(() => CivilizationLanLocalExport.IsInstalled(earlier, fixture),
                "An earlier age-editing fingerprint ignored an unowned civilization-mask change.");
            RequireSame(before, Snapshot(fixture), "Rejecting an external mask mismatch wrote fixture files.");
        }
        finally { File.WriteAllBytes(path, intact); }
        Require(CivilizationPackageManager.Uninstall(fixture, earlier.PackageId).Success &&
            CivilizationLanLocalExport.IsInstalled(removal, fixture) && CivilizationPackageManager.Uninstall(fixture, removal.PackageId).Success &&
            File.ReadAllBytes(path).AsSpan().SequenceEqual(original),
            "Removing earlier age-editing and current membership packages did not preserve each other and restore the baseline.");
        checks.Add("Previous age-editing LAN packets retain their exact mask-preserving fingerprints, coexist with intact tracked mask edits, reject external mask changes and remain independently removable.");
    }

    private static void VerifyExpandedAgeSupport(string fixture, List<string> checks)
    {
        var before = Snapshot(fixture);
        var gameData = new CivilizationGameData(fixture, CivilizationGameCatalog.Load(fixture));
        var project = CivilizationEditProject.CreateDefault();
        project.Name = "Expanded civilization age progression";
        project.GamePath = fixture;
        var owner = project.Civilizations.Single(civ => civ.Name == "Germany");
        var originalSources = gameData.GetAgeProgressionCandidates(owner).Select(item => item.Entity.SourceObjectId).ToHashSet();
        owner.Properties["age.initialAge"] = "5";
        var foreign = gameData.GetAgeProgressionCandidates(owner).FirstOrDefault(item => item.Entity.Category == CivilizationCategory.Units &&
            !item.IncludedByDefault && item.AddedProducerIds.Count > 0 && !originalSources.Contains(item.Entity.SourceObjectId));
        Require(foreign is not null, "Expanding supported ages did not make any matching foreign progression unit available.");
        var addition = foreign!.Entity.Clone();
        addition.Properties[foreign.MembershipKey] = "true";
        addition.Properties[foreign.ProducerKey] = foreign.AddedProducerIds[0].ToString(CultureInfo.InvariantCulture);
        owner.Entities.Add(addition);
        var producer = gameData.GetRoster(owner, CivilizationCategory.Buildings).FirstOrDefault(entity => entity.SourceObjectId == foreign.AddedProducerIds[0]) ??
            gameData.GetRoster(owner, CivilizationCategory.Units).Single(entity => entity.SourceObjectId == foreign.AddedProducerIds[0]);
        producer = producer.Clone();
        producer.Properties[Prefix(producer.Category) + ".initialAge"] = "5";
        owner.Entities.Add(producer);
        var plan = CivilizationGameInstaller.BuildPlan(project, fixture, forExport: true);
        Require(plan.CanInstall, "A verified foreign item in the project's expanded supported ages failed native authorization: " + string.Join("; ", plan.Issues));
        var package = CivilizationInstallerPackage.Create(project, fixture);
        Require(CivilizationLanPackageReview.Prepare(package, fixture).Package is not null,
            "Recipient review rejected progression using the project's authored civilization age support.");
        RequireSame(before, Snapshot(fixture), "Reviewing expanded civilization age progression wrote fixture files.");
        checks.Add("Authored civilization age support expands the available progression catalog and native export/review authorizes an eligible foreign unit with its verified producer in those ages.");
    }

    private static CivilizationInstallerPackage EndpointPackage(string fixture, CivilizationEntityDraft source,
        string name, string key, string value)
    {
        var project = CivilizationEditProject.CreateDefault();
        project.Name = name;
        project.GamePath = fixture;
        var entity = source.Clone();
        entity.Properties.Clear();
        entity.Properties[key] = value;
        project.Civilizations.Single(civ => civ.Name == "England").Entities.Add(entity);
        return CivilizationInstallerPackage.Create(project, fixture);
    }

    private static void RequireExactPatchedTables(string fixture, CivilizationInstallerPackage package, IReadOnlyDictionary<string, byte[]> originals)
    {
        foreach (var file in package.Files.Where(file => file.Patches.Count > 0 || file.WholeFile && CivilizationInstallerPackage.IsDatabasePath(file.RelativePath)))
        {
            var expected = file.WholeFile ? file.Replacement! : originals[file.RelativePath].ToArray();
            if (!file.WholeFile) foreach (var patch in file.Patches) patch.Replacement.CopyTo(expected, patch.Offset);
            Require(File.ReadAllBytes(Path.Combine(fixture, file.RelativePath)).AsSpan().SequenceEqual(expected),
                "Progression installation changed bytes outside the reviewed patch fields: " + file.RelativePath);
        }
    }

    private static void VerifyThemeAndLayout(AgeProgressionPanel panel)
    {
        Require(panel.BackColor == MedievalTheme.Surface && panel.ForeColor == MedievalTheme.Text && panel.Parent is not null,
            "Inline progression does not use the main-app surface and text colors.");
        var descendants = Descendants(panel).ToArray();
        foreach (var input in descendants.OfType<NumericUpDown>())
            Require(input.BackColor == MedievalTheme.Input && input.ForeColor == MedievalTheme.Text,
                "A progression numeric field uses system styling.");
        foreach (var input in descendants.OfType<MedievalComboBox>())
            Require(input.BackColor == MedievalTheme.Input && input.ForeColor == MedievalTheme.Text,
                "A progression choice uses system styling.");
        foreach (var input in descendants.OfType<MedievalChoiceSelector>())
        {
            var background = input.BackColor;
            Require(input.ForeColor == MedievalTheme.Text &&
                (background == MedievalTheme.Input || background == MedievalTheme.Surface || background == MedievalTheme.Sidebar),
                "A progression choice selector uses system styling.");
        }
        foreach (var action in descendants.OfType<Button>().Where(button => button.AccessibleName is
            "Add progression items to this civilization" or "Remove selected progression item from this civilization" or
            "Restore selected progression item's original membership"))
        {
            var bounds = panel.RectangleToClient(action.RectangleToScreen(action.ClientRectangle));
            Require(!action.Visible || bounds.Width > 0 && bounds.Height > 0 && panel.ClientRectangle.Contains(bounds),
                "An inline progression membership action is clipped or inaccessible: " + action.Text);
        }
    }

    private static void VerifyResponsiveLayout(CivilizationGameData gameData, CivilizationDraft civilization,
        string output, List<string> checks, bool onlyLargestAddCatalog = false)
    {
        foreach (var scale in onlyLargestAddCatalog ? new[] { 2f } : new[] { 1f, 1.25f, 1.5f, 2f })
        {
            using var host = new Form
            {
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(-30000, -30000), ClientSize = new Size(1000, 700),
                BackColor = MedievalTheme.Surface, Font = MedievalTheme.UiFont()
            };
            using var panel = new AgeProgressionPanel(gameData, civilization) { Dock = DockStyle.Fill };
            host.Controls.Add(panel);
            host.Show();
            Application.DoEvents();
            host.AutoScaleMode = AutoScaleMode.None;
            panel.AutoScaleMode = AutoScaleMode.None;
            var selected = panel.IncludedItemsForVerification.FirstOrDefault(item => item.DisplayName == "77mm AA Gun") ??
                panel.IncludedItemsForVerification.First(item => item.Entity.Category == CivilizationCategory.Units &&
                    int.Parse(gameData.GetValues(item.Entity)["unit.initialAge"], CultureInfo.InvariantCulture) <= 8 &&
                    int.Parse(gameData.GetValues(item.Entity)["unit.finalAge"], CultureInfo.InvariantCulture) >= 8);
            panel.SelectItemForVerification(selected.Entity.Id);
            panel.SetAgeFilterForVerification(8);
            if (scale == 1f)
            {
                var pristine = panel.EditedCivilization;
                var typeSelector = Descendants(panel).Single(control => control.AccessibleName == "Progression item type");
                Descendants(typeSelector).OfType<Button>().Single(button => button.Text == "Unit").PerformClick();
                Require(panel.VisibleItemsForVerification.Count > 0 &&
                    panel.VisibleItemsForVerification.All(item => item.Kind == "Unit"),
                    "Clicking the Unit selector did not filter the actual progression cards.");
                var ageSelector = Descendants(panel).Single(control => control.AccessibleName == "Filter progression by age");
                Descendants(ageSelector).OfType<Button>().Single(button => button.Text == "WWII").PerformClick();
                Require(panel.CaptureViewState().Age == 9 && panel.VisibleItemsForVerification.All(item =>
                    int.Parse(gameData.GetValues(item.Entity)["unit.initialAge"], CultureInfo.InvariantCulture) <= 9 &&
                    int.Parse(gameData.GetValues(item.Entity)["unit.finalAge"], CultureInfo.InvariantCulture) >= 9),
                    "Clicking the WWII selector did not filter the actual progression cards.");
                SendUiKey(Descendants(ageSelector).OfType<Button>().Single(button => button.Text == "WWII"), Keys.Left);
                Require(panel.CaptureViewState().Age == 8,
                    "A named age selector failed to route Left to its actual choice handler.");
                Descendants(typeSelector).OfType<Button>().Single(button => button.Text == "All types").PerformClick();
                var alternate = panel.VisibleItemsForVerification.First(item => item.Entity.Id != selected.Entity.Id &&
                    panel.VisibleItemsForVerification.Count(other => other.DisplayName == item.DisplayName) == 1);
                var windowCount = Application.OpenForms.Count;
                Descendants(panel).OfType<Button>().Single(button =>
                    button.AccessibleName == "Select progression item: " + alternate.DisplayName).PerformClick();
                Require(panel.SelectedItemForVerification == alternate.Entity.Id && Application.OpenForms.Count == windowCount,
                    "Clicking a progression card failed to select the inline editor or created a popup.");
                var next = panel.VisibleItemsForVerification.SkipWhile(item => item.Entity.Id != alternate.Entity.Id).Skip(1).First();
                SendUiKey(Descendants(panel).OfType<Button>().Single(button =>
                    button.AccessibleName == "Select progression item: " + alternate.DisplayName), Keys.Down);
                Require(panel.SelectedItemForVerification == next.Entity.Id,
                    "Down on a progression card moved focus without selecting its adjacent inline item.");
                panel.SelectItemForVerification(selected.Entity.Id);
                panel.SetAgeFilterForVerification(8);
                Require(panel.EditedCivilization.Entities.Count == pristine.Entities.Count && !panel.HasChanges && !panel.HasPendingChanges,
                    "Filtering progression through its visual selectors authored document changes.");
            }
            if (scale != 1f) host.Scale(new SizeF(scale, scale));
            foreach (var width in onlyLargestAddCatalog ? Array.Empty<int>() : new[] { 660, 1000, 1670 })
            {
                host.ClientSize = Scaled(new Size(width, 700), scale);
                PerformLayouts(host);
                Application.DoEvents();
                PerformLayouts(host);
                var context = $"France WWI at {width}px and {scale:0.##} layout scale";
                if (scale is 1f or 1.5f)
                    SaveScreenshot(panel, Path.Combine(output, $"age-progression-france-wwi-{width}-{scale:0.##}x.png"));
                VerifyResponsiveControls(panel, scale, context);
                Require(panel.CaptureViewState().Age == 8 && panel.SelectedItemForVerification == selected.Entity.Id &&
                    panel.VisibleAgeLabelsForVerification.All(label => !label.Any(char.IsDigit)),
                    context + ": resizing changed the selected item or named age filter.");
            }

            // Construct the add view before equivalent DPI scaling, just as a real-DPI panel is created.
            using var addHost = new Form
            {
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(-30000, -30000), ClientSize = new Size(1000, 700),
                BackColor = MedievalTheme.Surface, Font = MedievalTheme.UiFont()
            };
            using var addPanel = new AgeProgressionPanel(gameData, civilization) { Dock = DockStyle.Fill };
            addHost.Controls.Add(addPanel);
            addHost.Show();
            Application.DoEvents();
            addHost.AutoScaleMode = AutoScaleMode.None;
            addPanel.AutoScaleMode = AutoScaleMode.None;
            var add = Descendants(addPanel).OfType<Button>().Single(button =>
                button.AccessibleName == "Add progression items to this civilization");
            add.PerformClick();
            var foreign = addPanel.VisibleItemsForVerification.First(item => item.Entity.Category == CivilizationCategory.Units &&
                item.AddedProducerIds.Count > 0 && item.ProducerChoices.Count > 0);
            addPanel.SelectItemForVerification(foreign.Entity.Id);
            if (scale != 1f) addHost.Scale(new SizeF(scale, scale));
            addHost.ClientSize = Scaled(new Size(1000, 700), scale);
            PerformLayouts(addHost);
            Application.DoEvents();
            SaveScreenshot(addPanel, Path.Combine(output, $"age-progression-inline-add-catalog-{scale:0.##}x.png"));
            VerifyResponsiveControls(addPanel, scale, $"Inline add catalog at {scale:0.##} layout scale");
            var producer = Descendants(addPanel).OfType<MedievalChoiceSelector>().Single(combo =>
                combo.AccessibleName == "Progression item's producer");
            Require(producer.Visible && producer.Items.Count > 0 && producer.Width >= 96 * scale - 2,
                "The inline add catalog clips or loses the compatible producer choice.");
            Require(addPanel.FindForm() == addHost && !addPanel.IsIncludedForVerification(foreign.Entity.Id),
                "The Add catalog unexpectedly creates another window or authors membership before Add is chosen.");
            VerifyPagedProgression(addPanel, scale);
            if (scale == 1f) SaveScreenshot(addPanel, Path.Combine(output, "age-progression-inline-add-catalog.png"));
        }
        checks.Add(onlyLargestAddCatalog
            ? "The largest compatible add catalog keeps separate cards, readable controls and inline producer choices at 200% equivalent layout scale."
            : "Progression keeps named filters, the selected item, readable inputs and separate actions at 660/1000/1670px widths and 125/150/200% equivalent scales; compatible producer selection stays inline.");
    }

    private static void VerifyResponsiveControls(AgeProgressionPanel panel, float scale, string context)
    {
        VerifyThemeAndLayout(panel);
        var controls = Descendants(panel).ToArray();
        Require(!controls.OfType<ComboBox>().Any(), context + ": progression still uses dropdowns instead of choice buttons.");
        foreach (var name in new[] { "Find progression item", "Progression item type", "Progression list", "Filter progression by age" })
        {
            var control = controls.Single(control => control.AccessibleName == name);
            // Control.Scale exercises layout bounds; native single-line fields retain the height of their actual font.
            var minimumHeight = control is TextBox ? control.Font.Height + 2 : Math.Max(20, control.Font.Height + 6);
            Require(control.Visible && control.Width >= 96 * scale - 2 && control.Height >= minimumHeight &&
                panel.ClientRectangle.Contains(BoundsInPanel(panel, control)),
                context + ": a progression filter is clipped or too small: " + name +
                    $" ({control.Size}, Font.Height={control.Font.Height}, bounds={BoundsInPanel(panel, control)}).");
        }
        var list = controls.Single(control => control.AccessibleName == "Age progression items");
        Require(list.Visible && list.Width >= 230 * scale - 2 && list.Height >= 160 * scale - 2 &&
            panel.ClientRectangle.Contains(BoundsInPanel(panel, list)), context + ": the roster has no usable viewport: " +
                $"{list.Size}; bounds {BoundsInPanel(panel, list)} in {panel.ClientRectangle}.");
        Require(!Descendants(panel).OfType<ListView>().Any(), context + ": progression still uses a table instead of item cards.");
        var cards = Descendants(list).OfType<Button>().Where(button =>
            button.AccessibleName?.StartsWith("Select progression item: ", StringComparison.Ordinal) == true).ToArray();
        Require(cards.Length == panel.VisibleItemsForVerification.Count && cards.Length > 0,
            context + ": the visual roster lost its actual progression choices.");
        Require(cards.Length <= 64, context + ": a progression page exceeds the native coordinate-safe card limit.");
        foreach (var card in cards)
            Require(card.Width >= 220 * scale - 2 && card.Height >= 48 * scale - 2 &&
                card.Left >= 0 && card.Right <= list.ClientSize.Width,
                context + ": a progression card is too small or extends outside its roster.");
        for (var index = 1; index < cards.Length; index++)
            Require(!cards[index - 1].Bounds.IntersectsWith(cards[index].Bounds),
                context + $": progression cards {index - 1}/{index} of {cards.Length} overlap: " +
                    $"{cards[index - 1].Bounds} and {cards[index].Bounds}.");
        var detail = controls.OfType<Panel>().SingleOrDefault(control => control.AccessibleName == "Progression item details") ??
            controls.OfType<Panel>().First(control => control.AutoScroll);
        var inputs = Descendants(detail).Where(control => control is NumericUpDown or MedievalChoiceSelector or TextBox).ToArray();
        var form = Descendants(detail).Single(control => control.AccessibleName == "Progression editor form");
        Require(form.Width <= 680 * scale + 2, context + ": the item editor stretches across the workspace.");
        Require(inputs.Length > 0, context + ": selected-item fields disappeared.");
        foreach (var input in inputs)
        {
            // Child numeric edit windows are implementation details; the whole spinner is the field.
            if (input.Parent is NumericUpDown) continue;
            var maximum = input is MedievalChoiceSelector ? 560 : 500;
            Require(input.Width >= 96 * scale - 2 && input.Width <= maximum * scale + 2,
                context + ": a selected-item input is too narrow or stretched across the workspace: " + input.AccessibleName +
                    $" ({input.Width}px, allowed {96 * scale:0.##}–{maximum * scale:0.##}px).");
            var bounds = detail.RectangleToClient(input.RectangleToScreen(input.ClientRectangle));
            Require(bounds.Left >= 0 && bounds.Right <= detail.ClientSize.Width,
                context + ": a selected-item input extends horizontally beyond its editor: " + input.AccessibleName);
        }
        var actions = Descendants(detail).OfType<Button>().Where(button => button.Visible && button.AccessibleName is
            "Apply selected progression item edits" or "Reset this progression item's editable fields" or
            "Discard unapplied progression field edits").ToArray();
        foreach (var action in actions)
        {
            detail.ScrollControlIntoView(action);
            PerformLayouts(panel);
            var bounds = BoundsInPanel(panel, action);
            Require(action.Height >= 26 * scale - 2 && panel.ClientRectangle.Contains(bounds),
                context + ": a selected-item action cannot be reached by scrolling: " + action.Text);
            var text = TextRenderer.MeasureText(action.Text, action.Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            Require(text.Width + 8 * scale <= action.ClientSize.Width + 2,
                context + ": a selected-item action label is truncated: " + action.Text);
        }
        for (var index = 0; index < actions.Length; index++)
            for (var next = index + 1; next < actions.Length; next++)
                Require(!BoundsInPanel(panel, actions[index]).IntersectsWith(BoundsInPanel(panel, actions[next])),
                    context + ": selected-item actions overlap.");
        detail.AutoScrollPosition = Point.Empty;
        PerformLayouts(panel);
    }

    private static void VerifyPagedProgression(AgeProgressionPanel panel, float scale)
    {
        var original = panel.CaptureViewState();
        var expected = panel.FullFilteredItemsForVerification.Select(item => item.Entity.Id).ToHashSet(StringComparer.Ordinal);
        var next = Descendants(panel).OfType<Button>().Single(button =>
            button.AccessibleName == "Next progression items page");
        var previous = Descendants(panel).OfType<Button>().Single(button =>
            button.AccessibleName == "Previous progression items page");
        while (previous.Enabled) { previous.PerformClick(); Application.DoEvents(); }
        var all = new HashSet<string>(StringComparer.Ordinal);
        var page = 0;
        while (true)
        {
            var items = panel.VisibleItemsForVerification;
            Require(items.Count is > 0 and <= 64 && panel.CurrentPageForVerification == page,
                "A progression page is empty, exceeds the 64-card bound or has the wrong index.");
            var roster = Descendants(panel).Single(control => control.AccessibleName == "Age progression items");
            var cards = Descendants(roster).OfType<Button>().Where(button =>
                button.AccessibleName?.StartsWith("Select progression item: ", StringComparison.Ordinal) == true).ToArray();
            Require(cards.Length == items.Count, "A progression page loses a displayed source choice.");
            foreach (var item in items) Require(all.Add(item.Entity.Id), "A progression source item appears on more than one page.");
            foreach (var card in cards)
                Require(card.Height >= 48 * scale - 2 && card.Left >= 0 && card.Right <= roster.ClientSize.Width,
                    "A paged progression card is too small or extends horizontally outside its roster.");
            for (var index = 1; index < cards.Length; index++)
                Require(!cards[index - 1].Bounds.IntersectsWith(cards[index].Bounds),
                    $"Paged progression cards overlap at {scale:0.##} layout scale, page {page}.");
            if (!next.Enabled) break;
            next.PerformClick();
            Application.DoEvents();
            page++;
            Require(page < panel.PageCountForVerification, "Progression pagination fails to reach its last page.");
        }
        Require(all.SetEquals(expected) && page + 1 == panel.PageCountForVerification,
            "Progression pages do not partition the complete filtered source roster.");
        while (previous.Enabled) { previous.PerformClick(); Application.DoEvents(); }
        if (panel.PageCountForVerification > 1)
        {
            var boundary = panel.VisibleItemsForVerification.Last();
            var roster = Descendants(panel).Single(control => control.AccessibleName == "Age progression items");
            var card = Descendants(roster).OfType<Button>().Last(button =>
                button.AccessibleName?.StartsWith("Select progression item: ", StringComparison.Ordinal) == true);
            card.PerformClick();
            Require(panel.SelectedItemForVerification == boundary.Entity.Id,
                "Clicking the last progression source choice on a page failed to select it.");
            var destination = panel.FullFilteredItemsForVerification[64];
            SendUiKey(card, Keys.Down);
            Require(panel.CurrentPageForVerification == 1 && panel.SelectedItemForVerification == destination.Entity.Id,
                "Down at a progression page boundary failed to reveal and select the next actual source item.");
            previous.PerformClick();
            Application.DoEvents();
            Require(panel.CurrentPageForVerification == 0, "Previous failed to return from the second progression page.");
        }
        panel.RestoreViewState(original);
        Require(!panel.HasChanges && !panel.HasPendingChanges,
            "Paging or selecting progression source choices authored a document change.");
    }

    private static Size Scaled(Size size, float scale) => new(
        (int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));

    private static void SendUiKey(Button button, Keys key)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        Require((bool)button.GetType().GetMethod("IsInputKey", flags)!.Invoke(button, [key])!,
            "A progression navigation key is intercepted before its actual card or selector sees it: " + key);
        var args = new KeyEventArgs(key);
        button.GetType().GetMethod("OnKeyDown", flags)!.Invoke(button, [args]);
        Require(args.Handled && args.SuppressKeyPress,
            "A progression card or selector did not handle its actual navigation shortcut: " + key);
        Application.DoEvents();
    }

    private static Rectangle BoundsInPanel(Control panel, Control control) =>
        new(panel.PointToClient(control.PointToScreen(Point.Empty)), control.Size);

    private static void PerformLayouts(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) PerformLayouts(child);
    }

    private static void SaveScreenshot(Control control, string path)
    {
        using var image = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        image.Save(path);
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static string CopyFixture(string source, string output, string role)
    {
        var fixture = Path.Combine(output, "age-fixtures", role + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "VERIFICATION_ONLY.txt"), "Isolated progression fixture. Never launch the copied game executable.\n");
        foreach (var relative in RequiredFiles.Concat(new[] { "Language.dll", "Language2.dll", @"Data\db\dbcalamity.dat", @"Data\db\dbcivpowers.dat" }))
        {
            var origin = Path.Combine(source, relative);
            if (!File.Exists(origin)) continue;
            var target = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(origin, target);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }
        return fixture;
    }

    private static string Prefix(CivilizationCategory category) => category switch
    {
        CivilizationCategory.Units => "unit",
        CivilizationCategory.Buildings => "building",
        CivilizationCategory.Technologies => "technology",
        _ => throw new InvalidOperationException("No progression prefix for category " + category)
    };
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static IReadOnlyDictionary<string, string> Snapshot(string root)
    {
        var snapshot = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            path => "file:" + Path.GetRelativePath(root, path), Hash, StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            snapshot.Add("dir:" + Path.GetRelativePath(root, path), "");
        return snapshot;
    }
    private static void RequireSame(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, string message) =>
        Require(before.Count == after.Count && before.All(entry => after.TryGetValue(entry.Key, out var value) && entry.Value == value), message);
    private static void ExpectInvalid(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

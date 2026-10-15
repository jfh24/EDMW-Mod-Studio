using System.Security.Cryptography;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.UI;

internal static class CivilizationUnitEffectsVerification
{
    public static void Run(string gamePath, string outputDirectory)
    {
        var source = Path.GetFullPath(gamePath);
        var output = Path.GetFullPath(outputDirectory);
        if (output.Equals(source, StringComparison.OrdinalIgnoreCase) || output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Verification output must be outside the installed game.");
        Directory.CreateDirectory(output);
        var objectsPath = Path.Combine(source, "Data", "db", "dbobjects.dat");
        var buttonsPath = Path.Combine(source, "Data", "db", "dbbuttons.dat");
        var objectsHash = SHA256.HashData(File.ReadAllBytes(objectsPath));
        var buttonsHash = SHA256.HashData(File.ReadAllBytes(buttonsPath));
        var catalog = CivilizationGameCatalog.Load(source);
        var gameData = new CivilizationGameData(source, catalog);
        var nativeButtonChecks = CivilizationAbilityButtonsVerification.Verify(source);
        var ranger = catalog.Items.Single(item => item.SourceId == 749);
        var buttons = CivilizationUnitEffectBindings.ReadButtonChoices(File.ReadAllBytes(buttonsPath)).Select(button => (button.Id, button.Name)).ToArray();
        var sourceEntity = new CivilizationEntityDraft { SourceObjectId = ranger.SourceId, Category = ranger.Category, Name = ranger.Name };
        var project = CivilizationEditProject.CreateDefault();
        project.GamePath = source;
        project.Name = "Custom effect authoring verification";
        var civilization = project.Civilizations.Single(civ => civ.SourceCivilizationId == 12);
        civilization.Entities.Add(sourceEntity);
        var baseline = project.Serialize();
        var statChecks = VerifyStatOutputs(sourceEntity, catalog.Items, buttons, output);

        using var editor = new CivilizationEditorView(() => source);
        using var host = new Form { ClientSize = new Size(1400, 850) };
        host.Controls.Add(editor);
        Prepare(host);
        var path = Path.Combine(output, "unit-effects.edmwciv");
        project.Save(path);
        editor.OpenProjectFile(path);
        editor.SelectCivilization(civilization.Id);
        editor.SelectCategory(CivilizationCategory.Units);
        editor.SelectEntityForVerification(sourceEntity.Id);
        Require(Descendants(editor).OfType<Button>().Any(button => button.AccessibleName == "Add or edit custom effects attached to this unit or building" && button.Enabled),
            "A mapped unit needs an accessible attachment editor.");

        using (var dialog = editor.CreateUnitEffectsForVerification())
        {
            Prepare(dialog);
            dialog.AddRangerExampleForVerification();
            var aura = dialog.SnapshotForVerification.Single();
            Require(aura.TargetObjectId == 777 && aura.RadiusTiles == 5 && aura.Amount == 3, "Ranger/Sherman preset");
            Require(Control<Button>(dialog, "Effect target").Enabled && !Control<ComboBox>(dialog, "Ability activation mode").Enabled,
                "Proximity controls must enable target/radius and disable activation controls.");
            VerifyTargetOverview(dialog, catalog.Items, gameData, 777, output, "sherman");
            var citizen = catalog.Items.First(item => item.Category == CivilizationCategory.Units && item.Name.Contains("Citizen", StringComparison.OrdinalIgnoreCase));
            VerifyTargetOverview(dialog, catalog.Items, gameData, citizen.SourceId, output, "citizen");
            VerifyTargetOverview(dialog, catalog.Items, gameData, 878, output, "citizen-china");
            dialog.SelectTargetsForVerification([777, 749]);
            Require(dialog.SelectedTargetIdsForVerification.SequenceEqual([749, 777]) &&
                Control<Button>(dialog, "Effect target").Text.Contains("2 targets", StringComparison.Ordinal), "Multiple selected target types are summarized without a dropdown");
            Control<ComboBox>(dialog, "Effect trigger").SelectedIndex = 1;
            Require(dialog.SelectedTargetIdsForVerification.SequenceEqual([749]), "Activated effects lock the target types to their source");
            Control<ComboBox>(dialog, "Effect trigger").SelectedIndex = 0;
            Require(dialog.SelectedTargetIdsForVerification.SequenceEqual([749, 777]), "Switching back to proximity restores its staged target types");
            dialog.SelectTargetsForVerification([777, 99999]);
            Require(dialog.SelectedTargetIdsForVerification.SequenceEqual([777, 99999]) && !dialog.ApplyForVerification(),
                "Missing saved targets remain in the draft and block Apply until explicitly repaired");
            using (var targets = dialog.CreateTargetPickerForVerification())
            {
                Prepare(targets);
                Require(targets.SelectedObjectIds.SequenceEqual([777, 99999]) &&
                    !Control<Button>(targets, "Use selected affected targets").Enabled &&
                    Control<Label>(targets, "Selected target count").Text.Contains("1 unavailable", StringComparison.Ordinal),
                    "The target picker preserves and identifies unavailable selections instead of silently dropping them");
                Control<Button>(targets, "Clear selected affected targets").PerformClick();
                Require(targets.SelectedObjectIds.Count == 0 && dialog.SelectedTargetIdsForVerification.SequenceEqual([777, 99999]),
                    "Clearing staged picker selections and cancelling leaves the parent draft unchanged");
            }
            SelectTarget(dialog, 777);
            VerifyCards(dialog, activated: false);
            Snapshot(dialog, Path.Combine(output, "proximity-attachment.png"));
            var defaultSize = dialog.Size;
            dialog.Size = dialog.MinimumSize;
            Snapshot(dialog, Path.Combine(output, "proximity-attachment-minimum.png"));
            VerifyCards(dialog, activated: false);
            dialog.Size = defaultSize;
            dialog.RemoveOutputForVerification(0);
            Require(OutputSelectors(dialog).Count == 1 && !Control<Button>(dialog, "Remove effect output 1").Enabled,
                "An effect always retains at least one output");
            dialog.AddOutputForVerification();
            SetOutput(dialog, 2, CivilizationUnitEffectStat.ModelScale, 0.25m);
            dialog.AddOutputForVerification();
            SetOutput(dialog, 3, CivilizationUnitEffectStat.Health, 125);
            dialog.AddOutputForVerification();
            SetOutput(dialog, 4, CivilizationUnitEffectStat.MovementSpeed, 0.125m);
            dialog.RemoveOutputForVerification(2);
            Require(OutputSelectors(dialog).Count == 3 && Control<ComboBox>(dialog, "Effect output stat 3").SelectedIndex == StatIndex(CivilizationUnitEffectStat.MovementSpeed) &&
                    Control<NumericUpDown>(dialog, "Effect adjustment 3").Value == 0.125m,
                "Removing a middle output retains and renumbers the remaining draft rows");
            VerifyCards(dialog, activated: false);
            Snapshot(dialog, Path.Combine(output, "multiple-outputs-attachment.png"));
            dialog.Size = dialog.MinimumSize;
            VerifyCards(dialog, activated: false);
            Snapshot(dialog, Path.Combine(output, "multiple-outputs-attachment-minimum.png"));
            dialog.Size = defaultSize;
            dialog.AddForVerification();
            Control<TextBox>(dialog, "Effect name").Text = "Ranger scouting toggle";
            Control<ComboBox>(dialog, "Effect trigger").SelectedIndex = 1;
            Control<ComboBox>(dialog, "Ability activation mode").SelectedIndex = 0;
            Control<TextBox>(dialog, "In-game button caption").Text = "Scout";
            var outputStat = Control<ComboBox>(dialog, "Effect output stat");
            outputStat.SelectedIndex = CivilizationUnitEffects.StatChoices.ToList().FindIndex(stat => stat.Stat != CivilizationUnitEffectStat.LineOfSight && stat.DecimalPlaces > 0);
            Require(outputStat.SelectedIndex >= 0, "A fractional stat output is available");
            var fractionalStat = CivilizationUnitEffects.StatChoices[outputStat.SelectedIndex];
            Control<NumericUpDown>(dialog, "Effect adjustment").Value = 2.5m;
            Require(!Control<Button>(dialog, "Effect target").Enabled && Control<ComboBox>(dialog, "In-game button appearance").Enabled &&
                    !Control<NumericUpDown>(dialog, "Ability duration in seconds").Enabled, "Toggle controls");
            VerifyCards(dialog, activated: true);
            Snapshot(dialog, Path.Combine(output, "activated-attachment.png"));
            dialog.AddForVerification();
            Control<TextBox>(dialog, "Effect name").Text = "Temporary scouting";
            Control<ComboBox>(dialog, "Effect trigger").SelectedIndex = 1;
            Control<ComboBox>(dialog, "Ability activation mode").SelectedIndex = 1;
            Control<NumericUpDown>(dialog, "Ability duration in seconds").Value = 45;
            Control<CheckBox>(dialog, "Effect enabled").Checked = false;
            Require(Control<NumericUpDown>(dialog, "Ability duration in seconds").Enabled, "Timed ability duration control");
            Snapshot(dialog, Path.Combine(output, "timed-attachment.png"));
            dialog.Size = dialog.MinimumSize;
            Snapshot(dialog, Path.Combine(output, "timed-attachment-minimum.png"));
            VerifyCards(dialog, activated: true);
            var previewCard = Control<Control>(dialog, "Effect preview card");
            var editorScroll = (ScrollableControl)previewCard.Parent!.Parent!;
            Control<Button>(dialog, "Apply custom effects to project").Focus();
            editorScroll.AutoScrollPosition = new Point(0, editorScroll.VerticalScroll.Maximum);
            Snapshot(dialog, Path.Combine(output, "timed-attachment-minimum-preview.png"));
            Require(editorScroll.ClientRectangle.Contains(editorScroll.RectangleToClient(previewCard.RectangleToScreen(previewCard.ClientRectangle))),
                $"The complete effect preview can be reached by scrolling at minimum window size (fields {previewCard.Parent!.Bounds}, fields bottom {previewCard.Parent.Bottom}, viewport {editorScroll.ClientRectangle}, display {editorScroll.DisplayRectangle}, preview {editorScroll.RectangleToClient(previewCard.RectangleToScreen(previewCard.ClientRectangle))}, preview parent bottom {previewCard.Bottom}, scroll maximum {editorScroll.VerticalScroll.Maximum}, large change {editorScroll.VerticalScroll.LargeChange}, value {editorScroll.VerticalScroll.Value}, position {editorScroll.AutoScrollPosition})");
            dialog.Size = defaultSize;
            dialog.AddForVerification();
            dialog.RemoveForVerification();
            Require(dialog.SnapshotForVerification.Count == 3, "Remove deletes only the selected attachment");
            var auraId = dialog.SnapshotForVerification[0].Id;
            dialog.SelectForVerification(auraId);
            Require(OutputSelectors(dialog).Count == 3 && Control<NumericUpDown>(dialog, "Effect adjustment 2").Value == 0.25m &&
                    Control<NumericUpDown>(dialog, "Effect adjustment 3").Value == 0.125m,
                "Switching effect cards restores all outputs and fractional amounts");
            editor.ApplyUnitEffectsForVerification(dialog);
            Require(dialog.EditedEffects![1].Stat == fractionalStat.Stat && dialog.EditedEffects[1].Amount == 2.5m,
                "Activated abilities preserve the selected output and fractional adjustment");
        }

        var edited = editor.SnapshotProject;
        var savedEntity = edited.Civilizations.Single(civ => civ.Id == civilization.Id).Entities.Single(entity => entity.Id == sourceEntity.Id);
        var rules = CivilizationUnitEffects.Read(savedEntity);
        Require(rules.Count == 3 && rules.Select(rule => rule.Id).Distinct().Count() == 3, "Multiple stable attachment IDs");
        Require(CivilizationUnitEffects.GetOutputs(rules[0]).SequenceEqual(new CivilizationUnitEffectOutput[]
            { new(CivilizationUnitEffectStat.LineOfSight, 3), new(CivilizationUnitEffectStat.ModelScale, 0.25m), new(CivilizationUnitEffectStat.MovementSpeed, 0.125m) }),
            "One effect preserves multiple stat outputs under one trigger");
        Require(rules[1].ActivationMode == CivilizationUnitEffectActivationMode.Toggle && rules[1].TargetObjectId == 749 && rules[1].ButtonCaption == "Scout", "Activated ability binds to its source and preserves button config");
        Require(rules[2].ActivationMode == CivilizationUnitEffectActivationMode.Timed && rules[2].DurationSeconds == 45 && !rules[2].Enabled, "Timed and disabled state persistence");
        Require(edited.GetChanges().Count(change => change.Key == CivilizationUnitEffects.PropertyKey) == 3, "Change summary lists individual attachments");
        Require(edited.Serialize() == CivilizationEditProject.Deserialize(edited.Serialize()).Serialize(), "Project round trip");
        Descendants(editor).OfType<Button>().Single(button => button.AccessibleName == "Duplicate roster entry").PerformClick();
        var duplicated = editor.SnapshotProject.Civilizations.Single(civ => civ.Id == civilization.Id).Entities
            .Single(entity => entity.Id != sourceEntity.Id && entity.SourceObjectId == sourceEntity.SourceObjectId);
        Require(CivilizationUnitEffects.Read(duplicated).Select(rule => rule.Id).Intersect(rules.Select(rule => rule.Id)).Count() == 0,
            "Duplicating an entity creates new effect identities");
        editor.HandleShortcut(Keys.Control | Keys.Z);
        Require(editor.SnapshotProject.Serialize() == edited.Serialize(), "Duplicate attachments is undoable");
        editor.HandleShortcut(Keys.Control | Keys.Z);
        Require(editor.SnapshotProject.Serialize() == baseline, "Editor undo restores all attachments atomically");
        editor.HandleShortcut(Keys.Control | Keys.Y);
        Require(editor.SnapshotProject.Serialize() == edited.Serialize(), "Editor redo restores effect IDs and configuration");
        editor.SaveProjectFile(path);
        editor.OpenProjectFile(path);
        Require(editor.SnapshotProject.Serialize() == edited.Serialize(), "Editor save/reopen retains attachments");
        editor.SelectCivilization(civilization.Id);
        editor.SelectCategory(CivilizationCategory.Units);
        editor.SelectEntityForVerification(sourceEntity.Id);
        using (var reopened = editor.CreateUnitEffectsForVerification())
        {
            Prepare(reopened);
            Require(OutputSelectors(reopened).Count == 3 && Control<ComboBox>(reopened, "Effect output stat 2").SelectedIndex == StatIndex(CivilizationUnitEffectStat.ModelScale) &&
                    Control<NumericUpDown>(reopened, "Effect adjustment 2").Value == 0.25m && Control<NumericUpDown>(reopened, "Effect adjustment 3").Value == 0.125m,
                "Save/reopen restores multiple output rows in the actual editor controls");
        }
        editor.ResetCategoryForVerification();
        Require(!CivilizationUnitEffects.HasAttachments(editor.SnapshotProject.Civilizations.Single(civ => civ.Id == civilization.Id).Entities.Single(entity => entity.Id == sourceEntity.Id)),
            "Reset category removes custom attachments too");
        editor.HandleShortcut(Keys.Control | Keys.Z);
        Require(editor.SnapshotProject.Serialize() == edited.Serialize(), "Reset attachments is undoable");
        Snapshot(editor, Path.Combine(output, "effect-editor.png"));
        var attachmentCard = Descendants(editor).Single(control => control.AccessibleName == "Custom effect attachments");
        Require(attachmentCard.Controls.OfType<Label>().All(label => attachmentCard.ClientRectangle.Contains(label.Bounds)),
            "Attachment summaries and prototype help must wrap inside the editor card");

        var building = catalog.Items.First(item => item.Category == CivilizationCategory.Buildings);
        var buildingEntity = new CivilizationEntityDraft { Name = building.Name, Category = building.Category, SourceObjectId = building.SourceId };
        using (var dialog = new CivilizationUnitEffectsDialog(buildingEntity, catalog.Items, buttons))
        {
            dialog.AddForVerification();
            Require(dialog.ApplyForVerification() && dialog.EditedEffects!.Single().TargetObjectId == building.SourceId, "Buildings accept effect attachments");
        }
        var overviewDraft = sourceEntity.Clone();
        overviewDraft.Properties["unit.health"] = "987";
        using (var overview = new CivilizationUnitOverviewDialog(overviewDraft, gameData.GetValues(sourceEntity)))
            Require(Control<Label>(overview, "Overview value unit.health").Text == "987", "The overview displays project stat overrides above game values");
        Reject(() => CivilizationUnitEffects.Write(sourceEntity, [rules[0] with { Outputs = [new(CivilizationUnitEffectStat.LineOfSight, 0)] }]), "Zero adjustment");
        Reject(() => CivilizationUnitEffects.Write(sourceEntity, [rules[0] with { RadiusTiles = 65 }]), "Radius range");
        Reject(() => CivilizationUnitEffects.Write(sourceEntity, [rules[0], rules[0]]), "Duplicate effect ID");
        Reject(() => CivilizationUnitEffects.Write(sourceEntity, [rules[1], rules[1] with { Id = Guid.NewGuid().ToString("D") }]), "Conflicting activated button slots");
        var malformed = sourceEntity.Clone();
        malformed.Properties[CivilizationUnitEffects.PropertyKey] = "[{\"name\":\"Incomplete\"}]";
        Reject(() => CivilizationUnitEffects.Validate(malformed), "Missing effect members");
        Require(SHA256.HashData(File.ReadAllBytes(objectsPath)).AsSpan().SequenceEqual(objectsHash) &&
                SHA256.HashData(File.ReadAllBytes(buttonsPath)).AsSpan().SequenceEqual(buttonsHash), "Authoring must not modify game data");
        File.WriteAllText(Path.Combine(output, "unit-effects-result.txt"), "PASS: actual attachment controls, minimal effect cards at default and minimum window sizes, every stat output, multiple outputs under one trigger, middle output removal, one-output minimum, fractional adjustments, Ranger/Sherman preset, toggle and timed abilities, enabled state, remove, stable IDs, duplicate entry identities, validation, project undo/redo, save/reopen, reset, building attachment, unchanged source files.\n" +
            string.Join("\n", statChecks) + "\n" +
            string.Join("\n", nativeButtonChecks) + "\nUNVERIFIED: live gameplay, button activation in EDMW, save/load and multiplayer.\nActivated ability state is session-only and resets when loading a save.\n");
        Console.WriteLine("UNIT EFFECT AUTHORING PASS: all stat outputs, multiple outputs per trigger, output add/remove and minimum, fractional adjustments, cards at default/minimum sizes, preset, toggle/timed, enable/remove, duplicate identities, history, save/reopen, buildings, native button registration, validation, unchanged game files.");
    }

    private static IReadOnlyList<string> VerifyStatOutputs(CivilizationEntityDraft source,
        IReadOnlyList<CivilizationCatalogItem> objects, IReadOnlyList<(int Id, string Name)> buttons, string output)
    {
        var draft = source.Clone();
        draft.Properties.Remove(CivilizationUnitEffects.PropertyKey);
        using var dialog = new CivilizationUnitEffectsDialog(draft, objects, buttons);
        Prepare(dialog);
        dialog.AddForVerification();
        var selector = Control<ComboBox>(dialog, "Effect output stat");
        Require(selector.Items.Count == CivilizationUnitEffects.StatChoices.Count && selector.Items.Count == Enum.GetValues<CivilizationUnitEffectStat>().Length,
            "The effect output selector exposes every supported stat definition");
        var expected = new List<(CivilizationUnitEffectStat Stat, decimal Amount)>();
        for (var index = 0; index < CivilizationUnitEffects.StatChoices.Count; index++)
        {
            selector = Control<ComboBox>(dialog, "Effect output stat");
            var amount = Control<NumericUpDown>(dialog, "Effect adjustment");
            selector.SelectedIndex = index;
            var stat = CivilizationUnitEffects.StatChoices[index];
            Require(amount.Minimum == stat.MinimumAmount && amount.Maximum == stat.MaximumAmount && amount.DecimalPlaces == stat.DecimalPlaces,
                $"{stat.Name}: adjustment precision and range follow the selected output");
            var sample = stat.DecimalPlaces > 0 ? 2.5m : stat.MaximumAmount > 32 ? 125m : 7m;
            sample = Math.Clamp(sample, stat.MinimumAmount, stat.MaximumAmount);
            if (sample == 0) sample = stat.DefaultAmount;
            if (index % 2 != 0 && -sample >= stat.MinimumAmount) sample = -sample;
            amount.Value = sample;
            Control<TextBox>(dialog, "Effect name").Text = "Output: " + stat.Name;
            expected.Add((stat.Stat, sample));
            if (index < selector.Items.Count - 1) dialog.AddForVerification();
        }
        var firstId = dialog.SnapshotForVerification[0].Id;
        var lastId = dialog.SnapshotForVerification[^1].Id;
        dialog.SelectForVerification(firstId);
        Require(Control<ComboBox>(dialog, "Effect output stat").SelectedIndex == 0 && Control<NumericUpDown>(dialog, "Effect adjustment").Value == expected[0].Amount,
            "Selecting the first effect card restores its output and adjustment");
        var firstStat = CivilizationUnitEffects.StatChoices[0];
        var revisedAmount = Math.Clamp(expected[0].Amount + 1, firstStat.MinimumAmount, firstStat.MaximumAmount);
        Require(revisedAmount != expected[0].Amount, "The first output can be edited for selection persistence verification");
        Control<NumericUpDown>(dialog, "Effect adjustment").Value = revisedAmount;
        expected[0] = (firstStat.Stat, revisedAmount);
        dialog.SelectForVerification(lastId);
        Require(Control<ComboBox>(dialog, "Effect output stat").SelectedIndex == expected.Count - 1 && Control<NumericUpDown>(dialog, "Effect adjustment").Value == expected[^1].Amount,
            "Selecting the last effect card restores its output and adjustment");
        dialog.SelectForVerification(firstId);
        Require(Control<NumericUpDown>(dialog, "Effect adjustment").Value == revisedAmount, "Switching effect cards commits the previous card's draft changes");
        dialog.SelectForVerification(lastId);
        Snapshot(dialog, Path.Combine(output, "all-stat-outputs.png"));
        Require(dialog.ApplyForVerification(), "All stat outputs can be applied in one authoring session");
        var effects = dialog.EditedEffects!;
        Require(effects.Count == expected.Count && effects.Select(effect => (effect.Stat, effect.Amount)).SequenceEqual(expected),
            "Changing output preserves each attachment's selected stat and adjustment");
        CivilizationUnitEffects.Write(draft, effects);
        var roundTrip = CivilizationUnitEffects.Read(draft);
        Require(roundTrip.Count == effects.Count && roundTrip.Zip(effects).All(pair => pair.First.Id == pair.Second.Id &&
                CivilizationUnitEffects.GetOutputs(pair.First).SequenceEqual(CivilizationUnitEffects.GetOutputs(pair.Second))),
            "Every stat output and fractional adjustment round trips through attachment JSON");
        return expected.Select(item => $"OUTPUT PASS: {item.Stat} = {item.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}").ToArray();
    }

    private static void VerifyCards(CivilizationUnitEffectsDialog dialog, bool activated)
    {
        dialog.PerformLayout();
        Application.DoEvents();
        foreach (var name in new[] { "Effect identity card", "Effect trigger card", "Effect output card", "Ability button card" })
        {
            var card = Control<Control>(dialog, name);
            Require(!card.Visible || card.Width > 200 && card.Left >= 0 && card.Right <= card.Parent!.ClientSize.Width,
                $"{name} remains within the editor width at {dialog.Width} pixels (card {card.Bounds}, parent {card.Parent!.ClientSize})");
        }
        Require(Control<Control>(dialog, "Ability button card").Visible == activated,
            "Ability button settings appear only for activated effects");
        var inputNames = new[] { "Effect name", "Effect trigger" }.Concat(Descendants(dialog).Where(control =>
            control.AccessibleName?.StartsWith("Effect output stat", StringComparison.Ordinal) == true ||
            control.AccessibleName?.StartsWith("Effect adjustment", StringComparison.Ordinal) == true).Select(control => control.AccessibleName!));
        foreach (var name in inputNames)
        {
            var input = Descendants(dialog).Single(control => control.AccessibleName == name);
            Require(input.Visible && input.Width >= 80 && input.Height >= input.Font.Height && input.Left >= 0 && input.Right <= input.Parent!.ClientSize.Width,
                $"{name} remains readable inside its card");
        }
        foreach (var selector in OutputSelectors(dialog))
        {
            var suffix = selector.AccessibleName!["Effect output stat".Length..];
            var amount = Control<NumericUpDown>(dialog, "Effect adjustment" + suffix);
            Require(Math.Abs(selector.PointToScreen(Point.Empty).Y - amount.PointToScreen(Point.Empty).Y) <= 2,
                $"{selector.AccessibleName} and its adjustment remain aligned in one input row");
        }
    }

    private static IReadOnlyList<ComboBox> OutputSelectors(Control dialog) => Descendants(dialog).OfType<ComboBox>()
        .Where(control => control.AccessibleName?.StartsWith("Effect output stat", StringComparison.Ordinal) == true).ToArray();

    private static int StatIndex(CivilizationUnitEffectStat stat) => CivilizationUnitEffects.StatChoices.ToList().FindIndex(choice => choice.Stat == stat);

    private static void SetOutput(CivilizationUnitEffectsDialog dialog, int number, CivilizationUnitEffectStat stat, decimal amount)
    {
        var suffix = number == 1 ? "" : " " + number;
        Control<ComboBox>(dialog, "Effect output stat" + suffix).SelectedIndex = StatIndex(stat);
        Control<NumericUpDown>(dialog, "Effect adjustment" + suffix).Value = amount;
    }

    private static void VerifyTargetOverview(CivilizationUnitEffectsDialog effects, IReadOnlyList<CivilizationCatalogItem> objects,
        CivilizationGameData gameData, int targetId, string output, string snapshotName)
    {
        var target = objects.Single(item => item.SourceId == targetId);
        SelectTarget(effects, target.SourceId);
        using var overview = effects.CreateTargetOverviewForVerification() ?? throw new InvalidOperationException("The selected target portrait needs a stats overview.");
        Prepare(overview);
        var draft = new CivilizationEntityDraft { Name = target.Name, Category = target.Category, SourceObjectId = targetId };
        var values = gameData.GetValues(draft);
        var numericBindings = CivilizationObjectBindings.Fields(target.Category)
            .Where(field => field.Type is not (CivilizationBinaryType.String100 or CivilizationBinaryType.BooleanByte))
            .Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        var numeric = CivilizationSchema.GetFields(target.Category).Where(field => field.Kind == CivilizationFieldKind.Number || numericBindings.Contains(field.Key)).ToArray();
        foreach (var field in numeric)
        {
            var label = Control<Label>(overview, "Overview value " + field.Key);
            var expected = values.GetValueOrDefault(field.Key);
            Require(label.Text == (string.IsNullOrWhiteSpace(expected) ? "Not exposed" : expected.Trim()),
                $"{target.Name}: the overview preserves the actual or unavailable value for {field.Label}");
        }
        Require(values.ContainsKey("unit.health") && numeric.Any(field => field.Key.EndsWith("Cost", StringComparison.Ordinal) && values.ContainsKey(field.Key)),
            "Target overviews include mapped health and production costs");
        if (targetId == 878)
            Require(Control<Label>(overview, "Overview value unit.foodCost").Text == "40" &&
                    Control<Label>(overview, "Overview value unit.productionCoefficient").Text == "30000",
                "China Citizen overview uses its actual purchase command cost and production duration");
        Snapshot(overview, Path.Combine(output, "target-overview-" + snapshotName + ".png"));
        overview.Size = overview.MinimumSize;
        Snapshot(overview, Path.Combine(output, "target-overview-" + snapshotName + "-minimum.png"));
        foreach (var label in Descendants(overview).OfType<Label>().Where(label => label.AccessibleName?.StartsWith("Overview value ", StringComparison.Ordinal) == true))
            Require(label.Left >= 0 && label.Right <= label.Parent!.ClientSize.Width && label.Width >= 32,
                $"{label.AccessibleName} remains within its overview card at minimum width");
    }

    private static void SelectTarget(CivilizationUnitEffectsDialog dialog, int targetId)
    {
        dialog.SelectTargetForVerification(targetId);
        Require(dialog.SelectedTargetIdForVerification == targetId, "The overview target exists in the catalog");
    }

    private static T Control<T>(Control root, string accessibleName) where T : Control =>
        Descendants(root).OfType<T>().Single(control => control.AccessibleName == accessibleName);

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Prepare(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.None;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
    }

    private static void Snapshot(Control control, string path)
    {
        control.PerformLayout();
        Application.DoEvents();
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, control.ClientRectangle);
        bitmap.Save(path);
    }

    private static void Reject(Action action, string description)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(description + " was accepted.");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}

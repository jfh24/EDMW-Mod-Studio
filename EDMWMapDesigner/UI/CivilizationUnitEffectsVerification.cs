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
            Require(Control<ComboBox>(dialog, "Effect target").Enabled && !Control<ComboBox>(dialog, "Ability activation mode").Enabled,
                "Proximity controls must enable target/radius and disable activation controls.");
            Snapshot(dialog, Path.Combine(output, "proximity-attachment.png"));
            dialog.AddForVerification();
            Control<TextBox>(dialog, "Effect name").Text = "Ranger scouting toggle";
            Control<ComboBox>(dialog, "Effect trigger").SelectedIndex = 1;
            Control<ComboBox>(dialog, "Ability activation mode").SelectedIndex = 0;
            Control<TextBox>(dialog, "In-game button caption").Text = "Scout";
            Control<NumericUpDown>(dialog, "Sight adjustment in tiles").Value = 2;
            Require(!Control<ComboBox>(dialog, "Effect target").Enabled && Control<ComboBox>(dialog, "In-game button appearance").Enabled &&
                    !Control<NumericUpDown>(dialog, "Ability duration in seconds").Enabled, "Toggle controls");
            Snapshot(dialog, Path.Combine(output, "activated-attachment.png"));
            dialog.AddForVerification();
            Control<TextBox>(dialog, "Effect name").Text = "Temporary scouting";
            Control<ComboBox>(dialog, "Effect trigger").SelectedIndex = 1;
            Control<ComboBox>(dialog, "Ability activation mode").SelectedIndex = 1;
            Control<NumericUpDown>(dialog, "Ability duration in seconds").Value = 45;
            Control<CheckBox>(dialog, "Effect enabled").Checked = false;
            Require(Control<NumericUpDown>(dialog, "Ability duration in seconds").Enabled, "Timed ability duration control");
            dialog.AddForVerification();
            dialog.RemoveForVerification();
            Require(dialog.SnapshotForVerification.Count == 3, "Remove deletes only the selected attachment");
            editor.ApplyUnitEffectsForVerification(dialog);
        }

        var edited = editor.SnapshotProject;
        var savedEntity = edited.Civilizations.Single(civ => civ.Id == civilization.Id).Entities.Single(entity => entity.Id == sourceEntity.Id);
        var rules = CivilizationUnitEffects.Read(savedEntity);
        Require(rules.Count == 3 && rules.Select(rule => rule.Id).Distinct().Count() == 3, "Multiple stable attachment IDs");
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
        Reject(() => CivilizationUnitEffects.Write(sourceEntity, [rules[0] with { Amount = 0 }]), "Zero adjustment");
        Reject(() => CivilizationUnitEffects.Write(sourceEntity, [rules[0] with { RadiusTiles = 65 }]), "Radius range");
        Reject(() => CivilizationUnitEffects.Write(sourceEntity, [rules[0], rules[0]]), "Duplicate effect ID");
        Reject(() => CivilizationUnitEffects.Write(sourceEntity, [rules[1], rules[1] with { Id = Guid.NewGuid().ToString("D") }]), "Conflicting activated button slots");
        var malformed = sourceEntity.Clone();
        malformed.Properties[CivilizationUnitEffects.PropertyKey] = "[{\"name\":\"Incomplete\"}]";
        Reject(() => CivilizationUnitEffects.Validate(malformed), "Missing effect members");
        Require(SHA256.HashData(File.ReadAllBytes(objectsPath)).AsSpan().SequenceEqual(objectsHash) &&
                SHA256.HashData(File.ReadAllBytes(buttonsPath)).AsSpan().SequenceEqual(buttonsHash), "Authoring must not modify game data");
        File.WriteAllText(Path.Combine(output, "unit-effects-result.txt"), "PASS: actual attachment controls, Ranger/Sherman preset, toggle and timed abilities, enabled state, remove, stable IDs, duplicate entry identities, validation, project undo/redo, save/reopen, reset, building attachment, unchanged source files.\n" +
            string.Join("\n", nativeButtonChecks) + "\nUNVERIFIED: live gameplay, button activation in EDMW, save/load and multiplayer.\nActivated ability state is session-only and resets when loading a save.\n");
        Console.WriteLine("UNIT EFFECT AUTHORING PASS: controls, preset, toggle/timed, enable/remove, duplicate identities, history, save/reopen, buildings, native button registration, validation, unchanged game files.");
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

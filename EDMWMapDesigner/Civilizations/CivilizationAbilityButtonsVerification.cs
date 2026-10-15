using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EDMWMapDesigner.Civilizations;

internal static class CivilizationAbilityButtonsVerification
{
    public static IReadOnlyList<string> Verify(string gamePath)
    {
        byte[] Read(string name) => File.ReadAllBytes(Path.Combine(gamePath, "Data", "db", name));
        var objects = Read("dbobjects.dat");
        var technologies = Read("dbtechtree.dat");
        var buttons = Read("dbbuttons.dat");
        var events = Read("dbevents.dat");
        var originalHashes = new[] { objects, technologies, buttons, events }.Select(SHA256.HashData).ToArray();
        var source = new CivilizationEntityDraft { Name = "Ranger", Category = CivilizationCategory.Units,
            SourceObjectId = 749, SourceTechnologyId = 10166 };
        const int row = 11;
        var freeSlots = CivilizationUnitEffectBindings.AvailableButtonSlots(objects, technologies, buttons, source, row);
        Require(freeSlots.Count >= 2, "Ranger needs two free verified HUD slots for the test.");
        var effect = new CivilizationUnitEffect
        {
            Id = "49793502-bdb5-4914-9fc1-801d783f177c", Name = "Recon sight", TargetObjectId = 749,
            Trigger = CivilizationUnitEffectTrigger.Activated, ActivationMode = CivilizationUnitEffectActivationMode.Toggle,
            ButtonCaption = "Recon sight", ButtonIconId = 1350, ButtonSlot = freeSlots[0], Amount = 3
        };
        CivilizationActivatedAbilityRequest Request(CivilizationUnitEffect value) => new(row, source, value);
        CivilizationAbilityButtonBuild Build(params CivilizationUnitEffect[] values) =>
            CivilizationUnitEffectBindings.BuildAbilityButtons(objects, technologies, buttons, events, values.Select(Request).ToArray());
        var built = Build(effect);
        var registration = built.Registrations.Single();
        var originalRecords = CivilizationTechnologyBindings.ReadRecords(technologies);
        var resultingRecords = CivilizationTechnologyBindings.ReadRecords(built.Technologies);
        var ability = resultingRecords.Single(record => record.TechId == registration.TechnologyId);
        Require(resultingRecords.Count == originalRecords.Count + 1 && registration.CivilizationId == 12 && registration.SourceObjectId == 749,
            "One uniquely owned native command is appended.");
        Require(ability.NodeKind == 3 && ability.CommandId == 51 && ability.EventId == 0 &&
            !ability.CreatesObject && !ability.EditorOnly && ability.PrerequisiteTechnologyIds.Count == 0 &&
            ability.StartAge == 0 && ability.EndAge == 14 && ability.ProducerObjectIds.SequenceEqual([749]) &&
            ability.AllowedCivilizationRows.SequenceEqual([row]), "Immediate empty-event action identity and membership.");
        Require(ability.ButtonId == 1350 && ability.NameStringId == 1000000 + registration.TechnologyId &&
            ability.ToolTipStringId == 2000000 + registration.TechnologyId &&
            CivilizationTechnologyBindings.ReadProgressionButtonSlot(ability, row, buttons) == effect.ButtonSlot,
            "Visible game icon, reserved caption/tooltip resources and requested HUD slot.");
        Require(ability.WoodCost == 0 && ability.StoneCost == 0 && ability.GoldCost == 0 && ability.FoodCost == 0 &&
            BinaryPrimitives.ReadInt32LittleEndian(built.Technologies.AsSpan(ability.Offset + 136, 4)) == 0,
            "Fallback action has no resource cost.");
        Require(registration.Caption == "Recon sight" && registration.Tooltip.Contains("Click again to turn off.", StringComparison.Ordinal) &&
            registration.Tooltip.Contains("reset when a save is loaded", StringComparison.Ordinal), "Caption and session-only toggle help.");
        foreach (var record in originalRecords)
        {
            var resulting = resultingRecords.Single(item => item.TechId == record.TechId);
            Require(technologies.AsSpan(record.Offset, record.Length).SequenceEqual(built.Technologies.AsSpan(resulting.Offset, resulting.Length)),
                $"Existing technology {record.TechId} must retain every byte.");
        }
        var expectedObjects = (byte[])objects.Clone();
        CivilizationTechnologyBindings.WriteProgressionProducerAction(expectedObjects, 749, registration.TechnologyId);
        Require(expectedObjects.SequenceEqual(built.Objects), "Only the source object's action count/list changes.");

        var timed = effect with { Id = "49793502-bdb5-4914-9fc1-801d783f177d", ButtonSlot = freeSlots[1],
            ActivationMode = CivilizationUnitEffectActivationMode.Timed, DurationSeconds = 45 };
        var multiple = Build(effect, timed);
        var reversed = Build(timed, effect);
        Require(multiple.Objects.SequenceEqual(reversed.Objects) && multiple.Technologies.SequenceEqual(reversed.Technologies) &&
            multiple.Registrations.SequenceEqual(reversed.Registrations) && multiple.Registrations.Select(item => item.TechnologyId).Distinct().Count() == 2,
            "Technology allocation is deterministic and unique across attachment order.");
        Require(multiple.Registrations[1].Tooltip.Contains("Lasts 45 seconds", StringComparison.Ordinal), "Timed ability duration is disclosed.");
        var exported = Build(effect with { NativeTechnologyId = registration.TechnologyId });
        Require(exported.Objects.SequenceEqual(built.Objects) && exported.Technologies.SequenceEqual(built.Technologies),
            "Exported command metadata produces the identical native registration.");

        ExpectInvalid(() => Build(effect with { NativeTechnologyId = 10166 }), "Occupied native technology ID");
        ExpectInvalid(() => Build(effect, timed with { ButtonSlot = effect.ButtonSlot }), "Duplicate custom HUD slot");
        var occupied = Enumerable.Range(1, 41).Except(freeSlots).First();
        ExpectInvalid(() => Build(effect with { ButtonSlot = occupied }), "Occupied native HUD slot");
        ExpectInvalid(() => Build(effect with { ButtonIconId = 99999 }), "Missing icon resource");
        var missingTexture = (byte[])buttons.Clone();
        missingTexture.AsSpan(4 + effect.ButtonIconId * 228 + 100, 100).Clear();
        ExpectInvalid(() => CivilizationUnitEffectBindings.BuildAbilityButtons(objects, technologies, missingTexture, events, [Request(effect)]),
            "A named button without a texture reference");
        ExpectInvalid(() => Build(effect with { ButtonCaption = "Recon \u2603" }), "Unsupported caption encoding");
        ExpectInvalid(() => Build(effect with { Id = "malformed" }), "Malformed attachment identity");
        var staleSource = source.Clone();
        staleSource.SourceTechnologyId = 1;
        ExpectInvalid(() => CivilizationUnitEffectBindings.BuildAbilityButtons(objects, technologies, buttons, events,
            [new(row, staleSource, effect)]), "Stale source identity");
        var forgedObjects = (byte[])objects.Clone();
        var sourceOffset = CivilizationObjectBindings.ValidateRecord(objects, 749, CivilizationCategory.Units);
        BinaryPrimitives.WriteInt32LittleEndian(forgedObjects.AsSpan(sourceOffset + CivilizationObjectBindings.RecordIdentityOffset), 777);
        ExpectInvalid(() => CivilizationUnitEffectBindings.BuildAbilityButtons(forgedObjects, technologies, buttons, events, [Request(effect)]),
            "Forged native source row identity");
        var nonemptyEvents = new byte[events.Length + 4];
        events.AsSpan(0, 109).CopyTo(nonemptyEvents);
        BinaryPrimitives.WriteInt32LittleEndian(nonemptyEvents.AsSpan(105), 1);
        events.AsSpan(109).CopyTo(nonemptyEvents.AsSpan(113));
        ExpectInvalid(() => CivilizationUnitEffectBindings.BuildAbilityButtons(objects, technologies, buttons, nonemptyEvents, [Request(effect)]),
            "Fallback event zero must remain empty");
        var disabled = Build(effect with { Enabled = false });
        Require(disabled.Registrations.Count == 0 && ReferenceEquals(disabled.Objects, objects) && ReferenceEquals(disabled.Technologies, technologies),
            "Disabled abilities do not install buttons.");
        var inputs = new[] { objects, technologies, buttons, events };
        for (var i = 0; i < inputs.Length; i++) Require(SHA256.HashData(inputs[i]).SequenceEqual(originalHashes[i]), "All source input arrays are immutable.");
        return ["PASS: native ability button framing, source/civilization identity, empty fallback, caption/icon/slot mappings.",
            "PASS: deterministic unique IDs, exported metadata, timed/toggle help and exact stock-record preservation.",
            "PASS: occupied slots/IDs, invalid icons/captions/identities and nonempty fallback rejected; disabled abilities omitted."];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("Ability button verification failed: " + message);
    }

    private static void ExpectInvalid(Action action, string description)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("Ability button verification did not reject: " + description);
    }
}

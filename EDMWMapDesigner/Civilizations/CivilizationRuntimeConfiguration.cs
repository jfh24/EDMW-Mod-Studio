using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

public readonly record struct CivilizationRuntimeUnitOutput(CivilizationUnitEffectStat Stat, float Amount);

public readonly record struct CivilizationRuntimeUnitRule(Guid Id, int CivilizationId, int SourceObjectId,
    int TargetObjectId, float Amount, int RadiusTiles,
    CivilizationUnitEffectTrigger Trigger = CivilizationUnitEffectTrigger.Proximity,
    CivilizationUnitEffectActivationMode ActivationMode = CivilizationUnitEffectActivationMode.Toggle,
    int DurationMilliseconds = 0, int TechnologyId = 0, string Caption = "", bool Enabled = true,
    CivilizationUnitEffectStat Stat = CivilizationUnitEffectStat.LineOfSight,
    IReadOnlyList<CivilizationRuntimeUnitOutput>? Outputs = null);

public sealed record CivilizationRuntimeConfiguration(IReadOnlyList<CivilizationRuntimeRule> Research,
    IReadOnlyList<CivilizationRuntimeUnitRule> Units)
{
    public int Count => Research.Count + Units.Count;

    public static IReadOnlyList<CivilizationRuntimeUnitOutput> GetOutputs(CivilizationRuntimeUnitRule rule) =>
        rule.Outputs ?? [new(rule.Stat, rule.Amount)];

    public static bool Equivalent(CivilizationRuntimeUnitRule left, CivilizationRuntimeUnitRule right) =>
        left with { Amount = 0, Stat = CivilizationUnitEffectStat.LineOfSight, Outputs = null } ==
        right with { Amount = 0, Stat = CivilizationUnitEffectStat.LineOfSight, Outputs = null } &&
        GetOutputs(left).SequenceEqual(GetOutputs(right));

    public static IReadOnlyList<CivilizationRuntimeUnitRule> UnitRulesFor(CivilizationDraft civilization, CivilizationEntityDraft entity)
    {
        var effects = CivilizationUnitEffects.Read(entity).Where(effect => effect.Enabled).ToArray();
        if (effects.Length == 0) return [];
        if (civilization.SourceRowIndex is not (>= 3 and <= 11) ||
            civilization.SourceCivilizationId != civilization.SourceRowIndex + 1 || entity.SourceObjectId is null)
            throw new InvalidDataException("Runtime unit effects require an existing playable civilization and source object.");
        if (effects.Any(effect => effect.Trigger == CivilizationUnitEffectTrigger.Activated && effect.NativeTechnologyId is null))
            throw new InvalidDataException($"{entity.Name}: activated abilities need a generated in-game command before export.");
        foreach (var effect in effects)
            foreach (var output in CivilizationUnitEffects.GetOutputs(effect))
                if (!CivilizationUnitEffects.GetStatDefinition(output.Stat).RuntimeSupported)
                    throw new InvalidDataException($"{effect.Name}: {CivilizationUnitEffects.GetStatDefinition(output.Stat).Name} can be saved in this project, but its live runtime adapter is not available yet. Disable this effect before exporting.");
        if (effects.Sum(effect => (long)CivilizationUnitEffects.GetTargetObjectIds(effect).Count) > CivilizationRuntimeEffects.MaximumRules)
            throw new InvalidDataException($"{entity.Name}: the selected targets exceed the {CivilizationRuntimeEffects.MaximumRules} runtime rule limit.");
        var expanded = effects.SelectMany(effect =>
        {
            var outputs = CivilizationUnitEffects.GetOutputs(effect).Select(output =>
                new CivilizationRuntimeUnitOutput(output.Stat, (float)output.Amount)).ToArray();
            var targets = CivilizationUnitEffects.GetTargetObjectIds(effect);
            return targets.Select(target => new CivilizationRuntimeUnitRule(
            targets.Count == 1 ? Guid.Parse(effect.Id) : TargetRuleId(Guid.Parse(effect.Id), target), civilization.SourceCivilizationId!.Value,
            entity.SourceObjectId.Value, target, outputs[0].Amount,
            effect.Trigger == CivilizationUnitEffectTrigger.Proximity ? effect.RadiusTiles : 0,
            effect.Trigger,
            effect.Trigger == CivilizationUnitEffectTrigger.Proximity ? CivilizationUnitEffectActivationMode.Toggle : effect.ActivationMode,
            effect.Trigger == CivilizationUnitEffectTrigger.Activated && effect.ActivationMode == CivilizationUnitEffectActivationMode.Timed ? checked(effect.DurationSeconds * 1000) : 0,
            effect.Trigger == CivilizationUnitEffectTrigger.Activated ? effect.NativeTechnologyId!.Value : 0,
            effect.Trigger == CivilizationUnitEffectTrigger.Activated ? effect.ButtonCaption : "", Stat: outputs[0].Stat,
            Outputs: outputs.Length == 1 ? null : outputs));
        }).ToArray();
        if (expanded.Length > CivilizationRuntimeEffects.MaximumRules)
            throw new InvalidDataException($"{entity.Name}: the selected targets exceed the {CivilizationRuntimeEffects.MaximumRules} runtime rule limit.");
        return expanded;
    }

    private static Guid TargetRuleId(Guid effect, int target)
    {
        // Fixed namespace + authored identity + target: independent of selection order,
        // other targets, output values, source roster order and export command IDs.
        Span<byte> input = stackalloc byte[36];
        new Guid("f9e8fd43-d726-4c22-965c-9d21fc7b7d27").TryWriteBytes(input);
        effect.TryWriteBytes(input[16..]);
        BinaryPrimitives.WriteInt32LittleEndian(input[32..], target);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }

    internal static byte[]? DataFor(CivilizationDraft civilization, CivilizationEntityDraft entity)
    {
        var research = CivilizationRuntimeEffects.RulesFor(civilization, entity);
        var units = UnitRulesFor(civilization, entity);
        if (research.Count + units.Count == 0) return null;
        return units.Count == 0 ? CivilizationRuntimeEffects.Encode(research) : Encode(research, units);
    }

    public static byte[] Encode(IEnumerable<CivilizationRuntimeRule> research, IEnumerable<CivilizationRuntimeUnitRule> unitRules)
    {
        var researchBytes = CivilizationRuntimeEffects.Encode(research);
        var units = unitRules.OrderBy(rule => rule.Id).ToArray();
        var researchCount = (researchBytes.Length - 12) / 16;
        if (researchCount + units.Length > CivilizationRuntimeEffects.MaximumRules)
            throw new InvalidDataException("Too many runtime effect rules.");
        foreach (var rule in units) Validate(rule);
        var bytes = new byte[16 + researchCount * 16 + units.Sum(rule => 60 + GetOutputs(rule).Count * 8 + rule.Caption.Length)];
        "EDMWEF05"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), researchCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), units.Length);
        researchBytes.AsSpan(12).CopyTo(bytes.AsSpan(16));
        var offset = 16 + researchCount * 16;
        foreach (var rule in units)
        {
            var outputs = GetOutputs(rule);
            var record = bytes.AsSpan(offset, 60 + outputs.Count * 8 + rule.Caption.Length);
            rule.Id.TryWriteBytes(record);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], rule.CivilizationId);
            BinaryPrimitives.WriteInt32LittleEndian(record[20..], rule.SourceObjectId);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], rule.TargetObjectId);
            BinaryPrimitives.WriteInt32LittleEndian(record[28..], 0); // Reserved by the version 5 framing.
            BinaryPrimitives.WriteInt32LittleEndian(record[32..], rule.RadiusTiles);
            BinaryPrimitives.WriteUInt32LittleEndian(record[36..], (uint)rule.Trigger + 1u | (rule.Enabled ? 0u : 0x80000000u));
            BinaryPrimitives.WriteInt32LittleEndian(record[40..], (int)rule.ActivationMode + 1);
            BinaryPrimitives.WriteInt32LittleEndian(record[44..], rule.DurationMilliseconds);
            BinaryPrimitives.WriteInt32LittleEndian(record[48..], rule.TechnologyId);
            BinaryPrimitives.WriteInt32LittleEndian(record[52..], rule.Caption.Length);
            BinaryPrimitives.WriteInt32LittleEndian(record[56..], outputs.Count);
            for (var index = 0; index < outputs.Count; index++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(record[(60 + index * 8)..], (int)outputs[index].Stat);
                BinaryPrimitives.WriteSingleLittleEndian(record[(64 + index * 8)..], outputs[index].Amount);
            }
            Encoding.ASCII.GetBytes(rule.Caption, record[(60 + outputs.Count * 8)..]);
            offset += record.Length;
        }
        _ = Decode(bytes);
        return bytes;
    }

    public static CivilizationRuntimeConfiguration Decode(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual("EDMWEF01"u8))
            return new(CivilizationRuntimeEffects.Decode(bytes), []);
        var version5 = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual("EDMWEF05"u8);
        var version4 = version5 || bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual("EDMWEF04"u8);
        var version3 = version4 || bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual("EDMWEF03"u8);
        if (bytes.Length < 16 || (!version3 && !bytes.AsSpan(0, 8).SequenceEqual("EDMWEF02"u8)))
            throw new InvalidDataException("The runtime effect configuration header is invalid.");
        var researchCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var unitCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        if (researchCount < 0 || unitCount < 0 || (long)researchCount + unitCount > CivilizationRuntimeEffects.MaximumRules ||
            bytes.Length < 16L + researchCount * 16L + unitCount * (version4 ? 60L : version3 ? 56L : 36L))
            throw new InvalidDataException("The runtime effect configuration framing is invalid.");
        var researchBytes = new byte[12 + researchCount * 16];
        "EDMWEF01"u8.CopyTo(researchBytes);
        BinaryPrimitives.WriteInt32LittleEndian(researchBytes.AsSpan(8), researchCount);
        bytes.AsSpan(16, researchCount * 16).CopyTo(researchBytes.AsSpan(12));
        var research = CivilizationRuntimeEffects.Decode(researchBytes);
        var units = new List<CivilizationRuntimeUnitRule>();
        var ids = new HashSet<Guid>();
        var technologies = new HashSet<int>();
        var offset = 16 + researchCount * 16;
        for (var i = 0; i < unitCount; i++)
        {
            var size = version4 ? 60 : version3 ? 56 : 36;
            if (bytes.Length - offset < size) throw new InvalidDataException("A runtime unit rule is truncated.");
            var record = bytes.AsSpan(offset, size);
            var rule = new CivilizationRuntimeUnitRule(new Guid(record[..16]),
                BinaryPrimitives.ReadInt32LittleEndian(record[16..]), BinaryPrimitives.ReadInt32LittleEndian(record[20..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[24..]), version5 ? 0 : version4 ? BinaryPrimitives.ReadSingleLittleEndian(record[28..]) : BinaryPrimitives.ReadInt32LittleEndian(record[28..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[32..]));
            if (version3)
            {
                var captionLength = BinaryPrimitives.ReadInt32LittleEndian(record[52..]);
                var outputCount = version5 ? BinaryPrimitives.ReadInt32LittleEndian(record[56..]) : 0;
                if (version5 && (outputCount is < 1 or > CivilizationUnitEffects.MaximumOutputs ||
                    BinaryPrimitives.ReadInt32LittleEndian(record[28..]) != 0))
                    throw new InvalidDataException("A runtime effect output list is invalid.");
                var outputBytes = outputCount * 8;
                if (captionLength is < 0 or > 40 || bytes.Length - offset - size < outputBytes + captionLength ||
                    bytes.AsSpan(offset + size + outputBytes, captionLength).ContainsAnyExceptInRange((byte)32, (byte)126))
                    throw new InvalidDataException("A runtime ability caption is invalid.");
                var outputs = new CivilizationRuntimeUnitOutput[outputCount];
                for (var index = 0; index < outputCount; index++)
                    outputs[index] = new((CivilizationUnitEffectStat)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + size + index * 8)),
                        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + size + index * 8 + 4)));
                rule = rule with
                {
                    Trigger = (CivilizationUnitEffectTrigger)((BinaryPrimitives.ReadUInt32LittleEndian(record[36..]) & 0x7FFFFFFFu) - 1),
                    Enabled = (BinaryPrimitives.ReadUInt32LittleEndian(record[36..]) & 0x80000000u) == 0,
                    ActivationMode = (CivilizationUnitEffectActivationMode)(BinaryPrimitives.ReadInt32LittleEndian(record[40..]) - 1),
                    DurationMilliseconds = BinaryPrimitives.ReadInt32LittleEndian(record[44..]),
                    TechnologyId = BinaryPrimitives.ReadInt32LittleEndian(record[48..]),
                    Stat = version5 ? outputs[0].Stat : version4 ? (CivilizationUnitEffectStat)BinaryPrimitives.ReadInt32LittleEndian(record[56..]) : CivilizationUnitEffectStat.LineOfSight,
                    Amount = version5 ? outputs[0].Amount : rule.Amount,
                    Outputs = outputCount > 1 ? outputs : null,
                    Caption = Encoding.ASCII.GetString(bytes, offset + size + outputBytes, captionLength)
                };
                size += outputBytes + captionLength;
            }
            Validate(rule);
            if (!ids.Add(rule.Id) || (rule.Trigger == CivilizationUnitEffectTrigger.Activated && !technologies.Add(rule.TechnologyId)))
                throw new InvalidDataException("Runtime rules have duplicate effect or command identities.");
            units.Add(rule);
            offset += size;
        }
        if (offset != bytes.Length) throw new InvalidDataException("The runtime effect configuration has trailing data.");
        if (research.Any(rule => technologies.Contains(rule.TechnologyId)))
            throw new InvalidDataException("An ability command cannot also be used as a research bonus trigger.");
        return new(research, units);
    }

    private static void Validate(CivilizationRuntimeUnitRule rule)
    {
        if (rule.Id == Guid.Empty || rule.CivilizationId is < 4 or > 12 ||
            rule.SourceObjectId is < 0 or >= 100_000 || rule.TargetObjectId is < 0 or >= 100_000 ||
            !Enum.IsDefined(rule.Trigger) || !Enum.IsDefined(rule.ActivationMode) || rule.Caption is null)
            throw new InvalidDataException("A runtime unit rule is invalid.");
        var outputs = GetOutputs(rule);
        if (outputs.Count is < 1 or > CivilizationUnitEffects.MaximumOutputs)
            throw new InvalidDataException("A runtime unit effect must contain at least one output.");
        var stats = new HashSet<CivilizationUnitEffectStat>();
        foreach (var output in outputs)
        {
            if (!float.IsFinite(output.Amount) || !Enum.IsDefined(output.Stat) || !stats.Add(output.Stat))
                throw new InvalidDataException("A runtime unit effect has an invalid or duplicate stat output.");
            var stat = CivilizationUnitEffects.GetStatDefinition(output.Stat);
            if (!stat.RuntimeSupported)
                throw new InvalidDataException($"{stat.Name} does not yet have a supported runtime adapter.");
            if (output.Amount == 0 || output.Amount < (float)stat.MinimumAmount || output.Amount > (float)stat.MaximumAmount)
                throw new InvalidDataException($"The runtime adjustment for {stat.Name} is outside its supported range.");
            CivilizationUnitEffects.ValidateAmount(output.Stat, (decimal)output.Amount);
        }
        if (rule.Trigger == CivilizationUnitEffectTrigger.Proximity)
        {
            if (!rule.Enabled || rule.RadiusTiles is < 1 or > 64 || rule.ActivationMode != CivilizationUnitEffectActivationMode.Toggle ||
                rule.DurationMilliseconds != 0 || rule.TechnologyId != 0 || rule.Caption.Length != 0)
                throw new InvalidDataException("A runtime proximity rule is invalid.");
        }
        else if (rule.RadiusTiles != 0 || rule.TargetObjectId != rule.SourceObjectId || rule.TechnologyId is < 10000 or > 14999 ||
            string.IsNullOrWhiteSpace(rule.Caption) || rule.Caption.Length > 40 || rule.Caption.Any(character => character is < ' ' or > '~') ||
            (rule.ActivationMode == CivilizationUnitEffectActivationMode.Toggle ? rule.DurationMilliseconds != 0 : rule.DurationMilliseconds is < 1000 or > 3_600_000))
            throw new InvalidDataException("A runtime activated ability is invalid.");
    }
}

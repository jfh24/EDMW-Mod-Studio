using System.Buffers.Binary;

namespace EDMWMapDesigner.Civilizations;

/// <summary>
/// Reuses a portable export's record layout while deriving inherited field
/// values from the recipient's game. Sender snapshots cannot redirect an
/// unedited model mapping or another inherited source field during review.
/// </summary>
internal static class CivilizationPackageReviewSources
{
    internal static Dictionary<string, byte[]> BuildSources(CivilizationInstallerPackage incoming, string gamePath)
    {
        var root = Path.GetFullPath(gamePath);
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in incoming.Files.Where(file => CivilizationInstallerPackage.IsDatabasePath(file.RelativePath)))
        {
            var composable = CivilizationAbilityTableComposition.TryCreate(incoming, file, out var composition);
            var original = file.Original ?? (composable ? composition.Original : null);
            if (original is null) continue;
            var source = original.ToArray();
            // The manager checks exclusive replacements against their exact
            // current baseline before review reaches this helper.
            if (file.WholeFile && !composable)
            {
                result.Add(file.RelativePath, source);
                continue;
            }
            var current = ReadDatabase(CivilizationPackageManager.ResolveTarget(root, file.RelativePath));
            var ownFields = new CivilizationPackageFile
            {
                RelativePath = file.RelativePath,
                Patches = (composable ? composition.Patches : file.Patches).ToList()
            };
            var owned = CivilizationAbilityTableComposition.TranslatePatches(ownFields, source);
            var ownedSlots = owned.Select(patch => (patch.Offset, patch.Length)).ToHashSet();
            // Expected bytes are the reviewed ownership baseline. The source
            // snapshot is only layout evidence and may contain other values.
            foreach (var patch in owned) patch.Expected.CopyTo(source, patch.Offset);
            var technology = CivilizationInstallerPackage.IsTechnologyPath(file.RelativePath);
            var sourceRecords = technology ? CivilizationTechnologyBindings.ReadRecords(source).ToArray() : [];
            var currentRecords = technology
                ? CivilizationTechnologyBindings.ReadRecords(current).ToDictionary(record => record.TechId) : null;
            var sourceIndex = 0;
            foreach (var slot in CivilizationInstallerPackage.SemanticSlots(file.RelativePath, source).Distinct().OrderBy(slot => slot.Offset))
            {
                if (ownedSlots.Contains(slot)) continue;
                var currentOffset = slot.Offset;
                if (currentRecords is not null)
                {
                    while (sourceIndex + 1 < sourceRecords.Length && sourceRecords[sourceIndex + 1].Offset <= slot.Offset) sourceIndex++;
                    var sourceRecord = sourceRecords[sourceIndex];
                    if (!currentRecords.TryGetValue(sourceRecord.TechId, out var currentRecord))
                    {
                        // A foreign source command may be absent on this
                        // recipient. Its retained layout is not an owned edit.
                        continue;
                    }
                    currentOffset = currentRecord.Offset + slot.Offset - sourceRecord.Offset;
                    if (slot.Offset + slot.Length > sourceRecord.Offset + 436 ||
                        currentOffset + slot.Length > currentRecord.Offset + 436)
                        throw new InvalidDataException("A reviewed technology field is outside its fixed record.");
                }
                else
                {
                    var expectedIdentity = CivilizationInstallerPackage.IdentityGuard(file.RelativePath, source, slot.Offset);
                    var localIdentity = CivilizationInstallerPackage.IdentityGuard(file.RelativePath, current, currentOffset);
                    if (expectedIdentity.Offset != localIdentity.Offset || !expectedIdentity.Bytes.AsSpan().SequenceEqual(localIdentity.Bytes))
                        throw new InvalidDataException("A reviewed source record differs from the local game.");
                }
                if (currentOffset < 0 || currentOffset > current.Length - slot.Length)
                    throw new InvalidDataException("A reviewed source field is outside the local game table.");
                current.AsSpan(currentOffset, slot.Length).CopyTo(source.AsSpan(slot.Offset));
            }
            result.Add(file.RelativePath, source);
        }
        return result;
    }

    /// <summary>Compares owned operations while allowing sanitized inherited scalar values.</summary>
    internal static bool SameEdits(CivilizationInstallerPackage incoming, CivilizationInstallerPackage canonical)
    {
        if (incoming.Files.Count != canonical.Files.Count) return false;
        foreach (var received in incoming.Files)
        {
            var rebuilt = canonical.Files.SingleOrDefault(file => file.RelativePath.Equals(received.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (rebuilt is null || received.WholeFile != rebuilt.WholeFile) return false;
            if (CivilizationAbilityTableComposition.TryCreate(incoming, received, out var left))
            {
                if (!CivilizationAbilityTableComposition.TryCreate(canonical, rebuilt, out var right) ||
                    !left.CommandIds.SetEquals(right.CommandIds) || !SamePatches(received.RelativePath, left.Patches, right.Patches)) return false;
                if (CivilizationInstallerPackage.IsTechnologyPath(received.RelativePath))
                {
                    var leftRecords = CivilizationTechnologyBindings.ReadRecords(received.Replacement!).Where(record => left.CommandIds.Contains(record.TechId))
                        .ToDictionary(record => record.TechId);
                    var rightRecords = CivilizationTechnologyBindings.ReadRecords(rebuilt.Replacement!).Where(record => right.CommandIds.Contains(record.TechId))
                        .ToDictionary(record => record.TechId);
                    foreach (var command in leftRecords)
                    {
                        if (!rightRecords.TryGetValue(command.Key, out var record) || record.Length != command.Value.Length ||
                            !received.Replacement!.AsSpan(command.Value.Offset, command.Value.Length)
                                .SequenceEqual(rebuilt.Replacement!.AsSpan(record.Offset, record.Length))) return false;
                    }
                }
                else if (!SameActions(received.Replacement!, rebuilt.Replacement!, left.CommandIds)) return false;
                continue;
            }
            // Original carries inherited values and source-layout evidence. Its
            // integrity was checked by Review; compare the actual operations
            // with the canonical snapshot attached to both synthetic entries.
            if (!CivilizationPackageContentComparer.SameFiles([Comparable(received, rebuilt.Original)], [rebuilt])) return false;
        }
        return true;
    }

    private static CivilizationPackageFile Comparable(CivilizationPackageFile file, byte[]? original) => new()
    {
        RelativePath = file.RelativePath, WholeFile = file.WholeFile, ExpectedLength = file.ExpectedLength,
        StructuralHash = file.StructuralHash, ExpectedHash = file.ExpectedHash, Replacement = file.Replacement,
        Original = original, Patches = file.Patches
    };

    private static bool SamePatches(string path, IReadOnlyList<CivilizationPackagePatch> left, IReadOnlyList<CivilizationPackagePatch> right)
    {
        if (left.Count != right.Count) return false;
        var identityWithinRecord = CivilizationInstallerPackage.IsTechnologyPath(path) ? 104 : CivilizationObjectBindings.RecordIdentityOffset;
        (int Identity, int Offset, int Length) Key(CivilizationPackagePatch patch) =>
            (BinaryPrimitives.ReadInt32LittleEndian(patch.IdentityBytes), patch.Offset - patch.IdentityOffset + identityWithinRecord, patch.Length);
        var indexed = right.ToDictionary(Key);
        return left.All(patch => indexed.TryGetValue(Key(patch), out var compared) &&
            patch.Expected.AsSpan().SequenceEqual(compared.Expected) && patch.Replacement.AsSpan().SequenceEqual(compared.Replacement));
    }

    private static bool SameActions(byte[] left, byte[] right, IReadOnlySet<int> commands)
    {
        if (left.Length != right.Length || left.Length < 4) return false;
        var count = BinaryPrimitives.ReadInt32LittleEndian(left);
        if (count != BinaryPrimitives.ReadInt32LittleEndian(right) || count is < 1 or > 100000 ||
            left.LongLength != 4L + count * (long)CivilizationObjectBindings.RecordSize) return false;
        for (var row = 0; row < count; row++)
            if (!OwnedActions(left, row, commands).SequenceEqual(OwnedActions(right, row, commands))) return false;
        return true;
    }

    private static IEnumerable<int> OwnedActions(byte[] bytes, int row, IReadOnlySet<int> commands)
    {
        var start = 4 + row * CivilizationObjectBindings.RecordSize;
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(start + CivilizationObjectDetails.ActionTechnologyCountOffset));
        if (count is < 0 or > CivilizationObjectDetails.ActionTechnologyCount)
            throw new InvalidDataException("A reviewed ability source action list is invalid.");
        for (var index = 0; index < count; index++)
        {
            var id = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(start + CivilizationObjectDetails.ActionTechnologyIdsOffset + index * 4));
            if (commands.Contains(id)) yield return id;
        }
    }

    private static byte[] ReadDatabase(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 4 or > 32 * 1024 * 1024)
            throw new InvalidDataException("A reviewed source database exceeds its supported bounds.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
}

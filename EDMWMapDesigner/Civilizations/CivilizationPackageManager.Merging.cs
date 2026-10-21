using System.Text.Json.Serialization;

namespace EDMWMapDesigner.Civilizations;

public static partial class CivilizationPackageManager
{
    /// <summary>Creates one named Home entry while retaining each constituent installation and backup.</summary>
    public static CivilizationInstallResult MergeInstalled(string gamePath, IReadOnlyList<string> packageIds, string name)
    {
        try
        {
            var root = Root(gamePath);
            name = MergedName(name);
            EnsureGameClosed(root);
            using var packageLock = AcquireLock(root);
            ValidateNoMergedRecovery(root);
            EnsureNoInterruptedPackages(root);
            if (packageIds is null || packageIds.Count is < 2 or > 64)
                throw new InvalidDataException("Select between two and 64 installed civilization edits to merge.");
            if (packageIds.Distinct(StringComparer.Ordinal).Count() != packageIds.Count)
                throw new InvalidDataException("Select each installed civilization edit only once.");
            var groups = LoadMergedGroups(root);
            var selectedGroups = groups.Where(group => packageIds.Contains(group.Id, StringComparer.Ordinal)).ToArray();
            var memberIds = packageIds.SelectMany(id => groups.FirstOrDefault(group => group.Id == id)?.MemberIds ?? [id])
                .Distinct(StringComparer.Ordinal).ToList();
            if (memberIds.Count is < 2 or > 64 || memberIds.Contains("legacy", StringComparer.Ordinal))
                throw new InvalidDataException("A merged edit requires two to 64 current civilization packages. Earlier installations must be reinstalled first.");
            foreach (var id in memberIds) RequireId(id);
            if (groups.Except(selectedGroups).Any(group => group.MemberIds.Intersect(memberIds, StringComparer.Ordinal).Any()))
                throw new InvalidDataException("An edit already belongs to another merged entry. Select that merged entry or unmerge it first.");
            var installed = LoadInstalled(root);
            foreach (var id in memberIds)
            {
                var member = installed.SingleOrDefault(item => item.Package.PackageId == id)
                    ?? throw new InvalidDataException("A selected civilization edit is no longer installed.");
                CheckInstalledState(member.Package, root, installed);
            }
            // Replay recipients in the original installation order so any
            // progression prerequisites are installed before their consumers.
            memberIds = memberIds.OrderBy(id => installed.Single(item => item.Package.PackageId == id).InstalledAt)
                .ThenBy(id => id, StringComparer.Ordinal).ToList();
            var group = new MergedGroup { Id = Guid.NewGuid().ToString("N"), Name = name, MemberIds = memberIds, CreatedAt = DateTimeOffset.UtcNow };
            var journal = BeginMergedTransaction(root, "Merge", memberIds, [group.Id, .. selectedGroups.Select(item => item.Id)], []);
            return ExecuteMergedTransaction(root, journal, () =>
            {
                WriteJson(MergedGroupPath(root, group.Id), group);
                foreach (var previous in selectedGroups) File.Delete(MergedGroupPath(root, previous.Id));
                return new(true, $"Merged {memberIds.Count} civilization edits as '{name}'.");
            });
        }
        catch (Exception error) when (Expected(error)) { return new(false, error.Message); }
    }

    /// <summary>Restores the individual Home entries without changing any installed game data.</summary>
    public static CivilizationInstallResult Unmerge(string gamePath, string groupId)
    {
        try
        {
            var root = Root(gamePath);
            EnsureGameClosed(root);
            using var packageLock = AcquireLock(root);
            ValidateNoMergedRecovery(root);
            EnsureNoInterruptedPackages(root);
            var group = FindMergedGroup(root, groupId) ?? throw new InvalidDataException("This merged civilization entry is no longer installed.");
            var journal = BeginMergedTransaction(root, "Unmerge", group.MemberIds, [group.Id], []);
            return ExecuteMergedTransaction(root, journal, () =>
            {
                File.Delete(MergedGroupPath(root, group.Id));
                return new(true, $"Unmerged '{group.Name}'; its individual edits remain installed.");
            });
        }
        catch (Exception error) when (Expected(error)) { return new(false, error.Message); }
    }

    internal static bool IsMergedGroup(string root, string id) => FindMergedGroup(root, id) is not null;

    internal static IReadOnlyList<string> ReadGroupMembers(string root, string id) => FindMergedGroup(root, id)?.MemberIds.ToArray() ?? [];

    internal static CivilizationInstallerPackage? CreateMergedSharePackage(string root, string groupId)
    {
        ValidateNoMergedRecovery(root);
        var group = FindMergedGroup(root, groupId);
        if (group is null) return null;
        var installed = LoadInstalled(root);
        var components = new List<CivilizationInstallerPackage>();
        foreach (var id in group.MemberIds)
        {
            var member = installed.SingleOrDefault(item => item.Package.PackageId == id)
                ?? throw new InvalidDataException("A member of this merged edit is missing. Unmerge it or recover its installation before sharing.");
            CheckInstalledState(member.Package, root, installed);
            components.Add(member.ExportPackage ?? member.Package);
        }
        var project = CivilizationEditProject.CreateDefault();
        project.Name = group.Name;
        foreach (var civilization in project.Civilizations)
            civilization.Id = $"00000000-0000-0000-0000-{civilization.SourceCivilizationId!.Value:D12}";
        var package = new CivilizationInstallerPackage
        {
            FormatVersion = 2, PackageId = group.Id, ProjectName = group.Name, ProjectJson = project.Serialize(),
            Components = components, Changes = components.SelectMany(component => component.Changes).ToList()
        };
        CivilizationInstallerPackage.Validate(package);
        return package;
    }

    internal static IReadOnlyList<CivilizationInstalledPackage> ReadMergedInventory(string root, IReadOnlyList<CivilizationInstalledPackage> rawInstalled)
    {
        if (LoadMergedTransactions(root).Any(journal => journal.State == "Prepared"))
            return rawInstalled.Select(item => item with { Status = "Interrupted merged edit — recovery required" }).ToArray();
        var groups = LoadMergedGroups(root);
        var rows = rawInstalled.ToList();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group.MemberIds.Any(id => !claimed.Add(id)))
                throw new InvalidDataException("Installed merge groups overlap. Recover interrupted changes before continuing.");
            var members = rawInstalled.Where(item => group.MemberIds.Contains(item.PackageId, StringComparer.Ordinal)).ToArray();
            rows.RemoveAll(item => group.MemberIds.Contains(item.PackageId, StringComparer.Ordinal));
            var status = members.Length != group.MemberIds.Count ? "Incomplete merged edit — recovery required" :
                members.FirstOrDefault(item => item.Status != "Installed")?.Status ?? "Installed";
            rows.Add(new(group.Id, group.Name, group.CreatedAt, members.SelectMany(item => item.Changes).ToArray(), status,
                HasRuntimeEffects: members.Any(item => item.HasRuntimeEffects),
                RuntimeEffectsEnabled: members.Any(item => item.HasRuntimeEffects && item.RuntimeEffectsEnabled), ComponentCount: group.MemberIds.Count));
        }
        return rows.OrderByDescending(item => item.InstalledAt).ToArray();
    }

    /// <summary>Installs a portable merged envelope under one lock, rolling back newly installed members together.</summary>
    internal static CivilizationInstallResult InstallMerged(CivilizationInstallerPackage package, string root, bool allowAlreadyInstalled)
    {
        try
        {
            root = Root(root);
            CivilizationInstallerPackage.Validate(package);
            EnsureGameClosed(root);
            using var packageLock = AcquireLock(root);
            RecoverTransactions(root);
            RecoverMergedUnderLock(root);
            var groups = LoadMergedGroups(root);
            var ids = package.Components.Select(component => component.PackageId).ToList();
            var existingGroup = groups.SingleOrDefault(group => group.Id == package.PackageId);
            if (existingGroup is not null)
            {
                if (!allowAlreadyInstalled) throw new InvalidDataException("This merged civilization edit is already installed.");
                var exported = CreateMergedSharePackage(root, package.PackageId)!;
                if (!CivilizationPackageContentComparer.SameInstalledContent(package, exported)) throw new InvalidDataException("The installed merged identity belongs to different export contents.");
                return new(true, "Already installed on this computer.");
            }
            if (groups.Any(group => group.MemberIds.Intersect(ids, StringComparer.Ordinal).Any()))
                throw new InvalidDataException("A member of this merged edit belongs to another merged entry. Unmerge that entry before importing this one.");
            var installed = LoadInstalled(root);
            if (installed.Any(item => item.Package.PackageId == package.PackageId))
                throw new InvalidDataException("This merged identity belongs to an individual installed edit.");
            foreach (var component in package.Components)
            {
                ValidateIncomingNativeAbilityGroups(component);
                var existing = installed.SingleOrDefault(item => item.Package.PackageId == component.PackageId);
                if (existing is not null)
                {
                    if (!CivilizationPackageContentComparer.SameInstalledContent(component, existing.ExportPackage ?? existing.Package))
                        throw new InvalidDataException("An installed component identity belongs to different export contents.");
                    CheckInstalledState(existing.Package, root, installed);
                }
            }
            var review = ReviewMerged(package, root);
            if (review.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, review));
            var newcomers = package.Components.Where(component => installed.All(item => item.Package.PackageId != component.PackageId)).ToArray();
            var targets = newcomers.SelectMany(component => component.Files.Where(file => file.WholeFile || file.Patches.Count > 0))
                .Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var journal = BeginMergedTransaction(root, "Install", newcomers.Select(component => component.PackageId).ToArray(), [package.PackageId], targets);
            return ExecuteMergedTransaction(root, journal, () =>
            {
                var fileCount = 0;
                foreach (var component in newcomers)
                {
                    var effective = CivilizationRuntimePackageRebaser.Rebase(component,
                        ReadBounded(ResolveTarget(root, "Data\\db\\dbtechtree.dat")));
                    CheckCompatibility(effective, root, LoadInstalled(root), false);
                    var edits = PrepareInstall(effective, root);
                    RegisterMergedChanges(root, journal, edits);
                    var manifest = new PackageInstallation { Package = effective,
                        ExportPackage = ReferenceEquals(effective, component) ? null : component, InstalledAt = DateTimeOffset.UtcNow };
                    var result = Transact(root, effective, "Install", edits, () => WriteJson(ManifestPath(root, effective.PackageId), manifest));
                    if (!result.Success) throw new InvalidDataException(result.Message);
                    fileCount += result.FileCount;
                }
                WriteJson(MergedGroupPath(root, package.PackageId), new MergedGroup
                { Id = package.PackageId, Name = MergedName(package.ProjectName), MemberIds = ids, CreatedAt = DateTimeOffset.UtcNow });
                return new(true, $"Installed '{package.ProjectName}' with {ids.Count} civilization edits.", FileCount: fileCount);
            });
        }
        catch (Exception error) when (Expected(error)) { return new(false, error.Message); }
    }

    /// <summary>Preflights all members, then removes them as one recoverable operation.</summary>
    internal static CivilizationInstallResult UninstallMerged(string gamePath, string groupId)
    {
        try
        {
            var root = Root(gamePath);
            EnsureGameClosed(root);
            using var packageLock = AcquireLock(root);
            RecoverTransactions(root);
            RecoverMergedUnderLock(root);
            var group = FindMergedGroup(root, groupId) ?? throw new InvalidDataException("This merged civilization entry is no longer installed.");
            var installed = LoadInstalled(root);
            var members = group.MemberIds.Select(id => installed.SingleOrDefault(item => item.Package.PackageId == id)
                ?? throw new InvalidDataException("A merged component is missing. Unmerge the entry before removing its remaining edits.")).ToArray();
            var excludedPackageIds = group.MemberIds.ToHashSet(StringComparer.Ordinal);
            foreach (var member in members)
            {
                CheckCompatibility(member.Package, root, installed, true);
                _ = PrepareUninstall(member.Package, root, excludedPackageIds);
            }
            var targets = members.SelectMany(member => member.Package.Files.Where(file => file.WholeFile || file.Patches.Count > 0))
                .Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var journal = BeginMergedTransaction(root, "Uninstall", group.MemberIds, [group.Id], targets);
            return ExecuteMergedTransaction(root, journal, () =>
            {
                var fileCount = 0;
                foreach (var member in members.Reverse())
                {
                    var edits = PrepareUninstall(member.Package, root, excludedPackageIds);
                    RegisterMergedChanges(root, journal, edits);
                    var result = Transact(root, member.Package, "Uninstall", edits, () => File.Delete(ManifestPath(root, member.Package.PackageId)));
                    if (!result.Success) throw new InvalidDataException(result.Message);
                    fileCount += result.FileCount;
                }
                File.Delete(MergedGroupPath(root, group.Id));
                return new(true, $"Uninstalled '{group.Name}' and its {members.Length} civilization edits.", FileCount: fileCount);
            });
        }
        catch (Exception error) when (Expected(error)) { return new(false, error.Message); }
    }

    /// <summary>Caller holds the package lock. All runtime members are validated before their states change.</summary>
    internal static void SetMergedRuntimeEffectsEnabled(string root, string groupId, bool enabled)
    {
        ValidateNoMergedRecovery(root);
        EnsureNoInterruptedPackages(root);
        var group = FindMergedGroup(root, groupId) ?? throw new InvalidDataException("This merged civilization entry is no longer installed.");
        var installed = LoadInstalled(root);
        if (group.MemberIds.Any(id => installed.All(item => item.Package.PackageId != id)))
            throw new InvalidDataException("A member of this merged edit is missing. Unmerge the entry or recover its installation before changing runtime effects.");
        var members = installed.Where(item => group.MemberIds.Contains(item.Package.PackageId, StringComparer.Ordinal) &&
            item.Package.Files.Any(file => CivilizationRuntimeEffects.IsPath(file.RelativePath))).ToArray();
        if (members.Length == 0) throw new InvalidDataException("This merged edit has no runtime effects.");
        foreach (var member in members) CheckInstalledState(member.Package, root, installed);
        ValidateRuntimeCapacity(installed.Select(item => (item.Package, members.Contains(item) ? enabled : item.RuntimeEffectsEnabled)));
        var journal = BeginMergedTransaction(root, "Toggle", members.Select(item => item.Package.PackageId).ToArray(), [], []);
        var result = ExecuteMergedTransaction(root, journal, () =>
        {
            foreach (var member in members)
            {
                member.RuntimeEffectsEnabled = enabled;
                WriteJson(ManifestPath(root, member.Package.PackageId), member);
            }
            return new(true, "Merged runtime effects updated.");
        });
        if (!result.Success) throw new InvalidDataException(result.Message);
    }

    /// <summary>Caller holds the package lock and has recovered ordinary per-package transactions first.</summary>
    internal static int RecoverMergedUnderLock(string root)
    {
        var count = 0;
        foreach (var journal in LoadMergedTransactions(root).Where(item => item.State == "Prepared"))
        {
            RollbackMergedTransaction(root, journal);
            count++;
        }
        return count;
    }

    internal static void ValidateNoMergedRecovery(string root)
    {
        if (LoadMergedTransactions(root).Any(journal => journal.State == "Prepared"))
            throw new InvalidDataException("An interrupted merged edit needs recovery. Use Recover interrupted changes before continuing.");
    }

    private static string MergedName(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 120 || name.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
            throw new InvalidDataException("Enter a name for the merged edit using one to 120 characters.");
        return name;
    }

    private static void EnsureNoInterruptedPackages(string root)
    {
        if (LoadTransactions(root).Any(transaction => transaction.State != "Complete"))
            throw new InvalidDataException("Recover interrupted package changes before changing merged entries.");
    }

    private static string MergedGroupPath(string root, string id)
    {
        RequireId(id);
        return SafeStorage(root, "groups", id + ".json");
    }

    private static MergedGroup? FindMergedGroup(string root, string id)
    {
        RequireId(id);
        return LoadMergedGroups(root).SingleOrDefault(group => group.Id == id);
    }

    private static IReadOnlyList<MergedGroup> LoadMergedGroups(string root)
    {
        var directory = SafeStorage(root, "groups");
        if (!Directory.Exists(directory)) return [];
        var result = new List<MergedGroup>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var group = ReadJson<MergedGroup>(path);
            RequireId(group.Id);
            if (Path.GetFileNameWithoutExtension(path) != group.Id || group.Name != MergedName(group.Name) ||
                group.MemberIds is null || group.MemberIds.Count is < 2 or > 64 ||
                group.MemberIds.Distinct(StringComparer.Ordinal).Count() != group.MemberIds.Count || group.MemberIds.Contains(group.Id, StringComparer.Ordinal))
                throw new InvalidDataException("An installed merge group is invalid.");
            foreach (var id in group.MemberIds) RequireId(id);
            result.Add(group);
        }
        return result;
    }

    private static MergedTransaction BeginMergedTransaction(string root, string operation, IReadOnlyList<string> memberIds,
        IReadOnlyList<string> groupIds, IReadOnlyList<string> gameFiles)
    {
        var journal = new MergedTransaction { Id = Guid.NewGuid().ToString("N"), Operation = operation,
            MemberIds = memberIds.Distinct(StringComparer.Ordinal).ToList(), GroupIds = groupIds.Distinct(StringComparer.Ordinal).ToList() };
        journal.Directory = SafeStorage(root, "merge-transactions", journal.Id);
        Directory.CreateDirectory(journal.Directory);
        var metadataMemberIds = operation is "Merge" or "Unmerge" ? [] : journal.MemberIds;
        var targets = gameFiles.Select(path => (RelativePath: path, Protected: false))
            .Concat(metadataMemberIds.SelectMany(id => new[] { ManifestPath(root, id), SafeStorage(root, "backups", id + ".json") })
                .Concat(journal.GroupIds.Select(id => MergedGroupPath(root, id)))
                .Select(path => (RelativePath: Path.GetRelativePath(root, path), Protected: true)))
            .DistinctBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length is < 1 or > 100_000)
            throw new InvalidDataException("The merged operation exceeds its protected recovery file limit.");
        foreach (var target in targets)
        {
            var path = target.Protected ? BoundedPath(root, target.RelativePath) : ResolveTarget(root, target.RelativePath);
            var before = File.Exists(path) ? ReadBounded(path, target.Protected ? 384 * 1024 * 1024 : MaximumFileBytes) : null;
            var entry = new MergedTransactionFile { RelativePath = target.RelativePath, Protected = target.Protected,
                BackupName = journal.Files.Count + ".original", BeforeHash = before is null ? null : CivilizationInstallerPackage.Hash(before) };
            entry.AllowedHashes.Add(entry.BeforeHash);
            if (before is not null) WriteAtomic(Path.Combine(journal.Directory, entry.BackupName), before);
            journal.Files.Add(entry);
        }
        SaveMergedTransaction(journal);
        return journal;
    }

    private static void RegisterMergedChanges(string root, MergedTransaction journal, IReadOnlyList<PendingFile> pending)
    {
        foreach (var file in pending)
        {
            var tracked = journal.Files.Single(entry => !entry.Protected && entry.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase));
            foreach (var bytes in new[] { file.Before, file.After })
            {
                var hash = bytes is null ? null : CivilizationInstallerPackage.Hash(bytes);
                if (!tracked.AllowedHashes.Contains(hash, StringComparer.OrdinalIgnoreCase)) tracked.AllowedHashes.Add(hash);
            }
        }
        SaveMergedTransaction(journal);
    }

    private static CivilizationInstallResult ExecuteMergedTransaction(string root, MergedTransaction journal, Func<CivilizationInstallResult> operation)
    {
        try
        {
            var result = operation();
            if (!result.Success) throw new InvalidDataException(result.Message);
            journal.State = "Complete";
            SaveMergedTransaction(journal);
            return result;
        }
        catch (Exception error) when (Expected(error))
        {
            try
            {
                RecoverTransactions(root);
                RollbackMergedTransaction(root, journal);
                return new(false, error.Message + " The merged operation was rolled back.");
            }
            catch (Exception rollbackError) when (Expected(rollbackError))
            {
                return new(false, error.Message + " Recovery is required: " + rollbackError.Message, journal.Directory);
            }
        }
    }

    private static void RollbackMergedTransaction(string root, MergedTransaction journal)
    {
        // Check all protected originals and every game target before restoring
        // anything. Unknown external game changes must never be overwritten.
        var originals = new Dictionary<MergedTransactionFile, byte[]?>();
        foreach (var file in journal.Files)
        {
            var original = file.BeforeHash is null ? null : ReadBounded(Path.Combine(journal.Directory, file.BackupName),
                file.Protected ? 384 * 1024 * 1024 : MaximumFileBytes);
            if (!SameHash(original is null ? null : CivilizationInstallerPackage.Hash(original), file.BeforeHash))
                throw new InvalidDataException("A merged edit's protected original backup changed.");
            var target = file.Protected ? BoundedPath(root, file.RelativePath) : ResolveTarget(root, file.RelativePath);
            if (!file.Protected && !file.AllowedHashes.Contains(FileHash(target), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("A game file changed outside the interrupted merged operation: " + file.RelativePath);
            originals.Add(file, original);
        }
        foreach (var file in journal.Files.OrderBy(entry => entry.Protected))
            WriteAtomic(file.Protected ? BoundedPath(root, file.RelativePath) : ResolveTarget(root, file.RelativePath), originals[file]);
        journal.State = "Complete";
        SaveMergedTransaction(journal);
    }

    private static void SaveMergedTransaction(MergedTransaction journal) => WriteJson(Path.Combine(journal.Directory, "merge.json"), journal);

    private static IReadOnlyList<MergedTransaction> LoadMergedTransactions(string root)
    {
        var directory = SafeStorage(root, "merge-transactions");
        if (!Directory.Exists(directory)) return [];
        var result = new List<MergedTransaction>();
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            RejectReparse(folder);
            var path = Path.Combine(folder, "merge.json");
            if (!File.Exists(path)) continue;
            var journal = ReadJson<MergedTransaction>(path);
            RequireId(journal.Id);
            if (Path.GetFileName(folder) != journal.Id || journal.State is not ("Prepared" or "Complete") ||
                journal.Operation is not ("Merge" or "Unmerge" or "Install" or "Uninstall" or "Toggle") ||
                journal.MemberIds is null || journal.MemberIds.Count > 64 || journal.GroupIds is null || journal.GroupIds.Count > 65 ||
                journal.MemberIds.Count == 0 && journal.GroupIds.Count == 0 ||
                journal.Files is null || journal.Files.Count is < 1 or > 100_000)
                throw new InvalidDataException("A merged edit recovery journal is invalid.");
            foreach (var id in journal.MemberIds.Concat(journal.GroupIds)) RequireId(id);
            var metadataPaths = journal.MemberIds.SelectMany(id => new[] { ManifestPath(root, id), SafeStorage(root, "backups", id + ".json") })
                .Concat(journal.GroupIds.Select(id => MergedGroupPath(root, id))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in journal.Files)
            {
                if (file is null || !paths.Add(file.RelativePath) || Path.GetFileName(file.BackupName) != file.BackupName ||
                    !file.BackupName.EndsWith(".original", StringComparison.Ordinal) || !int.TryParse(file.BackupName[..^9], out var number) || number < 0 ||
                    file.BeforeHash is not null && !CivilizationInstallerPackage.ValidHash(file.BeforeHash) || file.AllowedHashes is null ||
                    file.AllowedHashes.Count is < 1 or > 130 || file.AllowedHashes.Any(hash => hash is not null && !CivilizationInstallerPackage.ValidHash(hash)))
                    throw new InvalidDataException("A merged edit recovery backup index is invalid.");
                if (file.Protected)
                {
                    if (!metadataPaths.Contains(BoundedPath(root, file.RelativePath)))
                        throw new InvalidDataException("A merged recovery target is outside its protected member metadata.");
                }
                else _ = ResolveTarget(root, file.RelativePath);
            }
            journal.Directory = folder;
            result.Add(journal);
        }
        return result;
    }

    private sealed class MergedGroup
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public List<string> MemberIds { get; set; } = [];
    }

    private sealed class MergedTransaction
    {
        public string Id { get; set; } = "";
        public string State { get; set; } = "Prepared";
        public string Operation { get; set; } = "";
        public List<string> MemberIds { get; set; } = [];
        public List<string> GroupIds { get; set; } = [];
        public List<MergedTransactionFile> Files { get; set; } = [];
        [JsonIgnore] public string Directory { get; set; } = "";
    }

    private sealed class MergedTransactionFile
    {
        public string RelativePath { get; set; } = "";
        public bool Protected { get; set; }
        public string BackupName { get; set; } = "";
        public string? BeforeHash { get; set; }
        public List<string?> AllowedHashes { get; set; } = [];
    }
}

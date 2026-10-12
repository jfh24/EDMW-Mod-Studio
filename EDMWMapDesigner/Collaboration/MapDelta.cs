using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Collaboration;

public sealed class ManualResourceChange
{
    public int X { get; set; }
    public int Y { get; set; }
    public ManualResource? Resource { get; set; }
}

public sealed class ManualPlayerStartChange
{
    public int PlayerSlot { get; set; }
    public ManualPlayerStart? PlayerStart { get; set; }
}

public sealed class MapDelta
{
    public int GridSize { get; set; }
    public int[] Indices { get; set; } = [];
    public int[] Cells { get; set; } = [];
    public byte[] ComponentRoles { get; set; } = [];
    public byte[] RampMask { get; set; } = [];
    public byte[] CliffMask { get; set; } = [];
    public sbyte[] ElevationMask { get; set; } = [];

    // Full lists remain on the wire so an incompatible peer cannot silently omit
    // data. Version 2 peers apply the operation lists, which merge independent edits.
    public List<ManualResource>? ManualResources { get; set; }
    public List<ManualResourceChange>? ManualResourceChanges { get; set; }
    public List<ManualPlayerStart>? ManualPlayerStarts { get; set; }
    public List<ManualPlayerStartChange>? ManualPlayerStartChanges { get; set; }
    public string? Name { get; set; }
    public string? Author { get; set; }
    public int? Variance { get; set; }
    public bool GenerationSeedChanged { get; set; }
    public int? GenerationSeed { get; set; }
    public bool? PreserveCoastline { get; set; }

    public static MapDelta? Create(MapProject before, MapProject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.GridSize != after.GridSize) return null;

        var changed = Enumerable.Range(0, after.Cells.Length)
            .Where(index => before.Cells[index] != after.Cells[index] ||
                            before.ComponentRoles[index] != after.ComponentRoles[index] ||
                            before.RampMask[index] != after.RampMask[index] ||
                            before.CliffMask[index] != after.CliffMask[index] ||
                            before.ElevationMask[index] != after.ElevationMask[index])
            .ToArray();
        var resourcesChanged = !before.ManualResources.Select(ResourceKey)
            .SequenceEqual(after.ManualResources.Select(ResourceKey));
        var playerStartsChanged = !before.ManualPlayerStarts.Select(PlayerStartKey)
            .SequenceEqual(after.ManualPlayerStarts.Select(PlayerStartKey));
        var nameChanged = !string.Equals(before.Name, after.Name, StringComparison.Ordinal);
        var authorChanged = !string.Equals(before.Author, after.Author, StringComparison.Ordinal);
        var varianceChanged = before.Variance != after.Variance;
        var generationSeedChanged = before.GenerationSeed != after.GenerationSeed;
        var coastlineChanged = before.PreserveCoastline != after.PreserveCoastline;
        if (changed.Length == 0 && !resourcesChanged && !playerStartsChanged &&
            !nameChanged && !authorChanged && !varianceChanged && !generationSeedChanged && !coastlineChanged)
            return null;

        return new MapDelta
        {
            GridSize = before.GridSize,
            Indices = changed,
            Cells = changed.Select(index => after.Cells[index]).ToArray(),
            ComponentRoles = changed.Select(index => after.ComponentRoles[index]).ToArray(),
            RampMask = changed.Select(index => after.RampMask[index]).ToArray(),
            CliffMask = changed.Select(index => after.CliffMask[index]).ToArray(),
            ElevationMask = changed.Select(index => after.ElevationMask[index]).ToArray(),
            ManualResources = resourcesChanged
                ? after.ManualResources.Select(resource => resource.Clone()).ToList()
                : null,
            ManualResourceChanges = resourcesChanged
                ? CreateResourceChanges(before.ManualResources, after.ManualResources)
                : null,
            ManualPlayerStarts = playerStartsChanged
                ? after.ManualPlayerStarts.Select(start => start.Clone()).ToList()
                : null,
            ManualPlayerStartChanges = playerStartsChanged
                ? CreatePlayerStartChanges(before.ManualPlayerStarts, after.ManualPlayerStarts)
                : null,
            Name = nameChanged ? after.Name : null,
            Author = authorChanged ? after.Author : null,
            Variance = varianceChanged ? after.Variance : null,
            GenerationSeedChanged = generationSeedChanged,
            GenerationSeed = generationSeedChanged ? after.GenerationSeed : null,
            PreserveCoastline = coastlineChanged ? after.PreserveCoastline : null
        };
    }

    public void ApplyTo(MapProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (GridSize != project.GridSize)
            throw new InvalidDataException(
                "Received a map change for a different project grid. Synchronization is required.");
        if (Indices.Length != Cells.Length || Indices.Length != ComponentRoles.Length ||
            Indices.Length != RampMask.Length || Indices.Length != ElevationMask.Length ||
            CliffMask.Length != 0 && Indices.Length != CliffMask.Length)
            throw new InvalidDataException("Received an invalid map change.");
        foreach (var index in Indices)
            if (index < 0 || index >= project.Cells.Length)
                throw new InvalidDataException("Received a map change outside the project grid.");

        // Apply to a clone and validate the complete resulting document before exposing
        // any remote state to save, export, undo/redo, or the canvas.
        var candidate = project.Clone();
        for (var i = 0; i < Indices.Length; i++)
        {
            var index = Indices[i];
            candidate.Cells[index] = Cells[i];
            candidate.ComponentRoles[index] = ComponentRoles[i];
            candidate.RampMask[index] = RampMask[i];
            // Older peers omit this optional field; preserve existing cliff authorship.
            if (CliffMask.Length != 0) candidate.CliffMask[index] = CliffMask[i];
            candidate.ElevationMask[index] = ElevationMask[i];
            var x = index % candidate.GridSize;
            var y = index / candidate.GridSize;
            var terrain = (TerrainKind)candidate.Cells[index];
            candidate.ManualResources.RemoveAll(resource => resource.X == x && resource.Y == y &&
                resource.Kind.RequiresWater() == terrain.IsLand());
            if (!MapProject.IsSafePlayerStartTerrain(terrain))
                candidate.ManualPlayerStarts.RemoveAll(start => start.X == x && start.Y == y);
        }

        ApplyResourceChanges(candidate);
        ApplyPlayerStartChanges(candidate);
        if (Name is not null) candidate.Name = Name;
        if (Author is not null) candidate.Author = Author;
        if (Variance.HasValue) candidate.Variance = Variance.Value;
        if (GenerationSeedChanged) candidate.GenerationSeed = GenerationSeed;
        if (PreserveCoastline.HasValue) candidate.PreserveCoastline = PreserveCoastline.Value;
        candidate.Validate();
        CopyProject(candidate, project);
    }

    private void ApplyResourceChanges(MapProject project)
    {
        if (ManualResourceChanges is null)
        {
            if (ManualResources is not null)
                project.ManualResources = ManualResources.Select(resource => resource.Clone()).ToList();
            return;
        }

        if (ManualResourceChanges.GroupBy(change => (change.X, change.Y)).Any(group => group.Count() != 1))
            throw new InvalidDataException("Received duplicate resource changes for one map cell.");
        foreach (var change in ManualResourceChanges)
        {
            if (change.X < 0 || change.Y < 0 || change.X >= project.GridSize || change.Y >= project.GridSize ||
                change.Resource is not null &&
                (change.Resource.X != change.X || change.Resource.Y != change.Y))
                throw new InvalidDataException("Received an invalid manual resource change.");
            project.ManualResources.RemoveAll(resource => resource.X == change.X && resource.Y == change.Y);
            if (change.Resource is not null) project.ManualResources.Add(change.Resource.Clone());
        }
        project.ManualResources.Sort(CompareResources);
    }

    private void ApplyPlayerStartChanges(MapProject project)
    {
        if (ManualPlayerStartChanges is null)
        {
            if (ManualPlayerStarts is not null)
                project.ManualPlayerStarts = ManualPlayerStarts.Select(start => start.Clone()).ToList();
            return;
        }

        if (ManualPlayerStartChanges.GroupBy(change => change.PlayerSlot).Any(group => group.Count() != 1))
            throw new InvalidDataException("Received duplicate changes for one player slot.");
        foreach (var change in ManualPlayerStartChanges)
        {
            if (change.PlayerSlot is < ManualPlayerStart.MinimumSlot or > ManualPlayerStart.MaximumSlot ||
                change.PlayerStart is not null && change.PlayerStart.PlayerSlot != change.PlayerSlot)
                throw new InvalidDataException("Received an invalid manual player-start change.");
            project.ManualPlayerStarts.RemoveAll(start => start.PlayerSlot == change.PlayerSlot);
            if (change.PlayerStart is not null) project.ManualPlayerStarts.Add(change.PlayerStart.Clone());
        }
        project.ManualPlayerStarts.Sort((left, right) => left.PlayerSlot.CompareTo(right.PlayerSlot));
    }

    private static List<ManualResourceChange>? CreateResourceChanges(
        IReadOnlyCollection<ManualResource> before, IReadOnlyCollection<ManualResource> after)
    {
        if (before.GroupBy(resource => (resource.X, resource.Y)).Any(group => group.Count() != 1) ||
            after.GroupBy(resource => (resource.X, resource.Y)).Any(group => group.Count() != 1))
            return null;
        var oldResources = before.ToDictionary(resource => (resource.X, resource.Y));
        var newResources = after.ToDictionary(resource => (resource.X, resource.Y));
        return oldResources.Keys.Union(newResources.Keys)
            .Where(key => !oldResources.TryGetValue(key, out var oldResource) ||
                          !newResources.TryGetValue(key, out var newResource) ||
                          oldResource.Kind != newResource.Kind)
            .OrderBy(key => key.Y)
            .ThenBy(key => key.X)
            .Select(key => new ManualResourceChange
            {
                X = key.X,
                Y = key.Y,
                Resource = newResources.TryGetValue(key, out var resource) ? resource.Clone() : null
            })
            .ToList();
    }

    private static List<ManualPlayerStartChange> CreatePlayerStartChanges(
        IReadOnlyCollection<ManualPlayerStart> before, IReadOnlyCollection<ManualPlayerStart> after)
    {
        var oldStarts = before.ToDictionary(start => start.PlayerSlot);
        var newStarts = after.ToDictionary(start => start.PlayerSlot);
        return oldStarts.Keys.Union(newStarts.Keys)
            .Where(slot => !oldStarts.TryGetValue(slot, out var oldStart) ||
                           !newStarts.TryGetValue(slot, out var newStart) ||
                           oldStart.X != newStart.X || oldStart.Y != newStart.Y)
            .OrderBy(slot => slot)
            .Select(slot => new ManualPlayerStartChange
            {
                PlayerSlot = slot,
                PlayerStart = newStarts.TryGetValue(slot, out var start) ? start.Clone() : null
            })
            .ToList();
    }

    private static int CompareResources(ManualResource left, ManualResource right)
    {
        var result = left.Y.CompareTo(right.Y);
        if (result != 0) return result;
        result = left.X.CompareTo(right.X);
        return result != 0 ? result : ((byte)left.Kind).CompareTo((byte)right.Kind);
    }

    private static void CopyProject(MapProject source, MapProject destination)
    {
        destination.Name = source.Name;
        destination.Author = source.Author;
        destination.GridSize = source.GridSize;
        destination.Variance = source.Variance;
        destination.GenerationSeed = source.GenerationSeed;
        destination.PreserveCoastline = source.PreserveCoastline;
        destination.ManualResources = source.ManualResources;
        destination.ManualPlayerStarts = source.ManualPlayerStarts;
        destination.Cells = source.Cells;
        destination.ComponentRoles = source.ComponentRoles;
        destination.RampMask = source.RampMask;
        destination.CliffMask = source.CliffMask;
        destination.ElevationMask = source.ElevationMask;
    }

    private static string ResourceKey(ManualResource resource) => $"{(byte)resource.Kind}:{resource.X}:{resource.Y}";
    private static string PlayerStartKey(ManualPlayerStart start) =>
        $"{start.PlayerSlot}:{start.X}:{start.Y}";
}

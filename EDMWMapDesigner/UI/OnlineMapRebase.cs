using EDMWMapDesigner.Collaboration;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.UI;

internal sealed record OnlineMapRebaseResult(MapProject Canonical, MapProject Visible, bool HasPending, bool DroppedPending);

/// <summary>Keeps unconfirmed local painting separate from the relay's confirmed document.</summary>
internal static class OnlineMapRebase
{
    public static OnlineMapRebaseResult Delta(MapProject canonical, MapProject visible, MapDelta received,
        MapDelta? submitted, bool isLocal)
    {
        var expected = canonical.Clone();
        if (isLocal && submitted is not null) submitted.ApplyTo(expected);
        var next = canonical.Clone();
        received.ApplyTo(next);
        // A local resize awaiting its full upload cannot be represented as a
        // delta. Keep that document while older-grid deltas advance the baseline.
        if (visible.GridSize != expected.GridSize)
            return new(next, visible.Clone(), true, false);
        return Overlay(next, visible, MapDelta.Create(expected, visible));
    }

    public static OnlineMapRebaseResult Project(MapProject canonical, MapProject visible, MapProject received,
        MapProject? submittedProject, bool isLocal)
    {
        received.Validate();
        var next = received.Clone();
        var expected = isLocal && submittedProject is not null ? submittedProject : canonical;
        if (visible.GridSize != expected.GridSize)
        {
            // The user resized again during upload, or their local replacement
            // is still awaiting upload while the same remote grid is refreshed.
            if (isLocal || received.GridSize == canonical.GridSize)
                return new(next, visible.Clone(), true, false);
            return new(next, next.Clone(), false, true);
        }
        var pending = MapDelta.Create(expected, visible);
        if (visible.GridSize != next.GridSize)
            return new(next, next.Clone(), false, pending is not null);
        return Overlay(next, visible, pending);
    }

    private static OnlineMapRebaseResult Overlay(MapProject canonical, MapProject visible, MapDelta? pending)
    {
        var rebased = canonical.Clone();
        if (pending is null) return new(canonical, rebased, false, false);
        try
        {
            PreservePlacementTerrain(pending, visible, rebased);
            pending.ApplyTo(rebased);
            return new(canonical, rebased, MapDelta.Create(canonical, rebased) is not null, false);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            // A remote replacement can invalidate an old resource/start edit.
            // Never publish an invalid optimistic map or corrupt the baseline.
            return new(canonical, canonical.Clone(), false, true);
        }
    }

    private static void PreservePlacementTerrain(MapDelta pending, MapProject visible, MapProject canonical)
    {
        var indices = pending.Indices.ToHashSet();
        foreach (var change in pending.ManualResourceChanges ?? [])
        {
            if (change.Resource is not { } resource) continue;
            if (resource.Kind.RequiresWater() != canonical[resource.X, resource.Y].IsWater())
                indices.Add(resource.Y * visible.GridSize + resource.X);
        }
        foreach (var change in pending.ManualPlayerStartChanges ?? [])
        {
            if (change.PlayerStart is not { } start) continue;
            if (!MapProject.IsSafePlayerStartTerrain(canonical[start.X, start.Y]))
                indices.Add(start.Y * visible.GridSize + start.X);
        }
        if (indices.Count == pending.Indices.Length) return;
        pending.Indices = indices.Order().ToArray();
        pending.Cells = pending.Indices.Select(index => visible.Cells[index]).ToArray();
        pending.ComponentRoles = pending.Indices.Select(index => visible.ComponentRoles[index]).ToArray();
        pending.RampMask = pending.Indices.Select(index => visible.RampMask[index]).ToArray();
        pending.CliffMask = pending.Indices.Select(index => visible.CliffMask[index]).ToArray();
        pending.ElevationMask = pending.Indices.Select(index => visible.ElevationMask[index]).ToArray();
    }

    public static IReadOnlyList<string> Verify()
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Online local-edit preservation failed: " + message);
        }
        var baseline = MapProject.CreateDefault(8);
        baseline.SetCell(2, 2, TerrainKind.Grassland);
        var uploaded = baseline.Clone();
        uploaded.SetCell(2, 2, TerrainKind.Mountain);
        var submitted = MapDelta.Create(baseline, uploaded)!;
        var laterStroke = uploaded.Clone();
        laterStroke.SetCell(2, 2, TerrainKind.Forest);
        laterStroke.Name = "Unsent name";
        var own = Delta(baseline, laterStroke, submitted, submitted, true);
        Require(own.Canonical[2, 2] == TerrainKind.Mountain && own.Visible[2, 2] == TerrainKind.Forest && own.HasPending &&
            own.Visible.Name == "Unsent name" && own.Canonical.Name == baseline.Name,
            "An older local echo must preserve a later same-cell stroke and unsent metadata.");
        var remote = own.Canonical.Clone();
        remote.SetCell(2, 2, TerrainKind.Desert);
        remote.SetCell(4, 4, TerrainKind.Hills);
        var remoteDelta = MapDelta.Create(own.Canonical, remote)!;
        var foreign = Delta(own.Canonical, own.Visible, remoteDelta, null, false);
        Require(foreign.Canonical[2, 2] == TerrainKind.Desert && foreign.Visible[2, 2] == TerrainKind.Forest &&
            foreign.Visible[4, 4] == TerrainKind.Hills && foreign.HasPending,
            "Foreign changes must advance the confirmed baseline and merge independent cells while preserving outstanding local intent.");
        var final = MapDelta.Create(foreign.Canonical, foreign.Visible)!;
        var settled = Delta(foreign.Canonical, foreign.Visible, final, final, true);
        Require(!settled.HasPending && MapDelta.Create(settled.Canonical, settled.Visible) is null,
            "Only the matching local echo may settle pending painting.");
        var full = Project(baseline, laterStroke, uploaded, uploaded, true);
        Require(full.Visible[2, 2] == TerrainKind.Forest && full.Canonical[2, 2] == TerrainKind.Mountain && full.HasPending,
            "A full-document echo must preserve painting performed while that upload was awaiting confirmation.");
        var replacement = Project(own.Canonical, own.Visible, remote, null, false);
        Require(replacement.Visible[2, 2] == TerrainKind.Forest && replacement.Visible[4, 4] == TerrainKind.Hills && replacement.HasPending,
            "A same-grid foreign snapshot preserves pending painting and merges its other cells.");
        var resized = Project(own.Canonical, own.Visible, MapProject.CreateDefault(16), null, false);
        Require(resized.Visible.GridSize == 16 && resized.DroppedPending && !resized.HasPending,
            "A remote resize explicitly drops edits from incompatible coordinates.");
        var localResize = own.Visible.Clone();
        localResize.Resize(16);
        var pendingResize = Delta(own.Canonical, localResize, remoteDelta, null, false);
        Require(pendingResize.Visible.GridSize == 16 && pendingResize.Canonical.GridSize == 8 && pendingResize.HasPending,
            "A local resize remains pending while old-grid foreign deltas arrive.");
        var withPlacement = baseline.Clone();
        withPlacement.ManualResources.Add(new() { Kind = ManualResourceKind.Gold, X = 2, Y = 2 });
        var flooded = baseline.Clone();
        flooded.SetCell(2, 2, TerrainKind.DeepWater);
        var placement = Delta(baseline, withPlacement, MapDelta.Create(baseline, flooded)!, null, false);
        Require(placement.Canonical[2, 2] == TerrainKind.DeepWater && placement.Visible[2, 2] == TerrainKind.Grassland &&
            placement.Visible.ManualResources.Count == 1 && placement.HasPending,
            "A pending placement preserves the compatible terrain needed for a valid next upload.");
        return ["Online local-edit rebase preserves later same-cell painting, unsent metadata, independent remote edits and full-upload edits; matching echoes settle intent; incompatible remote resizes drop stale edits explicitly."];
    }
}

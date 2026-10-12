namespace EDMWMapDesigner.Civilizations;

/// <summary>Validated project snapshots with atomic changes and a content-based saved baseline.</summary>
public sealed class CivilizationEditHistory
{
    private const int MaximumSnapshots = 100;
    private const long MaximumHistoryBytes = 128L * 1024 * 1024;
    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];
    // Captured arrays are private and never exposed to mutable project models.
    private readonly Dictionary<string, byte[]> _modelBlobs = new(StringComparer.OrdinalIgnoreCase);
    private string _savedMetadata;

    public CivilizationEditHistory(CivilizationEditProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Validate();
        Current = project.Clone();
        _savedMetadata = Current.SerializeHistoryMetadata();
    }

    public CivilizationEditProject Current { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsDirty => Current.SerializeHistoryMetadata() != _savedMetadata;

    public bool Change(Action<CivilizationEditProject> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var before = Current.SerializeHistoryMetadata();
        var next = Current.Clone();
        change(next);
        var after = next.SerializeHistoryMetadata();
        if (before == after) return false;
        _undo.Add(Capture(Current, before));
        _redo.Clear();
        Current = next;
        TrimHistory(preferUndo: true);
        return true;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        var target = Restore(_undo[^1]);
        _redo.Add(Capture(Current, Current.SerializeHistoryMetadata()));
        _undo.RemoveAt(_undo.Count - 1);
        Current = target;
        TrimHistory(preferUndo: false);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        var target = Restore(_redo[^1]);
        _undo.Add(Capture(Current, Current.SerializeHistoryMetadata()));
        _redo.RemoveAt(_redo.Count - 1);
        Current = target;
        TrimHistory(preferUndo: true);
        return true;
    }

    public void MarkSaved() => _savedMetadata = Current.SerializeHistoryMetadata();

    public void Replace(CivilizationEditProject project, bool markSaved = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        var metadata = project.SerializeHistoryMetadata();
        Current = project.Clone();
        _undo.Clear();
        _redo.Clear();
        _modelBlobs.Clear();
        if (markSaved) _savedMetadata = metadata;
    }

    private Snapshot Capture(CivilizationEditProject project, string metadata)
    {
        var modelData = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var asset in project.ModelAssets)
        {
            if (!_modelBlobs.TryGetValue(asset.Sha256, out var data))
            {
                data = (byte[])asset.Data.Clone();
                _modelBlobs.Add(asset.Sha256, data);
            }
            modelData.Add(asset.Id, data);
        }
        return new Snapshot(metadata, modelData);
    }

    private static CivilizationEditProject Restore(Snapshot snapshot) =>
        CivilizationEditProject.RestoreHistoryMetadata(snapshot.Metadata, snapshot.ModelData);

    private void TrimHistory(bool preferUndo)
    {
        while (_undo.Count + _redo.Count > MaximumSnapshots || RetainedBytes() > MaximumHistoryBytes)
        {
            var preferred = preferUndo ? _redo : _undo;
            var alternate = preferUndo ? _undo : _redo;
            if (preferred.Count > 0) preferred.RemoveAt(0);
            else if (alternate.Count > 0) alternate.RemoveAt(0);
            else break;
        }
        var retainedBlobs = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var snapshot in _undo.Concat(_redo))
            foreach (var data in snapshot.ModelData.Values) retainedBlobs.Add(data);
        foreach (var key in _modelBlobs.Where(pair => !retainedBlobs.Contains(pair.Value))
            .Select(pair => pair.Key).ToArray()) _modelBlobs.Remove(key);
    }

    private long RetainedBytes()
    {
        var bytes = 0L;
        var modelData = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var snapshot in _undo.Concat(_redo))
        {
            bytes += (long)snapshot.Metadata.Length * sizeof(char);
            bytes += (long)snapshot.ModelData.Count * 128;
            foreach (var data in snapshot.ModelData.Values) modelData.Add(data);
        }
        return bytes + modelData.Sum(data => data.LongLength);
    }

    private sealed record Snapshot(string Metadata, Dictionary<string, byte[]> ModelData);
}

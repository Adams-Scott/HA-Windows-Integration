namespace PcControl.Agent.Entities;

/// <summary>The in-memory entity list. Readers get immutable snapshots.</summary>
public sealed class EntityStore
{
    private readonly object _lock = new();
    private IReadOnlyList<Entity> _list = [];
    private IReadOnlyDictionary<string, Entity> _byId = new Dictionary<string, Entity>(StringComparer.Ordinal);
    private long _revision = 1;

    public long Revision => Interlocked.Read(ref _revision);

    public IReadOnlyList<Entity> All => _list;

    public (IReadOnlyList<Entity> Entities, long Revision) Snapshot()
    {
        lock (_lock) return (_list, _revision);
    }

    public Entity? Get(string id) => _byId.TryGetValue(id, out var e) ? e : null;

    public int ScriptCount => _list.Count(e => e is ScriptEntity);
    public int GameCount => _list.Count(e => e is GameEntity);

    /// <summary>
    /// Replaces the entity set with a fresh scan. Entities whose ID survives keep their object
    /// (and therefore their cached state and busy flag). Returns true if anything changed.
    /// </summary>
    public bool Replace(IEnumerable<Entity> fresh)
    {
        lock (_lock)
        {
            var next = new List<Entity>();
            var nextById = new Dictionary<string, Entity>(StringComparer.Ordinal);
            bool changed = false;

            foreach (var f in fresh)
            {
                if (nextById.ContainsKey(f.Id)) continue;
                Entity keep = f;
                if (_byId.TryGetValue(f.Id, out var existing) && existing.GetType() == f.GetType())
                {
                    if (!existing.SameMetadata(f))
                    {
                        existing.UpdateFrom(f);
                        changed = true;
                    }
                    keep = existing;
                }
                else
                {
                    changed = true;
                }
                next.Add(keep);
                nextById[keep.Id] = keep;
            }

            if (_byId.Keys.Any(k => !nextById.ContainsKey(k))) changed = true;

            _list = next;
            _byId = nextById;
            if (changed) _revision++;
            return changed;
        }
    }
}

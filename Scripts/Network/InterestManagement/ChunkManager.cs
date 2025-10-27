using System.Collections.Generic;
using UnityEngine;
using FishNet.Object;
using FishNet.Connection;

public class ChunkManager : NetworkBehaviour, IChunkManager
{
    [Header("AOI Grid")]
    public int cellSize = 128;
    [Range(0, 4)] public int ringRadius = 2;

    private readonly Dictionary<(int x, int y), HashSet<NetworkConnection>> _cellConns = new();
    private readonly Dictionary<NetworkConnection, (int x, int y)> _connCell = new();

    // Entità non player (NPC/loot/oggetti, senza connessione)
    private readonly Dictionary<(int x, int y), HashSet<NetworkObject>> _cellEntities = new();
    private readonly Dictionary<NetworkObject, (int x, int y)> _entityCell = new();

    // Per overlay/debug: espongo le connessioni per cella
    public IReadOnlyDictionary<(int x, int y), HashSet<NetworkConnection>> Entities => _cellConns;

    private (int x, int y) CellOf(Vector3 p)
    {
        int x = Mathf.FloorToInt(p.x / Mathf.Max(1, cellSize));
        int y = Mathf.FloorToInt(p.z / Mathf.Max(1, cellSize));
        return (x, y);
    }

    // ==== Players ====
    public void RegisterPlayer(IPlayerNetworkDriver drv)
    {
        if (!IsServerInitialized || drv == null) return;
        var nobj = drv.transform.GetComponent<NetworkObject>();
        var conn = nobj ? nobj.Owner : null;
        if (conn == null) return;

        var cell = CellOf(drv.transform.position);
        if (!_cellConns.TryGetValue(cell, out var set))
            _cellConns[cell] = set = new HashSet<NetworkConnection>();
        set.Add(conn);
        _connCell[conn] = cell;
    }

    public void UnregisterPlayer(IPlayerNetworkDriver drv)
    {
        if (!IsServerInitialized || drv == null) return;
        var nobj = drv.transform.GetComponent<NetworkObject>();
        var conn = nobj ? nobj.Owner : null;
        if (conn == null) return;

        if (_connCell.TryGetValue(conn, out var cell))
        {
            if (_cellConns.TryGetValue(cell, out var set))
            {
                set.Remove(conn);
                if (set.Count == 0) _cellConns.Remove(cell);
            }
            _connCell.Remove(conn);
        }
    }

    public void UpdatePlayerChunk(IPlayerNetworkDriver drv, Vector3 worldPos)
    {
        if (!IsServerInitialized || drv == null) return;
        var nobj = drv.transform.GetComponent<NetworkObject>();
        var conn = nobj ? nobj.Owner : null;
        if (conn == null) return;

        var newCell = CellOf(worldPos);
        if (_connCell.TryGetValue(conn, out var oldCell))
        {
            if (!oldCell.Equals(newCell))
            {
                if (_cellConns.TryGetValue(oldCell, out var prev))
                {
                    prev.Remove(conn);
                    if (prev.Count == 0) _cellConns.Remove(oldCell);
                }
                if (!_cellConns.TryGetValue(newCell, out var now))
                    _cellConns[newCell] = now = new HashSet<NetworkConnection>();
                now.Add(conn);
                _connCell[conn] = newCell;
            }
        }
        else
        {
            if (!_cellConns.TryGetValue(newCell, out var set))
                _cellConns[newCell] = set = new HashSet<NetworkConnection>();
            set.Add(conn);
            _connCell[conn] = newCell;
        }
    }

    public bool TryGetCellOf(NetworkConnection owner, out (int x, int y) cell)
        => _connCell.TryGetValue(owner, out cell);

    public void CollectWithinRadius(NetworkConnection centerConn, int radius, HashSet<NetworkConnection> results)
    {
        results.Clear();
        if (!_connCell.TryGetValue(centerConn, out var c)) return;

        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                var key = (c.x + dx, c.y + dz);
                if (_cellConns.TryGetValue(key, out var set))
                    foreach (var co in set) if (co != centerConn) results.Add(co);
            }
    }

    // ==== Entities (NPC/loot/oggetti) ====
    public void RegisterEntity(NetworkObject nobj)
    {
        if (!IsServerInitialized || nobj == null) return;
        var cell = CellOf(nobj.transform.position);
        if (!_cellEntities.TryGetValue(cell, out var set))
            _cellEntities[cell] = set = new HashSet<NetworkObject>();
        set.Add(nobj);
        _entityCell[nobj] = cell;
    }

    public void UnregisterEntity(NetworkObject nobj)
    {
        if (!IsServerInitialized || nobj == null) return;
        if (_entityCell.TryGetValue(nobj, out var cell))
        {
            if (_cellEntities.TryGetValue(cell, out var set))
            {
                set.Remove(nobj);
                if (set.Count == 0) _cellEntities.Remove(cell);
            }
            _entityCell.Remove(nobj);
        }
    }

    public void UpdateEntityChunk(NetworkObject nobj, Vector3 worldPos)
    {
        if (!IsServerInitialized || nobj == null) return;
        var newCell = CellOf(worldPos);
        if (_entityCell.TryGetValue(nobj, out var oldCell))
        {
            if (!oldCell.Equals(newCell))
            {
                if (_cellEntities.TryGetValue(oldCell, out var prev))
                {
                    prev.Remove(nobj);
                    if (prev.Count == 0) _cellEntities.Remove(oldCell);
                }
                if (!_cellEntities.TryGetValue(newCell, out var now))
                    _cellEntities[newCell] = now = new HashSet<NetworkObject>();
                now.Add(nobj);
                _entityCell[nobj] = newCell;
            }
        }
        else
        {
            if (!_cellEntities.TryGetValue(newCell, out var set))
                _cellEntities[newCell] = set = new HashSet<NetworkObject>();
            set.Add(nobj);
            _entityCell[nobj] = newCell;
        }
    }

    public void CollectEntitiesWithinRadius(NetworkConnection centerConn, int radius, HashSet<NetworkObject> results)
    {
        results.Clear();
        if (!_connCell.TryGetValue(centerConn, out var c)) return;

        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                var key = (c.x + dx, c.y + dz);
                if (_cellEntities.TryGetValue(key, out var set))
                    foreach (var e in set) results.Add(e);
            }
    }
}

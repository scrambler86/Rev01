using System.Collections.Generic;
using UnityEngine;
using FishNet.Object;
using FishNet.Connection;

/// <summary>
/// Griglia AOI semplice: mappa ogni connessione in una cella (x,y) di size configurabile,
/// e permette di collezionare connessioni nel raggio manhattan.
/// Espone anche snapshot per il debug overlay (GetRegisteredCount / GetCellsSnapshot).
/// </summary>
public class ChunkManager : NetworkBehaviour, IChunkManager
{
    [Header("Grid")]
    public int cellSize = 128;

    // conn -> cell
    private readonly Dictionary<NetworkConnection, Vector2Int> _connToCell = new();
    // cell -> set conn
    private readonly Dictionary<Vector2Int, HashSet<NetworkConnection>> _cellToConns = new();

    public override void OnStopServer()
    {
        base.OnStopServer();
        _connToCell.Clear();
        _cellToConns.Clear();
    }

    /// <summary>Registra un player nel sistema AOI (chiamato da OnStartServer del driver).</summary>
    public void RegisterPlayer(IPlayerNetworkDriver drv)
    {
        if (!IsServerInitialized) return;
        var conn = drv is NetworkBehaviour nb ? nb.Owner : null;
        if (conn == null) return;

        var pos = (drv as MonoBehaviour).transform.position;
        var cell = WorldToCell(pos);
        SetConnectionCell(conn, cell);
    }

    /// <summary>Rimuove il player dal sistema AOI (chiamato da OnStopServer/OnDisable del driver).</summary>
    public void UnregisterPlayer(IPlayerNetworkDriver drv)
    {
        if (!IsServerInitialized) return;
        var conn = drv is NetworkBehaviour nb ? nb.Owner : null;
        if (conn == null) return;

        if (_connToCell.Remove(conn))
        {
            foreach (var kvp in _cellToConns)
                kvp.Value.Remove(conn);

            var toRemove = new List<Vector2Int>();
            foreach (var kvp in _cellToConns)
                if (kvp.Value.Count == 0) toRemove.Add(kvp.Key);
            foreach (var c in toRemove) _cellToConns.Remove(c);
        }
    }

    /// <summary>Aggiorna la cella del player se è cambiata.</summary>
    public void UpdatePlayerChunk(IPlayerNetworkDriver drv, Vector3 worldPos)
    {
        if (!IsServerInitialized) return;
        var conn = drv is NetworkBehaviour nb ? nb.Owner : null;
        if (conn == null) return;

        var newCell = WorldToCell(worldPos);
        if (_connToCell.TryGetValue(conn, out var cur) && cur == newCell)
            return;

        SetConnectionCell(conn, newCell);
    }

    public bool TryGetCellOf(NetworkConnection conn, out Vector2Int cell)
        => _connToCell.TryGetValue(conn, out cell);

    public bool TryGetCellOf(IPlayerNetworkDriver drv, out Vector2Int cell)
    {
        var conn = drv is NetworkBehaviour nb ? nb.Owner : null;
        if (conn == null) { cell = default; return false; }
        return _connToCell.TryGetValue(conn, out cell);
    }

    /// <summary>
    /// Raccoglie le connessioni nel raggio manhattan intorno alla cella dell'anchor.
    /// </summary>
    public void CollectWithinRadius(NetworkConnection anchor, int ring, HashSet<NetworkConnection> into)
    {
        into.Clear();
        if (!_connToCell.TryGetValue(anchor, out var ac)) return;

        for (int dx = -ring; dx <= ring; dx++)
            for (int dy = -ring; dy <= ring; dy++)
            {
                if (Mathf.Abs(dx) + Mathf.Abs(dy) > ring) continue; // manhattan
                var c = new Vector2Int(ac.x + dx, ac.y + dy);
                if (_cellToConns.TryGetValue(c, out var set))
                {
                    foreach (var nc in set)
                        into.Add(nc);
                }
            }
    }

    // ---------- Snapshot per DebugOverlay ----------
    /// <summary>Numero totale di connessioni registrate nel sistema AOI.</summary>
    public int GetRegisteredCount() => _connToCell.Count;

    /// <summary>Riempe 'into' con snapshot (cella, conteggio) per ogni cella attiva.</summary>
    public void GetCellsSnapshot(List<(Vector2Int cell, int count)> into)
    {
        into.Clear();
        foreach (var kv in _cellToConns)
            into.Add((kv.Key, kv.Value?.Count ?? 0));
    }

    // Helpers
    private Vector2Int WorldToCell(Vector3 p)
    {
        int size = Mathf.Max(1, cellSize);
        int x = Mathf.FloorToInt(p.x / size);
        int y = Mathf.FloorToInt(p.z / size);
        return new Vector2Int(x, y);
    }

    private void SetConnectionCell(NetworkConnection conn, Vector2Int cell)
    {
        if (_connToCell.TryGetValue(conn, out var old))
        {
            if (_cellToConns.TryGetValue(old, out var sOld))
            {
                sOld.Remove(conn);
                if (sOld.Count == 0) _cellToConns.Remove(old);
            }
        }

        _connToCell[conn] = cell;

        if (!_cellToConns.TryGetValue(cell, out var sNew))
        {
            sNew = new HashSet<NetworkConnection>();
            _cellToConns[cell] = sNew;
        }
        sNew.Add(conn);
    }
}

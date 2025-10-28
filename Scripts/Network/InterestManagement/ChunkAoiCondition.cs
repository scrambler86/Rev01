using FishNet.Connection;
using FishNet.Object;
using FishNet.Observing;
using UnityEngine;

/// <summary>
/// Condizione di osservazione basata su ChunkManager (raggio manhattan).
/// Aggiungila come Condition nel NetworkObserver del prefab Player.
/// </summary>
[DisallowMultipleComponent]
public class ChunkAoiCondition : ObserverCondition
{
    [Tooltip("Raggio in celle (Manhattan) entro il quale un client può osservare questo oggetto.")]
    public int ringRadius = 2;

    private ChunkManager _chunk;

    public override void Initialize(NetworkObject nob)
    {
        base.Initialize(nob);
        _chunk = GameObject.FindObjectOfType<ChunkManager>();
        if (_chunk == null)
            Debug.LogWarning("[AOI] Nessun ChunkManager in scena. La condizione permette tutto (fallback).");
    }

    /// <summary>
    /// Dichiara che questa condizione è valutata periodicamente (Timed).
    /// Assicurati che nel NetworkObserver il Rebuild sia attivo (Timed) con un intervallo (es. 0.25s).
    /// </summary>
    public override ObserverConditionType GetConditionType() => ObserverConditionType.Timed;

    public override bool ConditionMet(NetworkConnection conn, bool alreadyAdded, out bool notProcessed)
    {
        notProcessed = false;
        if (_chunk == null) return true; // fallback: tutti vedono tutto

        // soggetto = owner di questo NetworkObject (il player a cui appartiene)
        var subjectOwner = base.NetworkObject.Owner;
        if (subjectOwner == null) return true;

        if (!_chunk.TryGetCellOf(subjectOwner, out var subjCell))
            return true; // se non mappato, lascia passare (fallback)

        if (!_chunk.TryGetCellOf(conn, out var obsCell))
            return false;

        int dx = Mathf.Abs(subjCell.x - obsCell.x);
        int dy = Mathf.Abs(subjCell.y - obsCell.y);
        return (dx + dy) <= Mathf.Max(0, ringRadius);
    }
}

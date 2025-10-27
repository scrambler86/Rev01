using UnityEngine;
using UnityEngine.UI;
using FishNet;

public class DebugOverlay : MonoBehaviour
{
    [SerializeField] Text fpsText;
    [SerializeField] Text pingText;
    [SerializeField] Text rttText;
    [SerializeField] Text backtimeText;
    [SerializeField] Text desyncText;
    [SerializeField] Text bandwidthText;

    void Update()
    {
        // ... il tuo HUD (non modificato)
    }

    void OnDrawGizmos()
    {
        var chunkManager = FindObjectOfType<ChunkManager>();
        if (chunkManager == null || chunkManager.Entities == null) return;

        int cs = Mathf.Max(1, chunkManager.cellSize);
        Gizmos.color = Color.cyan;

        foreach (var key in chunkManager.Entities.Keys)
        {
            // centro cella
            Vector3 center = new Vector3(key.x * cs + cs * 0.5f, 0, key.y * cs + cs * 0.5f);
            Gizmos.DrawWireCube(center, new Vector3(cs, 100, cs));
        }
    }
}

using UnityEngine;
using FishNet;

public class DebugOverlay : MonoBehaviour
{
    public bool showNet = true;
    public bool showAoi = true;

    private ChunkManager _aoi;
    private readonly System.Collections.Generic.List<(Vector2Int cell, int count)> _cells = new();

    void Awake()
    {
        _aoi = FindObjectOfType<ChunkManager>();
    }

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };

        GUILayout.BeginArea(new Rect(10, 10, 480, Screen.height - 20), GUI.skin.box);
        GUILayout.Label("<b>DEBUG OVERLAY</b>", style);

        if (showNet)
        {
            var cm = InstanceFinder.ClientManager;
            var sm = InstanceFinder.ServerManager;
            bool clientStarted = (cm != null && cm.Started);
            bool serverStarted = (sm != null && sm.Started);
            GUILayout.Label($"NET: ServerStarted={serverStarted}  ClientStarted={clientStarted}", style);
        }

        if (showAoi)
        {
            if (_aoi == null)
                _aoi = FindObjectOfType<ChunkManager>();

            if (_aoi != null)
            {
                int players = _aoi.GetRegisteredCount();
                _aoi.GetCellsSnapshot(_cells);
                GUILayout.Label($"AOI: players={players}  cells={_cells.Count}", style);

                // elenco celle attive
                for (int i = 0; i < _cells.Count; i++)
                {
                    var e = _cells[i];
                    GUILayout.Label($"  cell {e.cell.x},{e.cell.y} -> {e.count}", style);
                }
            }
            else
            {
                GUILayout.Label("AOI: ChunkManager non trovato in scena.", style);
            }
        }

        GUILayout.EndArea();
    }
}

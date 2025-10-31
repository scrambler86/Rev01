// BOOKMARK: FILE = MovementDebugOverlay.cs
using UnityEngine;
using UnityEngine.UI;
using Game.Network.Core;

public class MovementDebugOverlay : MonoBehaviour
{
    [Header("Refs")]
    public PlayerNetworkDriver driver;
    public Text debugText;

    void Update()
    {
        if (!driver || !debugText) return;

        // Legge i campi debug esposti dal driver
        string info =
            $"Speed: {driver.DebugPlanarSpeed:F2}\n" +
            $"Running: {driver.DebugIsRunning}\n" +
            $"Input Allowed: {driver.DebugAllowInput}\n" +
            $"Move Dir: {driver.DebugLastMoveDir}";

        debugText.text = info;
    }
}

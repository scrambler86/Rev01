using System;
using UnityEngine;

public class AntiCheatManager : MonoBehaviour, IAntiCheatValidator
{
    [Header("Logging")]
    public bool logSuspicious = false;

    [Header("Teleport / Distanza massima extra")]
    public float hardTeleportMeters = 12f;

    [Header("State gating")]
    public bool blockWhenCannotMove = true;

    public bool ValidateInput(IPlayerNetworkDriver drv, uint seq, double clientTimestamp,
                              Vector3 predictedPos, Vector3 lastServerPos,
                              float maxStepWithSlack, Vector3[] pathCorners, bool runningFlag)
    {
        var core = drv.transform.GetComponent<PlayerControllerCore>();
        if (blockWhenCannotMove && core != null && !core.AllowInput)
        {
            if (logSuspicious) Debug.LogWarning($"[AC] Move rejected (no move state) #{seq}");
            return false;
        }

        float dist = Vector2.Distance(new Vector2(predictedPos.x, predictedPos.z),
                                      new Vector2(lastServerPos.x, lastServerPos.z));
        if (dist > hardTeleportMeters)
        {
            if (logSuspicious) Debug.LogWarning($"[AC] Teleport too far {dist:F2}m #{seq}");
            return false;
        }

        if (dist > maxStepWithSlack)
        {
            if (logSuspicious) Debug.LogWarning($"[AC] Step too large {dist:F3}>{maxStepWithSlack:F3} #{seq}");
            return false;
        }

        // (Opzionale in futuro) Validare pathCorners su NavMesh lato server.

        return true;
    }
}

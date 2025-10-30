using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Validatore server-side degli input movimento del client.
/// Ritorna true se l'input sembra legittimo, false se è sospetto.
/// Viene chiamato da PlayerNetworkDriverFishNet.CmdSendInput().
/// </summary>
public class AntiCheatManager : MonoBehaviour, IAntiCheatValidator
{
    [Header("Switches")]
    [Tooltip("Controlla la distanza planare massima percorsa in 1 tick.")]
    public bool enableMaxStepCheck = true;

    [Tooltip("Verifica che la posizione proposta sia ancora su NavMesh.")]
    public bool enableNavMeshCheck = true;

    [Tooltip("Blocca spike verticali esagerati (salti/voli istantanei).")]
    public bool enableVerticalClamp = true;

    [Tooltip("Controllo extra di velocità massima simulata lato server (un po' più costoso).")]
    public bool enableCheapSim = false;

    [Tooltip("Logga warning quando falliscono i check.")]
    public bool debugLogs = true;

    [Header("Limits")]
    [Tooltip("raggio di sample NavMesh.SamplePosition, metri.")]
    public float navMeshSampleDist = 1.0f;

    [Tooltip("velocità verticale massima consentita (m/s).")]
    public float maxVerticalSpeed = 2.0f;

    [Tooltip("quanti soft-fail prima di avvisare in log.")]
    public int maxSoftFailsBeforeWarning = 30;

    [Tooltip("quanti soft-fail prima di segnalare quarantena/kick.")]
    public int maxSoftFailsBeforeQuarantine = 80;

    [Header("Telemetry (optional)")]
    public TelemetryManager telemetry;

    // conteggio dei soft-fail per clientId
    private readonly Dictionary<int, int> _softFailCounts = new Dictionary<int, int>();

    // =====================================================================
    // 1) Metodo richiesto dall'interfaccia IAntiCheatValidator
    // =====================================================================
    public bool ValidateInput(
        IPlayerNetworkDriver drv,
        uint seq,
        double timestamp,
        Vector3 predictedPos,
        Vector3 lastServerPos,
        float maxStepAllowance,
        Vector3[] pathCorners,
        bool running)
    {
        // fallback: usiamo Time.fixedDeltaTime come dt lato server
        float dtServer = Time.fixedDeltaTime;
        return ValidateInputInternal(
            drv,
            seq,
            timestamp,
            predictedPos,
            lastServerPos,
            maxStepAllowance,
            pathCorners,
            running,
            dtServer
        );
    }

    // =====================================================================
    // 2) Overload usato dal tuo driver quando può passare anche dtServer
    // =====================================================================
    public bool ValidateInput(
        PlayerNetworkDriverFishNet drv,
        uint seq,
        double timestamp,
        Vector3 predictedPos,
        Vector3 lastServerPos,
        float maxStepAllowance,
        Vector3[] pathCorners,
        bool running,
        float dtServer)
    {
        return ValidateInputInternal(
            drv,
            seq,
            timestamp,
            predictedPos,
            lastServerPos,
            maxStepAllowance,
            pathCorners,
            running,
            dtServer
        );
    }

    // =====================================================================
    // 3) Logica vera dei controlli, basata su IPlayerNetworkDriver
    // =====================================================================
    private bool ValidateInputInternal(
        IPlayerNetworkDriver drv,
        uint seq,
        double timestamp,
        Vector3 predictedPos,
        Vector3 lastServerPos,
        float maxStepAllowance,
        Vector3[] pathCorners,
        bool running,
        float dtServer)
    {
        bool ok = true;

        // ricava PlayerControllerCore per velocità consentita
        PlayerControllerCore core = null;
        MonoBehaviour mb = drv as MonoBehaviour;
        if (mb != null)
            core = mb.GetComponent<PlayerControllerCore>();

        float baseSpeed = core ? core.speed : 3.5f;
        float runMul = core ? core.runMultiplier : 1.6f;
        float allowedSpeed = running ? baseSpeed * runMul : baseSpeed;

        // 1) distanza planare massima per tick
        if (enableMaxStepCheck)
        {
            Vector2 a = new Vector2(predictedPos.x, predictedPos.z);
            Vector2 b = new Vector2(lastServerPos.x, lastServerPos.z);
            float planarDist = Vector2.Distance(a, b);

            if (planarDist > maxStepAllowance + 0.001f)
            {
                ok = false;
                if (debugLogs)
                    Debug.LogWarning($"[AC] client={drv?.OwnerClientId} seq={seq} planarDist={planarDist:0.###} > maxStep={maxStepAllowance:0.###}");
            }
        }

        // 2) validità NavMesh
        if (ok && enableNavMeshCheck)
        {
            if (!NavMesh.SamplePosition(predictedPos, out NavMeshHit nh, navMeshSampleDist, NavMesh.AllAreas))
            {
                ok = false;
                if (debugLogs)
                    Debug.LogWarning($"[AC] client={drv?.OwnerClientId} seq={seq} off-NavMesh pos={predictedPos}");
            }
            else
            {
                predictedPos = nh.position; // opzionale clamp
            }
        }

        // 3) clamp verticale
        if (ok && enableVerticalClamp)
        {
            float vy = (predictedPos.y - lastServerPos.y) / Mathf.Max(dtServer, 0.001f);
            if (Mathf.Abs(vy) > maxVerticalSpeed)
            {
                ok = false;
                if (debugLogs)
                    Debug.LogWarning($"[AC] client={drv?.OwnerClientId} seq={seq} vy={vy:0.###} > allowedVy={maxVerticalSpeed:0.###}");
            }
        }

        // 4) cheap sim di velocità massima percorribile
        if (ok && enableCheapSim)
        {
            float dist = Vector3.Distance(predictedPos, lastServerPos);
            float simMax = allowedSpeed * dtServer * 1.5f; // 50% slack
            if (dist > simMax + 0.001f)
            {
                ok = false;
                if (debugLogs)
                    Debug.LogWarning($"[AC] client={drv?.OwnerClientId} seq={seq} dist={dist:0.###} > simMax={simMax:0.###}");
            }
        }

        // bookkeeping / telemetria
        int cid = (drv != null) ? drv.OwnerClientId : -1;
        if (!_softFailCounts.ContainsKey(cid))
            _softFailCounts[cid] = 0;

        if (!ok)
        {
            _softFailCounts[cid]++;
            telemetry?.Increment($"anti_cheat.softfail.{cid}");

            if (_softFailCounts[cid] == maxSoftFailsBeforeWarning)
                Debug.LogWarning($"[AC] Client {cid} ha raggiunto {maxSoftFailsBeforeWarning} soft-fail di movimento.");
            else if (_softFailCounts[cid] >= maxSoftFailsBeforeQuarantine)
                Debug.LogWarning($"[AC] Client {cid} ha superato {maxSoftFailsBeforeQuarantine} soft-fail. Considera kick/quarantine.");
        }
        else
        {
            if (_softFailCounts[cid] > 0)
                _softFailCounts[cid] = Mathf.Max(0, _softFailCounts[cid] - 1);
        }

        return ok;
    }
}

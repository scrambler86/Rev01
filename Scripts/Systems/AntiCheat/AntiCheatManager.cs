using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// AntiCheatManager avanzato:
/// - soft-fails come prima
/// - cheap-sim server-side (opzionale) per verifiche aggiuntive
/// - escalation automatica via Telemetry counters
/// </summary>
public class AntiCheatManager : MonoBehaviour, IAntiCheatValidator
{
    [Header("Switches")]
    public bool enableMaxStepCheck = true;
    public bool enableNavMeshCheck = false;
    public bool enableVerticalClamp = false;
    public bool enableStateCheck = false;
    public bool debugLogs = false;

    [Header("Vertical")]
    public float maxVerticalSpeed = 4.0f;

    [Header("NavMesh")]
    public float navMeshSampleDist = 1.0f;

    [Header("Escalation")]
    public int maxSoftFailsBeforeQuarantine = 6;
    public float quarantineSeconds = 8f;
    public int maxSoftFailsBeforeWarning = 3;

    [Header("CheapSim")]
    public bool enableCheapSim = true;
    public float cheapSimTolerance = 0.5f; // metri

    private TelemetryManager _telemetry;

    void Awake()
    {
        _telemetry = FindObjectOfType<TelemetryManager>();
    }

    public virtual bool IsMovementAllowed(IPlayerNetworkDriver drv) => true;

    public bool ValidateInput(
        IPlayerNetworkDriver drv,
        uint seq,
        double timestampClient,
        Vector3 predictedPos,
        Vector3 lastServerPos,
        float maxStepAllowance,
        Vector3[] pathCorners,
        bool running)
    {
        const float dtFallback = 1f / 30f;
        return ValidateInput(drv, seq, timestampClient, predictedPos, lastServerPos,
                             maxStepAllowance, pathCorners, running, dtFallback);
    }

    public bool ValidateInput(
        IPlayerNetworkDriver drv,
        uint seq,
        double timestampClient,
        Vector3 predictedPos,
        Vector3 lastServerPos,
        float maxStepAllowance,
        Vector3[] pathCorners,
        bool running,
        float dtServer)
    {
        // 0) State
        if (enableStateCheck && !IsMovementAllowed(drv))
        {
            MarkSuspicious(drv, seq, "state-blocked");
            if (debugLogs) Debug.LogWarning($"[AC] Suspicious seq={seq} state-blocked");
            return false;
        }

        // 1) Max step
        if (enableMaxStepCheck)
        {
            float dist = Vector2.Distance(
                new Vector2(predictedPos.x, predictedPos.z),
                new Vector2(lastServerPos.x, lastServerPos.z));

            if (dist > maxStepAllowance)
            {
                MarkSuspicious(drv, seq, $"step dist={dist:0.###} > {maxStepAllowance:0.###}");
                if (debugLogs) Debug.LogWarning($"[AC] Suspicious seq={seq} step dist={dist:0.###} > {maxStepAllowance:0.###}");
                return false;
            }
        }

        // 2) NavMesh
        if (enableNavMeshCheck)
        {
            if (!NavMesh.SamplePosition(predictedPos, out _, navMeshSampleDist, NavMesh.AllAreas))
            {
                MarkSuspicious(drv, seq, "off-mesh");
                if (debugLogs) Debug.LogWarning($"[AC] Suspicious seq={seq} off-mesh");
                return false;
            }
        }

        // 3) Vertical
        if (enableVerticalClamp)
        {
            float dt = Mathf.Max(0.001f, dtServer);
            float vy = (predictedPos.y - lastServerPos.y) / dt;
            float limit = Mathf.Max(maxVerticalSpeed, 1.5f / dt);
            if (Mathf.Abs(vy) > limit)
            {
                MarkSuspicious(drv, seq, $"vy={vy:0.##} > {limit:0.##}");
                if (debugLogs) Debug.LogWarning($"[AC] Suspicious seq={seq} vy={vy:0.##} > {limit:0.##}");
                return false;
            }
        }

        // 4) Cheap sim validation (opzionale)
        if (enableCheapSim)
        {
            bool simOk = CheapSimValidate(drv, lastServerPos, predictedPos, dtServer, running);
            if (!simOk)
            {
                MarkSuspicious(drv, seq, "cheap-sim-fail");
                if (debugLogs) Debug.LogWarning($"[AC] Suspicious seq={seq} cheap-sim-fail");
                return false;
            }
        }

        return true;
    }

    // Semplice simulazione server-side per la posizione attesa (non una replica perfetta della fisica)
    bool CheapSimValidate(IPlayerNetworkDriver drv, Vector3 lastServerPos, Vector3 predictedPos, float dt, bool running)
    {
        if (drv == null) return true;
        // Applica una forward-step con velocità massima attesa
        float spd = 3.5f; // fallback speed (puoi prendere dal driver/core se necessario)
        if (drv is PlayerNetworkDriverFishNet pdrv)
        {
            var core = pdrv.GetComponent<PlayerControllerCore>();
            if (core != null) spd = core.speed * (core.IsRunning ? core.runMultiplier : 1f);
        }
        float maxMove = spd * Mathf.Max(0.001f, dt) * 1.15f; // tolleranza piccola
        float planar = Vector3.Distance(new Vector3(lastServerPos.x, 0, lastServerPos.z), new Vector3(predictedPos.x, 0, predictedPos.z));
        if (planar > maxMove + cheapSimTolerance) return false;
        return true;
    }

    void MarkSuspicious(IPlayerNetworkDriver drv, uint seq, string reason)
    {
        _telemetry?.Increment("anti_cheat.suspicious_events");
        if (_telemetry != null && drv != null)
        {
            string key = $"client.{drv.OwnerClientId}.softfails";
            _telemetry.Increment(key);
            long fails = _telemetry.GetInt(key);

            if (fails >= maxSoftFailsBeforeWarning && fails < maxSoftFailsBeforeQuarantine)
            {
                _telemetry?.Increment("anti_cheat.warnings");
                if (debugLogs) Debug.LogWarning($"[AC] Warning client={drv.OwnerClientId} softfails={fails} ({reason})");
            }
            else if (fails >= maxSoftFailsBeforeQuarantine)
            {
                _telemetry?.Increment("anti_cheat.quarantines");
                if (debugLogs) Debug.LogWarning($"[AC] Quarantine client={drv.OwnerClientId} after {fails} fails ({reason})");
                // Nota: l'applicazione effettiva della quarantena (ad es. bloccare input) è un'azione di policy che il server game manager dovrebbe effettuare.
            }
        }
    }
}

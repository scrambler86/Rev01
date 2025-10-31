// BOOKMARK: FILE = CanaryRuntime.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using FishNet;
using FishNet.Object;
using FishNet.Connection;
using Game.Network.Adapters.FishNet; // <-- usa l'adapter

/// <summary>
/// CanaryRuntime: invia payload di test (canary) ai client per verificare integrità trasporto.
/// - Nessuna dipendenza da Channel.*
/// - Usa FishNetAdapter per inviare TargetRpc ai client
/// - L'envelope può contenere metadata (len + hash) se serve ricostruzione client-side
/// </summary>
public class CanaryRuntime : NetworkBehaviour
{
    [Header("Canary")]
    [Tooltip("Lunghezza del payload di test in byte.")]
    public int canaryLen = 2048;

    [Tooltip("Dimensione singolo shard FEC.")]
    public int shardSize = 1024;

    [Tooltip("Numero di shard di parità (FEC). 0 = disabilitato.")]
    public int parity = 2;

    [Tooltip("Esegue automaticamente un invio ogni N secondi (solo server).")]
    public float intervalSec = 2.0f;

    [Tooltip("Avvio automatico su server.")]
    public bool autoRun = false;

    [Tooltip("Usare shard (FEC) invece di inviare il payload intero.")]
    public bool useShards = true;

    [Tooltip("Abilita il runtime (se false non invia nulla).")]
    public bool enabledRuntime = true;

    [Header("Logging")]
    public bool verboseLogs = false;

    private byte[] _canaryPayload;
#if UNITY_EDITOR
    protected new void OnValidate()
    {
        canaryLen = Mathf.Max(1, canaryLen);
        shardSize = Mathf.Max(64, shardSize);
        parity = Mathf.Max(0, parity);
        intervalSec = Mathf.Max(0.25f, intervalSec);
    }
#endif

    private void Awake()
    {
        _canaryPayload = BuildCanary(canaryLen);
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        if (!enabledRuntime) return;
        if (autoRun)
            InvokeRepeating(nameof(BroadcastOnce), 1f, intervalSec);
    }

    private void OnDisable()
    {
        if (IsServerInitialized)
            CancelInvoke(nameof(BroadcastOnce));
    }

    public static byte[] BuildCanary(int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; ++i) b[i] = (byte)(i & 0xFF);
        return b;
    }

    [ContextMenu("Canary/Broadcast Once")]
    public void BroadcastOnce() => BroadcastOnce(useShards);

    public void BroadcastOnce(bool shards)
    {
        if (!IsServerInitialized || !enabledRuntime) return;
        var dict = InstanceFinder.ServerManager?.Clients;
        if (dict == null || dict.Count == 0) return;

        foreach (var kv in dict)
        {
            var conn = kv.Value;
            if (conn == null) continue;
            SendCanaryTo(conn, shards);
        }
    }

    public void SendCanaryTo(NetworkConnection conn, bool shards)
    {
        if (!IsServerInitialized || conn == null || _canaryPayload == null) return;

        // Trova un FishNetAdapter server-side (uno qualsiasi in scena)
        var adapter = FindObjectOfType<FishNetAdapter>();
        if (adapter == null)
        {
            if (verboseLogs) Debug.LogWarning("[Canary] FishNetAdapter non trovato, annullo invio.");
            return;
        }

        if (shards && parity > 0)
        {
            // Qui puoi integrare il tuo FEC se serve. Per ora mandiamo lo shard "grezzo".
            // TODO: rimettere FEC se necessario (FecReedSolomon.BuildShards).
            var shard = _canaryPayload; // placeholder: inviamo l’intero come “shard”
            adapter.Dev_SendCanaryTo(conn, shard, true);
            if (verboseLogs) Debug.Log($"[Canary] SHARD sent to {conn.ClientId}, len={shard.Length}");
        }
        else
        {
            adapter.Dev_SendCanaryTo(conn, _canaryPayload, false);
            if (verboseLogs) Debug.Log($"[Canary] SNAPSHOT sent to {conn.ClientId}, len={_canaryPayload.Length}");
        }
    }
}

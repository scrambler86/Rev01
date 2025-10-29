using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using FishNet;                // InstanceFinder
using FishNet.Connection;     // NetworkConnection
using FishNet.Object;

public class CanaryTest : NetworkBehaviour
{
    [Header("Canary")]
    public int canaryLen = 2048;
    public int shardSize = 1024;
    public int parity = 1;
    public float intervalSec = 2.0f;
    public bool autoRun = false;
    public bool sendAsShards = true; // true = shards+parity, false = singolo full

    private byte[] _canaryPayload;
    private float _timer;

    void Awake()
    {
        BuildPayload();
    }

#if UNITY_EDITOR
    // Evita l'avviso CS0114: adesso stiamo override-ando quello di NetworkBehaviour
    protected override void OnValidate()
    {
        base.OnValidate();

        canaryLen = Mathf.Max(1, canaryLen);
        shardSize = Mathf.Max(1, shardSize);
        parity = Mathf.Max(0, parity);
    }
#endif

    void BuildPayload()
    {
        _canaryPayload = new byte[Mathf.Max(1, canaryLen)];
        for (int i = 0; i < _canaryPayload.Length; i++)
            _canaryPayload[i] = (byte)(i & 0xFF);
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        _timer = 0f;
    }

    void Update()
    {
        // fix CS0618: usa IsServerInitialized invece di IsServer
        if (!IsServerInitialized)
            return;

        if (!autoRun)
            return;

        _timer += Time.deltaTime;
        if (_timer >= intervalSec)
        {
            _timer = 0f;
            BroadcastOnce(sendAsShards);
        }
    }

    [ContextMenu("Canary → Broadcast FULL once")]
    public void BroadcastFullOnce()
    {
        BroadcastOnce(false);
    }

    [ContextMenu("Canary → Broadcast SHARDS once")]
    public void BroadcastShardsOnce()
    {
        BroadcastOnce(true);
    }

    void BroadcastOnce(bool shards)
    {
        // fix CS0618 anche qui
        if (!IsServerInitialized)
            return;

        if (_canaryPayload == null || _canaryPayload.Length == 0)
            BuildPayload();

        var clients = InstanceFinder.ServerManager?.Clients;
        if (clients == null)
            return;

        foreach (var kv in clients)
        {
            var conn = kv.Value;
            if (conn == null || !conn.IsActive)
                continue;

            SendCanaryTo(conn, shards);
        }
    }

    void SendCanaryTo(NetworkConnection conn, bool shards)
    {
        var driver = UnityEngine.Object.FindObjectOfType<PlayerNetworkDriverFishNet>();
        if (driver == null)
        {
            Debug.LogWarning("[Canary] PlayerNetworkDriverFishNet non trovato.");
            return;
        }

        // useremo sempre lo stesso messageId così il client può riconoscere “è canary”
        const uint messageId = 0xC0FFEEu;
        uint seq = unchecked((uint)Environment.TickCount);

        if (!shards || parity <= 0)
        {
            // FULL payload in un unico envelope
            ulong fullHash = EnvelopeUtil.ComputeHash64(_canaryPayload);

            var env = new Envelope
            {
                messageId = messageId,
                seq = seq,
                payloadLen = _canaryPayload.Length,
                payloadHash = fullHash,
                flags = 0x08 // 0x08 = canary full
            };

            byte[] packed = EnvelopeUtil.Pack(env, _canaryPayload);

            // manda come se fosse un normale snapshot (ma il driver lo marchia come canary e NON tenta di decodificarlo come movement)
            driver.SendPackedSnapshotToClient(conn, packed, fullHash);

            Debug.Log($"[Canary] FULL sent to {conn.ClientId}, len={_canaryPayload.Length} hash=0x{fullHash:X16}");
            return;
        }

        // SHARDS + parity: ogni shard porta i metadati della snapshot INTERA.
        var shardsList = FecReedSolomon.BuildShards(_canaryPayload, shardSize, parity);

        ulong fullHash2 = EnvelopeUtil.ComputeHash64(_canaryPayload);
        int fullLen2 = _canaryPayload.Length;

        for (int i = 0; i < shardsList.Count; i++)
        {
            var s = shardsList[i];

            var envShard = new Envelope
            {
                messageId = messageId,
                seq = seq,
                payloadLen = fullLen2,   // lunghezza TOTALE della snapshot completa
                payloadHash = fullHash2, // hash TOTALE della snapshot completa
                flags = 0x04 | 0x08      // 0x04 = shard, 0x08 = canary
            };

            byte[] packedShard = EnvelopeUtil.Pack(envShard, s);

            driver.SendPackedShardToClient(conn, packedShard);
        }

        Debug.Log($"[Canary] SHARDS sent to {conn.ClientId}, totalShards={shardsList.Count} fullLen={fullLen2}");
    }
}

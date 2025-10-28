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
    public bool useShards = true;

    private byte[] _canaryPayload;

    void Awake()
    {
        _canaryPayload = BuildCanary(canaryLen);
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        if (autoRun) InvokeRepeating(nameof(RunCanaryAll), 1f, intervalSec);
    }

    void RunCanaryAll()
    {
        // InstanceFinder.ServerManager.Clients is a dictionary-like container; iterate its Values
        var clientsDict = InstanceFinder.ServerManager.Clients;
        if (clientsDict == null) return;

        foreach (var kv in clientsDict)
        {
            var client = kv.Value;
            if (client == null) continue;
            SendCanaryTo(client.ClientId, useShards);
        }
    }

    public static byte[] BuildCanary(int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; ++i) b[i] = (byte)(i & 0xFF);
        return b;
    }

    public void SendCanaryTo(int clientId, bool shards)
    {
        var clientsDict = InstanceFinder.ServerManager.Clients;
        if (clientsDict == null) return;

        if (!clientsDict.TryGetValue(clientId, out var conn)) return;
        if (conn == null) return;

        var driver = UnityEngine.Object.FindObjectOfType<PlayerNetworkDriverFishNet>();
        if (driver == null) return;

        if (shards)
        {
            var sList = FecReedSolomon.BuildShards(_canaryPayload, shardSize, parity);
            foreach (var s in sList)
            {
                Envelope env = new Envelope { messageId = 0xC0FFEE, seq = (uint)Environment.TickCount, payloadLen = s.Length, payloadHash = EnvelopeUtil.ComputeHash64(s), flags = 0x4 };
                var packed = EnvelopeUtil.Pack(env, s);
                driver.SendPackedShardToClient(conn, packed);
            }
        }
        else
        {
            Envelope env = new Envelope { messageId = 0xC0FFEE, seq = (uint)Environment.TickCount, payloadLen = _canaryPayload.Length, payloadHash = EnvelopeUtil.ComputeHash64(_canaryPayload), flags = 0x8 | 0x1 };
            var packed = EnvelopeUtil.Pack(env, _canaryPayload);
            driver.SendPackedSnapshotToClient(conn, packed, EnvelopeUtil.ComputeHash64(_canaryPayload));
        }
    }

    [TargetRpc]
    void TargetReceiveBytes(NetworkConnection conn, byte[] bytes)
    {
        if (!EnvelopeUtil.TryUnpack(bytes, out var env, out var payload)) return;

        if ((env.flags & 0x4) != 0) // shard
        {
            var driver = UnityEngine.Object.FindObjectOfType<PlayerNetworkDriverFishNet>();
            if (driver != null) driver.HandlePackedShardPublic(payload);
            return;
        }

        if ((env.flags & 0x8) != 0) // canary full
        {
            bool ok = CheckCanary(payload);
            ServerReportCanaryResult(conn, env.seq, ok, payload?.Length ?? 0);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    void ServerReportCanaryResult(NetworkConnection who, uint seq, bool ok, int len)
    {
        Debug.Log($"[Canary] client {who.ClientId} seq={seq} ok={ok} len={len}");
    }

    static bool CheckCanary(byte[] payload)
    {
        if (payload == null) return false;
        for (int i = 0; i < payload.Length; ++i)
        {
            if (payload[i] != (byte)(i & 0xFF)) return false;
        }
        return true;
    }
}

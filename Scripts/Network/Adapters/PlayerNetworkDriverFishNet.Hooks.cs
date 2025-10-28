using UnityEngine;
using System.Collections.Generic;
using FishNet;                // InstanceFinder
using FishNet.Connection;     // NetworkConnection
using FishNet.Managing.Server; // optional clarity

// Helper statici per integrazione - chiamano i wrapper pubblici della classe driver.
public static class PlayerNetworkDriverFishNetHooks
{
    // Called by CanaryTest TargetReceive for shards
    public static void HandleExternalShard(byte[] shardBytes)
    {
        var driver = UnityEngine.Object.FindObjectOfType<PlayerNetworkDriverFishNet>();
        if (driver != null)
        {
            driver.HandlePackedShardPublic(shardBytes);
        }
    }

    // Helper: send with envelope and optional FEC shards using clientId lookup
    public static void SendWithEnvelopeToClient(int clientId, uint messageId, uint seq, byte flags, byte[] payload, bool useFec, int fecShardSize, int fecParityShards)
    {
        if (payload == null) return;

        var clientsDict = InstanceFinder.ServerManager.Clients;
        if (clientsDict == null) return;

        if (!clientsDict.TryGetValue(clientId, out var client)) return;
        if (client == null) return;

        var driver = UnityEngine.Object.FindObjectOfType<PlayerNetworkDriverFishNet>();
        if (driver == null) return;

        if (!useFec || fecParityShards <= 0)
        {
            Envelope env = new Envelope { messageId = messageId, seq = seq, payloadLen = payload.Length, payloadHash = EnvelopeUtil.ComputeHash64(payload), flags = flags };
            var packed = EnvelopeUtil.Pack(env, payload);
            driver.SendPackedSnapshotToClient(client, packed, EnvelopeUtil.ComputeHash64(payload));
            return;
        }

        var shards = FecReedSolomon.BuildShards(payload, fecShardSize, fecParityShards);
        foreach (var s in shards)
        {
            Envelope env = new Envelope { messageId = messageId, seq = seq, payloadLen = s.Length, payloadHash = EnvelopeUtil.ComputeHash64(s), flags = 0x4 };
            var packed = EnvelopeUtil.Pack(env, s);
            driver.SendPackedShardToClient(client, packed);
        }
    }
}

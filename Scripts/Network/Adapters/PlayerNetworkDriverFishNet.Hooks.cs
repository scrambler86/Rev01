using System;
using System.Collections.Generic;
using UnityEngine;
using FishNet;                 // InstanceFinder
using FishNet.Connection;      // NetworkConnection

/// <summary>
/// Hook/helper statici che parlano con PlayerNetworkDriverFishNet senza dover
/// accedere ai suoi campi interni.
/// 
/// - HandleExternalShard:
///     lato CLIENT: consegna uno shard (già ricevuto) al driver locale per
///     il reassembly FEC e la gestione canary, SENZA reinviarlo in rete.
///
/// - SendWithEnvelopeToClient:
///     lato SERVER: manda a un client un payload arbitrario, o come full block
///     singolo, o come shards+parità, wrappando ogni pezzo in un Envelope con
///     metadati coerenti (payloadLen/payloadHash dell'intero payload originale).
/// </summary>
public static class PlayerNetworkDriverFishNetHooks
{
    /// <summary>
    /// Lato CLIENT.
    /// Chiama questo quando hai già ricevuto uno shard "esterno" (per esempio
    /// da un RPC custom o da CanaryTest.TargetReceive) e vuoi che il driver
    /// lo processi come se fosse arrivato da TargetPackedShardTo().
    ///
    /// NOTA: Non rimanda nulla in rete. Passa direttamente allo stack locale.
    /// </summary>
    public static void HandleExternalShard(byte[] shardBytes)
    {
        if (shardBytes == null || shardBytes.Length == 0)
            return;

        var driver = UnityEngine.Object.FindObjectOfType<PlayerNetworkDriverFishNet>();
        if (driver == null)
        {
            Debug.LogWarning("[Hooks] HandleExternalShard chiamato ma PlayerNetworkDriverFishNet non trovato in scena.");
            return;
        }

        // IMPORTANTISSIMO:
        // Prima versione sbagliata cercava di fare SendPackedShardToClient(..., null, ...)
        // cioè "rimandare" lo shard da qualche parte.
        //
        // Questo è scorretto. Noi QUI siamo già sul client che ha ricevuto lo shard.
        // Quello che vogliamo è dire al driver locale "processa questo shard".
        //
        // HandlePackedShardPublic() è il wrapper pubblico nel driver che inoltra allo
        // stesso codice interno usato da TargetPackedShardTo().
        driver.HandlePackedShardPublic(shardBytes);
    }


    /// <summary>
    /// Lato SERVER.
    /// Spedisce un payload arbitrario a un singolo client:
    /// - se useFec == false, manda 1 envelope "full".
    /// - se useFec == true, splitta in shards (più 1+ parity) usando FEC/XOR, e
    ///   manda ciascun shard in un envelope con:
    ///       payloadLen  = lunghezza TOTALE del payload completo
    ///       payloadHash = hash del payload COMPLETO
    ///       flags       = 0x04 | flags (0x04 = shard)
    ///
    /// Parametri:
    ///   clientId          -> Client destinatario.
    ///   messageId         -> ID gruppo messaggi (tienilo costante per tutti gli shard di quello stesso payload).
    ///                        Se passi 0, viene generato automaticamente.
    ///   seq               -> sequence number "arbitrario" che vuoi timbrare nel pacchetto (può essere l'Environment.TickCount o simile).
    ///   flags             -> bit custom per il significato del payload.
    ///                        Esempio: 0x08 = "canary" (debug test packet).
    ///                        Se usi shards, verrà OR-ato con 0x04.
    ///   payload           -> i bytes completi che vuoi mandare (snapshot full, canary, ecc.).
    ///   useFec            -> true = manda shards con parità; false = manda blocco singolo.
    ///   fecShardSize      -> dimensione massima di ciascuna shard di dati.
    ///   fecParityShards   -> numero di shard di parità da aggiungere (>=1 abilita recovery di 1 shard perso).
    ///
    /// Dipendenze importanti:
    ///   - PlayerNetworkDriverFishNet.SendPackedSnapshotToClient()
    ///   - PlayerNetworkDriverFishNet.SendPackedShardToClient()
    ///   - Envelope / EnvelopeUtil / FecReedSolomon
    /// </summary>
    public static void SendWithEnvelopeToClient(
        int clientId,
        uint messageId,
        uint seq,
        byte flags,
        byte[] payload,
        bool useFec,
        int fecShardSize,
        int fecParityShards)
    {
        if (payload == null || payload.Length == 0)
            return;

        // Trova la connessione di destinazione dal ServerManager FishNet.
        var clientsDict = InstanceFinder.ServerManager?.Clients;
        if (clientsDict == null)
        {
            Debug.LogWarning("[Hooks] SendWithEnvelopeToClient: nessun ServerManager.Clients disponibile.");
            return;
        }

        if (!clientsDict.TryGetValue(clientId, out NetworkConnection clientConn) || clientConn == null || !clientConn.IsActive)
        {
            Debug.LogWarning($"[Hooks] SendWithEnvelopeToClient: client {clientId} non valido o non attivo.");
            return;
        }

        // Trova il driver server-side (authoritative per questo player).
        var driver = UnityEngine.Object.FindObjectOfType<PlayerNetworkDriverFishNet>();
        if (driver == null)
        {
            Debug.LogWarning("[Hooks] SendWithEnvelopeToClient: PlayerNetworkDriverFishNet non trovato in scena sul server.");
            return;
        }

        // Hash e lunghezza del payload COMPLETO.
        ulong fullHash = EnvelopeUtil.ComputeHash64(payload);
        int fullLen = payload.Length;

        // Se messageId == 0, generiamo un ID unico ora.
        uint groupMessageId = (messageId != 0)
            ? messageId
            : (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF);

        // Caso semplice: niente FEC/shard -> mandiamo un singolo envelope "full".
        if (!useFec || fecParityShards <= 0)
        {
            Envelope env = new Envelope
            {
                messageId = groupMessageId,
                seq = seq,
                payloadLen = fullLen,
                payloadHash = fullHash,
                flags = flags // es. 0x08 se "canary full", oppure 0x00 per snapshot gameplay standard
            };

            byte[] packed = EnvelopeUtil.Pack(env, payload);

            // Manda questo byte[] al client come se fosse un normale snapshot target RPC.
            // Il driver lato client farà poi:
            //  - EnvelopeUtil.TryUnpack(...)
            //  - se flags indica "canary" (0x08), lo ignora come movement
            //  - se flags è uno snapshot normale, lo passa a HandlePackedPayload(...)
            driver.SendPackedSnapshotToClient(clientConn, packed, fullHash);
            return;
        }

        // Caso FEC/shard:
        // Splittiamo il payload in data shards + parity shards.
        // Ogni shard è già nel formato:
        //   [2 byte totalShards][2 byte index][4 byte dataLen][data...]
        // come costruito da FecReedSolomon.BuildShards(...).
        List<byte[]> shards = FecReedSolomon.BuildShards(payload, fecShardSize, fecParityShards);

        for (int i = 0; i < shards.Count; i++)
        {
            byte[] shardBytes = shards[i];
            if (shardBytes == null || shardBytes.Length == 0)
                continue;

            // Per OGNI shard costruiamo un Envelope.
            // ATTENZIONE:
            // - payloadLen / payloadHash NON descrivono lo shard,
            //   ma l'intero payload 'payload'.
            // - flags deve contenere "sono shard" -> 0x04
            //   più eventuali altri bit (es. 0x08 "canary").
            Envelope shardEnv = new Envelope
            {
                messageId = groupMessageId,
                seq = seq,
                payloadLen = fullLen,
                payloadHash = fullHash,
                flags = (byte)(0x04 | flags) // 0x04 = shard, 0x08 = canary, ecc
            };

            byte[] packedShard = EnvelopeUtil.Pack(shardEnv, shardBytes);

            // Questo chiama il TargetRpc sul client:
            //   TargetPackedShardTo(clientConn, packedShard)
            // Il client poi fa:
            //   - TryUnpack envelope
            //   - bufferizza gli shard per messageId
            //   - quando ha tutte le shard (o ricostruisce via parity),
            //     riassembla il payload intero
            //   - se flags aveva 0x08 (canary), NON prova a feedare PackedMovement.TryUnpack.
            driver.SendPackedShardToClient(clientConn, packedShard);
        }
    }
}

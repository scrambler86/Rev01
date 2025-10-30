// BOOKMARK: FILE = INetAdapter.cs
using System;
using UnityEngine;
using Game.Network.Core;

namespace Game.Network
{
    /// <summary>
    /// Interfaccia neutra per l'adapter di rete.
    /// Implementazioni: FishNetAdapter, MirrorAdapter, NGOAdapter, ecc.
    /// </summary>
    public interface INetAdapter
    {
        bool IsOwner { get; }
        bool IsServer { get; }

        /// <summary>Il core si collega a runtime per ricevere callback dallo stack.</summary>
        void Bind(object coreHandle);

        /// <summary>Invia al server un batch di input locali.</summary>
        void SendInputs(NetInputCmd[] inputs);

        /// <summary>Evento: snapshot server->client.</summary>
        event Action<Vector3, Quaternion, uint, float> SnapshotReceived;
    }
}

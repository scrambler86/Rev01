// BOOKMARK: FILE = FishNetAdapter.cs
using UnityEngine;
using System;
using Game.Network.Core;

namespace Game.Network.Adapters.FishNet
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(global::FishNet.Object.NetworkObject))]
    [RequireComponent(typeof(PlayerNetworkDriver))] // garantisce il core sullo stesso GO
    public class FishNetAdapter : global::FishNet.Object.NetworkBehaviour, Game.Network.INetAdapter
    {
        [SerializeField] private PlayerNetworkDriver core;

        [Header("Time Provider (opzionale)")]
        [SerializeField] private MonoBehaviour timeProviderBehaviour; // deve implementare ITimeProvider
        private Game.Network.ITimeProvider _timeProvider;

        // Hiding voluto per evitare CS0108 warnings sul base class
        public new bool IsOwner => base.IsOwner;
        public new bool IsServer => base.IsServerInitialized;

        public event Action<Vector3, Quaternion, uint, float> SnapshotReceived;

        void Awake()
        {
            if (core == null) core = GetComponent<PlayerNetworkDriver>();
            core?.EnsureRuntimeWiring();

            if (timeProviderBehaviour is Game.Network.ITimeProvider tp)
                _timeProvider = tp;
            else
                _timeProvider = GetComponent<Game.Network.ITimeProvider>();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (core == null) core = GetComponent<PlayerNetworkDriver>();
            core?.BindAdapter(this);

            if (_timeProvider == null)
                _timeProvider = GetComponent<Game.Network.ITimeProvider>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            if (core == null) core = GetComponent<PlayerNetworkDriver>();
            core?.BindAdapter(this);
        }

        public void Bind(object coreHandle)
        {
            if (core == null)
                core = coreHandle as PlayerNetworkDriver ?? GetComponent<PlayerNetworkDriver>();

            if (_timeProvider == null)
            {
                if (timeProviderBehaviour is Game.Network.ITimeProvider tp)
                    _timeProvider = tp;
                else
                    _timeProvider = GetComponent<Game.Network.ITimeProvider>();
            }
        }

        // ===========================================================
        // INPUTS → SERVER → SNAPSHOT AGLI OBSERVER
        // ===========================================================
        public void SendInputs(NetInputCmd[] inputs)
        {
            if (inputs == null || inputs.Length == 0) return;
            ServerRpc_SendInputs(inputs);
        }

        [global::FishNet.Object.ServerRpc]
        private void ServerRpc_SendInputs(NetInputCmd[] inputs)
        {
            if (!IsServer || core == null) return;

            core.Server_SimulateFromInputs(inputs, out Vector3 pos, out Quaternion rot);

            uint ack = inputs[inputs.Length - 1].sequence;
            double now = (_timeProvider != null) ? _timeProvider.Now() : Time.unscaledTimeAsDouble;

            ObserversRpc_ReceiveSnapshot(pos, rot, ack, (float)now);
        }

        [global::FishNet.Object.ObserversRpc(BufferLast = true)]
        private void ObserversRpc_ReceiveSnapshot(Vector3 pos, Quaternion rot, uint ackSequence, float serverTime)
        {
            SnapshotReceived?.Invoke(pos, rot, ackSequence, serverTime);

            if (!IsOwner && core != null)
                core.Adapter_OnRemoteSnapshot(serverTime, pos, rot);
        }

        // ===========================================================
        // DEV CANARY (per CanaryRuntime) — RIPRISTINATO
        // ===========================================================
        /// <summary>
        /// Invio diagnostico a un singolo client. Usato da CanaryRuntime.
        /// </summary>
        public void Dev_SendCanaryTo(global::FishNet.Connection.NetworkConnection conn, byte[] payload, bool isShard)
        {
            if (!IsServerInitialized || conn == null || payload == null) return;
            TargetRpc_ReceiveCanary(conn, payload, isShard);
        }

        [global::FishNet.Object.TargetRpc]
        private void TargetRpc_ReceiveCanary(global::FishNet.Connection.NetworkConnection conn, byte[] payload, bool isShard)
        {
            // Hook client-side per payload diagnostici.
            // Se vuoi, notifica il core (opzionale):
            // core?.Dev_OnCanary(payload, isShard);
        }
    }
}

// BOOKMARK: FILE = FishNetAdapter.cs
using UnityEngine;
using FishNet.Object;
using Game.Network.Core;
using System;

namespace Game.Network.Adapters.FishNet
{
    public class FishNetAdapter : NetworkBehaviour, Game.Network.INetAdapter
    {
        [SerializeField] private PlayerNetworkDriver core;

        public bool IsOwner => base.IsOwner;
        public bool IsServer => base.IsServer;

        public event Action<Vector3, Quaternion, uint, float> SnapshotReceived;

        public void Bind(object coreHandle)
        {
            if (core == null)
                core = coreHandle as PlayerNetworkDriver ?? GetComponent<PlayerNetworkDriver>();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (core == null) core = GetComponent<PlayerNetworkDriver>();
            core?.BindAdapter(this);
        }

        public void SendInputs(NetInputCmd[] inputs)
        {
            if (inputs == null || inputs.Length == 0) return;
            ServerRpc_SendInputs(inputs);
        }

        [ServerRpc]
        private void ServerRpc_SendInputs(NetInputCmd[] inputs)
        {
            if (!IsServer || core == null) return;

            core.Server_SimulateFromInputs(inputs, out Vector3 pos, out Quaternion rot);

            uint ack = inputs[inputs.Length - 1].sequence;
            float serverTime = Time.unscaledTime;

            ObserversRpc_ReceiveSnapshot(pos, rot, ack, serverTime);
        }

        [ObserversRpc(BufferLast = true)]
        private void ObserversRpc_ReceiveSnapshot(Vector3 pos, Quaternion rot, uint ackSequence, float serverTime)
        {
            SnapshotReceived?.Invoke(pos, rot, ackSequence, serverTime);

            if (!IsOwner && core != null)
                core.Adapter_OnRemoteSnapshot(serverTime, pos, rot);
        }
    }
}

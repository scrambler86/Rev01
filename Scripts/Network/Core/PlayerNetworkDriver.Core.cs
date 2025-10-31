// BOOKMARK: FILE = PlayerNetworkDriver.Core.cs
using UnityEngine;
using System;

namespace Game.Network.Core
{
    /// <summary>
    /// Core MOVIMENTO agnostico dallo stack di rete (FishNet/Mirror/NGO).
    /// Richiede un INetAdapter sullo stesso GameObject che gestisce ownership/RPC/snapshots.
    /// </summary>
    public partial class PlayerNetworkDriver : MonoBehaviour
    {
        private Game.Network.INetAdapter _net;
        private Vector3 _velocity;
        private Vector3 _serverPos;
        private Quaternion _serverRot = Quaternion.identity;
        private float _lastSnapshotServerTime;
        private bool _bound;

        private void Awake()
        {
            if (!targetRb) targetRb = GetComponent<Rigidbody>();
            if (targetRb)
            {
                targetRb.interpolation = RigidbodyInterpolation.Interpolate;
                targetRb.useGravity = useGravity;
            }
        }

        private void OnEnable()
        {
            TryBindAdapterIfPresent();
        }

        private void OnDisable()
        {
            if (_net != null)
                _net.SnapshotReceived -= OnSnapshotFromAdapter;

            ClearAllBuffers();
            _bound = false;
        }

        private void Update()
        {
            if (!_bound || _net == null) { TryBindAdapterIfPresent(); }
            if (_net == null || targetRb == null) return;

            float dt = Time.deltaTime;

            if (_net.IsOwner)
            {
                CaptureLocalInput();
                SimulatePrediction(dt);
                FlushInputsToServer();
            }
            else
            {
                InterpolateRemoteVisual(dt);
            }
        }

        private void FixedUpdate()
        {
            // opzionale
        }

        public void BindAdapter(Game.Network.INetAdapter adapter)
        {
            if (adapter == null) return;
            if (_net != null)
                _net.SnapshotReceived -= OnSnapshotFromAdapter;

            _net = adapter;
            _net.Bind(this);
            _net.SnapshotReceived += OnSnapshotFromAdapter;
            _bound = true;
        }

        private void OnSnapshotFromAdapter(Vector3 pos, Quaternion rot, uint ackSequence, float serverTime)
        {
            Client_OnSnapshotReceived(pos, rot, ackSequence, serverTime);
        }

        private void TryBindAdapterIfPresent()
        {
            if (_bound) return;
            var comps = GetComponents<MonoBehaviour>();
            foreach (var c in comps)
            {
                if (c is Game.Network.INetAdapter net)
                {
                    BindAdapter(net);
                    break;
                }
            }
        }

        private void ClearAllBuffers()
        {
            _pendingInputs.Clear();
            _replayCache.Clear();
            _snapshots.Clear();
        }

        internal void Server_SimulateFromInputs(NetInputCmd[] inputs, out Vector3 outPos, out Quaternion outRot)
        {
            float dt = Time.deltaTime;
            if (inputs != null && inputs.Length > 0)
            {
                var last = inputs[inputs.Length - 1];
                Vector3 dir = new Vector3(last.move.x, 0f, last.move.y);
                float speed = CurrentSpeed(last.run);
                Vector3 desiredVel = dir * speed;

                _velocity = Vector3.MoveTowards(_velocity, desiredVel, acceleration * dt);
                Vector3 nextPos = targetRb.position + _velocity * dt;

                if (dir.sqrMagnitude > 0.0001f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(dir, Vector3.up);
                    targetRb.MoveRotation(Quaternion.Slerp(targetRb.rotation, targetRot, 12f * dt));
                }
                targetRb.MovePosition(nextPos);
            }

            outPos = targetRb.position;
            outRot = targetRb.rotation;
        }
    }
}
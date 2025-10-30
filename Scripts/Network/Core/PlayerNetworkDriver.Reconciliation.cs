// BOOKMARK: FILE = PlayerNetworkDriver.Reconciliation.cs
using UnityEngine;

namespace Game.Network.Core
{
    public partial class PlayerNetworkDriver
    {
        private uint _lastAckSequence;

        internal void Client_OnSnapshotReceived(Vector3 serverPos, Quaternion serverRot, uint ackSequence, float serverTime)
        {
            _serverPos = serverPos;
            _serverRot = serverRot;
            _lastAckSequence = ackSequence;
            _lastSnapshotServerTime = serverTime;

            for (int i = _pendingInputs.Count - 1; i >= 0; i--)
                if (_pendingInputs[i].sequence <= ackSequence)
                    _pendingInputs.RemoveAt(i);

            float dist = Vector3.Distance(targetRb.position, serverPos);
            if (dist > snapThreshold)
            {
                targetRb.position = serverPos;
                targetRb.rotation = serverRot;
                _velocity = Vector3.zero;
                return;
            }

            ApplyServerCorrectionSmooth(serverPos, serverRot, blendTime);

            float dt = Time.deltaTime;
            for (int i = 0; i < _pendingInputs.Count; i++)
            {
                var cmd = _pendingInputs[i];
                Vector3 dir = new Vector3(cmd.move.x, 0f, cmd.move.y);
                float speed = CurrentSpeed(cmd.run);
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
        }

        private void ApplyServerCorrectionSmooth(Vector3 serverPos, Quaternion serverRot, float blendTimeSec)
        {
            if (!targetRb) return;

            float t = Mathf.Clamp01(Time.deltaTime / Mathf.Max(0.0001f, blendTimeSec));
            Vector3 blendedPos = Vector3.Lerp(targetRb.position, serverPos, t);
            Quaternion blendedRot = Quaternion.Slerp(targetRb.rotation, serverRot, t);

            targetRb.MovePosition(blendedPos);
            targetRb.MoveRotation(blendedRot);
        }
    }
}

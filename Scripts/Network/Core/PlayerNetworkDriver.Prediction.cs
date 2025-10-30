// BOOKMARK: FILE = PlayerNetworkDriver.Prediction.cs
using System.Collections.Generic;
using UnityEngine;

namespace Game.Network.Core
{
    public partial class PlayerNetworkDriver
    {
        private uint _nextSequence;
        private readonly List<NetInputCmd> _pendingInputs = new();
        private readonly Queue<NetInputCmd> _replayCache  = new();

        private float CurrentSpeed(bool run) => run ? runSpeed : walkSpeed;

        private Vector2 ReadMovementAxes()
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            return new Vector2(h, v).normalized;
        }
        private bool ReadRun() => Input.GetKey(KeyCode.LeftShift);

        private void CaptureLocalInput()
        {
            var axes = ReadMovementAxes();
            var cmd = new NetInputCmd
            {
                sequence   = _nextSequence++,
                clientTime = Time.time,
                move       = axes,
                run        = ReadRun()
            };

            _pendingInputs.Add(cmd);
            if (_pendingInputs.Count > maxInputBuffer)
                _pendingInputs.RemoveAt(0);
        }

        private void SimulatePrediction(float dt)
        {
            if (targetRb == null || _pendingInputs.Count == 0) return;

            var latest = _pendingInputs[_pendingInputs.Count - 1];

            Vector3 dir = new Vector3(latest.move.x, 0f, latest.move.y);
            float targetSpeed = CurrentSpeed(latest.run);
            Vector3 desiredVel = dir * targetSpeed;

            _velocity = Vector3.MoveTowards(_velocity, desiredVel, acceleration * dt);
            Vector3 nextPos = targetRb.position + _velocity * dt;

            if (dir.sqrMagnitude > 0.0001f)
            {
                Quaternion targetRot = Quaternion.LookRotation(dir, Vector3.up);
                targetRb.MoveRotation(Quaternion.Slerp(targetRb.rotation, targetRot, 12f * dt));
            }

            targetRb.MovePosition(nextPos);
        }

        private void FlushInputsToServer()
        {
            if (_net == null || _pendingInputs.Count == 0) return;

            int batch = Mathf.Min(4, _pendingInputs.Count);
            int start = _pendingInputs.Count - batch;

            var batchToSend = new NetInputCmd[batch];
            for (int i = 0; i < batch; i++)
                batchToSend[i] = _pendingInputs[start + i];

            _net.SendInputs(batchToSend);

            for (int i = 0; i < batch; i++)
                _replayCache.Enqueue(batchToSend[i]);
            while (_replayCache.Count > maxInputBuffer)
                _replayCache.Dequeue();
        }
    }
}

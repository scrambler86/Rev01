// BOOKMARK: FILE = PlayerNetworkDriver.RemoteInterpolation.cs
using System.Collections.Generic;
using UnityEngine;

namespace Game.Network.Core
{
    public partial class PlayerNetworkDriver
    {
        private struct Snapshot
        {
            public float serverTime;
            public Vector3 pos;
            public Quaternion rot;
        }

        private readonly List<Snapshot> _snapshots = new();

        private void BufferRemoteSnapshot(float serverTime, Vector3 pos, Quaternion rot)
        {
            var s = new Snapshot { serverTime = serverTime, pos = pos, rot = rot };
            _snapshots.Add(s);
            if (_snapshots.Count > maxSnapshots)
                _snapshots.RemoveAt(0);
        }

        private void InterpolateRemoteVisual(float dt)
        {
            if (_snapshots.Count < 2 || targetRb == null) return;

            float targetTime = _lastSnapshotServerTime - interpDelay;

            int i1 = -1, i2 = -1;
            for (int i = _snapshots.Count - 2; i >= 0; i--)
            {
                if (_snapshots[i].serverTime <= targetTime && _snapshots[i + 1].serverTime >= targetTime)
                {
                    i1 = i; i2 = i + 1;
                    break;
                }
            }

            if (i1 == -1)
            {
                var last = _snapshots[_snapshots.Count - 1];
                targetRb.MovePosition(last.pos);
                targetRb.MoveRotation(last.rot);
                return;
            }

            var a = _snapshots[i1];
            var b = _snapshots[i2];

            float span = Mathf.Max(0.0001f, b.serverTime - a.serverTime);
            float t = Mathf.Clamp01((targetTime - a.serverTime) / span);

            Vector3 pos = Vector3.Lerp(a.pos, b.pos, t);
            Quaternion rot = Quaternion.Slerp(a.rot, b.rot, t);

            targetRb.MovePosition(pos);
            targetRb.MoveRotation(rot);
        }

        internal void Adapter_OnRemoteSnapshot(float serverTime, Vector3 pos, Quaternion rot)
        {
            _lastSnapshotServerTime = serverTime;
            BufferRemoteSnapshot(serverTime, pos, rot);
        }
    }
}
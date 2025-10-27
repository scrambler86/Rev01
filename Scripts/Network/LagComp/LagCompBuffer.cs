using System;
using UnityEngine;

public class LagCompBuffer : MonoBehaviour
{
    [Serializable]
    public struct Sample
    {
        public Vector3 pos;
        public Vector3 vel;
        public double serverTime;
    }

    public int capacity = 128; // ~4s @30Hz
    private Sample[] _buf;
    private int _head;
    private bool _filled;

    void Awake()
    {
        _buf = new Sample[Mathf.Max(16, capacity)];
        _head = 0; _filled = false;
    }

    public void Push(Vector3 pos, Vector3 vel, double serverTime)
    {
        _buf[_head] = new Sample { pos = pos, vel = vel, serverTime = serverTime };
        _head = (_head + 1) % _buf.Length;
        if (_head == 0) _filled = true;
    }

    public bool TryGetAt(double t, out Sample s0, out Sample s1, out float alpha)
    {
        s0 = default; s1 = default; alpha = 0f;
        int n = _filled ? _buf.Length : _head;
        if (n < 2) return false;

        for (int k = 0; k < n; k++)
        {
            int i = (_head - 1 - k + _buf.Length) % _buf.Length;
            int j = (i - 1 + _buf.Length) % _buf.Length;
            var A = _buf[j];
            var B = _buf[i];
            if (A.serverTime <= t && B.serverTime >= t)
            {
                s0 = A; s1 = B;
                double span = Math.Max(1e-6, B.serverTime - A.serverTime);
                alpha = (float)((t - A.serverTime) / span);
                return true;
            }
        }

        int last = (_head - 1 + _buf.Length) % _buf.Length;
        s0 = _buf[last]; s1 = s0; alpha = 1f;
        return true;
    }
}

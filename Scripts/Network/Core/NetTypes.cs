// BOOKMARK: FILE = NetTypes.cs
using System;
using UnityEngine;

namespace Game.Network.Core
{
    /// <summary>Struttura input serializzabile e neutra (no dipendenze dal framework di rete).</summary>
    [Serializable]
    public struct NetInputCmd
    {
        public uint sequence;
        public float clientTime;
        public Vector2 move;     // x,z
        public bool run;
    }
}
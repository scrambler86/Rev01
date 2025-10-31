// BOOKMARK: FILE = NetTimeFishNet.cs
using UnityEngine;

namespace Game.Network.Adapters.FishNet
{
    /// <summary>
    /// Time provider minimale: usa il tempo locale di Unity (unscaled).
    /// Compatibile con tutte le versioni. In futuro, se vuoi, lo
    /// colleghiamo al TimeManager di FishNet per un clock di rete.
    /// </summary>
    public sealed class NetTimeFishNet : Game.Network.ITimeProvider
    {
        public double Now()
        {
            return Time.unscaledTimeAsDouble;
        }
    }
}

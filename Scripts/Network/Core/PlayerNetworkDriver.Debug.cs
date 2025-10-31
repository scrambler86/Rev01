// BOOKMARK: FILE = PlayerNetworkDriver.Debug.cs
using UnityEngine;

namespace Game.Network.Core
{
    public partial class PlayerNetworkDriver
    {
        /// <summary>Ultima direzione di input catturata (XZ). Solo diagnostica/UI.</summary>
        public Vector3 DebugLastMoveDir
        {
            get
            {
                // Se abbiamo input in coda, usa l'ultimo.
                if (_pendingInputs != null && _pendingInputs.Count > 0)
                {
                    var latest = _pendingInputs[_pendingInputs.Count - 1];
                    return new Vector3(latest.move.x, 0f, latest.move.y);
                }
                // Se c'è un override esterno (click-to-move), usalo.
                if (_externalMoveWorldDir.HasValue)
                    return new Vector3(_externalMoveWorldDir.Value.x, 0f, _externalMoveWorldDir.Value.z);

                return Vector3.zero;
            }
        }

        /// <summary>Velocità planare attuale (magnitudo del vettore di movimento). Solo diagnostica/UI.</summary>
        public float DebugPlanarSpeed => _velocity.magnitude;

        /// <summary>Stato run attuale dell'ultimo input. Solo diagnostica/UI.</summary>
        public bool DebugIsRunning
        {
            get
            {
                if (_pendingInputs != null && _pendingInputs.Count > 0)
                    return _pendingInputs[_pendingInputs.Count - 1].run;

                if (_externalRun.HasValue)
                    return _externalRun.Value;

                return false;
            }
        }

        /// <summary>Flag "posso leggere input locale?" Per ora sempre true (gestiscilo se aggiungi gating).</summary>
        public bool DebugAllowInput => true;
    }
}

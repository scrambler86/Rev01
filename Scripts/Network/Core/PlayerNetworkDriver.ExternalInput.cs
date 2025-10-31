// BOOKMARK: FILE = PlayerNetworkDriver.ExternalInput.cs
using UnityEngine;

namespace Game.Network.Core
{
    public partial class PlayerNetworkDriver
    {
        private Vector3? _externalMoveWorldDir;
        private bool? _externalRun;

        /// <summary>Inietta una direzione mondo esterna (es. click-to-move). xz usati, y ignorata.</summary>
        public void SetExternalMove(Vector3 worldDir, bool? run = null)
        {
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude > 1f) worldDir.Normalize();
            _externalMoveWorldDir = worldDir;
            _externalRun = run;
        }

        /// <summary>Rimuove l'override esterno: torna ai controlli locali (WASD).</summary>
        public void ClearExternalMove()
        {
            _externalMoveWorldDir = null;
            _externalRun = null;
        }

        private bool TryReadExternal(out Vector2 axes, out bool run)
        {
            if (_externalMoveWorldDir.HasValue)
            {
                var d = _externalMoveWorldDir.Value;
                axes = new Vector2(d.x, d.z);
                run = _externalRun ?? ReadRun();
                return true;
            }
            axes = default;
            run = false;
            return false;
        }
    }
}

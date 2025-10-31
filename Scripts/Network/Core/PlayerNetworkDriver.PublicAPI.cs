// BOOKMARK: FILE = PlayerNetworkDriver.PublicAPI.cs
using UnityEngine;

namespace Game.Network.Core
{
    public partial class PlayerNetworkDriver
    {
        /// <summary>RigidBody usato dal driver (read-only).</summary>
        public Rigidbody TargetRb => targetRb;

        /// <summary>Velocità di camminata (read-only).</summary>
        public float WalkSpeed => walkSpeed;

        /// <summary>Velocità di corsa (read-only).</summary>
        public float RunSpeed => runSpeed;
    }
}

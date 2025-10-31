// BOOKMARK: FILE = PlayerNetworkDriver.cs
using UnityEngine;

namespace Game.Network.Core
{
    [AddComponentMenu("Game/Network/Player Network Driver")]
    [DisallowMultipleComponent]
    public partial class PlayerNetworkDriver : MonoBehaviour
    {
        /// <summary>Assicura wiring Rigidbody e applica la gravità.</summary>
        public void EnsureRuntimeWiring()
        {
            if (!TryGetComponent<Rigidbody>(out var rb))
                rb = gameObject.AddComponent<Rigidbody>();

            rb.isKinematic = false;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            rb.constraints = RigidbodyConstraints.FreezeRotationX |
                             RigidbodyConstraints.FreezeRotationY |
                             RigidbodyConstraints.FreezeRotationZ;

            targetRb = rb;

            // 👉 avvia il loop di grounding senza usare OnEnable/OnDisable
            EnsureGroundingStarted();
        }

#if UNITY_EDITOR
        [ContextMenu("Network/Ensure Runtime Wiring")]
        private void __EditorEnsureRuntimeWiring() => EnsureRuntimeWiring();
#endif
    }
}

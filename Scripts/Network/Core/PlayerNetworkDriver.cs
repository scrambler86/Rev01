// BOOKMARK: FILE = PlayerNetworkDriver.cs
using UnityEngine;

namespace Game.Network.Core
{
    [AddComponentMenu("Game/Network/Player Network Driver")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public partial class PlayerNetworkDriver : MonoBehaviour
    {
        /// <summary>
        /// Assicura il wiring del Rigidbody con settaggi corretti per net-movement.
        /// Chiama questo una sola volta in avvio (es. dall'Adapter, o dal tuo Awake esistente).
        /// </summary>
        public void EnsureRuntimeWiring()
        {
            if (!TryGetComponent<Rigidbody>(out var rb))
                rb = gameObject.AddComponent<Rigidbody>();

            // Config consigliate (niente freeze posizione!)
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            rb.constraints = RigidbodyConstraints.FreezeRotationX |
                             RigidbodyConstraints.FreezeRotationY |
                             RigidbodyConstraints.FreezeRotationZ;

            // salva nel campo privato definito negli altri parziali
            targetRb = rb;

            // piccolo snap iniziale (una sola volta) per prevenire hover se spawni a quota errata
            __SnapToGroundOnce();
        }

        // BOOKMARK: SNAPPING (call once)
        void __SnapToGroundOnce()
        {
            if (targetRb == null) return;

            // ray breve verso il basso per trovare il terreno immediatamente sotto i piedi
            Vector3 origin = targetRb.position + Vector3.up * 0.10f;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore))
            {
                // se siamo sospesi entro mezzo metro, riallinea dolcemente
                float gap = targetRb.position.y - hit.point.y;
                if (gap > 0f && gap <= 0.5f)
                {
                    Vector3 p = targetRb.position;
                    p.y = hit.point.y;
                    // MovePosition evita teletrasporti duri al primo frame
                    targetRb.MovePosition(p);
                }
            }
        }
    }
}

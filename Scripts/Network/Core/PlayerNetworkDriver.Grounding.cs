// BOOKMARK: FILE = PlayerNetworkDriver.Grounding.cs
using System.Collections;
using UnityEngine;

namespace Game.Network.Core
{
    public partial class PlayerNetworkDriver
    {
        [Header("Grounding")]
        [Tooltip("Layer considerati terreno per lo snap.")]
        public LayerMask groundMask = ~0;
        [Tooltip("Se il terreno è entro questa distanza sotto i piedi, snappa.")]
        public float snapGroundDistance = 0.35f;
        [Tooltip("Offset dell’origine del ray di ground check (sopra i piedi).")]
        public float groundRaycastUpOffset = 0.2f;
        [Tooltip("Lunghezza del ray verso il basso.")]
        public float groundRaycastDown = 2.0f;

        private Coroutine _groundingCo;

        /// <summary>Avvia (una volta) il loop di grounding senza usare OnEnable/OnDisable.</summary>
        public void EnsureGroundingStarted()
        {
            if (_groundingCo == null && isActiveAndEnabled)
                _groundingCo = StartCoroutine(GroundingLoop());
        }

        /// <summary>Ferma il loop di grounding, se attivo.</summary>
        public void StopGroundingIfRunning()
        {
            if (_groundingCo != null)
            {
                StopCoroutine(_groundingCo);
                _groundingCo = null;
            }
        }

        private IEnumerator GroundingLoop()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;

                if (targetRb == null) continue;

                // Se sta cadendo, lascia lavorare la fisica.
                if (targetRb.velocity.y < -0.1f) continue;

                Vector3 origin = targetRb.position + Vector3.up * groundRaycastUpOffset;
                float maxDist = groundRaycastDown + groundRaycastUpOffset;

                if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, maxDist, groundMask, QueryTriggerInteraction.Ignore))
                {
                    float diff = targetRb.position.y - hit.point.y;
                    if (diff > 0f && diff <= snapGroundDistance)
                    {
                        Vector3 p = targetRb.position;
                        p.y = hit.point.y;
                        targetRb.MovePosition(p);
                    }
                }
            }
        }
    }
}

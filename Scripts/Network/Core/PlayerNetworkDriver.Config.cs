// BOOKMARK: FILE = PlayerNetworkDriver.Config.cs
using UnityEngine;

namespace Game.Network.Core
{
    public partial class PlayerNetworkDriver : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float walkSpeed = 3.2f;
        [SerializeField] private float runSpeed  = 6.0f;
        [SerializeField] private float acceleration = 30f;

        [Header("Physics")]
        [SerializeField] private Rigidbody targetRb;
        [SerializeField] private bool useGravity = false;

        [Header("Prediction")]
        [SerializeField] private int   maxInputBuffer = 64;
        [SerializeField] private float maxRewindTime  = 0.250f; // 250 ms

        [Header("Reconciliation")]
        [SerializeField] private float snapThreshold  = 1.25f;  // m
        [SerializeField] private float blendTime      = 0.08f;  // s

        [Header("Interpolation (Remotes)")]
        [SerializeField] private float interpDelay    = 0.100f; // 100 ms
        [SerializeField] private int   maxSnapshots   = 64;

        [Header("Anti-Cheat (base)")]
        [SerializeField] private bool  enableAntiCheat = false;
        [SerializeField] private float maxSlope        = 38f;

        // === BOOKMARK: CONFIG_EXTRAS ===
        // Inserisci qui altri SerializeField se/quando serviranno.
    }
}

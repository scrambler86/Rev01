// BOOKMARK: FILE = PlayerPrefabAutoSetup.cs
using UnityEngine;
using Game.Network.Core;
using Game.Network.Adapters.FishNet;

[DefaultExecutionOrder(-5000)] // molto presto
[DisallowMultipleComponent]
public sealed class PlayerPrefabAutoSetup : MonoBehaviour
{
    [Header("ClickToMove (opzionale)")]
    public bool autoSetupClickToMove = true;
    public string navigatorChildName = "Navigator";

    void Awake()
    {
        // 1) Assicura Driver
        var driver = GetComponent<PlayerNetworkDriver>();
        if (driver == null)
            driver = gameObject.AddComponent<PlayerNetworkDriver>();

        // 2) Assicura Rigidbody + wiring
        driver.EnsureRuntimeWiring();
        var rb = driver.GetComponent<Rigidbody>();
        if (rb != null)
        {
            // gravità: se hai il toggle nel driver, EnsureRuntimeWiring l’ha già applicata.
            // come fallback fai sì che NON voli
            if (!rb.useGravity) rb.useGravity = true;
            if (rb.interpolation != RigidbodyInterpolation.Interpolate)
                rb.interpolation = RigidbodyInterpolation.Interpolate;
            if (rb.collisionDetectionMode != CollisionDetectionMode.Continuous)
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        }

        // 3) Assicura Adapter FishNet e bind al driver
        var adapter = GetComponent<FishNetAdapter>();
        if (adapter == null)
            adapter = gameObject.AddComponent<FishNetAdapter>();

        adapter.Bind(driver);        // collega core
        driver.BindAdapter(adapter); // (se il tuo driver espone questo hook)

        // 4) (Opzionale) ClickToMove: auto-camera e Navigator
        if (autoSetupClickToMove)
        {
            var ctm = GetComponent<ClickToMoveAgent>();
            if (ctm != null)
            {
                if (ctm.cam == null) ctm.cam = Camera.main;

                // trova/crea child Navigator con NavMeshAgent (solo pathfinding)
                var navChild = transform.Find(navigatorChildName);
                if (navChild == null)
                {
                    navChild = new GameObject(navigatorChildName).transform;
                    navChild.SetParent(transform, false);
                    navChild.localPosition = Vector3.zero;
                }
                var ag = navChild.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (ag == null) ag = navChild.gameObject.AddComponent<UnityEngine.AI.NavMeshAgent>();
                ag.updatePosition = false;
                ag.updateRotation = false;
                if (ag.stoppingDistance < 0.2f) ag.stoppingDistance = 0.2f;

                // di’ al CTM come si chiama il child
                ctm.navigatorChildName = navigatorChildName;
            }

            // bridge → driver
            var bridge = GetComponent<ClickToMoveToDriverBridge>();
            if (bridge == null) bridge = gameObject.AddComponent<ClickToMoveToDriverBridge>();
        }

        // Log diagnostico minimo in Editor
#if UNITY_EDITOR
        if (adapter == null || driver == null || rb == null)
            Debug.LogWarning("[PlayerPrefabAutoSetup] Wiring incompleto (adapter/driver/rb).");
#endif
    }
}

// BOOKMARK: FILE = ClickToMoveToDriverBridge.cs
using UnityEngine;
using Game.Network.Core;

[RequireComponent(typeof(ClickToMoveAgent))]
[RequireComponent(typeof(PlayerNetworkDriver))]
public class ClickToMoveToDriverBridge : MonoBehaviour
{
    [Tooltip("Se true, quando il CTM ha un path forza lo stato run.")]
    public bool forceRunWhilePath = false;

    private ClickToMoveAgent _ctm;
    private PlayerNetworkDriver _driver;

    void Awake()
    {
        _ctm = GetComponent<ClickToMoveAgent>();
        _driver = GetComponent<PlayerNetworkDriver>();
    }

    void Update()
    {
        if (_ctm == null || _driver == null) return;

        if (_ctm.HasPath)
        {
            Vector3 dv = _ctm.GetDesiredVelocity();
            dv.y = 0f;
            if (dv.sqrMagnitude > 0.0001f)
            {
                Vector3 worldDir = dv.normalized;
                _driver.SetExternalMove(worldDir, forceRunWhilePath ? true : (bool?)null);
                return;
            }
        }

        // nessun path: lascia al driver la lettura input locale
        _driver.ClearExternalMove();
    }
}

// BOOKMARK: FILE = ClickToMoveAgent.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using FishNet.Object;

public class ClickToMoveAgent : MonoBehaviour
{
    [Header("Camera input")]
    public Camera cam;
    public int clickButton = 1; // 0=LMB,1=RMB

    [Header("NavMesh sampling")]
    public float maxSampleDist = 12f;

    [Header("Anti-spam click")]
    public bool enableDebounce = true;
    public float clickCooldown = 0.06f;
    public bool enableMinRepathDistance = true;
    public float minRepathDistance = 0.25f;

    [Header("UI blocking")]
    public string uiLayerName = "UI";
    public bool blockUIClicks = true;
    public bool allowBypassKey = true;
    public KeyCode bypassKey = KeyCode.LeftAlt;

    [Header("Networking / Local Input")]
    public bool allowServerOnlyInput = true;

    [Header("Navigator child (opzionale)")]
    public string navigatorChildName = "Navigator"; // dove sta il NavMeshAgent

    private NavMeshAgent _agent;            // agent su child
    private NetworkObject _nobj;

    private float _nextClick;
    private bool _hasLastGoal;
    private Vector3 _lastGoal;

    private int _uiLayer = -1;
    private static readonly List<RaycastResult> _uiHits = new List<RaycastResult>();

    private bool _uiConsumeUntilUp;

    public bool HasPath => _agent && _agent.hasPath && !_agent.isStopped;

    void Awake()
    {
        if (!cam) cam = Camera.main;
        _nobj = GetComponent<NetworkObject>();
        _agent = FindAgentOnChild();

        if (_agent != null)
        {
            // L’agent serve SOLO per pathfinding/steering.
            _agent.updatePosition = false;
            _agent.updateRotation = false;
            _agent.autoRepath = true;
            _agent.autoBraking = true;
            _agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            _agent.avoidancePriority = 50;
            _agent.stoppingDistance = Mathf.Max(0.15f, _agent.stoppingDistance);

            // Allinealo all’inizio
            _agent.nextPosition = transform.position;
            _agent.Warp(transform.position);
        }
        else
        {
            Debug.LogWarning($"[ClickToMoveAgent] Nessun NavMeshAgent trovato su child '{navigatorChildName}'. " +
                             "Il CTM userà solo WASD o resterà idle.");
        }
    }

    void LateUpdate()
    {
        if (!cam) cam = Camera.main;
        if (_uiLayer == -1 && !string.IsNullOrEmpty(uiLayerName))
            _uiLayer = LayerMask.NameToLayer(uiLayerName);

        // Mantieni il child agent allineato alla posizione del player.
        if (_agent)
        {
            var t = transform.position;
            _agent.nextPosition = t;
            if (_agent.transform.position != t)
                _agent.Warp(t);
        }
    }

    bool HasLocalAuthorityForInput()
    {
        // Owner può sempre comandare; opzionale input su server (host)
        if (_nobj == null || !_nobj.IsSpawned) return true;
        if (_nobj.IsOwner) return true;
        if (allowServerOnlyInput && _nobj.IsServerInitialized) return true;
        return false;
    }

    void Update()
    {
        if (!HasLocalAuthorityForInput()) return;
        if (!cam || _agent == null) return;

        // sblocca consumo-UI al rilascio
        if (_uiConsumeUntilUp && Input.GetMouseButtonUp(clickButton))
            _uiConsumeUntilUp = false;

        bool bypass = allowBypassKey && Input.GetKey(bypassKey);

        if (Input.GetMouseButtonDown(clickButton))
        {
            if (_uiConsumeUntilUp && !bypass) return;

            if (!bypass && blockUIClicks && IsPointerOverUILayer())
            {
                _uiConsumeUntilUp = true;
                return;
            }

            if (enableDebounce && Time.time < _nextClick) return;
            _nextClick = Time.time + clickCooldown;

            Ray r = cam.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(r, out RaycastHit hit, 5000f, ~0, QueryTriggerInteraction.Ignore))
                return;

            if (!NavMesh.SamplePosition(hit.point, out NavMeshHit nh, maxSampleDist, NavMesh.AllAreas))
                return;

            if (enableMinRepathDistance && _hasLastGoal &&
                Vector3.Distance(nh.position, _lastGoal) < minRepathDistance)
                return;

            if (Vector3.Distance(transform.position, nh.position) <= _agent.stoppingDistance + 0.05f)
                return;

            _agent.isStopped = false;
            _agent.ResetPath();
            _agent.SetDestination(nh.position);

            _lastGoal = nh.position;
            _hasLastGoal = true;
        }
    }

    bool IsPointerOverUILayer()
    {
        if (!blockUIClicks) return false;
        if (EventSystem.current == null) return false;
        if (_uiLayer == -1) return false;

        var ped = new PointerEventData(EventSystem.current) { position = Input.mousePosition };
        _uiHits.Clear();
        EventSystem.current.RaycastAll(ped, _uiHits);

        for (int i = 0; i < _uiHits.Count; i++)
        {
            var go = _uiHits[i].gameObject;
            if (!go) continue;
            Transform t = go.transform;
            while (t != null)
            {
                if (t.gameObject.layer == _uiLayer)
                    return true;
                t = t.parent;
            }
        }
        return false;
    }

    public void CancelPath()
    {
        if (_agent == null) return;
        _agent.isStopped = true;
        if (_agent.hasPath) _agent.ResetPath();
        _hasLastGoal = false;
        _agent.nextPosition = transform.position;
    }

    public Vector3 GetDesiredVelocity()
    {
        return _agent ? _agent.desiredVelocity : Vector3.zero;
    }

    public float RemainingDistance() => _agent ? _agent.remainingDistance : Mathf.Infinity;
    public float StoppingDistance() => _agent ? _agent.stoppingDistance : 0.15f;
    public Vector3 SteeringTarget() => _agent ? _agent.steeringTarget : transform.position;

    public Vector3[] GetPathCorners()
    {
        if (_agent == null || !_agent.hasPath) return System.Array.Empty<Vector3>();
        var p = _agent.path; if (p == null) return System.Array.Empty<Vector3>();
        return p.corners;
    }

    private NavMeshAgent FindAgentOnChild()
    {
        // 1) prova nome child
        if (!string.IsNullOrWhiteSpace(navigatorChildName))
        {
            var t = transform.Find(navigatorChildName);
            if (t != null && t.TryGetComponent(out NavMeshAgent ag))
                return ag;
        }
        // 2) primo agent in children
        return GetComponentInChildren<NavMeshAgent>();
    }
}

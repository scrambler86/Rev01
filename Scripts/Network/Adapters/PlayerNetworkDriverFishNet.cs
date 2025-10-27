using UnityEngine;
using UnityEngine.AI;
using FishNet.Object;
using FishNet.Connection;
using FishNet;
using System;
using System.Collections.Generic;

[RequireComponent(typeof(PlayerControllerCore))]
[RequireComponent(typeof(Rigidbody))]
public class PlayerNetworkDriverFishNet : NetworkBehaviour, IPlayerNetworkDriver
{
    // ====== STATIC QUIT GUARD (no dipendenze esterne) ======
    private static bool s_AppQuitting = false;
    static PlayerNetworkDriverFishNet() { Application.quitting += OnAppQuit; }
    private static void OnAppQuit() { s_AppQuitting = true; }

    [Header("Refs")]
    [SerializeField] private PlayerControllerCore _core;
    [SerializeField] private Rigidbody _rb;
    [SerializeField] private NavMeshAgent _agent;
    [SerializeField] private ClickToMoveAgent _ctm;

    [Header("Owner → Server send")]
    [Range(10, 60)] public int sendRateHz = 30;

    [Header("Remotes interpolation")]
    [SerializeField] private double minBack = 0.14;
    [SerializeField] private double maxBack = 0.32;
    [Range(0.05f, 0.5f)] public float emaDelayA = 0.18f;
    [Range(0.05f, 0.5f)] public float emaJitterA = 0.18f;

    [Header("Reconciliation (Owner)")]
    public float deadZone = 0.14f;
    public float hardSnapDist = 0.90f;
    public float reconcileRate = 12f;

    [Header("Anti-cheat (Server)")]
    public float maxSpeedTolerance = 1.18f; // base
    public float slackK = 0.6f;             // stepSlack = 1 + (RTT_oneway*2)*K
    public float slackMin = 1.00f;
    public float slackMax = 1.75f;
    public bool validateNavMesh = true;
    public float navMeshMaxSampleDist = 1.0f;
    public float maxVerticalSpeed = 2.0f;

    [Header("Remoti visual/anim")]
    public bool remoteMoveVisualOnly = true;
    public float remoteAnimSmooth = 8f;
    public float remoteRunSpeedThreshold = 3.0f;

    [Header("Render smoothing (remotes)")]
    public float remoteVisualLerpSpeed = 16f;

    [Header("Height policy")]
    public bool ignoreNetworkY = true; // usa Y locale da ground probe

    [Header("Broadcast Scheduler (per-conn)")]
    public int nearRing = 1, midRing = 2, farRing = 3;
    public int nearHz = 30, midHz = 10, farHz = 3;

    [Header("Delta Compression")]
    public int keyframeEvery = 20; // 0 = off
    public int maxPosDeltaCm = 60;
    public int maxVelDeltaCms = 150;
    public int maxDtMs = 200;

    [Header("Input Rate Limit (Server)")]
    public int maxInputsPerSecond = 120;
    public int burstAllowance = 30;
    public float refillPerSecond = 120f;

    [Header("DEBUG / Fallback")]
    public bool forceBroadcastAll = false; // true = invia a tutti via ObserversRpc

    // IPlayerNetworkDriver
    public INetTime NetTime => _netTime;
    public int OwnerClientId => (Owner != null ? Owner.ClientId : -1);
    public uint LastSeqReceived => _lastSeqReceived;
    public void SetLastSeqReceived(uint seq) => _lastSeqReceived = seq;

    private INetTime _netTime;
    private IAntiCheatValidator _anti;
    private ChunkManager _chunk;

    private float _sendDt, _sendTimer;
    private uint _lastSeqSent, _lastSeqReceived;

    private readonly List<MovementSnapshot> _buffer = new(256);
    private double _emaDelay, _emaJitter, _back, _backTarget;

    private bool _reconcileActive, _doHardSnapNextFixed;
    private Vector3 _reconcileTarget, _pendingHardSnap;

    private Vector3 _serverLastPos;
    private double _serverLastTime;

    // remoti anim
    private Vector3 _remoteLastRenderPos;
    private float _remoteDisplaySpeed;

    private readonly Queue<InputState> _inputBuf = new(128);

    // AOI temp
    private readonly HashSet<NetworkConnection> _tmpNear = new();
    private readonly HashSet<NetworkConnection> _tmpMid = new();
    private readonly HashSet<NetworkConnection> _tmpFar = new();
    private readonly Dictionary<NetworkConnection, double> _nextSendAt = new();
    private double _nearInterval, _midInterval, _farInterval;

    // delta compression (server per conn)
    private readonly Dictionary<NetworkConnection, MovementSnapshot> _lastSentSnap = new();
    private readonly Dictionary<NetworkConnection, int> _sinceKeyframe = new();
    private readonly Dictionary<NetworkConnection, (short cellX, short cellY)> _lastSentCell = new();

    // delta anchor (client)
    private bool _haveAnchor;
    private short _anchorCellX, _anchorCellY;
    private MovementSnapshot _baseSnap;

    // rate limit (server per player)
    private float _tokens;
    private double _lastRefill;

    // teardown guard
    private bool _shuttingDown;

    void Awake()
    {
        if (!_core) _core = GetComponent<PlayerControllerCore>();
        if (!_rb) _rb = GetComponent<Rigidbody>();
        if (!_agent) _agent = GetComponent<NavMeshAgent>();
        if (!_ctm) _ctm = GetComponent<ClickToMoveAgent>();

        _netTime = new NetTimeFishNet();
        _anti = FindObjectOfType<AntiCheatManager>();
        _chunk = FindObjectOfType<ChunkManager>();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        _sendDt = 1f / Mathf.Max(1, sendRateHz);
        _nearInterval = 1.0 / Math.Max(1, nearHz);
        _midInterval = 1.0 / Math.Max(1, midHz);
        _farInterval = 1.0 / Math.Max(1, farHz);

        _core.SetAllowInput(IsOwner);

        if (IsOwner)
        {
            _rb.isKinematic = false; _rb.detectCollisions = true; _rb.interpolation = RigidbodyInterpolation.Interpolate;
        }
        else
        {
            _rb.isKinematic = true; _rb.detectCollisions = false; _rb.interpolation = RigidbodyInterpolation.None;
            _remoteLastRenderPos = transform.position;
            _remoteDisplaySpeed = 0f;
        }

        var tm = InstanceFinder.TimeManager;
        double rtt = (tm != null) ? Math.Max(0.01, tm.RoundTripTime / 1000.0) : 0.06;
        _back = _backTarget = ClampD(rtt * 0.6, minBack, maxBack);

        _haveAnchor = false; _baseSnap = default;

        _tokens = maxInputsPerSecond + burstAllowance;
        _lastRefill = _netTime.Now();
        _shuttingDown = false;
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        _shuttingDown = false;
        _chunk?.RegisterPlayer(this);
        _serverLastPos = transform.position;
        _tokens = maxInputsPerSecond + burstAllowance;
        _lastRefill = _netTime.Now();
    }

    public override void OnStopServer()
    {
        _shuttingDown = true;
        _chunk?.UnregisterPlayer(this);
        base.OnStopServer();
    }

    public override void OnStopClient()
    {
        _shuttingDown = true;
        base.OnStopClient();
    }

    void OnDisable()
    {
        _shuttingDown = true;
        if (IsServerInitialized && _chunk != null)
        {
            try { _chunk.UnregisterPlayer(this); } catch { }
        }
    }

    void FixedUpdate()
    {
        if (!IsSpawned || _shuttingDown || s_AppQuitting)
            return;

        if (IsOwner)
        {
            Owner_Send();

            if (_doHardSnapNextFixed)
            {
                _rb.position = _pendingHardSnap;
                if (_agent) _agent.nextPosition = _rb.position;
                _doHardSnapNextFixed = false;
            }

            if (_reconcileActive)
            {
                Vector3 cur = _rb.position;
                Vector3 next = Vector3.Lerp(cur, _reconcileTarget, Mathf.Clamp01(reconcileRate * Time.fixedDeltaTime));
                _rb.MovePosition(next);
                if (_agent) _agent.nextPosition = next;
                if ((next - _reconcileTarget).sqrMagnitude < 0.0004f) _reconcileActive = false;
            }
        }
        else
        {
            Remote_Update();
        }

        if (IsServerInitialized)
            _chunk?.UpdatePlayerChunk(this, _rb.position);
    }

    void Owner_Send()
    {
        if (_shuttingDown || s_AppQuitting) return;

        _sendDt = 1f / Mathf.Max(1, sendRateHz);
        _sendTimer += Time.fixedDeltaTime;
        if (_sendTimer < _sendDt) return;
        _sendTimer -= _sendDt;

        _lastSeqSent++;

        Vector3 pos = _rb.position;
        Vector3 dir = _core.DebugLastMoveDir;
        bool running = _core.IsRunning;
        bool isCTM = _ctm && _ctm.HasPath;
        Vector3[] pathCorners = isCTM ? _ctm.GetPathCorners() : null;
        double timestamp = _netTime.Now();

        CmdSendInput(dir, pos, running, _lastSeqSent, isCTM, pathCorners, timestamp);

        var input = new InputState(dir, running, _lastSeqSent, _sendDt, timestamp);
        _inputBuf.Enqueue(input);
        if (_inputBuf.Count > 128) _inputBuf.Dequeue();
    }

    void Remote_Update()
    {
        if (_buffer.Count == 0) return;

        _back = Mathf.Lerp((float)_back, (float)_backTarget, Time.deltaTime * 3f);
        double now = _netTime.Now();
        double renderT = now - _back;

        if (TryGetBracket(renderT, out MovementSnapshot A, out MovementSnapshot B))
        {
            double span = B.serverTime - A.serverTime;
            float t = (span > 1e-6) ? (float)((renderT - A.serverTime) / span) : 1f;
            t = Mathf.Clamp01(t);

            bool lowVel = (A.vel.sqrMagnitude < 0.0001f && B.vel.sqrMagnitude < 0.0001f);
            bool tinyMove = (A.pos - B.pos).sqrMagnitude < 0.000004f; // <2mm
            Vector3 target = lowVel || tinyMove
                ? Vector3.Lerp(A.pos, B.pos, t)
                : Hermite(A.pos, A.vel * (float)span, B.pos, B.vel * (float)span, t);

            DriveRemote(target, A.animState);
            CleanupOld(renderT - 0.35);
        }
        else
        {
            MovementSnapshot last = _buffer[_buffer.Count - 1];
            double dt = Math.Min(now - last.serverTime, 0.15);
            Vector3 target = last.pos + last.vel * (float)dt;
            DriveRemote(target, last.animState);
            CleanupOld(renderT - 0.35);
        }
    }

    // ---- rotazione remota anti “moonwalk” + smoothing ----
    void DriveRemote(Vector3 target, byte animState)
    {
        if (ignoreNetworkY && _core != null)
            target.y = _core.SampleGroundY(target);

        Transform vr = _core ? _core.VisualRoot : null;
        Vector3 current = (remoteMoveVisualOnly && vr) ? vr.position : _rb.position;
        float k = 1f - Mathf.Exp(-remoteVisualLerpSpeed * Time.deltaTime);
        Vector3 smoothed = Vector3.Lerp(current, target, k);

        if (remoteMoveVisualOnly && vr) vr.position = smoothed; else _rb.position = smoothed;

        // velocità + direzione per facing
        Vector3 moveVec = smoothed - _remoteLastRenderPos;
        float dt = Mathf.Max(Time.deltaTime, 1e-6f);
        float rawSpeed = moveVec.magnitude / dt;
        _remoteDisplaySpeed = Mathf.Lerp(_remoteDisplaySpeed, rawSpeed, Time.deltaTime * remoteAnimSmooth);

        if (vr != null)
        {
            Vector3 dir = moveVec; dir.y = 0f;
            if (dir.sqrMagnitude > 0.00004f)
            {
                Quaternion face = Quaternion.LookRotation(dir.normalized);
                vr.rotation = Quaternion.Slerp(vr.rotation, face, Time.deltaTime * 12f);
                vr.rotation = Quaternion.Euler(0f, vr.eulerAngles.y, 0f);
            }
        }

        _remoteLastRenderPos = smoothed;

        // anim
        _core.SafeAnimSpeedRaw(_remoteDisplaySpeed);
        bool shouldRun = (animState == 2) && (_remoteDisplaySpeed > remoteRunSpeedThreshold * 0.75f);
        _core.SafeAnimRun(shouldRun);
    }

    // ==================== SERVER ====================

    bool RateLimitOk(double now)
    {
        float cap = maxInputsPerSecond + burstAllowance;
        float refill = (float)((now - _lastRefill) * refillPerSecond);
        if (refill > 0f)
        {
            _tokens = Mathf.Min(cap, _tokens + refill);
            _lastRefill = now;
        }
        if (_tokens >= 1f) { _tokens -= 1f; return true; }
        return false;
    }

    [ServerRpc(RequireOwnership = false)]
    void CmdSendInput(Vector3 dir, Vector3 predictedPos, bool running, uint seq, bool isCTM, Vector3[] pathCorners, double timestamp)
    {
        if (_shuttingDown || s_AppQuitting) return;

        double now = _netTime.Now();
        if (!RateLimitOk(now)) return;

        double dt = Math.Max(0.001, now - _serverLastTime);
        _serverLastTime = now;

        // slack dinamico in base alla latenza del singolo pacchetto
        double oneWay = Math.Max(0.0, now - timestamp);
        float stepSlack = Mathf.Clamp(1f + (float)(oneWay * 2.0) * slackK, slackMin, slackMax);

        float speed = running ? _core.speed * _core.runMultiplier : _core.speed;
        float maxStep = speed * (float)dt * maxSpeedTolerance * stepSlack;

        if (_anti != null && !_anti.ValidateInput(this, seq, timestamp, predictedPos, _serverLastPos, maxStep, pathCorners, running))
            predictedPos = _serverLastPos;

        if (validateNavMesh && NavMesh.SamplePosition(predictedPos, out var nh, navMeshMaxSampleDist, NavMesh.AllAreas))
            predictedPos = nh.position;

        Vector3 delta = predictedPos - _serverLastPos;
        Vector3 vel = delta / (float)dt;

        if (Mathf.Abs(vel.y) > maxVerticalSpeed)
        {
            predictedPos.y = _serverLastPos.y;
            delta = predictedPos - _serverLastPos;
            vel = delta / (float)dt;
        }
        vel.y = 0f;

        _serverLastPos = predictedPos;

        byte anim = (byte)((vel.magnitude > 0.12f) ? (running ? 2 : 1) : 0);
        var snap = new MovementSnapshot(predictedPos, vel, now, seq, anim);

        GetComponent<LagCompBuffer>()?.Push(predictedPos, vel, now);

        TargetOwnerCorrection(Owner, seq, predictedPos);
        Server_BroadcastPacked(snap);
    }

    void Server_BroadcastPacked(MovementSnapshot snap)
    {
        if (_shuttingDown || s_AppQuitting) return;

        if (forceBroadcastAll || _chunk == null || Owner == null)
        {
            ObserversPackedSnapshot(PackFullForObservers(snap));
            return;
        }

        _chunk.CollectWithinRadius(Owner, nearRing, _tmpNear);
        _chunk.CollectWithinRadius(Owner, midRing, _tmpMid);
        _chunk.CollectWithinRadius(Owner, farRing, _tmpFar);

        foreach (var c in _tmpNear) _tmpMid.Remove(c);
        foreach (var c in _tmpNear) _tmpFar.Remove(c);
        foreach (var c in _tmpMid) _tmpFar.Remove(c);

        double now = _netTime.Now();

        short cellX = 0, cellY = 0;
        if (_chunk.TryGetCellOf(Owner, out var cell)) { cellX = (short)cell.x; cellY = (short)cell.y; }

        foreach (var conn in _tmpNear) TrySendPackedTo(conn, snap, cellX, cellY, now, 1.0 / Math.Max(1, nearHz));
        foreach (var conn in _tmpMid) TrySendPackedTo(conn, snap, cellX, cellY, now, 1.0 / Math.Max(1, midHz));
        foreach (var conn in _tmpFar) TrySendPackedTo(conn, snap, cellX, cellY, now, 1.0 / Math.Max(1, farHz));

        _tmpNear.Clear(); _tmpMid.Clear(); _tmpFar.Clear();
    }

    byte[] PackFullForObservers(MovementSnapshot snap)
    {
        short cx = 0, cy = 0;
        if (_chunk && Owner != null && _chunk.TryGetCellOf(Owner, out var cell))
        { cx = (short)cell.x; cy = (short)cell.y; }
        int cs = _chunk ? _chunk.cellSize : 128;
        return PackedMovement.PackFull(snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq, cx, cy, cs);
    }

    void TrySendPackedTo(NetworkConnection conn, MovementSnapshot snap, short cellX, short cellY, double now, double interval)
    {
        if (_shuttingDown || s_AppQuitting) return;
        if (conn == null || !conn.IsActive) return;
        if (_nextSendAt.TryGetValue(conn, out var t) && now < t) return;

        bool sendFull = false;
        if (!_lastSentCell.TryGetValue(conn, out var lastCell)) sendFull = true;
        else if (lastCell.cellX != cellX || lastCell.cellY != cellY) sendFull = true;
        _lastSentCell[conn] = (cellX, cellY);

        int sinceKF = 0; _sinceKeyframe.TryGetValue(conn, out sinceKF);
        if (keyframeEvery > 0 && sinceKF >= keyframeEvery) sendFull = true;

        byte[] payload = null;

        if (!sendFull && _lastSentSnap.TryGetValue(conn, out var last))
        {
            payload = PackedMovement.PackDelta(in last, snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq,
                                               cellX, cellY, _chunk.cellSize, maxPosDeltaCm, maxVelDeltaCms, maxDtMs);
            if (payload == null) sendFull = true;
        }

        if (sendFull)
        {
            payload = PackedMovement.PackFull(snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq,
                                              cellX, cellY, _chunk.cellSize);
            _sinceKeyframe[conn] = 0;
        }
        else
        {
            _sinceKeyframe[conn] = sinceKF + 1;
        }

        // NB: TargetRpc richiede che 'conn' sia OBSERVER di questo NetworkObject.
        TargetPackedSnapshotTo(conn, payload);

        _lastSentSnap[conn] = snap;
        _nextSendAt[conn] = now + interval;
    }

    [ObserversRpc]
    void ObserversPackedSnapshot(byte[] payload)
    {
        if (_shuttingDown || s_AppQuitting) return;
        HandlePackedPayload(payload);
    }

    [TargetRpc]
    void TargetPackedSnapshotTo(NetworkConnection conn, byte[] payload)
    {
        if (_shuttingDown || s_AppQuitting) return;
        HandlePackedPayload(payload);
    }

    void HandlePackedPayload(byte[] payload)
    {
        MovementSnapshot snap;
        int cs = _chunk ? _chunk.cellSize : 128;

        if (!PackedMovement.TryUnpack(payload, cs, ref _haveAnchor, ref _anchorCellX, ref _anchorCellY, ref _baseSnap, out snap))
            return;

        if (_buffer.Count == 0 || snap.serverTime >= _buffer[_buffer.Count - 1].serverTime)
            _buffer.Add(snap);
        else
        {
            for (int i = 0; i < _buffer.Count; i++)
            {
                if (snap.serverTime < _buffer[i].serverTime)
                {
                    _buffer.Insert(i, snap);
                    break;
                }
            }
        }
        if (_buffer.Count > 256) _buffer.RemoveAt(0);

        double now = _netTime.Now();
        double delay = Math.Max(0.0, now - snap.serverTime);
        _emaDelay = (_emaDelay <= 0.0) ? delay : (1.0 - emaDelayA) * _emaDelay + emaDelayA * delay;
        double dev = Math.Abs(delay - _emaDelay);
        _emaJitter = (_emaJitter <= 0.0) ? dev : (1.0 - emaJitterA) * _emaJitter + emaJitterA * dev;

        double targetBack = _emaDelay * 1.35 + _emaJitter * 1.6;
        _backTarget = ClampD(targetBack, minBack, maxBack);
        if (_back <= 0.0) _back = _backTarget;
    }

    [TargetRpc]
    void TargetOwnerCorrection(NetworkConnection conn, uint serverSeq, Vector3 serverPos)
    {
        if (_shuttingDown || s_AppQuitting) return;

        while (_inputBuf.Count > 0 && _inputBuf.Peek().seq <= serverSeq)
            _inputBuf.Dequeue();

        Vector3 corrected = serverPos;
        foreach (var inp in _inputBuf)
        {
            float spd = inp.running ? _core.speed * _core.runMultiplier : _core.speed;
            if (inp.dir.sqrMagnitude > 1e-6f)
                corrected += inp.dir.normalized * spd * inp.dt;
        }

        float errXZ = Vector2.Distance(
            new Vector2(_rb.position.x, _rb.position.z),
            new Vector2(corrected.x, corrected.z));
        if (errXZ < deadZone) return;

        if (ignoreNetworkY && _core != null)
            corrected.y = _core.SampleGroundY(corrected);

        if (errXZ > hardSnapDist)
        {
            _pendingHardSnap = corrected;
            _doHardSnapNextFixed = true;
            _reconcileActive = false;
        }
        else
        {
            _reconcileTarget = corrected;
            _reconcileActive = true;
        }
    }

    // ------- helpers -------
    static Vector3 Hermite(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        float h00 = 2f * t3 - 3f * t2 + 1f;
        float h10 = t3 - 2f * t2 + t;
        float h01 = -2f * t3 + 3f * t2;
        float h11 = t3 - t2;
        return h00 * p0 + h10 * v0 + h01 * p1 + h11 * v1;
    }

    bool TryGetBracket(double renderT, out MovementSnapshot A, out MovementSnapshot B)
    {
        A = default; B = default;
        int n = _buffer.Count;
        if (n < 2) return false;
        int r = -1;
        for (int i = 0; i < n; i++)
            if (_buffer[i].serverTime > renderT) { r = i; break; }
        if (r <= 0) return false;
        A = _buffer[r - 1]; B = _buffer[r];
        return true;
    }

    void CleanupOld(double cutoff)
    {
        int removeCount = 0;
        for (int i = 0; i < _buffer.Count; i++)
        {
            if (_buffer[i].serverTime < cutoff) removeCount++;
            else break;
        }
        if (removeCount > 0 && _buffer.Count - removeCount >= 2)
            _buffer.RemoveRange(0, removeCount);
    }

    static double ClampD(double v, double min, double max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }
}

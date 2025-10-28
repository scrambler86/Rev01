using UnityEngine;
using UnityEngine.AI;
using FishNet.Object;
using FishNet.Connection;
using FishNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Game.Network; // for ClockSyncManager (client-side hook)

[RequireComponent(typeof(PlayerControllerCore))]
[RequireComponent(typeof(Rigidbody))]
public class PlayerNetworkDriverFishNet : NetworkBehaviour, IPlayerNetworkDriver
{
    // ---- Quit guard (no RPC during shutdown) ----
    private static bool s_AppQuitting = false;
    private static void OnAppQuit() => s_AppQuitting = true;

    [Header("Refs")]
    [SerializeField] private PlayerControllerCore _core;
    [SerializeField] private Rigidbody _rb;
    [SerializeField] private NavMeshAgent _agent;
    [SerializeField] private ClickToMoveAgent _ctm;

    // ---------- ClockSync helper (client) ----------
    private ClockSyncManager _clockSync;

    [Header("Owner → Server send")]
    [Range(10, 60)] public int sendRateHz = 30;

    [Header("Remotes interpolation")]
    [SerializeField] private double minBack = 0.14;
    [SerializeField] private double maxBack = 0.32;
    [Range(0.05f, 0.5f)] public float emaDelayA = 0.18f;
    [Range(0.05f, 0.5f)] public float emaJitterA = 0.18f;

    [Header("Reconciliation (Owner)")]
    public float deadZone = 0.22f;
    public float hardSnapDist = 0.80f;
    public float reconcileRate = 12f;
    public float maxCorrectionSpeed = 6f;
    [Range(0f, 1f)] public float reconciliationSmoothing = 0.85f;
    public float hardSnapRateLimitSeconds = 1.0f;

    [Header("FEC parity (full-keyframe)")]
    [Tooltip("Number of parity shards (simple XOR). 0 = disabled")]
    public int fecParityShards = 1;
    [Tooltip("Max bytes per shard during splitting")]
    public int fecShardSize = 1024;

    [Header("Elastic correction (client)")]
    public float correctionDurationSeconds = 0.25f;
    public float correctionInitialMultiplier = 2.0f;
    public float correctionDecay = 0.55f;
    public float correctionMinVisible = 0.03f;

    [Header("Anti-cheat (Server)")]
    public float maxSpeedTolerance = 1.18f;
    public float slackK = 0.6f;
    public float slackMin = 1.00f;
    public float slackMax = 1.75f;
    public bool validateNavMesh = true;
    public float navMeshMaxSampleDist = 1.0f;
    public float maxVerticalSpeed = 2.0f;

    [Header("Remoti visual/anim")]
    public bool remoteMoveVisualOnly = true;
    public float remoteVisualLerpSpeed = 16f;
    public float remoteAnimSmooth = 8f;
    public float remoteRunSpeedThreshold = 3.0f;

    [Header("Height policy")]
    public bool ignoreNetworkY = true;

    [Header("Broadcast Scheduler (per-conn)")]
    public int nearRing = 1, midRing = 2, farRing = 3;
    public int nearHz = 30, midHz = 10, farHz = 3;

    [Header("Delta Compression")]
    public int keyframeEvery = 20;
    public int maxPosDeltaCm = 60;
    public int maxVelDeltaCms = 150;
    public int maxDtMs = 200;

    [Header("Input Rate Limit (Server)")]
    public int maxInputsPerSecond = 120;
    public int burstAllowance = 30;
    public float refillPerSecond = 120f;

    [Header("DEBUG / Fallback")]
    public bool forceBroadcastAll = false;

    [Header("DEBUG / Tests")]
    [Tooltip("When true forces sending full snapshots (no delta/FEC) to help isolate CRC/fragmentation issues")]
    public bool debugForceFullSnapshots = false;

    // ==== IPlayerNetworkDriver ====
    public INetTime NetTime => _netTime;
    public int OwnerClientId => (Owner != null ? Owner.ClientId : -1);
    public uint LastSeqReceived => _lastSeqReceived;
    public void SetLastSeqReceived(uint seq) => _lastSeqReceived = seq;
    public double ClientRttMs => _lastRttMs;

    // ==== privati ====
    private INetTime _netTime;
    private IAntiCheatValidator _anti;
    private ChunkManager _chunk;
    private TelemetryManager _telemetry;

    private float _sendDt, _sendTimer;
    private uint _lastSeqSent, _lastSeqReceived;

    private readonly List<MovementSnapshot> _buffer = new(256);
    private double _emaDelay, _emaJitter, _back, _backTarget;

    private bool _reconcileActive, _doHardSnapNextFixed;
    private Vector3 _reconcileTarget, _pendingHardSnap;

    private Vector3 _serverLastPos;
    private double _serverLastTime;

    private Vector3 _remoteLastRenderPos;
    private float _remoteDisplaySpeed;

    private readonly Queue<InputState> _inputBuf = new(128);

    private readonly HashSet<NetworkConnection> _tmpNear = new();
    private readonly HashSet<NetworkConnection> _tmpMid = new();
    private readonly HashSet<NetworkConnection> _tmpFar = new();
    private readonly Dictionary<NetworkConnection, double> _nextSendAt = new();

    private readonly Dictionary<NetworkConnection, MovementSnapshot> _lastSentSnap = new();
    private readonly Dictionary<NetworkConnection, int> _sinceKeyframe = new();
    private readonly Dictionary<NetworkConnection, (short cellX, short cellY)> _lastSentCell = new();

    private bool _haveAnchor;
    private short _anchorCellX, _anchorCellY;
    private MovementSnapshot _baseSnap;

    private float _tokens;
    private double _lastRefill;
    private double _lastRttMs;

    private bool _shuttingDown;

    private double _lastHardSnapTime = -9999.0;

    // ---------- ClockSync fields ----------
    private double _clockOffsetSeconds = 0.0;
    private double _clockOffsetEmaMs = 0.0;
    private double _clockOffsetJitterMs = 0.0;
    private const double CLOCK_ALPHA = 0.15;
    private const double CLOCK_ALPHA_JITTER = 0.12;

    // ---------- Reconcile cooldown ----------
    private double _lastReconcileSentTime = -9999.0;
    private const double RECONCILE_COOLDOWN_SEC = 0.20; // default, tune as needed

    // ---------- Reliable full-keyframe + FEC storage ----------
    private readonly Dictionary<NetworkConnection, byte[]> _lastFullPayload = new();
    private readonly Dictionary<NetworkConnection, double> _lastFullSentAt = new();
    private readonly Dictionary<NetworkConnection, int> _fullRetryCount = new();
    private readonly Dictionary<NetworkConnection, List<byte[]>> _lastFullShards = new(); // shards incl parity

    private const double FULL_RETRY_SECONDS = 0.6;
    private const int FULL_RETRY_MAX = 4;

    // ---------- Elastic correction state (client) ----------
    private bool _isApplyingElastic = false;
    private Vector3 _elasticStartPos;
    private Vector3 _elasticTargetPos;
    private float _elasticElapsed = 0f;
    private float _elasticDuration = 0f;
    private float _elasticCurrentMultiplier = 1f;

    // ---------- CRC rate-limited logging (no blocking) ----------
    private int _crcFailCount = 0;
    private double _crcFirstFailTime = -1.0;
    private double _crcLastLogTime = -9999.0;
    private const double CRC_LOG_WINDOW_SECONDS = 5.0; // window to accumulate events
    private const int CRC_LOG_MAX_PER_WINDOW = 5; // max logs emitted per window

    // ---------- lifecycle ----------
    void Awake()
    {
        Application.quitting -= OnAppQuit;
        Application.quitting += OnAppQuit;

        if (!_core) _core = GetComponent<PlayerControllerCore>();
        if (!_rb) _rb = GetComponent<Rigidbody>();
        if (!_agent) _agent = GetComponent<NavMeshAgent>();
        if (!_ctm) _ctm = GetComponent<ClickToMoveAgent>();

        _netTime = new NetTimeFishNet();
        _anti = FindObjectOfType<AntiCheatManager>();
        _chunk = FindObjectOfType<ChunkManager>();
        _telemetry = FindObjectOfType<TelemetryManager>();

        _clockSync = GetComponentInChildren<ClockSyncManager>();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        _sendDt = 1f / Mathf.Max(1, sendRateHz);
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

        _serverLastTime = _netTime.Now();
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        _shuttingDown = false;
        _chunk?.RegisterPlayer(this);
        _serverLastPos = transform.position;
        _tokens = maxInputsPerSecond + burstAllowance;
        _lastRefill = _netTime.Now();

        _serverLastTime = _netTime.Now();
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

    // ---------- main loop ----------
    void FixedUpdate()
    {
        if (!IsSpawned || _shuttingDown || s_AppQuitting)
            return;

        if (IsOwner)
        {
            Owner_Send();

            // Elastic correction application (client)
            if (_isApplyingElastic)
            {
                _elasticElapsed += Time.fixedDeltaTime;
                float t = Mathf.Clamp01(_elasticElapsed / _elasticDuration);
                float ease = 1f - Mathf.Pow(1f - t, 3f);
                Vector3 cur = _rb.position;
                Vector3 target = Vector3.Lerp(_elasticStartPos, _elasticTargetPos, ease);
                float maxAllowed = maxCorrectionSpeed * Time.fixedDeltaTime * _elasticCurrentMultiplier;
                Vector3 next = Vector3.MoveTowards(cur, target, maxAllowed);
                if (remoteMoveVisualOnly && _core && _core.visualRoot != null)
                    _core.visualRoot.position = next;
                else
                    _rb.position = next;
                if (_agent) _agent.nextPosition = next;

                float applied = Vector3.Distance(cur, next);
                _telemetry?.Observe($"client.{OwnerClientId}.elastic_applied_cm", applied * 100.0);
                _telemetry?.Observe($"client.{OwnerClientId}.elastic_progress", ease * 100.0);

                _elasticCurrentMultiplier *= correctionDecay;

                if (t >= 1f || Vector3.Distance(next, _elasticTargetPos) < correctionMinVisible)
                {
                    _isApplyingElastic = false;
                    _elasticElapsed = 0f;
                    _elasticCurrentMultiplier = 1f;
                    _telemetry?.Increment($"client.{OwnerClientId}.elastic_completed");
                }
            }

            if (_doHardSnapNextFixed && !_isApplyingElastic)
            {
                Vector3 cur = _rb.position;
                Vector3 target = _pendingHardSnap;
                float dist = Vector3.Distance(cur, target);
                if (dist > hardSnapDist * 1.1f && (Time.realtimeSinceStartup - (float)_lastHardSnapTime) > 0.05f)
                {
                    Vector3 next = Vector3.MoveTowards(cur, target, maxCorrectionSpeed * Time.fixedDeltaTime * 1.8f);
                    _rb.position = next;
                    if (_agent) _agent.nextPosition = next;
                    if (Vector3.Distance(next, target) < 0.05f) _doHardSnapNextFixed = false;
                }
                else
                {
                    _rb.position = target;
                    if (_agent) _agent.nextPosition = target;
                    _doHardSnapNextFixed = false;
                }
            }

            if (_reconcileActive && !_isApplyingElastic)
            {
                float maxAllowed = maxCorrectionSpeed * Time.fixedDeltaTime;
                Vector3 cur = _rb.position;
                Vector3 toTarget = _reconcileTarget - cur;
                float dist = toTarget.magnitude;

                if (dist <= 0.0001f)
                {
                    _reconcileActive = false;
                }
                else
                {
                    if (dist > hardSnapDist)
                    {
                        double now = _netTime.Now();
                        if (now - _lastHardSnapTime > hardSnapRateLimitSeconds)
                        {
                            _pendingHardSnap = _reconcileTarget;
                            _doHardSnapNextFixed = true;
                            _reconcileActive = false;
                            _lastHardSnapTime = now;
                            _telemetry?.Increment("reconcile.hard_snaps");
                            if (_telemetry != null) _telemetry.Increment($"client.{OwnerClientId}.hard_snaps");
                        }
                        else
                        {
                            Vector3 step = Vector3.ClampMagnitude(toTarget, maxAllowed);
                            Vector3 next = cur + step;
                            next = Vector3.Lerp(cur, next, 1f - reconciliationSmoothing);
                            _rb.MovePosition(next);
                            if (_agent) _agent.nextPosition = next;
                            _telemetry?.Increment("reconcile.rate_limited_snaps");
                        }
                    }
                    else
                    {
                        float alpha = 1f - Mathf.Exp(-reconcileRate * Time.fixedDeltaTime);
                        Vector3 desired = Vector3.Lerp(cur, _reconcileTarget, alpha);
                        Vector3 capped = Vector3.MoveTowards(cur, desired, maxAllowed);
                        Vector3 smoothed = Vector3.Lerp(cur, capped, 1f - reconciliationSmoothing);
                        _rb.MovePosition(smoothed);
                        if (_agent) _agent.nextPosition = smoothed;
                        if ((smoothed - _reconcileTarget).sqrMagnitude < 0.0004f) _reconcileActive = false;
                        _telemetry?.Increment("reconcile.smooth_steps");
                    }
                }
            }
        }
        else
        {
            Remote_Update();
        }

        if (IsServerInitialized)
            _chunk?.UpdatePlayerChunk(this, _rb.position);

        // Server-side: retry outstanding full keyframes (use IsServerStarted semantics)
        if (IsServerStarted)
        {
            if (_lastFullPayload.Count > 0 || _lastFullShards.Count > 0)
            {
                double now = _netTime.Now();
                var toRetry = new List<NetworkConnection>();
                foreach (var kv in _lastFullPayload)
                {
                    var conn = kv.Key;
                    if (conn == null || !conn.IsActive) continue;
                    if (!_lastFullSentAt.TryGetValue(conn, out var t)) continue;
                    int tries = _fullRetryCount.TryGetValue(conn, out var c) ? c : 0;
                    if (tries >= FULL_RETRY_MAX) continue;
                    if (now - t > FULL_RETRY_SECONDS) toRetry.Add(conn);
                }
                foreach (var kv in _lastFullShards)
                {
                    var conn = kv.Key;
                    if (conn == null || !conn.IsActive) continue;
                    if (!_lastFullSentAt.TryGetValue(conn, out var t)) continue;
                    int tries = _fullRetryCount.TryGetValue(conn, out var c) ? c : 0;
                    if (tries >= FULL_RETRY_MAX) continue;
                    if (now - t > FULL_RETRY_SECONDS) toRetry.Add(conn);
                }

                foreach (var conn in toRetry.Distinct())
                {
                    if (_lastFullShards.TryGetValue(conn, out var shards) && shards != null && shards.Count > 0)
                    {
                        foreach (var s in shards) TargetPackedShardTo(conn, s);
                    }
                    else if (_lastFullPayload.TryGetValue(conn, out var payload))
                    {
                        TargetPackedSnapshotTo(conn, payload, ComputeStateHashFromPayload(payload));
                    }
                    _lastFullSentAt[conn] = _netTime.Now();
                    _fullRetryCount[conn] = _fullRetryCount.TryGetValue(conn, out var old) ? old + 1 : 1;
                    _telemetry?.Increment("pack.full_retry");
                }
            }
        }
    }

    // ---------- owner side ----------
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

        double localClientTime = Time.timeAsDouble;
        double timestampToSend;
        if (_clockSync != null)
        {
            timestampToSend = _clockSync.ClientToServerTime(localClientTime);
        }
        else
        {
            timestampToSend = _netTime.Now();
        }

        _telemetry?.Observe($"client.{OwnerClientId}.sent_timestamp_diff_ms", (timestampToSend - localClientTime) * 1000.0);

        CmdSendInput(dir, pos, running, _lastSeqSent, isCTM, pathCorners, timestampToSend);

        var input = new InputState(dir, running, _lastSeqSent, _sendDt, localClientTime);
        _inputBuf.Enqueue(input);
        if (_inputBuf.Count > 128) _inputBuf.Dequeue();
    }

    // ---------- remotes render ----------
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
            bool tinyMove = (A.pos - B.pos).sqrMagnitude < 0.000004f;
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

    void DriveRemote(Vector3 target, byte animState)
    {
        if (ignoreNetworkY)
            target.y = transform.position.y;

        Transform vr = _core ? _core.visualRoot : null;
        Vector3 current = (remoteMoveVisualOnly && vr) ? vr.position : _rb.position;
        float k = 1f - Mathf.Exp(-remoteVisualLerpSpeed * Time.deltaTime);
        Vector3 smoothed = Vector3.Lerp(current, target, k);

        if (remoteMoveVisualOnly && vr) vr.position = smoothed; else _rb.position = smoothed;

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

        // Compute dt robustly
        double dtServerRaw = now - _serverLastTime;
        double dtClientEstimate = Math.Max(0.0, now - timestamp);
        double dt = Math.Max(0.001, Math.Max(dtServerRaw, dtClientEstimate));

        _serverLastTime = now;

        // RTT estimate
        double oneWay = Math.Max(0.0, now - timestamp);
        _lastRttMs = oneWay * 2000.0;

        float stepSlack = Mathf.Clamp(1f + (float)(oneWay * 2.0) * slackK, slackMin, slackMax);

        float speed = running ? _core.speed * _core.runMultiplier : _core.speed;
        float maxStep = speed * (float)dt * maxSpeedTolerance * stepSlack;

        // Telemetry: inputs received
        _telemetry?.Increment($"client.{OwnerClientId}.inputs_received");
        _telemetry?.Observe($"client.{OwnerClientId}.maxStep_cm", maxStep * 100.0);

        // Anti-cheat validation
        bool ok = true;
        float planarDist = 0f;
        if (_anti != null)
        {
            if (_anti is AntiCheatManager acm)
                ok = acm.ValidateInput(this, seq, timestamp, predictedPos, _serverLastPos, maxStep, pathCorners, running, (float)dt);
            else
                ok = _anti.ValidateInput(this, seq, timestamp, predictedPos, _serverLastPos, maxStep, pathCorners, running);
        }

        if (!ok)
        {
            Vector3 delta = predictedPos - _serverLastPos;
            Vector3 planar = new Vector3(delta.x, 0f, delta.z);
            planarDist = planar.magnitude;
            _telemetry?.Observe($"client.{OwnerClientId}.planarDist_cm", planarDist * 100.0);

            var tags = new Dictionary<string, string> {
                { "clientId", OwnerClientId.ToString() },
                { "event", "soft_clamp" }
            };
            var metrics = new Dictionary<string, double> {
                { "planarDist_cm", planarDist * 100.0 },
                { "maxStep_cm", maxStep * 100.0 },
                { "rtt_ms", _lastRttMs }
            };

            int sampleCount = 0;
            var inpList = new List<string>();
            foreach (var inp in _inputBuf)
            {
                if (sampleCount++ >= 6) break;
                inpList.Add($"{inp.seq}:{inp.dir.x:0.00},{inp.dir.z:0.00}");
            }
            if (inpList.Count > 0) tags["input_sample"] = string.Join("|", inpList);

            _telemetry?.Event("anti_cheat.soft_clamp", tags, metrics);

            if (planarDist > 1e-6f)
            {
                float allowed = Mathf.Max(0f, maxStep);
                if (planarDist > allowed)
                    predictedPos = _serverLastPos + planar.normalized * allowed + new Vector3(0f, Mathf.Clamp(delta.y, -maxVerticalSpeed * (float)dt, maxVerticalSpeed * (float)dt), 0f);
                else
                    predictedPos = _serverLastPos;
            }
            else
            {
                predictedPos = _serverLastPos;
            }

            if (_anti is AntiCheatManager ac && ac.debugLogs)
                Debug.LogWarning($"[AC] Soft-clamp seq={seq} clientDelta={planarDist:0.###} maxStep={maxStep:0.###} rttMs={_lastRttMs:0.0}");

            _telemetry?.Increment($"client.{OwnerClientId}.anti_cheat.soft_clamps");
            _telemetry?.Increment("anti_cheat.soft_clamps");
        }

        // NavMesh clamp telemetry
        if (validateNavMesh && NavMesh.SamplePosition(predictedPos, out var nh, navMeshMaxSampleDist, NavMesh.AllAreas))
            predictedPos = nh.position;

        Vector3 deltaPos = predictedPos - _serverLastPos;
        Vector3 vel = deltaPos / (float)dt;

        if (Mathf.Abs(vel.y) > maxVerticalSpeed)
        {
            predictedPos.y = _serverLastPos.y;
            deltaPos = predictedPos - _serverLastPos;
            vel = deltaPos / (float)dt;
        }
        vel.y = 0f;

        // keep prior serverLastPos used for later checks (planarErr)
        Vector3 oldServerLast = _serverLastPos;
        _serverLastPos = predictedPos;

        byte anim = (byte)((vel.magnitude > 0.12f) ? (running ? 2 : 1) : 0);
        var snap = new MovementSnapshot(predictedPos, vel, now, seq, anim);

        GetComponent<LagCompBuffer>()?.Push(predictedPos, vel, now);

        // Decide whether to force full keyframe for reliability/hysteresis
        bool requireFull = false;
        float planarErr = Vector3.Distance(predictedPos, oldServerLast);

        int sinceKFcount = 0;
        _sinceKeyframe.TryGetValue(Owner, out sinceKFcount);
        if (sinceKFcount >= Math.Max(1, keyframeEvery / 2)) requireFull = true;

        if (planarErr > hardSnapDist * 0.9f && (now - _lastReconcileSentTime) < RECONCILE_COOLDOWN_SEC * 1.5)
        {
            requireFull = true;
            _telemetry?.Increment("reconcile.suppressed_in_favor_of_full");
        }

        // Telemetry: broadcast decisions
        _telemetry?.Increment($"client.{OwnerClientId}.snap_broadcasts");

        // Server_BroadcastPacked will call TrySendPackedTo which now stores last full payload & shards when sendFull==true
        if (requireFull)
        {
            short cellX = 0, cellY = 0;
            if (_chunk.TryGetCellOf(Owner, out var ccell)) { cellX = (short)ccell.x; cellY = (short)ccell.y; }
            byte[] full = PackedMovement.PackFull(snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq, cellX, cellY, _chunk.cellSize);

            // compute state hash
            ulong stateHash = ComputeStateHashForSnapshot(snap);

            // build shards if FEC enabled
            if (fecParityShards > 0 && !debugForceFullSnapshots)
            {
                var shards = BuildFecShards(full, fecShardSize, fecParityShards);
                _lastFullShards[Owner] = shards;
                // send shards to client sequentially (client will reassemble)
                foreach (var s in shards) TargetPackedShardTo(Owner, s);
            }
            else
            {
                _lastFullPayload[Owner] = full;
                _lastFullSentAt[Owner] = _netTime.Now();
                _fullRetryCount[Owner] = 0;
                TargetPackedSnapshotTo(Owner, full, stateHash);
            }

            _telemetry?.Increment($"client.{OwnerClientId}.full_forced");
        }
        else
        {
            SendTargetOwnerCorrection(seq, predictedPos);
        }

        Server_BroadcastPacked(snap);
    }

    // ---------- Ping / ClockSync RPCs ----------
    [ServerRpc(RequireOwnership = false)]
    public void PingRequest(double clientSendTimeSeconds)
    {
        if (_shuttingDown || s_AppQuitting) return;

        double serverRecv = _netTime.Now();
        PingReply(Owner, clientSendTimeSeconds, serverRecv, _netTime.Now());
    }

    [TargetRpc]
    public void PingReply(NetworkConnection conn, double clientSendTimeSeconds, double serverRecvTimeSeconds, double serverSendTimeSeconds)
    {
        if (_shuttingDown || s_AppQuitting) return;
        OnClientReceivePingReply(clientSendTimeSeconds, serverRecvTimeSeconds, serverSendTimeSeconds);
    }

    void OnClientReceivePingReply(double clientSendTimeSeconds, double serverRecvTimeSeconds, double serverSendTimeSeconds)
    {
        double clientRecvTimeSeconds = _netTime.Now();

        double rttMs = Math.Max(0.0, (clientRecvTimeSeconds - clientSendTimeSeconds) * 1000.0);
        _lastRttMs = rttMs;

        double serverMid = (serverRecvTimeSeconds + serverSendTimeSeconds) * 0.5;
        double clientMid = clientSendTimeSeconds + (clientRecvTimeSeconds - clientSendTimeSeconds) * 0.5;
        double offsetMs = (serverMid - clientMid) * 1000.0;

        if (_clockOffsetEmaMs == 0.0) _clockOffsetEmaMs = offsetMs; else _clockOffsetEmaMs = (1.0 - CLOCK_ALPHA) * _clockOffsetEmaMs + CLOCK_ALPHA * offsetMs;
        double sampleJ = Math.Abs(offsetMs - _clockOffsetEmaMs);
        if (_clockOffsetJitterMs == 0.0) _clockOffsetJitterMs = sampleJ; else _clockOffsetJitterMs = (1.0 - CLOCK_ALPHA_JITTER) * _clockOffsetJitterMs + CLOCK_ALPHA_JITTER * sampleJ;

        _clockOffsetSeconds = _clockOffsetEmaMs / 1000.0;

        _telemetry?.Observe($"client.{OwnerClientId}.rtt_ms", rttMs);
        _telemetry?.Observe($"client.{OwnerClientId}.clock_offset_ms", offsetMs);

        _telemetry?.Event("clock.sample",
            new Dictionary<string, string> {
                { "clientId", OwnerClientId.ToString() },
                { "sampleType", "pingReply" }
            },
            new Dictionary<string, double> {
                { "rtt_ms", rttMs },
                { "offset_ms", offsetMs }
            });

        var csm = GetComponentInChildren<ClockSyncManager>();
        if (csm != null)
        {
            csm.RecordSample(rttMs, offsetMs);
        }
    }

    public double GetEstimatedClientToServerOffsetSeconds()
    {
        return _clockOffsetSeconds;
    }

    public double GetLastMeasuredRttMs() => _lastRttMs;

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

        // If debug flag set, force full snapshot to help isolate FEC/delta issues
        if (debugForceFullSnapshots) sendFull = true;

        byte[] payload = null;

        if (!sendFull && _lastSentSnap.TryGetValue(conn, out var last))
        {
            var lastSnapLocal = last;
            payload = PackedMovement.PackDelta(in lastSnapLocal, snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq,
                                               cellX, cellY, _chunk.cellSize, maxPosDeltaCm, maxVelDeltaCms, maxDtMs);
            if (payload == null)
            {
                sendFull = true;
                _telemetry?.Increment("pack.fallback_count");
            }
        }

        if (sendFull)
        {
            payload = PackedMovement.PackFull(snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq,
                                              cellX, cellY, _chunk.cellSize);
            _sinceKeyframe[conn] = 0;

            // compute state hash
            ulong stateHash = ComputeStateHashForSnapshot(snap);

            // store for retry
            _lastFullPayload[conn] = payload;
            _lastFullSentAt[conn] = now;
            _fullRetryCount[conn] = 0;

            // if FEC enabled and debug flag NOT set, build shards and send them
            if (fecParityShards > 0 && !debugForceFullSnapshots)
            {
                var shards = BuildFecShards(payload, fecShardSize, fecParityShards);
                _lastFullShards[conn] = shards;
                foreach (var s in shards) TargetPackedShardTo(conn, s);
            }
            else
            {
                TargetPackedSnapshotTo(conn, payload, stateHash);
            }
        }
        else
        {
            _sinceKeyframe[conn] = sinceKF + 1;
            var lastSnapLocal = _lastSentSnap[conn];
            var deltaPayload = PackedMovement.PackDelta(in lastSnapLocal, snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq, cellX, cellY, _chunk.cellSize, maxPosDeltaCm, maxVelDeltaCms, maxDtMs);
            TargetPackedSnapshotTo(conn, deltaPayload, ComputeStateHashForSnapshot(snap));
        }

        _lastSentSnap[conn] = snap;
        _nextSendAt[conn] = now + interval;
    }

    [ObserversRpc]
    void ObserversPackedSnapshot(byte[] payload)
    {
        if (_shuttingDown || s_AppQuitting) return;
        HandlePackedPayload(payload);
    }

    // Server -> Client: full snapshot payload + stateHash
    [TargetRpc]
    void TargetPackedSnapshotTo(NetworkConnection conn, byte[] payload, ulong stateHash)
    {
        if (_shuttingDown || s_AppQuitting) return;
        HandlePackedPayload(payload, stateHash);
    }

    // Server -> Client: shard (part of FEC). Client will reassemble when enough received.
    [TargetRpc]
    void TargetPackedShardTo(NetworkConnection conn, byte[] shard)
    {
        if (_shuttingDown || s_AppQuitting) return;
        HandlePackedShard(shard);
    }

    // Client -> Server ack for full snapshot seq (with optional stateHash verified)
    [ServerRpc(RequireOwnership = false)]
    void ServerAckFullSnapshot(uint ackSeq, ulong clientStateHash)
    {
        if (_shuttingDown || s_AppQuitting) return;
        var conn = base.Owner;
        if (conn == null) return;
        _lastFullPayload.Remove(conn);
        _lastFullSentAt.Remove(conn);
        _fullRetryCount.Remove(conn);
        _lastFullShards.Remove(conn);
        _telemetry?.Increment($"client.{OwnerClientId}.full_ack");
        // record mismatch if hashes differ (server expected vs client)
        // Note: to compute server expected we would need to map seq->hash store; approximate here by telemetry only
    }

    // Client-side: partial shard buffering
    private readonly List<ShardInfo> _shardBufferInfo = new();
    private int _expectedShardCount = 0;

    // Internal helper to hold per-shard dataLen and bytes
    private class ShardInfo
    {
        public ushort total;
        public ushort index;
        public int dataLen;
        public byte[] data;
    }

    void HandlePackedShard(byte[] shard)
    {
        // new header layout: total (ushort, 2) | index (ushort, 2) | dataLen (uint, 4) => header 8 bytes
        if (shard == null || shard.Length < 8) return;

        ushort total = BitConverter.ToUInt16(shard, 0);
        ushort idx = BitConverter.ToUInt16(shard, 2);
        uint dataLenU = BitConverter.ToUInt32(shard, 4);
        int dataLen = (int)dataLenU;

        if (dataLen < 0 || shard.Length < 8 + dataLen) return; // corrupted fragment

        byte[] data = new byte[dataLen];
        Array.Copy(shard, 8, data, 0, dataLen);

        // initialize buffer info if first fragment
        if (_shardBufferInfo.Count == 0)
        {
            _shardBufferInfo.Clear();
            for (int i = 0; i < total; i++) _shardBufferInfo.Add(null);
            _expectedShardCount = total;
        }

        if (idx < _shardBufferInfo.Count)
        {
            _shardBufferInfo[idx] = new ShardInfo { total = total, index = idx, dataLen = dataLen, data = data };
        }

        // check if all present (data != null)
        bool all = true;
        foreach (var s in _shardBufferInfo) { if (s == null) { all = false; break; } }
        if (!all) return;

        // At this point all data shards and parity shards may be present.
        // Distinguish data shards count = total - parityCount (when parityCount known on server it's not sent; infer that parity exists if fecParityShards>0)
        int totalShards = _shardBufferInfo.Count;
        int parityCount = fecParityShards; // assume server and client agree
        int dataShards = Math.Max(1, totalShards - parityCount);

        // compute payload length (sum of dataLen of first dataShards)
        long payloadLengthLong = 0;
        for (int i = 0; i < dataShards; i++)
        {
            payloadLengthLong += _shardBufferInfo[i].dataLen;
        }
        if (payloadLengthLong > int.MaxValue) { _shardBufferInfo.Clear(); _expectedShardCount = 0; return; }
        int payloadLength = (int)payloadLengthLong;

        // allocate payload and copy data shards in order using their dataLen
        byte[] payload = new byte[payloadLength];
        int writePos = 0;
        for (int i = 0; i < dataShards; i++)
        {
            var sin = _shardBufferInfo[i];
            Array.Copy(sin.data, 0, payload, writePos, sin.dataLen);
            writePos += sin.dataLen;
        }

        // If some data shards are missing but parity can recover, attempt simple XOR recovery:
        bool missingDataShard = false;
        for (int i = 0; i < dataShards; i++) if (_shardBufferInfo[i] == null) missingDataShard = true;
        if (missingDataShard && parityCount > 0)
        {
            // Simple XOR parity recovery supports single-missing for the naive parity scheme we use.
            // Build padded dataShards (pad each to shardSize) for XOR parity calculation.
            int shardPadSize = fecShardSize;
            byte[][] padded = new byte[totalShards][];
            for (int i = 0; i < totalShards; i++)
            {
                var sInfo = _shardBufferInfo[i];
                if (sInfo != null)
                {
                    padded[i] = new byte[shardPadSize];
                    Array.Clear(padded[i], 0, shardPadSize);
                    Array.Copy(sInfo.data, 0, padded[i], 0, sInfo.dataLen);
                }
                else
                {
                    padded[i] = new byte[shardPadSize];
                    Array.Clear(padded[i], 0, shardPadSize);
                }
            }

            // find missing index among data shards
            int missIdx = -1;
            for (int i = 0; i < dataShards; i++) if (_shardBufferInfo[i] == null) { missIdx = i; break; }
            if (missIdx >= 0)
            {
                // XOR across all shards (including parity) to recover missing padded shard
                byte[] recovered = new byte[shardPadSize];
                Array.Clear(recovered, 0, shardPadSize);
                for (int s = 0; s < totalShards; s++)
                {
                    var p = padded[s];
                    for (int b = 0; b < shardPadSize; b++) recovered[b] ^= p[b];
                }

                // determine actual data length for recovered shard: for interior shards it's shardPadSize except last data shard which may be shorter.
                int recoveredDataLen = shardPadSize;
                if (missIdx == dataShards - 1)
                {
                    // last data shard may be shorter: compute based on payloadLength and previous shards
                    int sumPrev = 0;
                    for (int i = 0; i < missIdx; i++) sumPrev += _shardBufferInfo[i].dataLen;
                    recoveredDataLen = payloadLength - sumPrev;
                    if (recoveredDataLen < 0) recoveredDataLen = 0;
                }

                byte[] recData = new byte[recoveredDataLen];
                Array.Copy(recovered, 0, recData, 0, recoveredDataLen);

                // place recovered data into payload at correct position
                int recWritePos = 0;
                for (int i = 0; i < missIdx; i++) recWritePos += _shardBufferInfo[i].dataLen;
                Array.Copy(recData, 0, payload, recWritePos, recoveredDataLen);

                // mark recovered shard as present to keep consistent cleanup
                _shardBufferInfo[missIdx] = new ShardInfo { total = (ushort)totalShards, index = (ushort)missIdx, dataLen = recoveredDataLen, data = recData };
            }
            else
            {
                // multiple missing data shards; cannot recover with single parity.
                // fallback: clear buffer and request full from server.
                _shardBufferInfo.Clear();
                _expectedShardCount = 0;
                RequestFullSnapshotFromServer();
                return;
            }
        }

        // At this point payload should be complete
        HandlePackedPayload(payload, ComputeStateHashFromPayload(payload));

        // cleanup
        _shardBufferInfo.Clear();
        _expectedShardCount = 0;
    }

    void HandlePackedPayload(byte[] payload)
    {
        // unknown stateHash case: compute hash from payload and proceed (used when no explicit stateHash sent)
        ulong h = ComputeStateHashFromPayload(payload);
        HandlePackedPayload(payload, h);
    }

    // Client: handle payload + server-provided stateHash, compute local prediction hash and request full if mismatch
    void HandlePackedPayload(byte[] payload, ulong serverStateHash)
    {
        MovementSnapshot snap;
        int cs = _chunk ? _chunk.cellSize : 128;

        if (!PackedMovement.TryUnpack(payload, cs, ref _haveAnchor, ref _anchorCellX, ref _anchorCellY, ref _baseSnap, out snap))
        {
            // Rate-limited, non-blocking CRC/unpack reporting
            ReportCrcFailureOncePerWindow("[PackedMovement] CRC mismatch or unpack failure detected");
            _telemetry?.Increment("pack.unpack_fail");
            // request full retransmit from server
            RequestFullSnapshotFromServer();
            return;
        }

        // Insert into buffer sorted
        if (_buffer.Count == 0 || snap.serverTime >= _buffer[_buffer.Count - 1].serverTime)
            _buffer.Add(snap);
        else
        {
            for (int i = 0; i < _buffer.Count; i++)
            {
                if (snap.serverTime < _buffer[i].serverTime)
                { _buffer.Insert(i, snap); break; }
            }
        }
        if (_buffer.Count > 256) _buffer.RemoveAt(0);

        // EMA delay/jitter updates
        double now = _netTime.Now();
        double delay = Math.Max(0.0, now - snap.serverTime);
        _emaDelay = (_emaDelay <= 0.0) ? delay : (1.0 - emaDelayA) * _emaDelay + emaDelayA * delay;
        double dev = Math.Abs(delay - _emaDelay);
        _emaJitter = (_emaJitter <= 0.0) ? dev : (1.0 - emaJitterA) * _emaJitter + emaJitterA * dev;

        _telemetry?.SetGauge($"client.{OwnerClientId}.buffer_size", _buffer.Count);

        double targetBack = _emaDelay * 1.35 + _emaJitter * 1.6;
        _backTarget = ClampD(targetBack, minBack, maxBack);
        if (_back <= 0.0) _back = _backTarget;

        // Client-side verification: compute stateHash from local prediction of that seq (best-effort)
        ulong clientHash = ComputeStateHashForSnapshot(snap); // best-effort deterministic hash
        if (clientHash != serverStateHash)
        {
            // telemetry event and request reliable full
            _telemetry?.Event("statehash.mismatch",
                new Dictionary<string, string> {
                    { "clientId", OwnerClientId.ToString() },
                    { "seq", snap.seq.ToString() }
                },
                new Dictionary<string, double> {
                    { "serverHash", (double)serverStateHash },
                    { "clientHash", (double)clientHash }
                });
            _telemetry?.Increment($"client.{OwnerClientId}.statehash_mismatch");

            // ask server for full snapshot immediately
            RequestFullSnapshotFromServer();
        }
    }

    // Client RPC -> ask server to send a full snapshot for this owner immediately
    [ServerRpc(RequireOwnership = false)]
    void RequestFullSnapshotServerRpc()
    {
        if (_shuttingDown || s_AppQuitting) return;
        // Server should send current authoritative full snapshot to this conn
        var conn = base.Owner;
        if (conn == null) return;

        // Create a full snapshot from current server state for this player and send
        Vector3 pos = _serverLastPos;
        Vector3 vel = Vector3.zero;
        double now = _netTime.Now();
        uint seq = _lastSeqSent;
        byte anim = 0;
        var snap = new MovementSnapshot(pos, vel, now, seq, anim);
        byte[] full = PackedMovement.PackFull(snap.pos, snap.vel, snap.animState, snap.serverTime, snap.seq, 0, 0, _chunk ? _chunk.cellSize : 128);
        ulong stateHash = ComputeStateHashForSnapshot(snap);

        if (fecParityShards > 0 && !debugForceFullSnapshots)
        {
            var shards = BuildFecShards(full, fecShardSize, fecParityShards);
            _lastFullShards[conn] = shards;
            foreach (var s in shards) TargetPackedShardTo(conn, s);
        }
        else
        {
            _lastFullPayload[conn] = full;
            _lastFullSentAt[conn] = _netTime.Now();
            _fullRetryCount[conn] = 0;
            TargetPackedSnapshotTo(conn, full, stateHash);
        }
        _telemetry?.Increment($"client.{OwnerClientId}.full_requested_by_client");
    }

    void RequestFullSnapshotFromServer()
    {
        // client calls server RPC to request full
        RequestFullSnapshotServerRpc();
    }

    // ------- helpers: hashing & FEC -------

    // Compute a compact 64-bit hash for a snapshot (pos.x,pos.z,vel.x,vel.z,seq,serverTime)
    static ulong ComputeStateHashForSnapshot(MovementSnapshot s)
    {
        // deterministic layout
        Span<byte> buf = stackalloc byte[8 * 6];
        BitConverter.TryWriteBytes(buf.Slice(0, 8), BitConverter.DoubleToInt64Bits(s.serverTime));
        BitConverter.TryWriteBytes(buf.Slice(8, 8), BitConverter.DoubleToInt64Bits(s.pos.x));
        BitConverter.TryWriteBytes(buf.Slice(16, 8), BitConverter.DoubleToInt64Bits(s.pos.z));
        BitConverter.TryWriteBytes(buf.Slice(24, 8), BitConverter.DoubleToInt64Bits(s.vel.x));
        BitConverter.TryWriteBytes(buf.Slice(32, 8), BitConverter.DoubleToInt64Bits(s.vel.z));
        BitConverter.TryWriteBytes(buf.Slice(40, 8), (long)s.seq);

        // use SHA256 and take first 8 bytes as ulong
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(buf.ToArray());
            return BitConverter.ToUInt64(hash, 0);
        }
    }

    // Helper when only payload present (best-effort)
    static ulong ComputeStateHashFromPayload(byte[] payload)
    {
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(payload ?? Array.Empty<byte>());
            return BitConverter.ToUInt64(hash, 0);
        }
    }

    // Build simple XOR-based FEC shards with explicit dataLen headers: split payload into N blocks of size shardSize and add parity shards
    List<byte[]> BuildFecShards(byte[] payload, int shardSize, int parityCount)
    {
        var shards = new List<byte[]>();
        if (payload == null || payload.Length == 0) return shards;

        int dataShards = (payload.Length + shardSize - 1) / shardSize;
        int totalShards = dataShards + parityCount;

        // create data shards (last shard may be short)
        for (int i = 0; i < dataShards; i++)
        {
            int start = i * shardSize;
            int len = Math.Min(shardSize, payload.Length - start);
            // header: total (ushort) | index (ushort) | dataLen (uint)
            byte[] s = new byte[2 + 2 + 4 + len];
            Array.Copy(BitConverter.GetBytes((ushort)totalShards), 0, s, 0, 2);
            Array.Copy(BitConverter.GetBytes((ushort)i), 0, s, 2, 2);
            Array.Copy(BitConverter.GetBytes((uint)len), 0, s, 4, 4);
            Array.Copy(payload, start, s, 8, len);
            shards.Add(s);
        }

        // create parity shards by XORing padded data shards (pad to shardSize for parity calculation)
        for (int p = 0; p < parityCount; p++)
        {
            int maxLen = shardSize;
            byte[] parityPayload = new byte[maxLen];
            Array.Clear(parityPayload, 0, maxLen);
            for (int i = 0; i < dataShards; i++)
            {
                int dataStart = 8; // in shards[i] actual data offset
                int dsLen = shards[i].Length - 8;
                for (int b = 0; b < maxLen; b++)
                {
                    byte vb = 0;
                    if (b < dsLen) vb = shards[i][8 + b];
                    parityPayload[b] ^= vb;
                }
            }
            // header: total | index | dataLen (we store maxLen for parity shard)
            byte[] parityShard = new byte[2 + 2 + 4 + maxLen];
            Array.Copy(BitConverter.GetBytes((ushort)totalShards), 0, parityShard, 0, 2);
            Array.Copy(BitConverter.GetBytes((ushort)(dataShards + p)), 0, parityShard, 2, 2);
            Array.Copy(BitConverter.GetBytes((uint)maxLen), 0, parityShard, 4, 4);
            Array.Copy(parityPayload, 0, parityShard, 8, maxLen);
            shards.Add(parityShard);
        }

        return shards;
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

    // Wrapper helper per inviare la TargetRpc al client owner in modo sicuro
    void SendTargetOwnerCorrection(uint serverSeq, Vector3 serverPos)
    {
        try
        {
            TargetOwnerCorrection(Owner, serverSeq, serverPos);
        }
        catch (Exception)
        {
            // ignore if Owner not available in this context
        }
    }

    [TargetRpc]
    void TargetOwnerCorrection(NetworkConnection conn, uint serverSeq, Vector3 serverPos)
    {
        if (_shuttingDown || s_AppQuitting) return;

        // cooldown check to avoid oscillation
        double now = _netTime.Now();
        if (now - _lastReconcileSentTime < RECONCILE_COOLDOWN_SEC)
        {
            _telemetry?.Increment("reconcile.cooldown_skipped");
            return;
        }

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

        if (ignoreNetworkY)
            corrected.y = transform.position.y;

        // Telemetry: reconcile request (rich event)
        var rTags = new Dictionary<string, string> {
            { "clientId", OwnerClientId.ToString() },
            { "reason", "server_correction" }
        };
        var rMetrics = new Dictionary<string, double> {
            { "errXZ_cm", errXZ * 100.0 },
            { "rtt_ms", _lastRttMs }
        };
        int sc = 0; var sample = new List<string>();
        foreach (var inp in _inputBuf) { if (sc++ >= 6) break; sample.Add($"{inp.seq}:{inp.dir.x:0.00},{inp.dir.z:0.00}"); }
        if (sample.Count > 0) rTags["input_sample"] = string.Join("|", sample);
        _telemetry?.Event("reconcile.requested", rTags, rMetrics);

        // set reconcile target and mark cooldown used
        _reconcileTarget = corrected;
        _reconcileActive = true;
        _lastReconcileSentTime = now;

        _telemetry?.Increment($"client.{OwnerClientId}.reconcile.requested_smooth");

        // Start elastic correction on client (if owner) instead of immediate hard-snap
        if (IsOwner)
        {
            StartElasticCorrection(corrected);
        }
    }

    // ------- elastic helper -------
    void StartElasticCorrection(Vector3 target)
    {
        // Only start if noticeable
        float dist = Vector3.Distance(_rb.position, target);
        if (dist < correctionMinVisible) return;

        _isApplyingElastic = true;
        _elasticStartPos = _rb.position;
        _elasticTargetPos = target;
        _elasticElapsed = 0f;
        _elasticDuration = Mathf.Max(0.05f, correctionDurationSeconds); // clamp minimal duration
        _elasticCurrentMultiplier = correctionInitialMultiplier;

        // telemetry event
        _telemetry?.Event("elastic.start",
            new Dictionary<string, string> {
                { "clientId", OwnerClientId.ToString() },
                { "startPos", $"{_elasticStartPos.x:0.00},{_elasticStartPos.y:0.00},{_elasticStartPos.z:0.00}" },
                { "targetPos", $"{_elasticTargetPos.x:0.00},{_elasticTargetPos.y:0.00},{_elasticTargetPos.z:0.00}" }
            },
            new Dictionary<string, double> {
                { "dist_cm", dist * 100.0 },
                { "duration_s", _elasticDuration }
            });

        _telemetry?.Increment($"client.{OwnerClientId}.elastic_started");
    }

    // ------- CRC reporting helper (rate-limited, non-blocking) -------
    private void ReportCrcFailureOncePerWindow(string msg)
    {
        double now = Time.realtimeSinceStartup;
        if (_crcFirstFailTime < 0.0) _crcFirstFailTime = now;

        _crcFailCount++;

        // if we are outside the accumulation window, emit summary and reset
        if (now - _crcFirstFailTime >= CRC_LOG_WINDOW_SECONDS)
        {
            int toLog = Math.Min(_crcFailCount, CRC_LOG_MAX_PER_WINDOW);
            Debug.LogWarning($"{msg} — occurrences in last {CRC_LOG_WINDOW_SECONDS:0.#}s: {_crcFailCount}. Emitting {toLog} sample(s).");
            // emit up to toLog sample warnings (non-blocking)
            for (int i = 0; i < toLog; ++i)
                Debug.LogWarning($"{msg} [sample {i + 1}/{toLog}]");

            // telemetry metric
            _telemetry?.Observe("client.crc_fail_count", _crcFailCount);

            // reset window
            _crcFailCount = 0;
            _crcFirstFailTime = -1.0;
            _crcLastLogTime = now;
            return;
        }

        // If not yet window end, but we've exceeded a local burst threshold, produce one immediate log and continue counting
        if (_crcFailCount == CRC_LOG_MAX_PER_WINDOW && (now - _crcLastLogTime) > 0.5)
        {
            Debug.LogWarning($"{msg} — repeated (count={_crcFailCount})");
            _telemetry?.Observe("client.crc_fail_burst", _crcFailCount);
            _crcLastLogTime = now;
        }

        // otherwise do not log anything now (silent accumulation)
    }

    // ------- PUBLIC WRAPPERS (for external hooks/helpers) -------
    // expose minimal public wrappers so external helpers (Canary, Hooks) can call into driver without accessing privates

    // Public wrapper to allow external callers to forward raw shard bytes
    public void HandlePackedShardPublic(byte[] shard)
    {
        HandlePackedShard(shard);
    }

    // Public wrapper to send a packed snapshot envelope to a specific client connection
    public void SendPackedSnapshotToClient(NetworkConnection conn, byte[] packedEnvelope, ulong stateHash)
    {
        TargetPackedSnapshotTo(conn, packedEnvelope, stateHash);
    }

    // Public wrapper to send a packed shard envelope to a specific client connection
    public void SendPackedShardToClient(NetworkConnection conn, byte[] packedShard)
    {
        TargetPackedShardTo(conn, packedShard);
    }
}

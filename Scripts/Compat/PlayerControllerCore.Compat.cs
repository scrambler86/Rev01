// BOOKMARK: FILE = PlayerControllerCore.Compat.cs
using UnityEngine;
using Game.Network.Core;

/// <summary>
/// COMPAT WRAPPER (temporaneo) per mantenere il tipo 'PlayerControllerCore'
/// così che i sistemi legacy (ChatManager, AntiCheatManager, ecc.) continuino
/// a compilare. Non muove il player: forwarda verso PlayerNetworkDriver quando possibile.
/// Obiettivo: sbloccare la build. Poi migri i riferimenti al Driver e rimuovi questo file.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("")] // non mostrarlo nei menu
public class PlayerControllerCore : MonoBehaviour
{
    [Header("Visual (opzionale)")]
    public Transform visualRoot;

    // Flag legacy
    [SerializeField] private bool _allowInput = true;
    [SerializeField] private bool _canMove = true;

    // Bridge verso il sistema nuovo
    private PlayerNetworkDriver _driver;
    private ClickToMoveAgent _ctm;

    void Awake()
    {
        TryGetComponent(out _driver);
        TryGetComponent(out _ctm);
        if (!visualRoot) visualRoot = transform;
    }

    // -------- Legacy API usate da overlay/chat/anticheat --------

    /// <summary>Velocità planare (diagnostica).</summary>
    public float DebugPlanarSpeed => _driver ? _driver.DebugPlanarSpeed : 0f;

    /// <summary>Ultima direzione di movimento (diagnostica).</summary>
    public Vector3 DebugLastMoveDir => _driver ? _driver.DebugLastMoveDir : Vector3.zero;

    /// <summary>Stato di corsa (diagnostica).</summary>
    public bool IsRunning => _driver ? _driver.DebugIsRunning : false;

    /// <summary>Input permesso (legacy flag).</summary>
    public bool AllowInput
    {
        get => _driver ? _driver.DebugAllowInput : _allowInput;
        set => _allowInput = value;
    }

    /// <summary>Compat: abilita/disabilita input.</summary>
    public void SetAllowInput(bool allow) => AllowInput = allow;

    /// <summary>Compat: abilita/disabilita movimento.</summary>
    public void SetCanMove(bool can) => _canMove = can;

    public bool CanMove => _canMove;

    // -------- Metodi/campi richiesti dai tuoi script legacy --------

    /// <summary>
    /// Legacy: arresta il movimento. Qui annulliamo path del CTM, azzeriamo la velocità
    /// e rimuoviamo eventuale input esterno sul Driver.
    /// </summary>
    public void StopMovement()
    {
        // Cancella eventuale path CTM
        if (_ctm != null) _ctm.CancelPath();

        // Rimuove input esterno al driver
        if (_driver != null)
        {
            _driver.ClearExternalMove();
            if (_driver.TargetRb) _driver.TargetRb.velocity = Vector3.zero;
        }
        else
        {
            // fallback: azzera rigidbody locale se presente
            if (TryGetComponent<Rigidbody>(out var rb))
                rb.velocity = Vector3.zero;
        }
    }

    /// <summary>
    /// Legacy: velocità base (prima era 'speed').
    /// Mappata su WalkSpeed del Driver, con fallback sensato.
    /// </summary>
    public float speed
        => _driver ? _driver.WalkSpeed : 3.5f;

    /// <summary>
    /// Legacy: moltiplicatore di corsa (prima era 'runMultiplier').
    /// Calcolato da RunSpeed/WalkSpeed quando possibile.
    /// </summary>
    public float runMultiplier
    {
        get
        {
            if (_driver && _driver.WalkSpeed > 0f)
                return _driver.RunSpeed / _driver.WalkSpeed;
            return 2f; // fallback
        }
    }
}

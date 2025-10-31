// IPlayerNetworkDriver.cs
// REVIVO-NET-BASELINE — Interfaccia comune per i sistemi che parlano col driver
// BOOKMARK: [FILE IPlayerNetworkDriver]

public interface IPlayerNetworkDriver
{
    INetTime NetTime { get; }
    int OwnerClientId { get; }
    uint LastSeqReceived { get; }
    void SetLastSeqReceived(uint seq);

    // RTT stimato per questo client (ms), lato server o stimato lato client
    double ClientRttMs { get; }
}

public interface IPlayerNetworkDriver
{
    INetTime NetTime { get; }
    int OwnerClientId { get; }
    uint LastSeqReceived { get; }
    void SetLastSeqReceived(uint seq);

    // 👉 nuovo: RTT misurato per questo client (ms), lato server
    double ClientRttMs { get; }
}

using UnityEngine;

public interface IPlayerNetworkDriver
{
    int OwnerClientId { get; }   // rinominato per non oscurare NetworkBehaviour.OwnerId
    uint LastSeqReceived { get; }
    INetTime NetTime { get; }

    void SetLastSeqReceived(uint seq);
    Transform transform { get; }
}

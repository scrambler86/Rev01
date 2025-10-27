using UnityEngine;

public struct MovementSnapshot
{
    public Vector3 pos;
    public Vector3 vel;
    public double serverTime;
    public uint seq;
    public byte animState; // 0=idle,1=walk,2=run
    public MovementSnapshot(Vector3 pos, Vector3 vel, double serverTime, uint seq, byte animState = 0)
    { this.pos=pos; this.vel=vel; this.serverTime=serverTime; this.seq=seq; this.animState=animState; }
}

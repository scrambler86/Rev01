using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Packer FULL/DELTA per snapshot di movimento con anchor di cella.
/// FULL: cellX,cellY + pos/vel quantizzate, anim, time, seq.
/// DELTA: differenze piccole (cm) rispetto alla base, anim opzionale, dt ms, dseq.
/// </summary>
public static class PackedMovement
{
    [Flags]
    public enum Flags : byte
    {
        None = 0,
        IsDelta = 1 << 0,
        HasPos = 1 << 1,
        HasVel = 1 << 2,
        HasAnim = 1 << 3,
        HasDtMs = 1 << 4
    }

    const float POS_SCALE = 100f; // 1 cm
    const float VEL_SCALE = 100f; // 1 cm/s

    static (short cx, short cy, short cz) QPos(Vector3 w, Vector3 origin)
    {
        float lx = (w.x - origin.x) * POS_SCALE;
        float ly = w.y * POS_SCALE;
        float lz = (w.z - origin.z) * POS_SCALE;
        return ((short)Mathf.Clamp(Mathf.RoundToInt(lx), short.MinValue, short.MaxValue),
                (short)Mathf.Clamp(Mathf.RoundToInt(ly), short.MinValue, short.MaxValue),
                (short)Mathf.Clamp(Mathf.RoundToInt(lz), short.MinValue, short.MaxValue));
    }
    static Vector3 DPos(short cx, short cy, short cz, Vector3 origin)
        => new(origin.x + cx / POS_SCALE, cy / POS_SCALE, origin.z + cz / POS_SCALE);

    static (short vx, short vy, short vz) QVel(Vector3 v)
    {
        float vx = v.x * VEL_SCALE, vy = v.y * VEL_SCALE, vz = v.z * VEL_SCALE;
        return ((short)Mathf.Clamp(Mathf.RoundToInt(vx), short.MinValue, short.MaxValue),
                (short)Mathf.Clamp(Mathf.RoundToInt(vy), short.MinValue, short.MaxValue),
                (short)Mathf.Clamp(Mathf.RoundToInt(vz), short.MinValue, short.MaxValue));
    }
    static Vector3 DVel(short vx, short vy, short vz)
        => new(vx / VEL_SCALE, vy / VEL_SCALE, vz / VEL_SCALE);

    public static Vector3 CellOrigin(int cellSize, short cellX, short cellY)
        => new(cellX * cellSize, 0f, cellY * cellSize);

    // -------- FULL --------
    public static byte[] PackFull(Vector3 pos, Vector3 vel, byte anim, double serverTime, uint seq,
                                  short cellX, short cellY, int cellSize)
    {
        using var ms = new MemoryStream(64);
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)Flags.None);
        bw.Write(cellX); bw.Write(cellY);

        var origin = CellOrigin(cellSize, cellX, cellY);
        var (px, py, pz) = QPos(pos, origin);
        var (vx, vy, vz) = QVel(vel);
        bw.Write(px); bw.Write(py); bw.Write(pz);
        bw.Write(vx); bw.Write(vy); bw.Write(vz);

        bw.Write(anim);
        uint msTime = (uint)Mathf.Clamp(Mathf.RoundToInt((float)(serverTime * 1000.0)), 0, int.MaxValue);
        bw.Write(msTime);
        bw.Write(seq);
        return ms.ToArray();
    }

    // -------- DELTA --------
    public static byte[] PackDelta(in MovementSnapshot last,
                                   Vector3 pos, Vector3 vel, byte anim, double serverTime, uint seq,
                                   short cellX, short cellY, int cellSize,
                                   int maxPosDeltaCm, int maxVelDeltaCms, int maxDtMs)
    {
        using var ms = new MemoryStream(48);
        using var bw = new BinaryWriter(ms);

        var origin = CellOrigin(cellSize, cellX, cellY);
        var (px, py, pz) = QPos(pos, origin);
        var (lpx, lpy, lpz) = QPos(last.pos, origin);

        int dpx = px - lpx, dpy = py - lpy, dpz = pz - lpz;
        if (Mathf.Abs(dpx) > maxPosDeltaCm || Mathf.Abs(dpy) > maxPosDeltaCm || Mathf.Abs(dpz) > maxPosDeltaCm)
            return null;

        var (vx, vy, vz) = QVel(vel);
        var (lvx, lvy, lvz) = QVel(last.vel);
        int dvx = vx - lvx, dvy = vy - lvy, dvz = vz - lvz;
        if (Mathf.Abs(dvx) > maxVelDeltaCms || Mathf.Abs(dvy) > maxVelDeltaCms || Mathf.Abs(dvz) > maxVelDeltaCms)
            return null;

        int dtMs = Mathf.RoundToInt((float)((serverTime - last.serverTime) * 1000.0));
        if (Mathf.Abs(dtMs) > maxDtMs) return null;

        var f = Flags.IsDelta | Flags.HasPos | Flags.HasVel | Flags.HasDtMs;
        if (anim != last.animState) f |= Flags.HasAnim;

        bw.Write((byte)f);
        bw.Write((short)dpx); bw.Write((short)dpy); bw.Write((short)dpz);
        bw.Write((short)dvx); bw.Write((short)dvy); bw.Write((short)dvz);
        if ((f & Flags.HasAnim) != 0) bw.Write(anim);
        bw.Write((short)dtMs);
        bw.Write((ushort)(seq - last.seq));
        return ms.ToArray();
    }

    // -------- UNPACK --------
    public static bool TryUnpack(byte[] data, int cellSize,
                                 ref bool haveAnchor, ref short cellX, ref short cellY,
                                 ref MovementSnapshot baseSnap, out MovementSnapshot snap)
    {
        snap = default;
        try
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            var f = (Flags)br.ReadByte();

            if ((f & Flags.IsDelta) == 0)
            {
                cellX = br.ReadInt16(); cellY = br.ReadInt16();
                var origin = CellOrigin(cellSize, cellX, cellY);
                short px = br.ReadInt16(), py = br.ReadInt16(), pz = br.ReadInt16();
                short vx = br.ReadInt16(), vy = br.ReadInt16(), vz = br.ReadInt16();
                byte anim = br.ReadByte();
                uint msTime = br.ReadUInt32();
                uint seq = br.ReadUInt32();

                var pos = DPos(px, py, pz, origin);
                var vel = DVel(vx, vy, vz);
                double st = msTime / 1000.0;

                snap = new MovementSnapshot(pos, vel, st, seq, anim);
                haveAnchor = true;
                baseSnap = snap;
                return true;
            }
            else
            {
                if (!haveAnchor) return false;

                var origin = CellOrigin(cellSize, cellX, cellY);

                int dpx = 0, dpy = 0, dpz = 0;
                int dvx = 0, dvy = 0, dvz = 0;
                byte anim = baseSnap.animState;
                int dtMs = 0;

                if ((f & Flags.HasPos) != 0) { dpx = br.ReadInt16(); dpy = br.ReadInt16(); dpz = br.ReadInt16(); }
                if ((f & Flags.HasVel) != 0) { dvx = br.ReadInt16(); dvy = br.ReadInt16(); dvz = br.ReadInt16(); }
                if ((f & Flags.HasAnim) != 0) anim = br.ReadByte();
                if ((f & Flags.HasDtMs) != 0) dtMs = br.ReadInt16();
                ushort dseq = br.ReadUInt16();

                var (lpx, lpy, lpz) = QPos(baseSnap.pos, origin);
                var (lvx, lvy, lvz) = QVel(baseSnap.vel);
                short px = (short)(lpx + dpx), py = (short)(lpy + dpy), pz = (short)(lpz + dpz);
                short vx = (short)(lvx + dvx), vy = (short)(lvy + dvy), vz = (short)(lvz + dvz);

                var pos = DPos(px, py, pz, origin);
                var vel = DVel(vx, vy, vz);
                double st = baseSnap.serverTime + dtMs / 1000.0;
                uint seq = baseSnap.seq + dseq;

                snap = new MovementSnapshot(pos, vel, st, seq, anim);
                baseSnap = snap;
                return true;
            }
        }
        catch { return false; }
    }
}

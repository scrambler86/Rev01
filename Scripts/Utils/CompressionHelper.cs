using UnityEngine;
using System.IO;

public static class CompressionHelper
{
    private const float SCALE = 1000f; // 1mm precision

    public static byte[] CompressSnapshot(Vector3 pos, Vector3 vel, byte animState, uint seq, double time)
    {
        using (var ms = new MemoryStream(32))
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write((short)Mathf.Clamp(pos.x * SCALE, -32768, 32767));
            bw.Write((short)Mathf.Clamp(pos.y * SCALE, -32768, 32767));
            bw.Write((short)Mathf.Clamp(pos.z * SCALE, -32768, 32767));

            bw.Write((short)Mathf.Clamp(vel.x * SCALE, -32768, 32767));
            bw.Write((short)Mathf.Clamp(vel.y * SCALE, -32768, 32767));
            bw.Write((short)Mathf.Clamp(vel.z * SCALE, -32768, 32767));

            bw.Write(animState);
            bw.Write(seq);
            bw.Write(time);

            return ms.ToArray();
        }
    }

    public static (Vector3 pos, Vector3 vel, byte animState, uint seq, double time) DecompressSnapshot(byte[] data)
    {
        using (var ms = new MemoryStream(data))
        using (var br = new BinaryReader(ms))
        {
            Vector3 pos = new Vector3(
                br.ReadInt16() / SCALE,
                br.ReadInt16() / SCALE,
                br.ReadInt16() / SCALE
            );

            Vector3 vel = new Vector3(
                br.ReadInt16() / SCALE,
                br.ReadInt16() / SCALE,
                br.ReadInt16() / SCALE
            );

            byte animState = br.ReadByte();
            uint seq = br.ReadUInt32();
            double time = br.ReadDouble();

            return (pos, vel, animState, seq, time);
        }
    }
}
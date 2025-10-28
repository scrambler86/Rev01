using System;
using System.Security.Cryptography;
using System.Text;

public struct Envelope
{
    public uint messageId;
    public uint seq;
    public int payloadLen;
    public ulong payloadHash;
    public byte flags; // bitflags: 0x1 = full, 0x2 = delta, 0x4 = shard, 0x8 = canary

    public const int HeaderSize = 4 + 4 + 4 + 8 + 1; // 21 bytes
}

public static class EnvelopeUtil
{
    // Pack envelope (little-endian) prefixed to payload
    public static byte[] Pack(Envelope env, byte[] payload)
    {
        int tot = Envelope.HeaderSize + (payload?.Length ?? 0);
        var outb = new byte[tot];
        int off = 0;
        Array.Copy(BitConverter.GetBytes(env.messageId), 0, outb, off, 4); off += 4;
        Array.Copy(BitConverter.GetBytes(env.seq), 0, outb, off, 4); off += 4;
        Array.Copy(BitConverter.GetBytes(env.payloadLen), 0, outb, off, 4); off += 4;
        Array.Copy(BitConverter.GetBytes(env.payloadHash), 0, outb, off, 8); off += 8;
        outb[off++] = env.flags;
        if (payload != null && payload.Length > 0) Array.Copy(payload, 0, outb, off, payload.Length);
        return outb;
    }

    // Try unpack: returns false if buffer shorter than header or length mismatch
    public static bool TryUnpack(byte[] raw, out Envelope env, out byte[] payload)
    {
        env = default;
        payload = null;
        if (raw == null || raw.Length < Envelope.HeaderSize) return false;
        int off = 0;
        env.messageId = BitConverter.ToUInt32(raw, off); off += 4;
        env.seq = BitConverter.ToUInt32(raw, off); off += 4;
        env.payloadLen = BitConverter.ToInt32(raw, off); off += 4;
        env.payloadHash = BitConverter.ToUInt64(raw, off); off += 8;
        env.flags = raw[off++];

        if (raw.Length < Envelope.HeaderSize + env.payloadLen) return false;
        if (env.payloadLen > 0)
        {
            payload = new byte[env.payloadLen];
            Array.Copy(raw, Envelope.HeaderSize, payload, 0, env.payloadLen);
        }
        else payload = Array.Empty<byte>();

        // Verify hash lazily — caller chooses to validate
        return true;
    }

    // Compute 64-bit payload hash: prefer xxHash64 if available; fallback to SHA256-trunc
    public static ulong ComputeHash64(byte[] data)
    {
        if (data == null) return 0UL;
        // Fallback SHA256-trunc
        using (var sha = SHA256.Create())
        {
            var h = sha.ComputeHash(data);
            return BitConverter.ToUInt64(h, 0);
        }
    }
}

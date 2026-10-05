using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Gemx;

[StructLayout(LayoutKind.Explicit, Size = 128)]

public struct PaddedLong
{
    [FieldOffset(64)] public long V;
}

public sealed class SpscRing<T> where T : struct
{
    readonly T[] _b;
    readonly long _m;
    PaddedLong _head, _tail;

    public SpscRing(int pow2)
    {
        if (pow2 < 2 || (pow2 & (pow2 - 1)) != 0) throw new ArgumentException("pow2");
        _b = new T[pow2];
        _m = pow2 - 1;
    }

    public bool TryWrite(in T x)
    {
        long t = _tail.V;
        if (t - Volatile.Read(ref _head.V) > _m) return false;
        _b[t & _m] = x;
        Volatile.Write(ref _tail.V, t + 1);
        return true;
    }

    public bool TryRead(out T x)
    {
        long h = _head.V;
        if (h == Volatile.Read(ref _tail.V)) { x = default; return false; }
        x = _b[h & _m];

        Volatile.Write(ref _head.V, h + 1);
        return true;
    }
}

public sealed class FrameRing
{
    const int Hdr = 16;
    readonly byte[] _b;
    readonly int _cap;
    readonly long _m;
    PaddedLong _head, _tail;
    int _adv;

    public FrameRing(int pow2)
    {
        if (pow2 < 64 || (pow2 & (pow2 - 1)) != 0) throw new ArgumentException("pow2");
        _b = new byte[pow2];
        _cap = pow2;
        _m = pow2 - 1;
    }

    static int Rec(int len) => Hdr + ((len + 7) & ~7);

    public bool TryWrite(ReadOnlySpan<byte> p, long recvNs)
    {
        int need = Rec(p.Length);
        if (need > _cap / 2) return false;
        long t = _tail.V, h = Volatile.Read(ref _head.V);
        int pos = (int)(t & _m);
        int skip = pos + need > _cap ? _cap - pos : 0;
        if (t - h + skip + need > _cap) return false;

        if (skip != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_b.AsSpan(pos), -1);
            t += skip;
            pos = 0;
        }
        BinaryPrimitives.WriteInt32LittleEndian(_b.AsSpan(pos), p.Length);
        BinaryPrimitives.WriteInt64LittleEndian(_b.AsSpan(pos + 8), recvNs);
        p.CopyTo(_b.AsSpan(pos + Hdr));
        Volatile.Write(ref _tail.V, t + need);
        return true;
    }

    public bool TryPeek(out ReadOnlySpan<byte> p, out long recvNs)
    {
        long h = _head.V;
        while (true)
        {
            if (h == Volatile.Read(ref _tail.V)) { p = default; recvNs = 0; return false; }
            int pos = (int)(h & _m);
            int len = BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(pos));
            if (len < 0)
            {
                h += _cap - pos;
                Volatile.Write(ref _head.V, h);
                continue;
            }
            recvNs = BinaryPrimitives.ReadInt64LittleEndian(_b.AsSpan(pos + 8));
            p = _b.AsSpan(pos + Hdr, len);
            _adv = Rec(len);
            return true;
        }

    }

    public void Release() => Volatile.Write(ref _head.V, _head.V + _adv);
}
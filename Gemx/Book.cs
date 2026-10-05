using System.Runtime.CompilerServices;

namespace Gemx;

public sealed class BookSide
{
    readonly bool _bid;
    readonly long[] _k, _q;
    int _n;
    public bool Truncated;

    public BookSide(bool bid, int cap)
    {
        _bid = bid;
        _k = new long[cap];
        _q = new long[cap];
    }

    public int Count => _n;

    public void Clear() { _n = 0; Truncated = false; }

    int Find(long k)
    {
        int lo = 0, hi = _n - 1;
        while (lo <= hi)
        {

            int mid = (lo + hi) >>> 1;
            long v = _k[mid];
            if (v < k) lo = mid + 1; else if (v > k) hi = mid - 1; else return mid;
        }
        return ~lo;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Set(long px, long q)
    {
        long k = _bid ? px : -px;
        int i = Find(k);
        if (i >= 0)
        {
            if (q == 0)
            {
                Array.Copy(_k, i + 1, _k, i, _n - i - 1);
                Array.Copy(_q, i + 1, _q, i, _n - i - 1);
                _n--;
            }
            else _q[i] = q;
            return;
        }
        if (q == 0) return;
        i = ~i;
        if (_n == _k.Length)
        {
            Truncated = true;
            if (i == 0) return;
            Array.Copy(_k, 1, _k, 0, i - 1);
            Array.Copy(_q, 1, _q, 0, i - 1);
            i--;

        }
        else
        {
            Array.Copy(_k, i, _k, i + 1, _n - i);
            Array.Copy(_q, i, _q, i + 1, _n - i);
            _n++;
        }
        _k[i] = k;
        _q[i] = q;
    }

    public bool Level(int fromBest, out long px, out long q)
    {
        if (fromBest < 0 || fromBest >= _n) { px = 0; q = 0; return false; }
        int j = _n - 1 - fromBest;
        px = _bid ? _k[j] : -_k[j];
        q = _q[j];
        return true;
    }
}

public enum Applied : byte { Ok, Stale, Gap }

public sealed class L2Book
{
    public readonly BookSide Bids, Asks;
    public long LastId;
    public bool Synced;

    public L2Book(int cap = 4096)
    {
        Bids = new BookSide(true, cap);

        Asks = new BookSide(false, cap);
    }

    public void Reset() { Bids.Clear(); Asks.Clear(); Synced = false; LastId = 0; }

    public Applied Apply(long first, long last, ReadOnlySpan<long> bp, ReadOnlySpan<long> bq, ReadOnlySpan<long> ap, ReadOnlySpan<long> aq)
    {
        if (!Synced) { Bids.Clear(); Asks.Clear(); }
        else
        {
            if (last <= LastId) return Applied.Stale;
            if (first > LastId + 1) return Applied.Gap;
        }
        for (int i = 0; i < bp.Length; i++) Bids.Set(bp[i], bq[i]);
        for (int i = 0; i < ap.Length; i++) Asks.Set(ap[i], aq[i]);
        LastId = last;
        Synced = true;
        return Applied.Ok;
    }

    public bool Crossed => Bids.Level(0, out long b, out _) && Asks.Level(0, out long a, out _) && b >= a;
}
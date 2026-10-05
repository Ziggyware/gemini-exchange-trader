namespace Gemx;

public static class Fixed8
{
    public const long Scale = 100_000_000L;
    const long MaxInt = 92_233_720_367L;

    public static bool TryParse(ReadOnlySpan<byte> s, out long v)
    {
        v = 0;
        int n = s.Length, i = 0;
        if (n == 0) return false;
        bool neg = s[0] == (byte)'-';
        if (neg) i = 1;
        long ip = 0;
        int id = 0;

        for (; i < n && s[i] != (byte)'.'; i++)
        {
            uint d = (uint)(s[i] - (byte)'0');
            if (d > 9) return false;
            ip = ip * 10 + d;
            if (ip > MaxInt) return false;
            id++;
        }
        long fp = 0;
        int fd = 0;
        if (i < n)
        {
            for (i++; i < n; i++)
            {
                uint d = (uint)(s[i] - (byte)'0');
                if (d > 9) return false;
                if (fd < 8) { fp = fp * 10 + d; fd++; }
                else if (d != 0) return false;
            }
        }
        if (id == 0 && fd == 0) return false;
        for (; fd < 8; fd++) fp *= 10;
        v = ip * Scale + fp;
        if (neg) v = -v;
        return true;
    }

    public static int Format(long v, Span<byte> dst)
    {
        int p = 0;
        ulong u;
        if (v < 0) { dst[p++] = (byte)'-'; u = (ulong)(-v); } else u = (ulong)v;

        ulong ip = u / (ulong)Scale, fp = u % (ulong)Scale;
        Span<byte> tmp = stackalloc byte[20];
        int t = 0;
        do { tmp[t++] = (byte)('0' + (int)(ip % 10)); ip /= 10; } while (ip != 0);
        while (t > 0) dst[p++] = tmp[--t];
        if (fp != 0)
        {
            dst[p++] = (byte)'.';
            int digits = 8;
            while (fp % 10 == 0) { fp /= 10; digits--; }
            Span<byte> f = stackalloc byte[8];
            for (int k = digits - 1; k >= 0; k--) { f[k] = (byte)('0' + (int)(fp % 10)); fp /= 10; }
            for (int k = 0; k < digits; k++) dst[p++] = f[k];
        }
        return p;
    }

    public static long Increment(double x)
    {
        if (x >= 1) return (long)Math.Round(Math.Pow(10, 8 - x));
        long v = (long)Math.Round(x * Scale);
        return v < 1 ? 1 : v;
    }
}

namespace Gemx;

public struct LagGauge
{
    long _base, _last;
    bool _init;

    public long Observe(long recvNs, long exchNs)

    {
        long d = recvNs - exchNs;
        if (!_init) { _init = true; _base = d; _last = recvNs; return 0; }
        long dt = recvNs - _last;
        if (dt < 0) dt = 0;
        _last = recvNs;
        long b = _base + dt / 1_000_000;
        _base = d < b ? d : b;
        return d - _base;
    }

    public void Reset() => this = default;
}

public sealed class Signals
{
    public double Mid, Micro, Sigma2, Ofi, AvgSize;
    public int Samples;
    readonly double _gridSec, _lambda, _ofiTau;
    readonly long _stepNs;
    double _pb, _pbq, _pa, _paq, _gridMid;
    long _lastNs, _gridNs;
    bool _init;

    public Signals(double gridSec = 0.1, double volHalfLifeSec = 30, double ofiTauSec = 1.0)
    {
        _gridSec = gridSec;
        _stepNs = (long)(gridSec * 1e9);
        _lambda = 1 - Math.Pow(2, -gridSec / volHalfLifeSec);
        _ofiTau = ofiTauSec;
    }


    public double OfiNorm => AvgSize > 0 ? Ofi / AvgSize : 0;

    public void OnTicker(long ns, double bid, double bq, double ask, double aq)
    {
        double mid = 0.5 * (bid + ask);
        double tot = bq + aq;
        Micro = tot > 0 ? (bid * aq + ask * bq) / tot : mid;
        if (!_init)
        {
            _init = true;
            _gridNs = ns;
            _gridMid = mid;
            AvgSize = 0.5 * tot;
        }
        else
        {
            double dt = (ns - _lastNs) * 1e-9;
            if (dt < 0) dt = 0;
            double decay = Math.Exp(-dt / _ofiTau);
            double e = (bid >= _pb ? bq : 0) - (bid <= _pb ? _pbq : 0) - (ask <= _pa ? aq : 0) + (ask >= _pa ? _paq : 0);
            Ofi = Ofi * decay + e;
            AvgSize += (1 - decay) * (0.5 * tot - AvgSize);
            long k = (ns - _gridNs) / _stepNs;
            if (k >= 1)
            {
                double ret = mid - _gridMid;
                double s2 = Sigma2 * Math.Pow(1 - _lambda, k - 1);
                Sigma2 = (1 - _lambda) * s2 + _lambda * ret * ret / _gridSec;
                _gridNs += k * _stepNs;
                _gridMid = mid;
                Samples = (int)Math.Min(int.MaxValue, Samples + k);
            }

        }
        Mid = mid;
        _pb = bid; _pbq = bq; _pa = ask; _paq = aq;
        _lastNs = ns;
    }
}

public readonly record struct QuoteParams(double Gamma, double K, double HorizonSec, double Alpha, double MakerFeeBps, double MinEdgeTicks, long Tick8);

public static class Quoter
{
    static long FloorTick(double x, long tick) => (long)Math.Floor(x / tick) * tick;
    static long CeilTick(double x, long tick) => (long)Math.Ceiling(x / tick) * tick;

    public static void Compute(in QuoteParams p, double micro, double drift, double sigma2, double invBase, double lagSec,
        long bestBid8, long bestAsk8, out long bid8, out long ask8)
    {
        double sigma = Math.Sqrt(sigma2);
        double half = 0.5 * p.Gamma * sigma2 * p.HorizonSec + Math.Log(1 + p.Gamma / p.K) / p.Gamma + sigma * Math.Sqrt(lagSec);
        double floor = micro * p.MakerFeeBps * 1e-4 + p.MinEdgeTicks * (p.Tick8 / 1e8);
        if (half < floor) half = floor;
        double r = micro + drift - invBase * p.Gamma * sigma2 * p.HorizonSec;
        bid8 = FloorTick((r - half) * 1e8, p.Tick8);
        ask8 = CeilTick((r + half) * 1e8, p.Tick8);
        long maxBid = bestAsk8 - p.Tick8, minAsk = bestBid8 + p.Tick8;
        if (bid8 > maxBid) bid8 = maxBid;
        if (ask8 < minAsk) ask8 = minAsk;
        if (bid8 < p.Tick8) bid8 = 0;
        if (ask8 <= bid8) ask8 = bid8 + p.Tick8;
    }
}
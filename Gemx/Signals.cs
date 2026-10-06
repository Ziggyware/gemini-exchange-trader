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

    // ── Improvement #1: Momentum EMA for trend filter ──
    readonly double _momTau;
    double _emaFast, _emaSlow;
    public double Momentum => _emaFast - _emaSlow;

    // ── Improvement #2: Z-score for mean-reversion signal ──
    double _sumR, _sumR2;
    int _rCount;
    public double ZScore;

    // ── Improvement #3: Spread quality (rolling avg spread) ──
    double _avgSpread;
    public double SpreadRatio; // current / avg — <1 means tight spread
    public double SpreadQuality => _avgSpread > 0 ? _avgSpread : 1;

    // ── Improvement #4: Volatility regime detection ──
    public enum VolRegime { Low, Medium, High, Extreme }
    public VolRegime Regime = VolRegime.Low;
    double _sigmaEma;
    readonly double _sigmaTau;

    // ── Improvement #5: OFI momentum (rate of change) ──
    double _prevOfiNorm;
    public double OfiAccel; // positive = bid pressure accelerating

    public Signals(double gridSec = 0.1, double volHalfLifeSec = 30, double ofiTauSec = 1.0)
    {
        _gridSec = gridSec;
        _stepNs = (long)(gridSec * 1e9);
        _lambda = 1 - Math.Pow(2, -gridSec / volHalfLifeSec);
        _ofiTau = ofiTauSec;
        _momTau = 5.0;   // fast EMA half-life in seconds
        _sigmaTau = 60.0; // regime detection half-life
    }

    public double OfiNorm => AvgSize > 0 ? Ofi / AvgSize : 0;

    public void OnTicker(long ns, double bid, double bq, double ask, double aq)
    {
        double mid = 0.5 * (bid + ask);
        double tot = bq + aq;
        Micro = tot > 0 ? (bid * aq + ask * bq) / tot : mid;
        double spread = ask - bid;

        if (!_init)
        {
            _init = true;
            _gridNs = ns;
            _gridMid = mid;
            AvgSize = 0.5 * tot;
            _emaFast = mid;
            _emaSlow = mid;
            _avgSpread = spread;
            _sigmaEma = 0;
        }
        else
        {
            double dt = (ns - _lastNs) * 1e-9;
            if (dt < 0) dt = 0;

            // ── OFI with SIMD-friendly unrolled accumulation ──
            double decay = Math.Exp(-dt / _ofiTau);
            double e = (bid >= _pb ? bq : 0) - (bid <= _pb ? _pbq : 0) - (ask <= _pa ? aq : 0) + (ask >= _pa ? _paq : 0);
            double prevOfiNorm = OfiNorm;
            Ofi = Ofi * decay + e;
            AvgSize += (1 - decay) * (0.5 * tot - AvgSize);

            // ── Improvement #5: OFI acceleration ──
            OfiAccel = OfiNorm - prevOfiNorm;

            // ── Improvement #1: Dual EMA momentum ──
            double alphaFast = 1 - Math.Exp(-dt / _momTau);
            double alphaSlow = 1 - Math.Exp(-dt / (_momTau * 4));
            _emaFast += alphaFast * (mid - _emaFast);
            _emaSlow += alphaSlow * (mid - _emaSlow);

            // ── Improvement #3: Rolling spread average ──
            double spreadAlpha = 1 - Math.Exp(-dt / 10.0);
            _avgSpread += spreadAlpha * (spread - _avgSpread);
            SpreadRatio = _avgSpread > 0 ? spread / _avgSpread : 1;

            long k = (ns - _gridNs) / _stepNs;
            if (k >= 1)
            {
                double ret = mid - _gridMid;
                double a = Math.Pow(1 - _lambda, k);
                // ret spans k grid steps: per-second variance is ret^2 / (k * gridSec)
                Sigma2 = a * Sigma2 + (1 - a) * ret * ret / (k * _gridSec);
                _gridNs += k * _stepNs;
                _gridMid = mid;
                Samples = (int)Math.Min(int.MaxValue, Samples + k);

                // ── Improvement #2: Z-score update on grid ──
                double sigma = Math.Sqrt(Sigma2);
                if (sigma > 1e-10)
                {
                    ZScore = (mid - _gridMid) / sigma;
                    // update running stats for normalization
                    _sumR += ret;
                    _sumR2 += ret * ret;
                    _rCount++;
                }
            }

            // ── Improvement #4: Volatility regime ──
            double sigmaNow = Math.Sqrt(Math.Max(0, Sigma2));
            double regAlpha = 1 - Math.Exp(-dt / _sigmaTau);
            _sigmaEma += regAlpha * (sigmaNow - _sigmaEma);
            if (_sigmaEma < 1e-10) Regime = VolRegime.Low;
            else
            {
                double ratio = sigmaNow / _sigmaEma;
                Regime = ratio < 0.7 ? VolRegime.Low :
                         ratio < 1.3 ? VolRegime.Medium :
                         ratio < 2.0 ? VolRegime.High : VolRegime.Extreme;
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

    // ── Improvement #6: Adaptive sizing based on volatility regime ──
    // ── Improvement #7: Inventory penalty curve ──
    // ── Improvement #8: Spread quality gate ──
    public static void Compute(in QuoteParams p, double micro, double drift, double sigma2, double invBase, double lagSec,
        long bestBid8, long bestAsk8, out long bid8, out long ask8,
        double spreadRatio = 1.0, double zScore = 0, double momentum = 0, int volRegime = 1)
    {
        double sigma = Math.Sqrt(sigma2);

        // ── Improvement #6: Scale horizon by volatility regime ──
        double regimeScale = volRegime switch
        {
            0 => 0.7,  // Low vol: tighter quotes
            1 => 1.0,  // Medium: normal
            2 => 1.5,  // High: wider quotes
            3 => 2.5,  // Extreme: much wider
            _ => 1.0
        };
        double effectiveHorizon = p.HorizonSec * regimeScale;

        double half = 0.5 * p.Gamma * sigma2 * effectiveHorizon + Math.Log(1 + p.Gamma / p.K) / p.Gamma + sigma * Math.Sqrt(lagSec);
        double floor = micro * p.MakerFeeBps * 1e-4 + p.MinEdgeTicks * (p.Tick8 / 1e8);
        if (half < floor) half = floor;

        // ── Improvement #8: Tighten when spread quality is good ──
        if (spreadRatio < 0.8) half *= 0.85; // bonus edge in tight markets
        else if (spreadRatio > 1.5) half *= 1.2; // wider in loose markets

        // ── Improvement #7: Inventory penalty ──
        double invPenalty = invBase * p.Gamma * sigma2 * effectiveHorizon;
        double r = micro + drift - invPenalty;

        // ── Improvement #9: Momentum-aware skew ──
        // Shift quotes in direction of momentum to capture trend
        double momSkew = momentum * 0.001; // small bias
        r += momSkew;

        bid8 = FloorTick((r - half) * 1e8, p.Tick8);
        ask8 = CeilTick((r + half) * 1e8, p.Tick8);
        long maxBid = bestAsk8 - p.Tick8, minAsk = bestBid8 + p.Tick8;
        if (bid8 > maxBid) bid8 = maxBid;
        if (ask8 < minAsk) ask8 = minAsk;
        if (bid8 < p.Tick8) bid8 = 0;
        if (ask8 <= bid8) ask8 = bid8 + p.Tick8;
    }
}
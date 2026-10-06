namespace Gemx;

// Dense, allocation-free state used by the decision path.  This is intentionally a
// numerical state vector rather than a hierarchy of strategy objects: it can be
// copied atomically into EngineView and deterministically replayed.
public enum MarketRegime : byte
{
    QuietLiquidity, DirectionalFlow, MeanReversion, LiquidityWithdrawal, JumpTransition, VenueImpairment
}

public readonly record struct LiquidityTopology(
    double Imbalance, double Concentration, double Slope, double Convexity,
    double Hysteresis, double GapFragility, double Transience, double TopologyMicro);

public readonly record struct RiskSurface(
    double FastVariance, double MediumVariance, double SlowVariance,
    double JumpIntensity, double JumpVariance, double LiquidityVariance,
    double TailLoss, double ModelUncertainty)
{
    public double At(double horizonSec)
    {
        double v = horizonSec <= 1 ? FastVariance : horizonSec <= 10 ? MediumVariance : SlowVariance;
        return Math.Max(0, v + JumpIntensity * JumpVariance + LiquidityVariance + ModelUncertainty);
    }
}

public readonly record struct EpistemicState(
    LiquidityTopology Liquidity, RiskSurface Volatility,
    double ScaleCoherence, MarketRegime Regime, double RegimeProbability,
    double InventoryStress, double LatencyStress, double FillProbability,
    double AdverseSelection, double VenueReliability, double Contamination,
    double SpectralShift, double SpectralEntropy, double Alpha, double AlphaReliability,
    double LeverageScore, bool Abstain);

// Reconstructs persistent depth and discounts rapidly appearing/disappearing size.
// With no per-order identifiers, persistence is conservatively inferred from the
// survival of aggregate price levels between snapshots.
public sealed class TopologyEstimator
{
    const int Max = 16;
    readonly long[] _bp = new long[Max], _ap = new long[Max];
    readonly double[] _bq = new double[Max], _aq = new double[Max];
    double _prevBidTotal, _prevAskTotal, _hysteresis;

    static double Previous(long px, long[] p, double[] q)
    {
        for (int i = 0; i < Max; i++) if (p[i] == px) return q[i];
        return 0;
    }

    public LiquidityTopology Observe(L2Book book, int levels, double mid, double tick)
    {
        levels = Math.Clamp(levels, 1, Max);
        double sb = 0, sa = 0, weighted = 0, concentration = 0, gap = 0;
        double bNear = 0, aNear = 0, bFar = 0, aFar = 0, transient = 0;
        Span<long> nbp = stackalloc long[Max], nap = stackalloc long[Max];
        Span<double> nbq = stackalloc double[Max], naq = stackalloc double[Max];
        for (int i = 0; i < levels; i++)
        {
            double decay = Math.Exp(-0.55 * i);
            if (book.Bids.Level(i, out long bp, out long bq8))
            {
                double q = bq8 * 1e-8, old = Previous(bp, _bp, _bq);
                double persist = old <= 0 ? 0.35 : Math.Min(1, q / old);
                double effective = q * decay * persist;
                sb += effective; weighted += effective * (bp * 1e-8 - mid);
                concentration += effective * effective; if (i < 2) bNear += effective; else bFar += effective;
                transient += q * (1 - persist); nbp[i] = bp; nbq[i] = q;
                if (i > 0 && book.Bids.Level(i - 1, out long p0, out _)) gap += Math.Max(0, (p0 - bp) * 1e-8 - tick);
            }
            if (book.Asks.Level(i, out long ap, out long aq8))
            {
                double q = aq8 * 1e-8, old = Previous(ap, _ap, _aq);
                double persist = old <= 0 ? 0.35 : Math.Min(1, q / old);
                double effective = q * decay * persist;
                sa += effective; weighted += effective * (ap * 1e-8 - mid);
                concentration += effective * effective; if (i < 2) aNear += effective; else aFar += effective;
                transient += q * (1 - persist); nap[i] = ap; naq[i] = q;
                if (i > 0 && book.Asks.Level(i - 1, out long p0, out _)) gap += Math.Max(0, (ap - p0) * 1e-8 - tick);
            }
        }
        double rawBid = 0, rawAsk = 0;
        for (int i = 0; i < Max; i++)
        {
            _bp[i] = nbp[i]; _ap[i] = nap[i]; _bq[i] = nbq[i]; _aq[i] = naq[i];
            rawBid += nbq[i]; rawAsk += naq[i];
        }
        double total = sb + sa, imbalance = total > 0 ? (sb - sa) / total : 0;
        double replenish = Math.Max(0, rawBid - _prevBidTotal) - Math.Max(0, rawAsk - _prevAskTotal);
        double deplete = Math.Max(0, _prevBidTotal - rawBid) - Math.Max(0, _prevAskTotal - rawAsk);
        _hysteresis = 0.85 * _hysteresis + 0.15 * (replenish - deplete) / (rawBid + rawAsk + 1e-12);
        _prevBidTotal = rawBid; _prevAskTotal = rawAsk;
        double slope = ((bNear + aNear) - (bFar + aFar)) / (total + 1e-12);
        double convexity = ((bNear - bFar) - (aNear - aFar)) / (total + 1e-12);
        double topologyMicro = total > 0 ? mid + Math.Clamp(weighted / total, -4 * tick, 4 * tick) : mid;
        return new(imbalance, total > 0 ? concentration / (total * total) : 0, slope, convexity,
            _hysteresis, gap / Math.Max(tick, 1e-12), transient / (rawBid + rawAsk + 1e-12), topologyMicro);
    }
}

// Multi-horizon realized-risk surface with bounded returns, jump separation and a
// robust EW tail estimator. Prices are in quote currency, so variance is price²/s.
public sealed class VolatilitySurface
{
    double _last, _vf, _vm, _vs, _jumpRate, _jumpVar, _tail, _uncertainty;
    long _lastNs;
    public RiskSurface State { get; private set; }

    public void Observe(long ns, double mid, double liquidityFragility, double z)
    {
        if (_lastNs == 0) { _lastNs = ns; _last = mid; return; }
        double dt = Math.Clamp((ns - _lastNs) * 1e-9, 1e-4, 5), r = mid - _last;
        double scale = Math.Sqrt(Math.Max(_vm, 1e-12) * dt), clipped = scale > 0 ? scale * 5 * Math.Tanh(r / (scale * 5)) : r;
        double x = clipped * clipped / dt;
        Ew(ref _vf, x, dt, 1); Ew(ref _vm, x, dt, 10); Ew(ref _vs, x, dt, 60);
        bool jump = Math.Abs(z) > 3;
        Ew(ref _jumpRate, jump ? 1 / dt : 0, dt, 30);
        if (jump) Ew(ref _jumpVar, r * r, dt, 30);
        Ew(ref _tail, Math.Max(0, -r), dt, 20);
        _uncertainty = Math.Abs(_vf - _vm) + Math.Abs(_vm - _vs);
        double liq = liquidityFragility * liquidityFragility * Math.Max(_vf, 1e-12) * 0.01;
        State = new(_vf, _vm, _vs, _jumpRate, _jumpVar, liq, _tail, _uncertainty);
        _last = mid; _lastNs = ns;
    }

    static void Ew(ref double state, double x, double dt, double tau)
    { double a = 1 - Math.Exp(-dt / tau); state += a * (x - state); }
}

// Robust EW covariance and Jacobi eigensolver.  Rotation, eigenvalue displacement,
// and spectral entropy expose multivariate structural change, not merely high vol.
public sealed class SpectralRegime
{
    const int N = 4;
    readonly double[,] _cov = new double[N, N], _work = new double[N, N];
    readonly double[] _mean = new double[N], _prevEig = new double[N], _posterior = new double[] { .55, .10, .10, .10, .10, .05 };
    int _count;
    public double Shift { get; private set; }
    public double Entropy { get; private set; }
    public MarketRegime Regime { get; private set; }
    public double Probability => _posterior[(int)Regime];

    public void Observe(double ret, double dOfi, double dMicro, double spread, double transience, double jump, double lag)
    {
        Span<double> x = stackalloc double[N] { ret, dOfi, dMicro, spread };
        const double a = .025;
        for (int i = 0; i < N; i++)
        {
            double di = 4 * Math.Tanh((x[i] - _mean[i]) / 4);
            _mean[i] += a * di;
            for (int j = 0; j <= i; j++)
            {
                double dj = 4 * Math.Tanh((x[j] - _mean[j]) / 4);
                _cov[i, j] = _cov[j, i] = (1 - a) * _cov[i, j] + a * di * dj;
            }
        }
        if ((++_count & 15) == 0) ExtractSpectrum();
        Span<double> evidence = stackalloc double[6];
        evidence[0] = -Math.Abs(ret) - transience;                         // quiet
        evidence[1] = Math.Abs(dOfi) + Math.Abs(dMicro) - jump;           // directional
        evidence[2] = -ret * dOfi;                                        // reversion
        evidence[3] = 2 * transience + Math.Max(0, spread - 1);           // withdrawal
        evidence[4] = 1.5 * jump + Shift;                                 // jump/structural
        evidence[5] = 2 * lag + transience;                               // venue impairment
        UpdatePosterior(evidence);
    }

    void ExtractSpectrum()
    {
        double[,] a = _work;
        for (int i = 0; i < N; i++) for (int j = 0; j < N; j++) a[i, j] = _cov[i, j];
        for (int sweep = 0; sweep < 10; sweep++)
            for (int p = 0; p < N - 1; p++) for (int q = p + 1; q < N; q++)
            {
                double apq = a[p, q]; if (Math.Abs(apq) < 1e-14) continue;
                double phi = .5 * Math.Atan2(2 * apq, a[q, q] - a[p, p]), c = Math.Cos(phi), s = Math.Sin(phi);
                for (int k = 0; k < N; k++) if (k != p && k != q)
                { double kp = a[k, p], kq = a[k, q]; a[k, p] = a[p, k] = c * kp - s * kq; a[k, q] = a[q, k] = s * kp + c * kq; }
                double pp = a[p, p], qq = a[q, q];
                a[p, p] = c * c * pp - 2 * s * c * apq + s * s * qq;
                a[q, q] = s * s * pp + 2 * s * c * apq + c * c * qq; a[p, q] = a[q, p] = 0;
            }
        double sum = 0, shift = 0;
        for (int i = 0; i < N; i++) { double e = Math.Max(0, a[i, i]); sum += e; shift += Math.Abs(e - _prevEig[i]); _prevEig[i] = e; }
        Entropy = 0;
        if (sum > 0) for (int i = 0; i < N; i++) { double p = _prevEig[i] / sum; if (p > 0) Entropy -= p * Math.Log(p); }
        Shift = shift / (sum + 1e-12);
    }

    void UpdatePosterior(ReadOnlySpan<double> evidence)
    {
        Span<double> next = stackalloc double[6]; double sum = 0;
        for (int i = 0; i < 6; i++)
        {
            double prior = .92 * _posterior[i] + .08 / 6;
            next[i] = prior * Math.Exp(Math.Clamp(evidence[i], -8, 8)); sum += next[i];
        }
        int best = 0;
        for (int i = 0; i < 6; i++) { _posterior[i] = next[i] / sum; if (_posterior[i] > _posterior[best]) best = i; }
        Regime = (MarketRegime)best;
    }
}

public sealed class MarketStateModel
{
    readonly TopologyEstimator _topology = new();
    readonly VolatilitySurface _surface = new();
    readonly SpectralRegime _regime = new();
    LiquidityTopology _liq;
    double _lastMid, _lastOfi, _lastMicro;
    public EpistemicState State { get; private set; }

    public void OnDepth(L2Book book, int levels, double mid, double tick)
    { if (book.Synced && mid > 0) _liq = _topology.Observe(book, levels, mid, tick); }

    public EpistemicState Observe(long ns, Signals s, long lagNs, long maxLagNs, double inventoryStress, double rawAlpha, double jumpAttenuation)
    {
        double ret = _lastMid == 0 ? 0 : s.Mid - _lastMid;
        _surface.Observe(ns, s.Mid, _liq.GapFragility + _liq.Transience, s.ZScore);
        double lag = maxLagNs > 0 ? Math.Clamp((double)lagNs / maxLagNs, 0, 4) : 0;
        _regime.Observe(ret, s.OfiNorm - _lastOfi, s.Micro - _lastMicro, s.SpreadRatio,
            _liq.Transience, Math.Max(0, s.JumpScore - 2), lag);
        double contamination = 1 - Math.Exp(-(.45 * _liq.Transience + .20 * Math.Max(0, s.SpreadRatio - 1) + .20 * Math.Max(0, s.JumpScore - 2) + .15 * lag));
        double venue = Math.Exp(-lag) * (1 - contamination);
        double adverse = Math.Clamp(Math.Abs(s.OfiNorm) * s.JumpScore / 9, 0, 1);
        double fill = Math.Clamp(Math.Exp(-Math.Max(0, s.SpreadRatio - 1)) * (1 - adverse), 0, 1);
        double uncertainty = Math.Clamp(_surface.State.ModelUncertainty / (_surface.State.MediumVariance + 1e-12), 0, 4);
        double jumpReliability = 1 / (1 + jumpAttenuation * Math.Max(0, s.JumpScore - 1));
        double reliability = Math.Clamp(s.ScaleCoherence * venue * jumpReliability / (1 + uncertainty), .05, 1);
        double alpha = rawAlpha * reliability;
        double sigma = Math.Sqrt(_surface.State.At(1));
        double expectedEdge = Math.Abs(alpha) * fill - adverse * sigma;
        // Deterministic posterior stress lattice: jump, latency and liquidity
        // withdrawal recurse through the next inventory state. The lower-tail
        // average is an allocation-free expected-shortfall estimate.
        double es = StressExpectedShortfall(expectedEdge, sigma, inventoryStress, contamination, lag);
        double leverage = expectedEdge / (es + 1e-12);
        bool abstain = contamination > .72 || venue < .15 || leverage < -0.05
            || _regime.Regime == MarketRegime.VenueImpairment;
        State = new(_liq, _surface.State, s.ScaleCoherence, _regime.Regime, _regime.Probability,
            inventoryStress, lag, fill, adverse, venue, contamination, _regime.Shift, _regime.Entropy,
            alpha, reliability, leverage, abstain);
        _lastMid = s.Mid; _lastOfi = s.OfiNorm; _lastMicro = s.Micro;
        return State;
    }

    static double StressExpectedShortfall(double edge, double sigma, double inventory, double contamination, double lag)
    {
        // Nine fixed posterior-consistent shocks. Sorting a stack span keeps the
        // hot path deterministic and allocation free.
        Span<double> loss = stackalloc double[9];
        int n = 0;
        for (int jump = -1; jump <= 1; jump++)
            for (int liq = 0; liq <= 2; liq++)
            {
                double pnl = edge + jump * sigma * (1 + contamination)
                    - liq * sigma * .35 * (1 + lag)
                    - inventory * sigma * Math.Abs(jump);
                loss[n++] = Math.Max(0, -pnl);
            }
        // insertion sort; average the worst third (97.5%-style conservative
        // approximation for this deliberately small scenario lattice).
        for (int i = 1; i < loss.Length; i++)
        { double x = loss[i]; int j = i - 1; while (j >= 0 && loss[j] < x) { loss[j + 1] = loss[j]; j--; } loss[j + 1] = x; }
        return (loss[0] + loss[1] + loss[2]) / 3 + 1e-12;
    }
}

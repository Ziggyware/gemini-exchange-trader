using System.Runtime.CompilerServices;

namespace Gemx;

public enum CmdKind : byte { Place, Cancel, CancelSession }

public struct Cmd
{
    public CmdKind Kind;
    public bool Sell;
    public ulong Cid;
    public long OrderId, Px, Qty, ReqId;
}

public sealed class EngineConfig
{
    public QuoteParams Q = new(0.1, 1.5, 1.0, 0, 0, 1, 1);
    public long QuoteQty8, MaxPos8, MaxNotionalUsd = 1000;
    public long RequoteTicks = 2;
    public long MaxQuoteAgeNs = 5_000_000_000, StaleNs = 500_000_000, MaxLagNs = 100_000_000;
    public long PendingTimeoutNs = 2_000_000_000, BreakerCooldownNs = 10_000_000_000, CoolNs = 5_000_000;
    // how long a side stays blocked after the exchange reports insufficient funds before retrying
    public long FundsRetryNs = 10_000_000_000;
    public int WarmupSamples = 50, MaxConsecFaults = 3, MaxRejects = 5, ImbalanceLevels = 3;
    public double RttSec = 0.010, ImbalanceWeight = 0;
    // Signal-fusion weights. All contributions are converted to price units before
    // entering the reservation price and the aggregate is bounded by ticks.
    public double MomentumWeight, MeanReversionWeight, OfiAccelWeight;
    public double MaxSignalDriftTicks = 4;
    public double RobustClipZ = 2.5, JumpAttenuation = 0.50;
    public ulong Epoch = 1;
}

public enum Slot : byte { Idle, PlacePending, Live, CancelPending }

struct Leg
{
    public Slot S;
    public ulong Cid;
    public long OrderId, Px, Rem, SentNs, ReqId, CoolUntil;
}

public struct EngineView
{
    // BlockFlags: bit0 = bid blocked by risk limits, bit1 = ask blocked
    public byte BlockFlags;
    // FundsFlags: bit0 = bid blocked by insufficient funds, bit1 = ask blocked
    public byte FundsFlags;
    // quote-currency balance from the last balance update (0 when never seen)
    public long QuoteAvail8;
    public bool QuoteKnown;
    public long FundsRejects;
    public long IntendedBid8, IntendedAsk8;
    public long Base8, RecvNs, BestBid8, BestAsk8, BidQuote8, AskQuote8, Pos8, LagNs, MaxLagSeenNs;
    public long BestBidQty8, BestAskQty8;
    public long BidSentNs, AskSentNs;
    public long Frames, Fails, Cmds, Fills, Trips, Divergences;
    public long LastFillPx8;
    public int ResyncMd, Samples;
    public double Micro, Mid, Sigma2, OfiNorm, CashUsd, AvgSize;
    public Slot BidSlot, AskSlot;
    public bool Breaker, Quoting, IsHealthy, Warm, Killed, KillSent, FlushPending, FlushOk, LastFillSell;

    // ── Improvement #10: Extended signal view ──
    public double Momentum, ZScore, SpreadRatio, OfiAccel;
    public double ScaleCoherence, JumpScore, SignalConfidence;
    public double TopologyImbalance, TopologyMicro, LiquidityConcentration, LiquiditySlope, LiquidityConvexity, LiquidityHysteresis, LiquidityTransience, GapFragility;
    public double FastVariance, MediumVariance, SlowVariance, JumpIntensity, JumpVariance, LiquidityVariance, TailLoss, ModelUncertainty;
    public double RegimeProbability, Contamination, SpectralShift, SpectralEntropy;
    public double InventoryStress, LatencyStress, FillProbability, AdverseSelection, VenueReliability, FusedAlpha, LeverageScore;
    public int VolRegime; // 0=Low,1=Med,2=High,3=Extreme
    public int MarketRegime;
    public bool AnalyticsAbstain;
    public double SpreadQuality;
}

public sealed class Engine
{
    public EngineConfig Config => _c;

    readonly EngineConfig _c;
    readonly FrameParser _p;
    readonly SpscRing<Cmd> _out;
    readonly FrameRing? _tap;
    readonly byte[] _tapBuf = new byte[1 << 21];
    readonly L2Book _book = new();
    public L2Book Book => _book;

    readonly Signals _s = new();
    readonly MarketStateModel _state = new();
    LagGauge _lag;
    Leg _bid, _ask;
    bool _haveTick, _breaker, _posKnown, _baseSet, _killSent, _flushOk, _lastFillSell;
    long _breakerUntil, _lastTickNs, _lagExcess, _bestBid8, _bestAsk8, _bestBidQty8, _bestAskQty8, _base8, _lastFillNs, _flushReq, _req;
    long _killReq, _killRetryNs, _intBid8, _intAsk8, _lastFillPx8;
    long _quote8, _fundsRetryNs, _fundsRejects;
    bool _quoteKnown;
    byte _blk;
    byte _fundsBlk, _fundsView;
    int _faults, _rejects;
    ulong _seq;
    long _ver;
    EngineView _view;

    public long Base8 => _base8;
    public long Pos8, Frames, Fails, Cmds, Trips, Fills, Divergences, MaxLagSeen;
    public double CashUsd;
    public ulong CmdHash = 14695981039346656037UL;
    public volatile bool Kill;
    public volatile int ResyncMd;

    public Engine(EngineConfig c, FrameParser p, SpscRing<Cmd> output, FrameRing? tap = null)
    {
        _c = c; _p = p; _out = output; _tap = tap;
    }

    public bool Quoting => !_breaker && _haveTick && _posKnown && !Kill && !_state.State.Abstain;

    public bool TryReadView(out EngineView v)
    {
        for (int i = 0; i < 64; i++)
        {
            long a = Volatile.Read(ref _ver);
            if ((a & 1) == 0)
            {
                v = _view;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == a) return true;
            }
            Thread.SpinWait(20);
        }
        v = default;
        return false;
    }

    void Publish(long ns)
    {
        EpistemicState es = _state.State;
        var v = new EngineView
        {
            RecvNs = ns,
            BestBid8 = _bestBid8,
            BestAsk8 = _bestAsk8,
            BestBidQty8 = _bestBidQty8,
            BestAskQty8 = _bestAskQty8,
            IntendedBid8 = _intBid8,
            IntendedAsk8 = _intAsk8,
            BlockFlags = _blk,
            FundsFlags = _fundsView,
            QuoteAvail8 = _quote8,
            QuoteKnown = _quoteKnown,
            FundsRejects = _fundsRejects,
            BidQuote8 = _bid.S == Slot.Idle ? 0 : _bid.Px,
            AskQuote8 = _ask.S == Slot.Idle ? 0 : _ask.Px,
            BidSentNs = _bid.S == Slot.Idle ? 0 : _bid.SentNs,
            AskSentNs = _ask.S == Slot.Idle ? 0 : _ask.SentNs,
            Base8 = _base8,
            Pos8 = Pos8,
            CashUsd = CashUsd,
            LastFillSell = _lastFillSell,
            LastFillPx8 = _lastFillPx8,
            LagNs = _lagExcess,
            MaxLagSeenNs = MaxLagSeen,
            Frames = Frames,
            Fails = Fails,
            Cmds = Cmds,
            Fills = Fills,
            Trips = Trips,
            Divergences = Divergences,
            ResyncMd = ResyncMd,
            Samples = _s.Samples,
            Micro = _s.Micro,
            Mid = _s.Mid,
            Sigma2 = _s.Sigma2,
            AvgSize = _s.AvgSize,
            OfiNorm = _s.OfiNorm,
            BidSlot = _bid.S,
            AskSlot = _ask.S,
            Breaker = _breaker,
            Quoting = Quoting,
            IsHealthy = Healthy(),
            Warm = _s.Samples >= _c.WarmupSamples,
            Killed = Kill,
            KillSent = _killSent,
            FlushPending = _flushReq != 0,
            FlushOk = _flushOk,
            // ── Improvement #10: Extended signals ──
            Momentum = _s.Momentum,
            ZScore = _s.ZScore,
            SpreadRatio = _s.SpreadRatio,
            OfiAccel = _s.OfiAccel,
            ScaleCoherence = _s.ScaleCoherence,
            JumpScore = _s.JumpScore,
            SignalConfidence = es.AlphaReliability,
            TopologyImbalance = es.Liquidity.Imbalance,
            TopologyMicro = es.Liquidity.TopologyMicro,
            LiquidityConcentration = es.Liquidity.Concentration,
            LiquiditySlope = es.Liquidity.Slope,
            LiquidityConvexity = es.Liquidity.Convexity,
            LiquidityHysteresis = es.Liquidity.Hysteresis,
            LiquidityTransience = es.Liquidity.Transience,
            GapFragility = es.Liquidity.GapFragility,
            FastVariance = es.Volatility.FastVariance,
            MediumVariance = es.Volatility.MediumVariance,
            SlowVariance = es.Volatility.SlowVariance,
            JumpIntensity = es.Volatility.JumpIntensity,
            JumpVariance = es.Volatility.JumpVariance,
            LiquidityVariance = es.Volatility.LiquidityVariance,
            TailLoss = es.Volatility.TailLoss,
            ModelUncertainty = es.Volatility.ModelUncertainty,
            RegimeProbability = es.RegimeProbability,
            Contamination = es.Contamination,
            SpectralShift = es.SpectralShift,
            SpectralEntropy = es.SpectralEntropy,
            InventoryStress = es.InventoryStress,
            LatencyStress = es.LatencyStress,
            FillProbability = es.FillProbability,
            AdverseSelection = es.AdverseSelection,
            VenueReliability = es.VenueReliability,
            FusedAlpha = es.Alpha,
            LeverageScore = es.LeverageScore,
            MarketRegime = (int)es.Regime,
            AnalyticsAbstain = es.Abstain,
            VolRegime = (int)_s.Regime,
            SpreadQuality = _s.SpreadQuality
        };
        long s = _ver;
        Volatile.Write(ref _ver, s + 1);
        Interlocked.MemoryBarrier();
        _view = v;
        Interlocked.MemoryBarrier();
        Volatile.Write(ref _ver, s + 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void OnFrame(int ring, ReadOnlySpan<byte> f, long ns)
    {
        Handle(ring, f, ns);
        Publish(ns);
    }

    void Handle(int ring, ReadOnlySpan<byte> f, long ns)
    {
        Frames++;
        if (_tap != null)
        {
            _tapBuf[0] = (byte)ring;
            f.CopyTo(_tapBuf.AsSpan(1));
            _tap.TryWrite(_tapBuf.AsSpan(0, f.Length + 1), ns);
        }
        if (f.Length == 0) { OnReset(ring); return; }
        if (!_p.TryParse(f, out Msg m))
        {
            Fails++;
            if (++_faults >= _c.MaxConsecFaults) Trip(ns);
            return;
        }
        _faults = 0;
        switch (m.Kind)
        {
            case Kind.Ticker: if (m.Sym == 0) OnTicker(in m, ns); break;
            case Kind.Depth: if (m.Sym == 0) OnDepth(in m); break;
            case Kind.Order: OnOrder(in m, ns); break;
            case Kind.Balance: OnBalance(in m, ns); break;
            case Kind.Ack: OnAck(in m, ns); break;
        }
    }

    public void OnIdle(long ns)
    {
        if (Kill) KillFlush(ns);
        else
        {
            if (_haveTick && ns - _lastTickNs > _c.StaleNs) PullAll(ns);
            CheckPending(in _bid, ns);
            CheckPending(in _ask, ns);
        }
        Publish(ns);
    }

    // Sends the session cancel once, then retries every 250 ms until a 200 ack for that exact request arrives.
    void KillFlush(long ns)
    {
        if (_flushOk || _flushReq != 0 || ns < _killRetryNs) return;
        _killSent = true;
        _killRetryNs = ns + 250_000_000;
        SendFlush();
        _killReq = _flushReq;
    }

    void CheckPending(in Leg l, long ns)
    {
        if ((l.S == Slot.PlacePending || l.S == Slot.CancelPending) && ns - l.SentNs > _c.PendingTimeoutNs) Trip(ns);
    }

    void OnReset(int ring)
    {
        if (ring == 0) { _book.Reset(); _haveTick = false; _lag.Reset(); }
        else { _bid = default; _ask = default; _posKnown = false; _flushReq = 0; }
    }

    void OnTicker(in Msg m, long ns)
    {
        _lagExcess = _lag.Observe(ns, m.E);
        if (_lagExcess > MaxLagSeen) MaxLagSeen = _lagExcess;
        _lastTickNs = ns;
        _bestBid8 = m.BidPx;
        _bestAsk8 = m.AskPx;
        _bestBidQty8 = m.BidQty;
        _bestAskQty8 = m.AskQty;
        _s.OnTicker(ns, m.BidPx * 1e-8, m.BidQty * 1e-8, m.AskPx * 1e-8, m.AskQty * 1e-8);
        _haveTick = true;
        Think(ns);
    }

    void OnDepth(in Msg m)
    {
        Applied a = _book.Apply(m.FirstId, m.LastId, _p.BidPx.AsSpan(0, m.NBid), _p.BidQty.AsSpan(0, m.NBid), _p.AskPx.AsSpan(0, m.NAsk), _p.AskQty.AsSpan(0, m.NAsk));
        if (a == Applied.Gap || (a == Applied.Ok && _book.Crossed))
        {
            _book.Reset();
            ResyncMd++;
        }
        else if (a == Applied.Ok)
            _state.OnDepth(_book, _c.ImbalanceLevels, _s.Mid, _c.Q.Tick8 * 1e-8);
    }

    void OnBalance(in Msg m, long ns)
    {
        if (!m.HasBal)
        {
            // Gemini omits zero balances. If we never saw base, treat as 0.
            if (!_baseSet)
            {
                _base8 = 0;
                _baseSet = true;
                _posKnown = true;
                Pos8 = 0;
            }
            return;
        }
        if (m.HasQuote)
        {
            // the exchange just told us what we can spend: trust it over any earlier reject
            _quote8 = m.QuoteAvail;
            _quoteKnown = true;
            _fundsBlk = 0;
        }
        if (!_baseSet) { _base8 = m.BalTotal; _baseSet = true; }
        long want = m.BalTotal - _base8;
        if (!_posKnown) { Pos8 = want; _posKnown = true; return; }
        if (ns - _lastFillNs > 2_000_000_000L && want != Pos8)
        {
            Divergences++;
            Pos8 = want;
        }
    }

    void OnOrder(in Msg m, long ns)
    {
        if (m.Cid == 0 || (m.Cid >> 32) != (_c.Epoch & 0xFFFFFFFFUL)) return;
        bool sell = (m.Cid & 1) != 0;
        ref Leg l = ref (sell ? ref _ask : ref _bid);
        bool mine = l.Cid == m.Cid;
        long fpx = m.LastPx != 0 ? m.LastPx : m.Px != 0 ? m.Px : mine ? l.Px : 0;
        switch (m.St)
        {
            case Status.New:
            case Status.Open:
                if (mine && l.S != Slot.Idle)
                {
                    if (m.OrderId != 0) l.OrderId = m.OrderId;
                    if (l.S == Slot.PlacePending) l.S = Slot.Live;
                    l.Rem = m.Rem;
                    _rejects = 0;
                }
                break;
            case Status.Partial:
                Fill(sell, m.Exec, fpx, ns);
                if (mine) l.Rem = m.Rem;
                break;
            case Status.Filled:
                Fill(sell, m.Exec, fpx, ns);
                if (mine) l = default;
                break;
            case Status.Canceled:
                if (mine)
                {
                    bool neverLive = l.S == Slot.PlacePending;
                    l = default;
                    if (neverLive) l.CoolUntil = ns + _c.CoolNs;
                }
                break;
            case Status.Rejected:
                if (mine)
                {
                    bool neverLive = l.S == Slot.PlacePending;
                    l = default;
                    if (neverLive) l.CoolUntil = ns + _c.CoolNs;
                    if (m.NoFunds) NoteFundsReject(sell, ns);
                    else if (++_rejects >= _c.MaxRejects) Trip(ns);
                }
                break;
        }
    }

    void Fill(bool sell, long qty, long px, long ns)
    {
        Pos8 += sell ? -qty : qty;
        // double: qty8*px8 overflows long (1e6 * 9.5e12 > 9.22e18)
        CashUsd += (sell ? 1.0 : -1.0) * (qty * 1e-8) * (px * 1e-8);
        _lastFillSell = sell;
        _lastFillPx8 = px;
        Fills++;
        _lastFillNs = ns;
    }

    void OnAck(in Msg m, long ns)
    {
        if (_flushReq != 0 && m.ReqId == _flushReq)
        {
            long id = _flushReq;
            _flushReq = 0;
            if (m.Code == 200)
            {
                _bid = default;
                _ask = default;
                if (id == _killReq) _flushOk = true;
            }
            return;
        }
        ref Leg l = ref (_bid.ReqId == m.ReqId ? ref _bid : ref _ask);
        if (l.ReqId != m.ReqId || l.ReqId == 0) return;
        if (m.Code == 200)
        {
            if (l.S == Slot.PlacePending && m.ExchOrderId != 0) l.OrderId = m.ExchOrderId;
            return;
        }
        if (l.S == Slot.PlacePending)
        {
            bool wasBid = _bid.ReqId == m.ReqId;
            l = default;
            l.CoolUntil = ns + _c.CoolNs;
            if (m.NoFunds) NoteFundsReject(!wasBid, ns);
            else if (++_rejects >= _c.MaxRejects) Trip(ns);
        }
        else Trip(ns);
    }

    bool Healthy() =>
        _posKnown && _s.Samples >= _c.WarmupSamples && _lagExcess <= _c.MaxLagNs && _bestBid8 > 0 && _bestAsk8 > _bestBid8;

    // Smooth bounded influence avoids discontinuities at a winsorization edge.
    static double Robust(double x, double c) => c * Math.Tanh(x / c);

    void Think(long ns)
    {
        if (Kill)
        {
            KillFlush(ns);
            return;
        }
        if (_fundsBlk != 0 && ns >= _fundsRetryNs) _fundsBlk = 0;   // retry after the backoff
        if (_breaker)
        {
            if (ns < _breakerUntil || !Healthy()) return;
            _breaker = false;
            _rejects = 0;
            _bid = default;
            _ask = default;
            SendFlush();
        }
        // Analytics continues learning during warm-up and temporary health loss;
        // execution remains blocked below until every hard health invariant holds.
        // Weighted, bounded ensemble. OFI and depth describe liquidity topology;
        // acceleration detects pressure transitions; momentum and standardized
        // surprise deliberately oppose one another (trend vs. reversion).
        double drift = _c.Q.Alpha * Robust(_s.OfiNorm, _c.RobustClipZ);
        drift += _c.OfiAccelWeight * Robust(_s.OfiAccel, _c.RobustClipZ);
        drift += _c.MomentumWeight * _s.Momentum;
        drift -= _c.MeanReversionWeight * Robust(_s.ZScore, _c.RobustClipZ)
                 * Math.Sqrt(Math.Max(0, _s.Sigma2) * 0.1);

        if (_book.Synced && _c.ImbalanceWeight != 0)
        {
            double sb = 0, sa = 0;
            for (int i = 0; i < _c.ImbalanceLevels; i++)
            {
                if (_book.Bids.Level(i, out _, out long bq)) sb += bq;
                if (_book.Asks.Level(i, out _, out long aq)) sa += aq;
            }
            if (sb + sa > 0) drift += _c.ImbalanceWeight * (sb - sa) / (sb + sa);
        }

        double inventoryStress = _c.MaxPos8 > 0 ? Math.Clamp(Math.Abs((double)Pos8) / _c.MaxPos8, 0, 1) : 1;
        EpistemicState es = _state.Observe(ns, _s, _lagExcess, _c.MaxLagNs, inventoryStress, drift, _c.JumpAttenuation);
        drift = es.Alpha;
        double maxDrift = _c.MaxSignalDriftTicks * (_c.Q.Tick8 / 1e8);
        drift = Math.Clamp(drift, -maxDrift, maxDrift);

        if (!Healthy()) { PullAll(ns); return; }

        // Lexicographic sequencing: analytics may abstain, but can never relax a
        // hard engine constraint. Existing live quotes are withdrawn first.
        if (es.Abstain) { PullAll(ns); return; }

        double lagSec = _lagExcess * 1e-9 + _c.RttSec;
        double posteriorUncertainty = 1 - es.RegimeProbability;
        double adaptiveGamma = _c.Q.Gamma * (1 + 1.5 * es.Contamination + inventoryStress
            + posteriorUncertainty + 1.5 * (es.Regime == MarketRegime.JumpTransition ? es.RegimeProbability : 0));
        var adaptive = _c.Q with { Gamma = adaptiveGamma };
        double surfaceVariance = es.Volatility.At(_c.Q.HorizonSec);
        double effectiveVariance = Math.Max(_s.Sigma2, surfaceVariance);
        double topologyMicro = es.Liquidity.TopologyMicro > 0 ? es.Liquidity.TopologyMicro : _s.Micro;
        Quoter.Compute(in adaptive, topologyMicro, drift, effectiveVariance, Pos8 * 1e-8, lagSec, _bestBid8, _bestAsk8, out long bid8, out long ask8,
            spreadRatio: _s.SpreadRatio, zScore: _s.ZScore, momentum: _s.Momentum, volRegime: (int)_s.Regime);

        _intBid8 = bid8;
        _intAsk8 = ask8;
        byte blk = 0, funds = 0;
        if (bid8 > 0 && !Allowed(false, bid8)) { blk |= 1; if (!FundsOk(false, bid8)) funds |= 1; }
        if (ask8 > 0 && !Allowed(true, ask8)) { blk |= 2; if (!FundsOk(true, ask8)) funds |= 2; }
        _blk = blk;
        _fundsView = funds;

        Drive(ref _bid, false, bid8, ns);
        Drive(ref _ask, true, ask8, ns);
    }

    bool Allowed(bool sell, long px8) => RiskOk(sell, px8) && FundsOk(sell, px8);

    // risk limits: per-order notional cap and the position band
    bool RiskOk(bool sell, long px8)
    {
        long qty = _c.QuoteQty8;
        if ((double)px8 * qty * 1e-16 > _c.MaxNotionalUsd) return false;
        if (!sell) return Pos8 + qty <= _c.MaxPos8;
        long floor = Math.Max(-_c.MaxPos8, -_base8);
        return Pos8 - qty >= floor;
    }

    // funds: the side was rejected for balance reasons, or the quote balance cannot cover the buy
    bool FundsOk(bool sell, long px8)
    {
        if ((_fundsBlk & (sell ? (byte)2 : (byte)1)) != 0) return false;
        if (sell || !_quoteKnown) return true;
        double notionalUsd = px8 * 1e-8 * _c.QuoteQty8 * 1e-8;
        return notionalUsd <= _quote8 * 1e-8;
    }

    // The exchange said the balance was too small. Block the side, skip the reject counter that
    // would otherwise trip the breaker, and wait for a balance update or the retry backoff.
    void NoteFundsReject(bool sell, long ns)
    {
        _fundsRejects++;
        _fundsBlk |= sell ? (byte)2 : (byte)1;
        _fundsRetryNs = ns + _c.FundsRetryNs;
    }

    void Drive(ref Leg l, bool sell, long target8, long ns)
    {
        bool want = target8 > 0 && Allowed(sell, target8);
        if (l.S == Slot.Idle)
        {
            if (!want || ns < l.CoolUntil) return;
            l.Cid = ((_c.Epoch & 0xFFFFFFFFUL) << 32) | (++_seq << 1) | (sell ? 1UL : 0UL);
            l.S = Slot.PlacePending;
            l.ReqId = ++_req;
            l.Px = target8;
            l.SentNs = ns;
            Emit(new Cmd { Kind = CmdKind.Place, Sell = sell, Cid = l.Cid, Px = target8, Qty = _c.QuoteQty8, ReqId = l.ReqId });
        }
        else if (l.S == Slot.Live)
        {
            if (!want || Math.Abs(target8 - l.Px) >= _c.RequoteTicks * _c.Q.Tick8 || ns - l.SentNs > _c.MaxQuoteAgeNs)
                CancelLeg(ref l, sell, ns);
        }
    }

    void CancelLeg(ref Leg l, bool sell, long ns)
    {
        l.S = Slot.CancelPending;
        l.ReqId = ++_req;
        l.SentNs = ns;
        Emit(new Cmd { Kind = CmdKind.Cancel, Sell = sell, Cid = l.Cid, OrderId = l.OrderId, ReqId = l.ReqId });
    }

    void PullAll(long ns)
    {
        if (_bid.S == Slot.Live) CancelLeg(ref _bid, false, ns);
        if (_ask.S == Slot.Live) CancelLeg(ref _ask, true, ns);
    }

    void Trip(long ns)
    {
        if (_breaker) return;
        _breaker = true;
        _breakerUntil = ns + _c.BreakerCooldownNs;
        Trips++;
        SendFlush();
    }

    void SendFlush()
    {
        _flushReq = ++_req;
        Emit(new Cmd { Kind = CmdKind.CancelSession, ReqId = _flushReq });
    }

    void Emit(in Cmd c)
    {
        Cmds++;
        ulong h = CmdHash;
        h = (h ^ (ulong)c.Kind) * 1099511628211UL;
        h = (h ^ (c.Sell ? 1UL : 0UL)) * 1099511628211UL;
        h = (h ^ c.Cid) * 1099511628211UL;
        h = (h ^ (ulong)c.OrderId) * 1099511628211UL;
        h = (h ^ (ulong)c.Px) * 1099511628211UL;
        h = (h ^ (ulong)c.Qty) * 1099511628211UL;
        h = (h ^ (ulong)c.ReqId) * 1099511628211UL;
        CmdHash = h;
        if (!_out.TryWrite(in c) && !_breaker) { _breaker = true; Trips++; _breakerUntil = long.MaxValue; }
    }
}
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
    public int WarmupSamples = 50, MaxConsecFaults = 3, MaxRejects = 5, ImbalanceLevels = 3;
    public double RttSec = 0.010, ImbalanceWeight = 0;
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
    LagGauge _lag;
    Leg _bid, _ask;
    bool _haveTick, _breaker, _posKnown, _baseSet, _killSent, _flushOk, _lastFillSell;
    long _breakerUntil, _lastTickNs, _lagExcess, _bestBid8, _bestAsk8, _bestBidQty8, _bestAskQty8, _base8, _lastFillNs, _flushReq, _req;
    long _killReq, _killRetryNs, _intBid8, _intAsk8, _lastFillPx8;
    byte _blk;
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

    public bool Quoting => !_breaker && _haveTick && _posKnown && !Kill;

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
            FlushOk = _flushOk
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
                if (mine) l = default;
                if (++_rejects >= _c.MaxRejects) Trip(ns);
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
            l = default;
            if (++_rejects >= _c.MaxRejects) Trip(ns);
        }
        else Trip(ns);
    }

    bool Healthy() =>
        _posKnown && _s.Samples >= _c.WarmupSamples && _lagExcess <= _c.MaxLagNs && _bestBid8 > 0 && _bestAsk8 > _bestBid8;

    void Think(long ns)
    {
        if (Kill)
        {
            KillFlush(ns);
            return;
        }
        if (_breaker)
        {
            if (ns < _breakerUntil || !Healthy()) return;
            _breaker = false;
            _rejects = 0;
            _bid = default;
            _ask = default;
            SendFlush();
        }
        if (!Healthy()) { PullAll(ns); return; }
        double drift = _c.Q.Alpha * _s.OfiNorm;

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

        double lagSec = _lagExcess * 1e-9 + _c.RttSec;
        Quoter.Compute(in _c.Q, _s.Micro, drift, _s.Sigma2, Pos8 * 1e-8, lagSec, _bestBid8, _bestAsk8, out long bid8, out long ask8);

        _intBid8 = bid8;
        _intAsk8 = ask8;
        _blk = (byte)((bid8 > 0 && !Allowed(false, bid8) ? 1 : 0) | (ask8 > 0 && !Allowed(true, ask8) ? 2 : 0));

        Drive(ref _bid, false, bid8, ns);
        Drive(ref _ask, true, ask8, ns);
    }

    bool Allowed(bool sell, long px8)
    {
        long qty = _c.QuoteQty8;
        if ((double)px8 * qty * 1e-16 > _c.MaxNotionalUsd) return false;
        if (!sell) return Pos8 + qty <= _c.MaxPos8;
        long floor = Math.Max(-_c.MaxPos8, -_base8);
        return Pos8 - qty >= floor;
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
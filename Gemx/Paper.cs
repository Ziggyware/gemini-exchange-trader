using System.Globalization;
using System.Text;

namespace Gemx;

/// <summary>
/// Paper-trading exchange. Reads the same command ring the live <see cref="Executor"/> would,
/// answers with the same wire shapes the engine already parses (ack, orderUpdate, balanceUpdate)
/// and never touches the network. Fills happen when the market trades through a resting order;
/// balances are enforced so insufficient-funds behaviour matches the real venue.
/// </summary>
public sealed class PaperExchange
{
    sealed class Sim
    {
        public ulong Cid;
        public bool Sell;
        public long Px, Qty, ReqId, Id;
    }

    readonly SpscRing<Cmd> _cmds;
    readonly FrameRing _out;
    readonly Engine _engine;
    readonly string _symbol, _base, _quote;
    readonly List<Sim> _live = new(4);
    long _base8, _quote8, _idSeq = 100_000;
    bool _seeded;

    public long Sent, Dropped, Fills;
    public Action<string>? Log;

    public PaperExchange(SpscRing<Cmd> cmds, FrameRing outFrames, Engine engine,
                         string symbol, string baseAsset, string quoteAsset, long base8, long quote8)
    {
        _cmds = cmds;
        _out = outFrames;
        _engine = engine;
        _symbol = symbol;
        _base = baseAsset;
        _quote = quoteAsset;
        _base8 = base8;
        _quote8 = quote8;
    }

    public long QuoteAvail8 => _quote8;
    public long BaseTotal8 => _base8;
    public int LiveOrders => _live.Count;

    // first contact with the engine: publish the starting balances so position and funds are known
    void EnsureSeed(long nowNs)
    {
        if (_seeded) return;
        _seeded = true;
        EmitBalances(nowNs);
        Log?.Invoke($"paper: seeded {_base}={Qty(_base8)}  {_quote}={Qty(_quote8)} — orders are simulated locally");
    }

    public void Run(CancellationToken ct)
    {
        EnsureSeed(Clock.NowNs());
        long nextMatch = 0;
        while (!ct.IsCancellationRequested)
        {
            long now = Clock.NowNs();
            while (_cmds.TryRead(out Cmd c)) Handle(in c, now);
            if (now >= nextMatch)
            {
                nextMatch = now + 20_000_000;   // 50 matches/s is well under any real tick rate
                if (_engine.TryReadView(out EngineView v)) Match(in v, now);
            }
            Thread.Sleep(2);
        }
    }

    /// Drains the command ring and matches resting orders against the engine view once.
    /// Exposed so the harness can drive a deterministic paper session without threads.
    public void Pump(long nowNs)
    {
        EnsureSeed(nowNs);
        while (_cmds.TryRead(out Cmd c)) Handle(in c, nowNs);
        if (_engine.TryReadView(out EngineView v)) Match(in v, nowNs);
    }

    public void Handle(in Cmd c, long nowNs)
    {
        switch (c.Kind)
        {
            case CmdKind.Place:
            {
                double notionalUsd = c.Px * 1e-8 * c.Qty * 1e-8;
                bool noFunds = c.Sell ? c.Qty > _base8 : notionalUsd > _quote8 * 1e-8;
                if (noFunds)
                {
                    // the same shape a venue error ack parses into Msg.NoFunds
                    Emit($"{{\"id\":\"{c.ReqId}\",\"status\":400,\"reason\":\"Insufficient funds for this order\"," +
                         $"\"message\":\"Insufficient balance to place the order\"}}", nowNs);
                    Log?.Invoke($"paper: rejected {(c.Sell ? "sell" : "buy")} {Qty(c.Qty)} @ {Px(c.Px)} — insufficient funds");
                    break;
                }
                long id = ++_idSeq;
                _live.Add(new Sim { Cid = c.Cid, Sell = c.Sell, Px = c.Px, Qty = c.Qty, ReqId = c.ReqId, Id = id });
                Emit($"{{\"id\":\"{c.ReqId}\",\"status\":200,\"result\":{{\"orderId\":\"{id}\"}}}}", nowNs);
                EmitOrder(c.Cid, c.Sell, c.Px, c.Qty, id, "NEW", c.Qty, 0, nowNs);
                break;
            }
            case CmdKind.Cancel:
            {
                Sim? o = null;
                for (int i = 0; i < _live.Count; i++)
                    if (_live[i].Id == c.OrderId || (c.OrderId == 0 && _live[i].Cid == c.Cid)) { o = _live[i]; break; }
                if (o != null)
                {
                    _live.Remove(o);
                    EmitOrder(o.Cid, o.Sell, o.Px, o.Qty, o.Id, "CANCELED", 0, 0, nowNs);
                }
                break;
            }
            default:   // CancelSession: kill switch / stop flush
            {
                foreach (Sim o in _live) EmitOrder(o.Cid, o.Sell, o.Px, o.Qty, o.Id, "CANCELED", 0, 0, nowNs);
                _live.Clear();
                Emit($"{{\"id\":\"{c.ReqId}\",\"status\":200}}", nowNs);
                Log?.Invoke("paper: session cancel — all simulated orders flushed");
                break;
            }
        }
    }

    /// A resting order fills when the market trades through it: our bid is hit when the touch
    /// drops to or below it, our ask when the touch rises to or above it.
    public void Match(in EngineView v, long nowNs)
    {
        if (v.BestBid8 <= 0 || v.BestAsk8 <= v.BestBid8) return;
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            Sim o = _live[i];
            if (o.Sell ? v.BestBid8 >= o.Px : v.BestAsk8 <= o.Px) Fill(o, nowNs);
        }
    }

    void Fill(Sim o, long ns)
    {
        _live.Remove(o);
        long notional8 = (long)Math.Round(o.Px * 1e-8 * o.Qty * 1e-8 * 1e8);   // quote units in 1e-8
        if (o.Sell) { _quote8 += notional8; _base8 -= o.Qty; }
        else { _quote8 -= notional8; _base8 += o.Qty; }
        Fills++;
        EmitOrder(o.Cid, o.Sell, o.Px, o.Qty, o.Id, "FILLED", 0, o.Qty, ns);
        EmitBalances(ns);
        Log?.Invoke($"paper: fill {(o.Sell ? "sell" : "buy")} {Qty(o.Qty)} @ {Px(o.Px)} → {_base} {Qty(_base8)}  {_quote} {Qty(_quote8)}");
    }

    // ------------------------------------------------------------------ wire shapes
    void EmitOrder(ulong cid, bool sell, long px8, long qty8, long id, string status, long rem, long exec, long ns)
    {
        Span<byte> c = stackalloc byte[17];
        Cid.Format(cid, c);
        Emit($"{{\"e\":\"orderUpdate\",\"E\":{ns},\"s\":\"{_symbol}\",\"i\":{id},\"c\":\"{Encoding.ASCII.GetString(c)}\"," +
             $"\"S\":\"{(sell ? "SELL" : "BUY")}\",\"X\":\"{status}\",\"p\":\"{Px(px8)}\",\"q\":\"{Px(qty8)}\"," +
             $"\"z\":\"{Px(rem)}\",\"Z\":\"{Px(exec)}\",\"L\":\"{Px(px8)}\",\"m\":true,\"T\":{ns}}}", ns);
    }

    void EmitBalances(long ns)
    {
        Emit($"{{\"e\":\"balanceUpdate\",\"E\":{ns},\"u\":{ns},\"B\":" +
             $"[{{\"a\":\"{_base}\",\"f\":\"{Px(_base8)}\",\"c\":\"{Px(_base8)}\"}}," +
             $"{{\"a\":\"{_quote}\",\"f\":\"{Px(_quote8)}\",\"c\":\"{Px(_quote8)}\"}}]}}", ns);
    }

    void Emit(string json, long ns)
    {
        if (_out.TryWrite(Encoding.UTF8.GetBytes(json), ns)) Sent++;
        else Dropped++;
    }

    static string Px(long x8)
    {
        Span<byte> b = stackalloc byte[32];
        int n = Fixed8.Format(x8, b);
        return Encoding.ASCII.GetString(b[..n]);
    }

    static string Qty(long x8) => (x8 / 1e8).ToString("0.########", CultureInfo.InvariantCulture);
}

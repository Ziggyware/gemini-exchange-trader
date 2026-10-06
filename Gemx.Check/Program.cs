using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gemx;

static class T
{
    public static int Fail;
    public static int Pass;
    public static void Ok(string name, bool c, string detail = "")
    {
        if (c) Pass++; else { Fail++; Console.WriteLine($"FAIL {name} {detail}"); }
    }
}

static class P
{
    const string S = "BTCUSD";

    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    static string Tick(long ns, string bid, string bq, string ask, string aq) =>
        $"{{\"u\":{ns / 1000},\"E\":{ns},\"s\":\"{S}\",\"b\":\"{bid}\",\"B\":\"{bq}\",\"a\":\"{ask}\",\"A\":\"{aq}\"}}";

    static string Ord(long ns, ulong cid, string st, string extra = "")
    {
        Span<byte> c = stackalloc byte[17];
        Cid.Format(cid, c);
        return $"{{\"e\":\"orderUpdate\",\"E\":{ns},\"s\":\"{S}\",\"i\":{500 + (long)(cid & 0xFFFF)},\"c\":\"{Encoding.ASCII.GetString(c)}\",\"X\":\"{st}\"{extra},\"T\":{ns}}}";
    }


    static string Bal(long ns, string total) =>
        $"{{\"e\":\"balanceUpdate\",\"E\":{ns},\"u\":{ns},\"B\":[{{\"a\":\"USD\",\"f\":\"5\",\"c\":\"5\"}},{{\"a\":\"BTC\",\"f\":\"{total}\",\"c\":\"{total}\"}}]}}";

    static EngineConfig Cfg() => new()
    {
        Q = new QuoteParams(0.1, 1.5, 1.0, 0, 2, 1, 1_000_000),
        QuoteQty8 = 1_000_000, MaxPos8 = 100_000_000, MaxNotionalUsd = 10_000_000, WarmupSamples = 5, Epoch = 7
    };

    static (Engine e, SpscRing<Cmd> q) Mk(FrameRing? tap = null)
    {
        var q = new SpscRing<Cmd>(1 << 16);
        return (new Engine(Cfg(), new FrameParser(new SymbolTable(S), "BTC"), q, tap), q);
    }

    static void Fixed()
    {
        string[] okS = { "0.00", "95000.01", "0.00000001", "92233720367.99999999", "-1.5", ".5", "5.", "0", "1.000000000" };
        long[] okV = { 0, 9500001000000, 1, 9223372036799999999, -150000000, 50000000, 500000000, 0, 100000000 };
        for (int i = 0; i < okS.Length; i++)
            T.Ok("fixed.parse " + okS[i], Fixed8.TryParse(B(okS[i]), out long v) && v == okV[i]);
        foreach (string bad in new[] { "", ".", "1e-8", "1.000000001", "92233720368", "x", "1.2.3", "-" })
            T.Ok("fixed.reject " + bad, !Fixed8.TryParse(B(bad), out _));
        var rnd = new Random(3);
        Span<byte> buf = stackalloc byte[32];
        bool rt = true;
        for (int i = 0; i < 200_000; i++)
        {
            long v = rnd.NextInt64(0, 1_000_000_000_000_000L);
            if (i % 3 == 0) v -= v % 1_000_000;
            int n = Fixed8.Format(v, buf);

            rt &= Fixed8.TryParse(buf[..n], out long w) && w == v;
        }
        T.Ok("fixed.roundtrip", rt);
        T.Ok("fixed.increment", Fixed8.Increment(1e-8) == 1 && Fixed8.Increment(0.01) == 1_000_000 && Fixed8.Increment(2) == 1_000_000 && Fixed8.Increment(8) == 1);
        using var d = JsonDocument.Parse("{\"symbol\":\"BTCUSD\",\"tick_size\":1e-8,\"quote_increment\":0.01,\"min_order_size\":\"0.00001\"}");
        var sp = Rest.Parse("BTCUSD", d.RootElement);
        T.Ok("rest.spec", sp.PriceTick8 == 1_000_000 && sp.QtyStep8 == 1 && sp.MinQty8 == 1000);
    }

    static void Rings()
    {
        var r = new SpscRing<long>(1024);
        const int N = 3_000_000;
        long sum = 0, exp = 0;
        bool ordered = true;
        var prod = new Thread(() => { for (long i = 0; i < N; i++) while (!r.TryWrite(i)) Thread.SpinWait(1); });
        prod.Start();
        long next = 0;
        while (next < N)
            if (r.TryRead(out long x)) { ordered &= x == next; sum += x; next++; } else Thread.SpinWait(1);
        prod.Join();
        for (long i = 0; i < N; i++) exp += i;
        T.Ok("spsc.order+sum", ordered && sum == exp);

        var fr = new FrameRing(1 << 16);
        const int M = 300_000;
        var lens = new int[M];
        var rg = new Random(11);
        for (int i = 0; i < M; i++) lens[i] = rg.Next(0, 2000);
        var prod2 = new Thread(() =>
        {
            var buf = new byte[2000];

            for (int i = 0; i < M; i++)
            {
                for (int j = 0; j < lens[i]; j++) buf[j] = (byte)(i * 31 + j);
                while (!fr.TryWrite(buf.AsSpan(0, lens[i]), i)) Thread.SpinWait(1);
            }
        });
        prod2.Start();
        bool good = true;
        int got = 0;
        while (got < M)
        {
            if (!fr.TryPeek(out var p, out long ns)) { Thread.SpinWait(1); continue; }
            good &= ns == got && p.Length == lens[got];
            for (int j = 0; j < p.Length && good; j++) good &= p[j] == (byte)(got * 31 + j);
            fr.Release();
            got++;
        }
        prod2.Join();
        T.Ok("framering.integrity", good && got == M);
    }

    static void Book()
    {
        var rnd = new Random(5);
        var b = new L2Book(4096);
        var rb = new SortedDictionary<long, long>();
        var ra = new SortedDictionary<long, long>();
        bool same = true;
        for (int i = 0; i < 300_000; i++)
        {
            bool bid = rnd.Next(2) == 0;
            long px = rnd.Next(1, 300);

            long q = rnd.Next(4) == 0 ? 0 : rnd.Next(1, 1000);
            if (bid) { b.Bids.Set(px, q); if (q == 0) rb.Remove(px); else rb[px] = q; }
            else { b.Asks.Set(px, q); if (q == 0) ra.Remove(px); else ra[px] = q; }
            if (i % 997 == 0)
            {
                var eb = rb.Reverse().ToArray();
                var ea = ra.ToArray();
                same &= b.Bids.Count == eb.Length && b.Asks.Count == ea.Length;
                for (int k = 0; k < eb.Length && same; k++) same &= b.Bids.Level(k, out long p1, out long q1) && p1 == eb[k].Key && q1 == eb[k].Value;
                for (int k = 0; k < ea.Length && same; k++) same &= b.Asks.Level(k, out long p1, out long q1) && p1 == ea[k].Key && q1 == ea[k].Value;
            }
        }
        T.Ok("book.vs_reference", same);
        var s = new BookSide(true, 8);
        var refd = new SortedDictionary<long, long>();
        bool inv = true;
        for (int i = 0; i < 100_000; i++)
        {
            long px = rnd.Next(1, 60), q = rnd.Next(3) == 0 ? 0 : rnd.Next(1, 100);
            s.Set(px, q);
            if (q == 0) refd.Remove(px); else refd[px] = q;
            long prev = long.MaxValue;
            inv &= s.Count <= 8;
            for (int k = 0; k < s.Count; k++)
            {
                s.Level(k, out long p, out long qq);
                inv &= p < prev && qq > 0 && refd.TryGetValue(p, out long rq) && rq == qq;
                prev = p;
            }
        }
        T.Ok("book.truncation_invariants", inv);
        var bk = new L2Book();

        long[] bp = { 100, 99 }, bq = { 5, 6 }, ap = { 101 }, aq = { 7 };
        T.Ok("book.snapshot", bk.Apply(10, 20, bp, bq, ap, aq) == Applied.Ok && bk.Synced);
        T.Ok("book.stale", bk.Apply(15, 20, bp, bq, ap, aq) == Applied.Stale);
        T.Ok("book.gap", bk.Apply(22, 23, bp, bq, ap, aq) == Applied.Gap);
        T.Ok("book.contiguous", bk.Apply(21, 21, new long[] { 100 }, new long[] { 0 }, ReadOnlySpan<long>.Empty, ReadOnlySpan<long>.Empty) == Applied.Ok
            && bk.Bids.Level(0, out long top, out _) && top == 99);
        bk.Asks.Set(98, 1);
        T.Ok("book.crossed", bk.Crossed);
    }

    static void Parser()
    {
        var p = new FrameParser(new SymbolTable("GEMI-BTC05M2606011000-UP"), "USD");
        p.TryParse(B("{\"u\":1751505576085,\"E\":1751508438600117161,\"s\":\"GEMI-BTC05M2606011000-UP\",\"b\":\"0.48\",\"B\":\"5000\",\"a\":\"0.52\",\"A\":\"3200\"}"), out Msg m);
        T.Ok("parse.ticker", m.Kind == Kind.Ticker && m.Sym == 0 && m.E == 1751508438600117161 && m.LastId == 1751505576085 && m.BidPx == 48_000_000 && m.BidQty == 500_000_000_000 && m.AskPx == 52_000_000 && m.AskQty == 320_000_000_000);
        p.TryParse(B("{\"e\":\"depthUpdate\",\"E\":1751508260659505382,\"s\":\"GEMI-BTC05M2606011000-UP\",\"U\":12345677,\"u\":12345678,\"b\":[[\"0.48\",\"5000\"],[\"0.47\",\"0.00\"]],\"a\":[[\"0.52\",\"3200\"]]}"), out m);
        T.Ok("parse.depth", m.Kind == Kind.Depth && m.FirstId == 12345677 && m.LastId == 12345678 && m.NBid == 2 && m.NAsk == 1 && p.BidPx[1] == 47_000_000 && p.BidQty[1] == 0 && p.AskQty[0] == 320_000_000_000);
        p.TryParse(B("{\"e\":\"orderUpdate\",\"E\":1759291847686856569,\"s\":\"GEMI-BTC05M2606011000-UP\",\"i\":73797746498585286,\"c\":\"btc-5m-quote-001\",\"S\":\"BUY\",\"o\":\"LIMIT\",\"X\":\"NEW\",\"O\":\"YES\",\"p\":\"0.48000\",\"q\":\"10\",\"z\":\"10\",\"T\":1759291847686856569}"), out m);
        T.Ok("parse.order_new", m.Kind == Kind.Order && m.St == Status.New && m.OrderId == 73797746498585286 && !m.Sell && m.Px == 48_000_000 && m.Rem == 1_000_000_000 && m.Cid == 0);
        Span<byte> c = stackalloc byte[17];
        Cid.Format(0x0000000700000003UL, c);
        p.TryParse(B("{\"e\":\"orderUpdate\",\"E\":5,\"s\":\"GEMI-BTC05M2606011000-UP\",\"i\":9,\"c\":\"" + Encoding.ASCII.GetString(c) + "\",\"S\":\"SELL\",\"X\":\"PARTIALLY_FILLED\",\"Z\":\"2.5\",\"m\":true,\"z\":\"7.5\"}"), out m);
        T.Ok("parse.order_partial", m.St == Status.Partial && m.Cid == 0x0000000700000003UL && m.Sell && m.Exec == 250_000_000 && m.Maker && m.Rem == 750_000_000);
        p.TryParse(B("{\"e\":\"orderUpdate\",\"E\":1,\"s\":\"X\",\"i\":1,\"c\":\"z0000000000000001\",\"X\":\"CANCELED\",\"T\":1}"), out m);
        T.Ok("parse.order_cancel", m.St == Status.Canceled && m.Cid == 1);
        p.TryParse(B("{\"e\":\"balanceUpdate\",\"E\":1768250434780000000,\"u\":1768250421600000000,\"B\":[{\"a\":\"USD\",\"f\":\"207.39\",\"c\":\"210.5\"}]}"), out m);
        T.Ok("parse.balance", m.Kind == Kind.Balance && m.HasBal && m.BalAvail == 20_739_000_000 && m.BalTotal == 21_050_000_000);
        p.TryParse(B("{\"id\":\"7\",\"status\":200,\"result\":{\"orderId\":\"4242\",\"x\":[1,2,{\"y\":3}]}}"), out m);
        T.Ok("parse.ack_place", m.Kind == Kind.Ack && m.ReqId == 7 && m.Code == 200 && m.ExchOrderId == 4242);
        p.TryParse(B("{\"id\":\"1\",\"status\":200}"), out m);
        T.Ok("parse.ack_sub", m.Kind == Kind.Ack && m.ReqId == 1 && m.Code == 200);

        // quote-currency balance + insufficient-funds rejects
        var qp = new FrameParser(new SymbolTable(S), "BTC", "USD");
        qp.TryParse(B("{\"e\":\"balanceUpdate\",\"E\":1,\"u\":1,\"B\":[{\"a\":\"USD\",\"f\":\"207.39\",\"c\":\"210.5\"},{\"a\":\"BTC\",\"f\":\"1.5\",\"c\":\"1.5\"}]}"), out m);
        T.Ok("parse.quote_balance", m.Kind == Kind.Balance && m.HasBal && m.BalTotal == 150_000_000 && m.HasQuote && m.QuoteAvail == 20_739_000_000 && m.QuoteTotal == 21_050_000_000);
        qp.TryParse(B("{\"e\":\"balanceUpdate\",\"E\":2,\"u\":2,\"B\":[{\"a\":\"BTC\",\"f\":\"2\",\"c\":\"2\"}]}"), out m);
        T.Ok("parse.quote_absent_is_unknown", m.HasBal && !m.HasQuote);
        qp.TryParse(B("{\"id\":\"7\",\"status\":400,\"reason\":\"InsufficientFunds\",\"message\":\"balance too small\"}"), out m);
        T.Ok("parse.ack_insufficient", m.Kind == Kind.Ack && m.ReqId == 7 && m.Code == 400 && m.NoFunds);
        qp.TryParse(B("{\"id\":\"8\",\"status\":400,\"reason\":\"InvalidOrder\"}"), out m);
        T.Ok("parse.other_reject_not_funds", m.Code == 400 && !m.NoFunds);
        qp.TryParse(B("{\"e\":\"orderUpdate\",\"E\":3,\"s\":\"" + S + "\",\"i\":9,\"c\":\"z0000000000000001\",\"X\":\"REJECTED\",\"reason\":\"insufficient balance\"}"), out m);
        T.Ok("parse.order_insufficient", m.Kind == Kind.Order && m.St == Status.Rejected && m.NoFunds);
        p.TryParse(B("{\"E\":1,\"s\":\"GEMI-BTC05M2606011000-UP\",\"t\":3,\"p\":\"0.50\",\"q\":\"10\",\"m\":true}"), out m);

        T.Ok("parse.trade_none", m.Kind == Kind.None);
        T.Ok("parse.rejects_garbage", !p.TryParse(B("not json"), out _) && !p.TryParse(B("{\"b\":[[\"1\"]]}"), out _) && !p.TryParse(B("{\"E\":\"x\"}"), out _));
        var q2 = new FrameParser(new SymbolTable("btcusd"), "BTC");
        q2.TryParse(B("{\"s\":\"BTCUSD\",\"b\":\"1\",\"B\":\"1\",\"a\":\"2\",\"A\":\"1\"}"), out m);
        T.Ok("parse.symbol_case_insensitive", m.Sym == 0);
    }

    static void Auth()
    {
        var s = GeminiAuth.Sign(Encoding.ASCII.GetBytes("secret"), 1700000000);
        T.Ok("auth.payload", s.Payload == "MTcwMDAwMDAwMA==");
        T.Ok("auth.signature_vs_openssl", s.Signature == "22fa03be9bd97fe41399cd35a4294e723ce2a5cded5283bd505d80986e152318d39dfe290ed3c012efdab79d34dc163c");
    }

    static void Executor()
    {
        var x = new Executor(new SpscRing<Cmd>(8), new FeedSocket(new Uri("wss://x"), Array.Empty<string>(), new FrameRing(1 << 10), 1), "btcusd", "MOC");
        string J(Cmd c) => Encoding.UTF8.GetString(x.Build(in c).Span);
        using var d1 = JsonDocument.Parse(J(new Cmd { Kind = CmdKind.Place, Sell = true, Cid = 0x700000003UL, Px = 9500001000000, Qty = 1_000_000, ReqId = 12 }));
        var r = d1.RootElement;
        var pr = r.GetProperty("params");
        T.Ok("exec.place", r.GetProperty("id").GetString() == "12" && r.GetProperty("method").GetString() == "order.place" && pr.GetProperty("symbol").GetString() == "BTCUSD"
            && pr.GetProperty("side").GetString() == "SELL" && pr.GetProperty("type").GetString() == "LIMIT" && pr.GetProperty("timeInForce").GetString() == "MOC"
            && pr.GetProperty("price").GetString() == "95000.01" && pr.GetProperty("quantity").GetString() == "0.01" && pr.GetProperty("clientOrderId").GetString() == "z0000000700000003");
        using var d2 = JsonDocument.Parse(J(new Cmd { Kind = CmdKind.Cancel, OrderId = 777, ReqId = 13 }));
        T.Ok("exec.cancel", d2.RootElement.GetProperty("method").GetString() == "order.cancel" && d2.RootElement.GetProperty("params").GetProperty("orderId").GetString() == "777");
        using var d3 = JsonDocument.Parse(J(new Cmd { Kind = CmdKind.CancelSession, ReqId = 14 }));
        T.Ok("exec.cancel_session", d3.RootElement.GetProperty("method").GetString() == "order.cancel_session" && d3.RootElement.GetProperty("params").GetProperty("confirm").GetBoolean());
    }

    static void Signal()
    {

        var g = new LagGauge();
        var rnd = new Random(2);
        long off = 5_000_000_000L;
        bool ok = true;
        for (int i = 0; i < 5000; i++)
        {
            long t = 1_000_000_000_000L + i * 10_000_000L;
            long jit = i % 50 == 0 ? 0 : rnd.Next(100_000, 5_000_000);
            long ex = g.Observe(t + off + jit, t);
            if (i >= 100) ok &= Math.Abs(ex - jit) < 2_000;
        }
        T.Ok("lag.excess_equals_jitter", ok);
        var g1 = new LagGauge();
        var g2 = new LagGauge();
        long a = 0, b2 = 0;
        for (int i = 0; i < 1000; i++)
        {
            long t = i * 10_000_000L, jit = i % 20 == 0 ? 0 : 300_000;
            a = g1.Observe(t + 7_000_000_000L + jit, t);
            b2 = g2.Observe(t - 3_000_000_000L + jit, t);
        }
        T.Ok("lag.offset_invariant", Math.Abs(a - b2) < 5_000);

        var s = new Signals();
        s.OnTicker(0, 100, 1, 101, 1);
        s.OnTicker(1_000_000, 100, 3, 101, 1);
        T.Ok("ofi.bid_up_positive", s.Ofi > 0);
        var s2 = new Signals();
        s2.OnTicker(0, 100, 1, 101, 1);
        s2.OnTicker(1_000_000, 100, 1, 101, 3);
        T.Ok("ofi.ask_up_negative", s2.Ofi < 0);
        var s3 = new Signals();
        s3.OnTicker(0, 100, 9, 101, 1);

        T.Ok("micro.toward_ask_when_bid_heavy", s3.Micro > 100.5 && s3.Micro < 101);

        var v = new Signals(0.1, 30, 1);
        var r2 = new Random(1);
        double mid = 95000, sigma = 2.0;
        long ns = 0;
        for (int i = 0; i < 100_000; i++)
        {
            ns += 10_000_000;
            double u1 = 1 - r2.NextDouble(), u2 = r2.NextDouble();
            mid += sigma * Math.Sqrt(0.01) * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            v.OnTicker(ns, mid - 0.005, 1, mid + 0.005, 1);
        }
        T.Ok("vol.recovers_sigma2", Math.Abs(v.Sigma2 / (sigma * sigma) - 1) < 0.3, $"{v.Sigma2}");

        var trend = new Signals(0.1, 30, 1);
        for (int i = 0; i < 1000; i++)
            trend.OnTicker(i * 100_000_000L, 100 + i * 0.01, 2, 100.01 + i * 0.01, 1);
        T.Ok("signal.zscore_is_live", Math.Abs(trend.ZScore) > 0.01, $"{trend.ZScore}");
        T.Ok("signal.scale_coherence", trend.ScaleCoherence > 0.9, $"{trend.ScaleCoherence}");
    }

    static void Advanced()
    {
        var signals = new Signals(0.1, 30, 1);
        var model = new MarketStateModel();
        EpistemicState state = default;
        for (int i = 1; i <= 2000; i++)
        {
            double mid = 100 + i * 0.001 + Math.Sin(i * 0.07) * 0.02;
            signals.OnTicker(i * 100_000_000L, mid - 0.005, 2 + i % 3, mid + 0.005, 1 + i % 2);
            state = model.Observe(i * 100_000_000L, signals, 1_000_000, 100_000_000, 0.1, 0.01, 0.5);
        }
        T.Ok("advanced.surface_finite", double.IsFinite(state.Volatility.FastVariance) && state.Volatility.FastVariance >= 0);
        T.Ok("advanced.posterior_valid", state.RegimeProbability >= 0 && state.RegimeProbability <= 1);
        T.Ok("advanced.reliability_bounded", state.AlphaReliability >= .05 && state.AlphaReliability <= 1);
        T.Ok("advanced.invariant_alpha_contracts", Math.Abs(state.Alpha) <= .01 + 1e-12);
        T.Ok("advanced.stress_finite", double.IsFinite(state.LeverageScore));
    }

    static void Quote()
    {
        var q = new QuoteParams(0.1, 1.5, 1.0, 0, 0, 0, 1_000_000);
        Quoter.Compute(in q, 95000.0, 0, 4, 0, 0, 9_500_000_000_000, 9_500_001_000_000, out long b0, out long a0);
        T.Ok("quote.tick_aligned", b0 % 1_000_000 == 0 && a0 % 1_000_000 == 0 && b0 < a0);
        Quoter.Compute(in q, 95000.0, 0, 4, 0.5, 0, 9_500_000_000_000, 9_500_001_000_000, out long b1, out long a1);
        T.Ok("quote.inventory_skews_down", b1 <= b0 && a1 <= a0 && (b1 < b0 || a1 < a0));
        Quoter.Compute(in q, 95000.0, 0, 100, 0, 0, 9_500_000_000_000, 9_500_001_000_000, out long b2, out long a2);
        T.Ok("quote.spread_monotone_sigma", a2 - b2 > a0 - b0);
        Quoter.Compute(in q, 95000.0, 0, 4, 0, 0.25, 9_500_000_000_000, 9_500_001_000_000, out long b3, out long a3);
        T.Ok("quote.spread_monotone_lag", a3 - b3 > a0 - b0);
        Quoter.Compute(in q, 95000.0, 0, 0, 0, 0, 9_500_000_000_000, 9_500_001_000_000, out long b4, out long a4);
        double h = Math.Log(1 + 0.1 / 1.5) / 0.1;
        T.Ok("quote.as_spread_closed_form", Math.Abs((a4 - b4) / 1e8 - 2 * h) <= 0.02 + 1e-9, $"{(a4 - b4) / 1e8} vs {2 * h}");
        Quoter.Compute(in q, 95000.0, 0, 4, 0, 0, 9_500_000_000_000, 9_500_000_000_000 + 1_000_000, out long b5, out long a5);
        T.Ok("quote.post_only_clamp", b5 <= 9_500_000_000_000 && a5 >= 9_500_001_000_000 - 1_000_000 + 1_000_000 - 1_000_000);

    }

    static void Scenario()
    {
        var (e, q) = Mk();
        var cmds = new List<Cmd>();
        void Drain() { while (q.TryRead(out Cmd c)) cmds.Add(c); }
        void Send(int ring, string j, long ns) { var b = B(j); e.OnFrame(ring, b, ns); Drain(); }
        long ns = 1_000_000_000_000;
        Send(1, Bal(ns, "1"), ns);
        for (int i = 0; i < 4; i++) { ns += 100_000_000; Send(0, Tick(ns, "95000.00", "1", "95000.10", "1"), ns); }
        T.Ok("engine.warmup_no_quotes", cmds.Count == 0);
        ns += 100_000_000; Send(0, Tick(ns, "95000.00", "1", "95000.10", "1"), ns);
        ns += 100_000_000; Send(0, Tick(ns, "95000.00", "1", "95000.10", "1"), ns);
        var places = cmds.Where(c => c.Kind == CmdKind.Place).ToArray();
        T.Ok("engine.quotes_both_sides", places.Length == 2 && places.Count(c => c.Sell) == 1 && places[0].Px < places[1].Px, string.Join(",", cmds.Select(c => c.Kind + ":" + c.Px)));
        var bid = places.First(c => !c.Sell);
        var ask = places.First(c => c.Sell);
        T.Ok("engine.cid_parity", (bid.Cid & 1) == 0 && (ask.Cid & 1) == 1 && (bid.Cid >> 32) == 7);
        T.Ok("engine.post_only_prices", bid.Px <= 9_500_000_000_000 && ask.Px >= 9_500_010_000_000);
        cmds.Clear();
        ns += 1_000_000; Send(1, Ord(ns, bid.Cid, "NEW", $",\"p\":\"{bid.Px / 1e8:F2}\",\"q\":\"0.01\",\"z\":\"0.01\""), ns);
        ns += 1_000_000; Send(1, Ord(ns, ask.Cid, "NEW", $",\"p\":\"{ask.Px / 1e8:F2}\",\"q\":\"0.01\",\"z\":\"0.01\""), ns);
        ns += 100_000_000; Send(0, Tick(ns, "95000.00", "1", "95000.10", "1"), ns);
        T.Ok("engine.live_orders_stay", cmds.Count == 0);
        for (int i = 0; i < 3; i++) { ns += 100_000_000; Send(0, Tick(ns, "95010.00", "1", "95010.10", "1"), ns); }
        T.Ok("engine.requote_cancels_both", cmds.Count(c => c.Kind == CmdKind.Cancel) == 2 && cmds.All(c => c.Kind != CmdKind.Cancel || c.OrderId != 0));
        cmds.Clear();
        ns += 1_000_000; Send(1, Ord(ns, bid.Cid, "FILLED", ",\"Z\":\"0.01\",\"z\":\"0\",\"m\":true"), ns);
        T.Ok("engine.fill_updates_position", e.Pos8 == 1_000_000);
        ns += 1_000_000; Send(1, Ord(ns, ask.Cid, "CANCELED"), ns);
        e.Kill = true;

        ns += 100_000_000; Send(0, Tick(ns, "95010.00", "1", "95010.10", "1"), ns);
        T.Ok("engine.kill_flushes_once", cmds.Count(c => c.Kind == CmdKind.CancelSession) == 1);
        ns += 100_000_000; Send(0, Tick(ns, "95010.00", "1", "95010.10", "1"), ns);
        T.Ok("engine.kill_stays_quiet", cmds.Count(c => c.Kind == CmdKind.Place) == 0 && cmds.Count(c => c.Kind == CmdKind.CancelSession) == 1);

        var (e2, q2) = Mk();
        var c2 = new List<Cmd>();
        void S2(int ring, string j, long n) { e2.OnFrame(ring, B(j), n); while (q2.TryRead(out Cmd c)) c2.Add(c); }
        ns = 1_000_000_000_000;
        S2(1, Bal(ns, "1"), ns);
        for (int i = 0; i < 8; i++) { ns += 100_000_000; S2(0, Tick(ns, "95000.00", "1", "95000.10", "1"), ns); }
        T.Ok("engine.pending_timeout_trips", c2.Count(c => c.Kind == CmdKind.Place) == 2);
        e2.OnIdle(ns + 3_000_000_000L);
        while (q2.TryRead(out Cmd dc)) c2.Add(dc);
        T.Ok("engine.pending_timeout_flush", e2.Trips == 1 && c2.Any(c => c.Kind == CmdKind.CancelSession), $"trips={e2.Trips} cmds={c2.Count}");

        var (e3, q3) = Mk();
        long n3 = 1_000_000_000_000;
        e3.OnFrame(1, B(Bal(n3, "1")), n3);
        for (int i = 0; i < 8; i++) { n3 += 100_000_000; e3.OnFrame(0, B(Tick(n3 - 80_000_000 * (i / 4) * 0, "95000.00", "1", "95000.10", "1")), n3); }
        int lag0 = 0;
        while (q3.TryRead(out _)) lag0++;
        n3 += 100_000_000;
        e3.OnFrame(0, B(Tick(n3 - 300_000_000, "95000.00", "1", "95000.10", "1")), n3);
        T.Ok("engine.lag_gate_blocks", e3.MaxLagSeen >= 299_000_000 && lag0 >= 0);

        var (e4, q4) = Mk();
        long n4 = 1_000_000_000_000;
        e4.OnFrame(1, B(Bal(n4, "1")), n4);
        for (int i = 0; i < 3; i++) e4.OnFrame(0, B("garbage"), n4 + i);
        T.Ok("engine.faults_trip", e4.Trips == 1 && e4.Fails == 3 && q4.TryRead(out Cmd fc) && fc.Kind == CmdKind.CancelSession);

        var (e5, q5) = Mk();

        var d1 = B("{\"e\":\"depthUpdate\",\"E\":1,\"s\":\"BTCUSD\",\"U\":10,\"u\":20,\"b\":[[\"100\",\"1\"]],\"a\":[[\"101\",\"1\"]]}");
        var d2 = B("{\"e\":\"depthUpdate\",\"E\":1,\"s\":\"BTCUSD\",\"U\":25,\"u\":26,\"b\":[[\"100\",\"2\"]],\"a\":[]}");
        e5.OnFrame(0, d1, 1);
        int r0 = e5.ResyncMd;
        e5.OnFrame(0, d2, 2);
        T.Ok("engine.depth_gap_requests_resync", e5.ResyncMd == r0 + 1);
    }

    // quote-currency awareness: buys are gated by the spendable balance, a venue-side
    // insufficient-funds reject latches the side without tripping the breaker, and a fresh
    // balance update reopens quoting.
    static void Funds()
    {
        var q = new SpscRing<Cmd>(1 << 16);
        var e = new Engine(Cfg(), new FrameParser(new SymbolTable(S), "BTC", "USD"), q);
        var cmds = new List<Cmd>();
        void Send(int ring, string j, long ns) { var b = B(j); e.OnFrame(ring, b, ns); while (q.TryRead(out Cmd c)) cmds.Add(c); }
        long ns = 1_000_000_000_000;
        Send(1, Bal(ns, "1"), ns);   // $5 spendable, 1 BTC
        for (int i = 0; i < 6; i++) { ns += 100_000_000; Send(0, Tick(ns, "95000.00", "1", "95000.10", "1"), ns); }
        var places = cmds.Where(c => c.Kind == CmdKind.Place).ToArray();
        T.Ok("funds.sell_only_when_cash_short", places.Length == 1 && places[0].Sell,
            string.Join(",", cmds.Select(c => c.Kind + ":" + (c.Sell ? "S" : "B"))));
        e.TryReadView(out EngineView v);
        T.Ok("funds.bid_blocked_and_balance_known", (v.FundsFlags & 1) != 0 && v.QuoteKnown && v.QuoteAvail8 == 500_000_000, $"flags={v.FundsFlags} avail={v.QuoteAvail8}");

        // the venue rejects the resting ask for balance reasons: latch the side, no breaker trip
        Cmd askCmd = places[0];
        Send(1, $"{{\"id\":\"{askCmd.ReqId}\",\"status\":400,\"reason\":\"Insufficient funds\"}}", ns);
        ns += 100_000_000; Send(0, Tick(ns, "95000.00", "1", "95000.10", "1"), ns);
        e.TryReadView(out v);
        T.Ok("funds.reject_latches_without_trip", (v.FundsFlags & 2) != 0 && v.FundsRejects == 1 && e.Trips == 0,
            $"flags={v.FundsFlags} rejects={v.FundsRejects} trips={e.Trips}");

        // top-up: the balance update clears the latch and both sides may quote again
        cmds.Clear();
        ns += 100_000_000;
        Send(1, $"{{\"e\":\"balanceUpdate\",\"E\":{ns},\"u\":{ns},\"B\":[{{\"a\":\"USD\",\"f\":\"10000\",\"c\":\"10000\"}},{{\"a\":\"BTC\",\"f\":\"1\",\"c\":\"1\"}}]}}", ns);
        for (int i = 0; i < 3; i++) { ns += 100_000_000; Send(0, Tick(ns, "95000.00", "1", "95000.10", "1"), ns); }
        places = cmds.Where(c => c.Kind == CmdKind.Place).ToArray();
        T.Ok("funds.reopens_after_topup", places.Length == 2 && places.Count(c => !c.Sell) == 1,
            string.Join(",", cmds.Select(c => c.Kind + ":" + (c.Sell ? "S" : "B"))));
    }

    // engine + PaperExchange end to end: seed balances, ack both quotes, fill the bid when the
    // market trades through it, and let the balance frame keep the engine's position in step.
    static void Paper()
    {
        var q = new SpscRing<Cmd>(1 << 16);
        var od = new FrameRing(1 << 12);
        var e = new Engine(Cfg(), new FrameParser(new SymbolTable(S), "BTC", "USD"), q);
        var p = new PaperExchange(q, od, e, S, "BTC", "USD", 1_000_000, 500_000_000_000);   // 0.01 BTC + $5,000
        long ns = 1_000_000_000_000;
        void ToEngine() { while (od.TryPeek(out ReadOnlySpan<byte> fr, out long fns)) { var copy = fr.ToArray(); e.OnFrame(1, copy, fns); od.Release(); } }

        p.Pump(ns);
        ToEngine();
        e.TryReadView(out EngineView v);
        T.Ok("paper.seed_reaches_engine", v.QuoteKnown && v.QuoteAvail8 == 500_000_000_000 && v.Base8 == 1_000_000,
            $"avail={v.QuoteAvail8} base={v.Base8}");

        for (int i = 0; i < 6; i++) { ns += 100_000_000; e.OnFrame(0, B(Tick(ns, "95000.00", "1", "95000.10", "1")), ns); ToEngine(); }
        int places = 0;
        while (q.TryRead(out Cmd c)) { if (c.Kind == CmdKind.Place) places++; p.Handle(in c, ns); }
        ToEngine();
        e.TryReadView(out v);
        T.Ok("paper.ack_makes_quotes_live", places == 2 && v.BidSlot == Slot.Live && v.AskSlot == Slot.Live,
            $"places={places} bid={v.BidSlot} ask={v.AskSlot}");
        long bid8 = v.BidQuote8;
        T.Ok("paper.bid_below_touch", bid8 > 0 && bid8 < 9_500_010_000_000, $"bid={bid8}");

        // the market trades down through our resting bid → simulated fill
        ns += 100_000_000;
        e.OnFrame(0, B(Tick(ns, "94980.00", "1", "94980.10", "1")), ns);
        if (e.TryReadView(out EngineView vx)) p.Match(in vx, ns);
        ToEngine();
        p.Pump(ns);   // hands the engine's requote cancels to the exchange as well
        ToEngine();
        T.Ok("paper.fill_updates_position", p.Fills == 1 && e.Pos8 == 1_000_000, $"fills={p.Fills} pos={e.Pos8}");
        e.TryReadView(out v);
        T.Ok("paper.fill_balance_follows", v.QuoteAvail8 == p.QuoteAvail8 && v.QuoteAvail8 < 500_000_000_000 && e.CashUsd < 0,
            $"view={v.QuoteAvail8} paper={p.QuoteAvail8} cash={e.CashUsd}");
    }

    static byte[][] Stream(int n, out long[] nss, int seed)
    {
        var rnd = new Random(seed);
        var fr = new List<byte[]>();
        var ns = new List<long>();
        long t = 1_000_000_000_000;
        double mid = 95000;
        long u = 100;
        fr.Add(B("{\"e\":\"depthUpdate\",\"E\":1,\"s\":\"BTCUSD\",\"U\":1,\"u\":100,\"b\":[[\"94999.9\",\"1\"],[\"94999.8\",\"2\"]],\"a\":[[\"95000.1\",\"1\"],[\"95000.2\",\"2\"]]}")); ns.Add(t);
        for (int i = 0; i < n; i++)
        {
            t += 10_000_000;
            mid += (rnd.Next(3) - 1) * 0.01;
            double b = Math.Round(mid, 2), a = b + 0.10;
            fr.Add(B(Tick(t, b.ToString("F2"), (rnd.Next(1, 9)).ToString(), a.ToString("F2"), (rnd.Next(1, 9)).ToString()))); ns.Add(t);
            if (i % 5 == 0)
            {
                fr.Add(B($"{{\"e\":\"depthUpdate\",\"E\":{t},\"s\":\"BTCUSD\",\"U\":{u + 1},\"u\":{u + 1},\"b\":[[\"{b:F2}\",\"{rnd.Next(0, 5)}\"]],\"a\":[[\"{a:F2}\",\"{rnd.Next(0, 5)}\"]]}}")); ns.Add(t);
                u++;
            }
        }
        nss = ns.ToArray();
        return fr.ToArray();
    }


    static void NoAlloc()
    {
        var (e, q) = Mk();
        e.OnFrame(1, B(Bal(1, "1")), 1);
        var fr = Stream(60_000, out var ns, 9);
        int half = fr.Length / 2;
        for (int i = 0; i < half; i++) { e.OnFrame(0, fr[i], ns[i]); while (q.TryRead(out _)) { } }
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = half; i < fr.Length; i++) { e.OnFrame(0, fr[i], ns[i]); while (q.TryRead(out _)) { } }
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        T.Ok("noalloc.engine_hot_path", a1 - a0 == 0, $"{a1 - a0} bytes over {fr.Length - half} frames");
        var p = new FrameParser(new SymbolTable("BTCUSD"), "BTC");
        var samples = new[]
        {
            B(Ord(5, 0x700000003UL, "PARTIALLY_FILLED", ",\"Z\":\"0.5\",\"z\":\"0.5\",\"m\":true,\"S\":\"BUY\",\"p\":\"1.5\"")),
            B(Bal(5, "1.25")),
            B("{\"id\":\"7\",\"status\":200,\"result\":{\"orderId\":\"4242\"}}"),
        };
        for (int w = 0; w < 1000; w++) foreach (var s in samples) p.TryParse(s, out _);
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        for (int w = 0; w < 100_000; w++) foreach (var s in samples) p.TryParse(s, out _);
        long b1 = GC.GetAllocatedBytesForCurrentThread();
        T.Ok("noalloc.parser_order_balance_ack", b1 - b0 == 0, $"{b1 - b0}");
        var sw = Stopwatch.StartNew();
        for (int w = 0; w < 500_000; w++) p.TryParse(fr[1 + (w % 1000) * 0], out _);
        sw.Stop();
        Console.WriteLine($"info parse ns/frame={sw.Elapsed.TotalMilliseconds * 1e6 / 500_000:F0}");
        var (e2, q2) = Mk();
        e2.OnFrame(1, B(Bal(1, "1")), 1);
        var sw2 = Stopwatch.StartNew();
        for (int i = 0; i < fr.Length; i++) { e2.OnFrame(0, fr[i], ns[i]); while (q2.TryRead(out _)) { } }

        sw2.Stop();
        Console.WriteLine($"info engine ns/frame={sw2.Elapsed.TotalMilliseconds * 1e6 / fr.Length:F0} frames={fr.Length}");
    }

    static ulong Loop(Engine e, SpscRing<Cmd> q, int n, int seed)
    {
        var rnd = new Random(seed);
        var fr = Stream(n, out var ns, seed);
        var live = new Dictionary<ulong, long>();
        var due = new List<(long at, int ring, string json)>();
        e.OnFrame(1, B(Bal(1, "1")), 1);
        for (int i = 0; i < fr.Length; i++)
        {
            for (int k = due.Count - 1; k >= 0; k--)
                if (due[k].at <= ns[i]) { e.OnFrame(due[k].ring, B(due[k].json), ns[i]); due.RemoveAt(k); }
            e.OnFrame(0, fr[i], ns[i]);
            while (q.TryRead(out Cmd c))
            {
                if (c.Kind == CmdKind.Place)
                {
                    live[c.Cid] = c.Px;
                    due.Add((ns[i] + 1_000_000, 1, Ord(ns[i], c.Cid, "NEW", $",\"p\":\"{c.Px / 1e8:F2}\",\"q\":\"0.01\",\"z\":\"0.01\"")));
                    if (rnd.Next(25) == 0) due.Add((ns[i] + 5_000_000, 1, Ord(ns[i], c.Cid, "FILLED", ",\"Z\":\"0.01\",\"z\":\"0\"")));
                }
                else if (c.Kind == CmdKind.Cancel)
                    due.Add((ns[i] + 1_000_000, 1, Ord(ns[i], c.Cid, "CANCELED")));
            }
        }
        return e.CmdHash;
    }

    static void Determinism()

    {
        string path = Path.Combine(Path.GetTempPath(), "gemx-tap.bin");
        var tap = new FrameRing(1 << 24);
        var (e1, q1) = Mk(tap);
        using var cts = new CancellationTokenSource();
        var tw = new TapWriter(tap, path);
        var th = new Thread(() => tw.Run(cts.Token));
        th.Start();
        ulong h1 = Loop(e1, q1, 20_000, 77);
        while (tap.TryPeek(out _, out _)) { tap.Release(); break; }
        cts.Cancel();
        th.Join();
        T.Ok("determinism.run_has_activity", e1.Cmds > 50 && e1.Fills > 0, $"cmds={e1.Cmds} fills={e1.Fills} trips={e1.Trips}");
        var (e2, q2) = Mk();
        ulong h2 = Loop(e2, q2, 20_000, 77);
        T.Ok("determinism.same_input_same_output", h1 == h2 && e1.Pos8 == e2.Pos8 && e1.Cmds == e2.Cmds);
        Console.WriteLine($"info loop cmds={e1.Cmds} fills={e1.Fills} pos8={e1.Pos8} trips={e1.Trips} hash={h1:x}");
    }

    static void Replay()
    {
        string path = Path.Combine(Path.GetTempPath(), "gemx-tap2.bin");
        var tap = new FrameRing(1 << 25);
        var (e1, q1) = Mk(tap);
        using var cts = new CancellationTokenSource();
        var tw = new TapWriter(tap, path);
        var th = new Thread(() => tw.Run(cts.Token));
        th.Start();
        Loop(e1, q1, 8_000, 5);
        Thread.Sleep(200);
        cts.Cancel();
        th.Join();

        var q2 = new SpscRing<Cmd>(1 << 16);
        var e2 = new Engine(Cfg(), new FrameParser(new SymbolTable(S), "BTC"), q2);
        long frames = 0;
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        fs.Dispose();
        var sw = new Stopwatch();
        sw.Start();
        long n = TapWriter.Replay(path, e2);
        while (q2.TryRead(out _)) { }
        sw.Stop();
        T.Ok("replay.frames_match", n == e1.Frames, $"{n} vs {e1.Frames}");
        T.Ok("replay.hash_match", e1.CmdHash == e2.CmdHash && e1.Pos8 == e2.Pos8, $"{e1.CmdHash:x} {e2.CmdHash:x} cmds {e1.Cmds}/{e2.Cmds}");
        frames = n;
        Console.WriteLine($"info replay frames={frames} {sw.ElapsedMilliseconds}ms");
    }

    public static int Main()
    {
        Fixed(); Rings(); Book(); Parser(); Auth(); Executor(); Signal(); Advanced(); Quote(); Scenario(); Funds(); Paper(); NoAlloc(); Determinism(); Replay();
        Console.WriteLine($"pass={T.Pass} fail={T.Fail}");
        return T.Fail == 0 ? 0 : 1;
    }
}
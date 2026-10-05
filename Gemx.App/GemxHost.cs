using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Gemx.App;

public sealed class GemxHost
{
    const long MdSubId = 900_000_001L, OrderSubId = 900_000_002L;

    public readonly Engine Engine;
    public readonly SymbolSpec Spec;
    public readonly ConcurrentQueue<string> Logs = new();
    public readonly int PriceDecimals, QtyDecimals;
    public readonly FeedSocket Md, Orders;
    public readonly Executor Exec;
    public volatile bool AuthRejected;

    readonly FrameRing _mdRing = new(1 << 24), _odRing = new(1 << 20);
    readonly SpscRing<Cmd> _cmds = new(1 << 12);
    readonly FrameRing? _tap;
    readonly TapWriter? _tapWriter;
    readonly CancellationTokenSource _cts = new(), _tapCts = new(), _ordersCts;
    readonly Thread _engineThread, _execThread;
    readonly Thread? _tapThread;
    readonly Task _mdTask, _orderTask;

    public static async Task<GemxHost> StartAsync(AppSettings s)
    {
        var ep = s.Endpoints();
        string key = (Environment.GetEnvironmentVariable("GEMX_API_KEY", EnvironmentVariableTarget.User) ?? "").Trim();
        string secret = (Environment.GetEnvironmentVariable("GEMX_API_SECRET", EnvironmentVariableTarget.User) ?? "").Trim();
        if (key.Length == 0 || secret.Length == 0) throw new InvalidOperationException("GEMX_API_KEY and GEMX_API_SECRET must be set in the environment of this process; restart the app after setting them");
        SymbolSpec spec;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            spec = await Rest.GetSpec(http, s.Symbol.Trim(), cts.Token, s.RestHost.Trim().TrimEnd('/'));
        EngineConfig cfg = s.ToConfig(spec);
        
        return new GemxHost(s, ep, spec, cfg, key, Encoding.ASCII.GetBytes(secret));
    }

    public EngineConfig Config => Engine.Config;
    static int Decimals(long step8)
    {
        int d = 8;
        while (d > 0 && step8 % 10 == 0) { step8 /= 10; d--; }
        return d;
    }

    GemxHost(AppSettings s, (Uri Md, string[] MdSubs, Uri Orders, string[] OrderSubs) ep, SymbolSpec spec, EngineConfig cfg, string key, byte[] secret)
    {
        Spec = spec;
        PriceDecimals = Decimals(spec.PriceTick8);
        QtyDecimals = Decimals(spec.QtyStep8);
        _ordersCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        if (s.RecordTap)
        {
            Directory.CreateDirectory(s.TapDirectory);
            _tap = new FrameRing(1 << 25);
            _tapWriter = new TapWriter(_tap, s.TapPath());
        }
        string symbol = s.Symbol.Trim();
        Engine = new Engine(cfg, new FrameParser(new SymbolTable(symbol), s.BaseAsset.Trim()), _cmds, _tap);
        Md = new FeedSocket(ep.Md, ep.MdSubs, _mdRing, MdSubId, null, () => Engine.ResyncMd) { Log = m => Log("md: " + m) };
        Orders = new FeedSocket(ep.Orders, ep.OrderSubs, _odRing, OrderSubId, o => GeminiAuth.Apply(o, key, secret)) { Log = OnOrdersLog };
        Exec = new Executor(_cmds, Orders, symbol, s.TimeInForce.Trim()) { Log = m => Log("exec: " + m) };

        CancellationToken ct = _cts.Token;
        _engineThread = new Thread(EngineLoop) { Name = "gemx-engine", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _execThread = new Thread(() => Exec.Run(ct)) { Name = "gemx-exec", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        if (_tapWriter != null) _tapThread = new Thread(() => _tapWriter.Run(_tapCts.Token)) { Name = "gemx-tap", IsBackground = true };

        _tapThread?.Start();
        _execThread.Start();
        _engineThread.Start();
        _mdTask = Task.Run(() => Md.RunAsync(ct));
        _orderTask = Task.Run(() => Orders.RunAsync(_ordersCts.Token));
        Log($"started {symbol} priceTick8={spec.PriceTick8} qtyStep8={spec.QtyStep8} minQty8={spec.MinQty8} epoch={cfg.Epoch} orders={ep.Orders}");
    }


    void OnOrdersLog(string m)
    {
        Log("orders: " + m);
        if (m.Contains("401") || m.Contains("403") ||
            m.Contains("InvalidNonce") || m.Contains("InvalidApiKey") ||
            m.Contains("AuthFailed"))
        {
            AuthRejected = true;
            _ordersCts.Cancel();
        }
    }

    public void Log(string m)
    {
        if (Logs.Count < 10_000) Logs.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {m}");
    }

    void EngineLoop()
    {
        CancellationToken ct = _cts.Token;
        var spin = new SpinWait();
        long nextIdle = 0;
        while (!ct.IsCancellationRequested)
        {
            bool a = _mdRing.TryPeek(out ReadOnlySpan<byte> pa, out long na);
            bool b = _odRing.TryPeek(out ReadOnlySpan<byte> pb, out long nb);
            if (a && (!b || na <= nb))
            {
                Engine.OnFrame(0, pa, na);
                _mdRing.Release();
                spin.Reset();
            }
            else if (b)
            {
                Engine.OnFrame(1, pb, nb);
                _odRing.Release();
                spin.Reset();
            }
            else
            {
                long now = Clock.NowNs();
                if (now >= nextIdle) { Engine.OnIdle(now); nextIdle = now + 1_000_000; }
                spin.SpinOnce(-1);
            }
        }
    }

    public async Task StopAsync()
    {
        bool nothingOnExchange = Exec.Sent == 0 && Orders.Current == null;
        Engine.Kill = true;
        bool flushed = false;
        if (!nothingOnExchange)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                if (Engine.TryReadView(out EngineView v) && v.KillSent && !v.FlushPending) { flushed = true; break; }
                await Task.Delay(20);
            }
        }
        if (flushed) Log("stop: session cancel acknowledged");
        else if (nothingOnExchange) Log("stop: no command was ever sent and the order socket is down; this session has no orders on the exchange");
        else Log("stop: session cancel NOT confirmed, verify open orders on the exchange");
        _cts.Cancel();
        await Task.WhenAny(Task.WhenAll(_mdTask, _orderTask), Task.Delay(3000));
        await Task.Run(() => { _execThread.Join(2000); _engineThread.Join(2000); });
        if (_tapThread != null)
        {
            _tapCts.Cancel();
            await Task.Run(() => _tapThread.Join(5000));
        }
        Log("stopped");
    }
}
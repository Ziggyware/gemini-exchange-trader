using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace Gemx.App;

public sealed class AppSettings
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gemx", "settings.json");

    [Category("Endpoints"), Description("Market-data WebSocket URI (ws:// or wss://).")]
    public string MdUri { get; set; } = "wss://ws.gemini.com";

    [Category("Endpoints"), Description("Comma-separated stream names for the market-data SUBSCRIBE. Must include {symbol}@bookTicker; {symbol} expands to the lower-cased Symbol. Optional: {symbol}@depth for the imbalance signal.")]
    public string MdSubs { get; set; } = "{symbol}@bookTicker,{symbol}@depth";

    [Category("Endpoints"), Description("Authenticated order WebSocket URI (ws:// or wss://).")]
    public string OrderUri { get; set; } = "wss://ws.gemini.com";

    [Category("Endpoints"), Description("Comma-separated stream names for the order-socket SUBSCRIBE. Must include orders@account and balances@account.")]
    public string OrderSubs { get; set; } = "orders@account,balances@account";

    [Category("Endpoints"), Description("Append cancelOnDisconnect=true to the order URI so the exchange cancels this session's orders when the connection drops.")]
    public bool CancelOnDisconnect { get; set; } = true;

    [Category("Endpoints"), Description("REST host used for /v1/symbols/details/{symbol}.")]
    public string RestHost { get; set; } = "https://api.gemini.com";

    [Category("Market"), Description("Trading symbol. Resolved by case-insensitive match against the 's' field.")]
    public string Symbol { get; set; } = "BTCUSD";

    [Category("Market"), Description("Base asset code matched against balance updates.")]
    public string BaseAsset { get; set; } = "BTC";

    [Category("Market"), Description("timeInForce value sent on order.place.")]
    public string TimeInForce { get; set; } = "MOC";

    [Category("Market"), Description("Quote asset code matched against balance updates for the funds check. Leave empty to derive it from Symbol by stripping BaseAsset (BTCUSD - BTC = USD).")]
    public string QuoteAsset { get; set; } = "";

    [Category("Session"), Description("Paper trading: quote against the live feed but fill locally. No orders leave this machine and no API keys are required.")]
    public bool PaperTrading { get; set; } = false;

    [Category("Session"), Description("Paper starting cash in quote currency. Buys are blocked once it is spent, exactly like a real balance.")]
    public double PaperCashUsd { get; set; } = 10000;

    [Category("Session"), Description("Paper starting base-asset holdings, in base units.")]
    public double PaperBaseQty { get; set; } = 0;

    [Category("Quoting"), Description("Risk aversion gamma (> 0).")]
    public double Gamma { get; set; } = 0.18;

    [Category("Quoting"), Description("Order-arrival decay k (> 0), in 1/price units.")]
    public double K { get; set; } = 1.8;

    [Category("Quoting"), Description("Inventory horizon T in seconds (> 0).")]
    public double HorizonSec { get; set; } = 0.75;

    [Category("Quoting"), Description("Drift coefficient applied to normalised order-flow imbalance, in price units.")]
    public double Alpha { get; set; } = 0.025;

    [Category("Quoting"), Description("Maker fee in basis points; sets the half-spread floor.")]
    public double MakerFeeBps { get; set; } = 0;

    [Category("Quoting"), Description("Minimum half-spread edge in price ticks.")]
    public double MinEdgeTicks { get; set; } = 2;

    [Category("Quoting"), Description("Cancel and replace when the target moves by at least this many ticks.")]
    public long RequoteTicks { get; set; } = 2;

    [Category("Quoting"), Description("Weight of top-of-book depth imbalance added to the drift (0 disables). Needs a depth stream in MdSubs.")]
    public double ImbalanceWeight { get; set; } = 0.02;

    [Category("Signal fusion"), Description("Weight of short/long EMA price momentum in the reservation-price ensemble.")]
    public double MomentumWeight { get; set; } = 0.12;

    [Category("Signal fusion"), Description("Contrarian weight applied to standardized short-interval return surprise.")]
    public double MeanReversionWeight { get; set; } = 0.08;

    [Category("Signal fusion"), Description("Weight of normalized order-flow acceleration.")]
    public double OfiAccelerationWeight { get; set; } = 0.01;

    [Category("Signal fusion"), Description("Hard cap on total directional displacement, in ticks. This invariant prevents signal conviction from bypassing execution risk.")]
    public double MaxSignalDriftTicks { get; set; } = 4;

    [Category("Signal fusion"), Description("Smooth robust-influence scale for normalized signals. Smaller values reject outliers more aggressively.")]
    public double RobustClipZ { get; set; } = 2.5;

    [Category("Signal fusion"), Description("Directional-confidence penalty for standardized jump surprise.")]
    public double JumpAttenuation { get; set; } = 0.5;

    [Category("Quoting"), Description("Book levels summed per side for the imbalance.")]
    public int ImbalanceLevels { get; set; } = 3;

    [Category("Quoting"), Description("Volatility grid samples required before quoting.")]
    public int WarmupSamples { get; set; } = 300;

    [Category("Risk"), Description("Quantity per quote, decimal base units. Must be a multiple of the symbol's quantity step and at least its minimum.")]
    public string QuoteQty { get; set; } = "0.0001";

    [Category("Risk"), Description("Absolute position limit in base units, measured from the balance at first balance update.")]
    public string MaxPosition { get; set; } = "0.001";

    [Category("Risk"), Description("Maximum notional per quote in USD.")]
    public long MaxNotionalUsd { get; set; } = 1000;

    [Category("Risk"), Description("Trip the breaker when feed lag excess exceeds this many milliseconds.")]
    public double MaxLagMs { get; set; } = 100;

    [Category("Risk"), Description("Assumed order round-trip time in milliseconds, added to the lag term in the spread.")]
    public double RttMs { get; set; } = 10;

    [Category("Recording"), Description("Record every frame to a tap file for deterministic replay.")]
    public bool RecordTap { get; set; } = false;

    [Category("Recording"), Description("Directory for tap-yyyyMMdd-HHmmss.bin files.")]
    public string TapDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gemx");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings() : new AppSettings();
        }
        catch (JsonException) { return new AppSettings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public string TapPath() => Path.Combine(TapDirectory, $"tap-{DateTime.Now:yyyyMMdd-HHmmss}.bin");

    /// Quote asset used for the funds check: explicit setting, else Symbol minus BaseAsset.
    public string ResolveQuoteAsset()
    {
        if (!string.IsNullOrWhiteSpace(QuoteAsset)) return QuoteAsset.Trim().ToUpperInvariant();
        string sym = Symbol.Trim().ToUpperInvariant(), b = BaseAsset.Trim().ToUpperInvariant();
        if (b.Length == 0 || sym.Length <= b.Length) return "";
        if (sym.StartsWith(b, StringComparison.Ordinal)) return sym[b.Length..];
        if (sym.EndsWith(b, StringComparison.Ordinal)) return sym[..^b.Length];
        return "";
    }

    public void ValidateSession()
    {
        if (PaperTrading && PaperCashUsd <= 0)
            throw new FormatException("PaperCashUsd must be > 0 when PaperTrading is on");
        if (PaperTrading && PaperBaseQty < 0)
            throw new FormatException("PaperBaseQty must be >= 0");
    }

    static readonly char[] Sep = { ',', ';', '\n' };

    static string[] Subs(string name, string text, string symbol)
    {
        string expanded = text.Replace("{symbol}", symbol.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        string[] a = expanded.Split(Sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (a.Length == 0) throw new FormatException($"{name}: at least one stream is required");
        foreach (string s in a)
            if (s.IndexOf('@') < 1 || s.EndsWith('@'))
                throw new FormatException($"{name}: '{s}' is not a stream name. Streams look like btcusd@bookTicker or orders@account; values such as depthUpdate are message event types, not streams");
        return a;
    }

    static void Require(string name, string[] subs, string stream)
    {
        if (!subs.Any(s => s.Equals(stream, StringComparison.OrdinalIgnoreCase)))
            throw new FormatException($"{name}: the engine requires the stream '{stream}'");
    }

    static Uri Ws(string name, string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? u) || (u.Scheme != "wss" && u.Scheme != "ws"))
            throw new FormatException($"{name}: '{text}' is not an absolute ws:// or wss:// URI");
        return u;
    }

    static long Q8(string name, string text)
    {
        if (!Fixed8.TryParse(Encoding.ASCII.GetBytes(text.Trim()), out long v) || v <= 0)
            throw new FormatException($"{name}: '{text}' is not a positive decimal with at most 8 fractional digits");
        return v;
    }

    public (Uri Md, string[] MdSubs, Uri Orders, string[] OrderSubs) Endpoints()
    {
        if (string.IsNullOrWhiteSpace(Symbol)) throw new FormatException("Symbol is required");
        if (string.IsNullOrWhiteSpace(BaseAsset)) throw new FormatException("BaseAsset is required");
        if (string.IsNullOrWhiteSpace(TimeInForce)) throw new FormatException("TimeInForce is required");
        if (!Uri.TryCreate(RestHost.Trim(), UriKind.Absolute, out Uri? r) || r.Scheme != "https")
            throw new FormatException($"RestHost: '{RestHost}' is not an https URI");
        string symbol = Symbol.Trim();
        string[] md = Subs(nameof(MdSubs), MdSubs, symbol);
        string[] od = Subs(nameof(OrderSubs), OrderSubs, symbol);
        Require(nameof(MdSubs), md, symbol.ToLowerInvariant() + "@bookTicker");
        Require(nameof(OrderSubs), od, "orders@account");
        Require(nameof(OrderSubs), od, "balances@account");
        Uri orders = Ws(nameof(OrderUri), OrderUri);
        if (CancelOnDisconnect && orders.Query.IndexOf("cancelOnDisconnect", StringComparison.OrdinalIgnoreCase) < 0)
        {
            var b = new UriBuilder(orders)
            {
                Query = orders.Query.Length == 0 ? "cancelOnDisconnect=true" : orders.Query.TrimStart('?') + "&cancelOnDisconnect=true"
            };
            orders = b.Uri;
        }
        return (Ws(nameof(MdUri), MdUri), md, orders, od);
    }

    public EngineConfig ToConfig(SymbolSpec spec)
    {
        long qty = Q8(nameof(QuoteQty), QuoteQty);
        long maxPos = Q8(nameof(MaxPosition), MaxPosition);
        if (qty < spec.MinQty8) throw new FormatException($"QuoteQty is below the symbol minimum ({spec.MinQty8} in 1e-8 units)");
        if (qty % spec.QtyStep8 != 0) throw new FormatException($"QuoteQty is not a multiple of the quantity step ({spec.QtyStep8} in 1e-8 units)");
        if (maxPos < qty) throw new FormatException("MaxPosition is smaller than QuoteQty");
        if (!(Gamma > 0) || !(K > 0) || !(HorizonSec > 0)) throw new FormatException("Gamma, K and HorizonSec must be > 0");
        if (MakerFeeBps < 0 || MinEdgeTicks < 0 || RequoteTicks < 1 || WarmupSamples < 1 || MaxNotionalUsd < 1 || MaxLagMs <= 0 || RttMs < 0 || ImbalanceLevels < 1
            || MomentumWeight < 0 || MeanReversionWeight < 0 || OfiAccelerationWeight < 0 || MaxSignalDriftTicks <= 0
            || RobustClipZ <= 0 || JumpAttenuation < 0)
            throw new FormatException("a risk, quoting, or signal-fusion parameter is out of range");
        return new EngineConfig
        {
            Q = new QuoteParams(Gamma, K, HorizonSec, Alpha, MakerFeeBps, MinEdgeTicks, spec.PriceTick8),
            QuoteQty8 = qty,
            MaxPos8 = maxPos,
            MaxNotionalUsd = MaxNotionalUsd,
            RequoteTicks = RequoteTicks,
            WarmupSamples = WarmupSamples,
            MaxLagNs = (long)(MaxLagMs * 1e6),
            RttSec = RttMs * 1e-3,
            ImbalanceWeight = ImbalanceWeight,
            MomentumWeight = MomentumWeight,
            MeanReversionWeight = MeanReversionWeight,
            OfiAccelWeight = OfiAccelerationWeight,
            MaxSignalDriftTicks = MaxSignalDriftTicks,
            RobustClipZ = RobustClipZ,
            JumpAttenuation = JumpAttenuation,
            ImbalanceLevels = ImbalanceLevels,
            Epoch = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0xFFFFFFFFL)
        };
    }
}
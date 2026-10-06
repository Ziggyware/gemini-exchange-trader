using System.Net.Http.Json;
using System.Text.Json;

namespace Gemx.App;

public static class CandleHistory
{
    static readonly HttpClient _http = new(){ Timeout = TimeSpan.FromSeconds(20) };

    // Gemini: GET /v2/candles/:symbol/:timeframe -> [[timestamp, open, high, low, close, volume],...]
    public static async Task<List<Candle>> FetchAsync(string symbol, string tfGemini, int limit = 500)
    {
        string url = $"https://api.gemini.com/v2/candles/{symbol.ToLowerInvariant()}/{tfGemini}";
        var doc = await _http.GetFromJsonAsync<JsonElement>(url);
        var list = new List<Candle>(doc.GetArrayLength());
        foreach(var el in doc.EnumerateArray())
        {
            // [ 1670000000000, 85943.38, 85955.35, 85920.1, 85943.39, 1.23 ]
            long tsMs = el[0].GetInt64();
            double o = el[1].GetDouble();
            double h = el[2].GetDouble();
            double l = el[3].GetDouble();
            double c = el[4].GetDouble();
            double v = el.GetArrayLength() > 5 ? el[5].GetDouble() : 0;
            list.Add(new Candle{ StartNs = tsMs * 1_000_000L, O=o, H=h, L=l, C=c, V=v, Ticks=1 });
        }
        list.Sort((a,b)=>a.StartNs.CompareTo(b.StartNs));
        if(list.Count>limit) list = list.TakeLast(limit).ToList();
        return list;
    }

    public static string ToGemini(TF tf) => tf switch
    {
        TF.M1 => "1m",
        TF.M5 => "5m",
        TF.M15 => "15m",
        TF.M30 => "30m",
        TF.H1 => "1hr",
        TF.D1 => "1day",
        TF.W1 => "1day", // we fetch daily then aggregate
        TF.M1_30D => "1day",
        _ => "1m"
    };

    public static List<Candle> AggregateWeek(List<Candle> daily)
    {
        var map = new SortedDictionary<long, List<Candle>>();
        foreach(var d in daily)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(d.StartNs/1_000_000L).UtcDateTime;
            // Monday as week start
            int diff = ((int)dt.DayOfWeek - 1 + 7) % 7;
            var weekStart = new DateTime(dt.Year, dt.Month, dt.Day).AddDays(-diff);
            long key = new DateTimeOffset(weekStart, TimeSpan.Zero).ToUnixTimeMilliseconds();
            if(!map.TryGetValue(key, out var ls)) map[key]=ls=new List<Candle>();
            ls.Add(d);
        }
        return map.Select(kv=> new Candle{
            StartNs = kv.Key*1_000_000L,
            O = kv.Value.First().O,
            H = kv.Value.Max(x=>x.H),
            L = kv.Value.Min(x=>x.L),
            C = kv.Value.Last().C,
            V = kv.Value.Sum(x=>x.V),
            Ticks = kv.Value.Count
        }).ToList();
    }

    public static List<Candle> AggregateMonth(List<Candle> daily)
    {
        var map = new SortedDictionary<(int y,int m), List<Candle>>();
        foreach(var d in daily)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(d.StartNs/1_000_000L).UtcDateTime;
            var key = (dt.Year, dt.Month);
            if(!map.TryGetValue(key, out var ls)) map[key]=ls=new List<Candle>();
            ls.Add(d);
        }
        return map.Select(kv=> new Candle{
            StartNs = new DateTimeOffset(kv.Key.y, kv.Key.m, 1, 0,0,0, TimeSpan.Zero).ToUnixTimeMilliseconds()*1_000_000L,
            O = kv.Value.First().O,
            H = kv.Value.Max(x=>x.H),
            L = kv.Value.Min(x=>x.L),
            C = kv.Value.Last().C,
            V = kv.Value.Sum(x=>x.V),
            Ticks = kv.Value.Count
        }).ToList();
    }
}
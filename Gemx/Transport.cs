using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gemx;

public static class Clock
{
    public static long NowNs() => (DateTime.UtcNow.Ticks - 621355968000000000L) * 100;
}

public static class GeminiAuth
{
    static long _last;

    public static (string Nonce, string Payload, string Signature) Sign(byte[] secret, long nonce)
    {
        string n = nonce.ToString(CultureInfo.InvariantCulture);
        string payload = Convert.ToBase64String(Encoding.ASCII.GetBytes(n));
        string sig = Convert.ToHexString(HMACSHA384.HashData(secret, Encoding.ASCII.GetBytes(payload))).ToLowerInvariant();
        return (n, payload, sig);
    }

    public static void Apply(ClientWebSocketOptions o, string key, byte[] secret)
    {
        long n = Math.Max(Interlocked.Read(ref _last) + 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        Interlocked.Exchange(ref _last, n);
        var s = Sign(secret, n);
        o.SetRequestHeader("X-GEMINI-APIKEY", key);
        o.SetRequestHeader("X-GEMINI-NONCE", s.Nonce);
        o.SetRequestHeader("X-GEMINI-PAYLOAD", s.Payload);
        o.SetRequestHeader("X-GEMINI-SIGNATURE", s.Signature);
    }
}

public sealed class FeedSocket
{
    readonly Uri _uri;
    readonly string[] _subs;
    readonly FrameRing _ring;
    readonly Action<ClientWebSocketOptions>? _auth;
    readonly Func<int>? _resync;
    readonly long _subId;
    public volatile ClientWebSocket? Current;
    public Action<string>? Log;

    public FeedSocket(Uri uri, string[] subs, FrameRing ring, long subId, Action<ClientWebSocketOptions>? auth = null, Func<int>? resync = null)
    {
        _uri = uri; _subs = subs; _ring = ring; _subId = subId; _auth = auth; _resync = resync;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        int backoff = 1100;
        while (!ct.IsCancellationRequested)
        {
            try
            {

                await Session(ct);
                backoff = 1100;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // tell the engine this feed is gone (matters when only the orders token was cancelled)
                Current = null;
                _ring.TryWrite(default, Clock.NowNs());
                break;
            }
            catch (Exception e) { Log?.Invoke(e.Message); }
            Current = null;
            while (!_ring.TryWrite(default, Clock.NowNs()) && !ct.IsCancellationRequested) await Task.Delay(1, CancellationToken.None);
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, 15000);
        }
    }

    async Task Session(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        _auth?.Invoke(ws.Options);
        await ws.ConnectAsync(_uri, ct);
        string sub = $"{{\"id\":\"{_subId}\",\"method\":\"SUBSCRIBE\",\"params\":[{string.Join(',', _subs.Select(s => '"' + s + '"'))}]}}";
        await ws.SendAsync(Encoding.UTF8.GetBytes(sub), WebSocketMessageType.Text, true, ct);
        Current = ws;
        int epoch = _resync?.Invoke() ?? 0;
        var buf = new byte[1 << 20];
        int len = 0;
        long t0 = 0;
        while (ws.State == WebSocketState.Open)
        {
            ValueWebSocketReceiveResult r = await ws.ReceiveAsync(buf.AsMemory(len), ct);
            if (len == 0) t0 = Clock.NowNs();
            if (r.MessageType == WebSocketMessageType.Close) break;
            len += r.Count;
            if (!r.EndOfMessage)
            {
                if (len == buf.Length) throw new InvalidOperationException("frame too large");
                continue;
            }
            if (len == 0) continue; // an empty frame in the ring means "reset"; never forward one from the wire
            if (!_ring.TryWrite(buf.AsSpan(0, len), t0)) throw new InvalidOperationException("ring overflow");
            len = 0;
            if (_resync != null && _resync() != epoch) throw new InvalidOperationException("resync requested");
        }
    }
}

public sealed class Executor
{
    readonly SpscRing<Cmd> _q;
    readonly FeedSocket _sock;
    readonly byte[] _sym;
    readonly byte[] _tif;
    readonly ArrayBufferWriter<byte> _bw = new(1024);
    readonly Utf8JsonWriter _w;
    public long Sent, Dropped;
    public Action<string>? Log;

    public Executor(SpscRing<Cmd> q, FeedSocket orderSocket, string symbol, string timeInForce = "MOC")
    {
        _q = q; _sock = orderSocket;
        _sym = Encoding.ASCII.GetBytes(symbol.ToUpperInvariant());
        _tif = Encoding.ASCII.GetBytes(timeInForce);
        _w = new Utf8JsonWriter(_bw);
    }

    public void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)

        {
            if (!_q.TryRead(out Cmd c)) {  continue; }
            Build(in c);
            ClientWebSocket? ws = _sock.Current;
            if (ws == null || ws.State != WebSocketState.Open) { Dropped++; continue; }
            try
            {
                ws.SendAsync(_bw.WrittenMemory, WebSocketMessageType.Text, true, ct).AsTask().GetAwaiter().GetResult();
                Sent++;
            }
            catch (Exception e) { Dropped++; Log?.Invoke(e.Message); }
        }
    }

    public ReadOnlyMemory<byte> Build(in Cmd c)
    {
        _bw.Clear();
        _w.Reset(_bw);
        Span<byte> num = stackalloc byte[32];
        Span<byte> cid = stackalloc byte[17];
        _w.WriteStartObject();
        Utf8Formatter.TryFormat(c.ReqId, num, out int n);
        _w.WriteString("id"u8, num[..n]);
        switch (c.Kind)
        {
            case CmdKind.Place:
                _w.WriteString("method"u8, "order.place"u8);
                _w.WriteStartObject("params"u8);
                _w.WriteString("symbol"u8, _sym);
                _w.WriteString("side"u8, c.Sell ? "SELL"u8 : "BUY"u8);
                _w.WriteString("type"u8, "LIMIT"u8);
                _w.WriteString("timeInForce"u8, _tif);

                n = Fixed8.Format(c.Px, num);
                _w.WriteString("price"u8, num[..n]);
                n = Fixed8.Format(c.Qty, num);
                _w.WriteString("quantity"u8, num[..n]);
                Cid.Format(c.Cid, cid);
                _w.WriteString("clientOrderId"u8, cid);
                _w.WriteEndObject();
                break;
            case CmdKind.Cancel:
                _w.WriteString("method"u8, "order.cancel"u8);
                _w.WriteStartObject("params"u8);
                Utf8Formatter.TryFormat(c.OrderId, num, out n);
                _w.WriteString("orderId"u8, num[..n]);
                _w.WriteEndObject();
                break;
            default:
                _w.WriteString("method"u8, "order.cancel_session"u8);
                _w.WriteStartObject("params"u8);
                _w.WriteBoolean("confirm"u8, true);
                _w.WriteEndObject();
                break;
        }
        _w.WriteEndObject();
        _w.Flush();
        return _bw.WrittenMemory;
    }
}

public sealed class TapWriter
{
    readonly FrameRing _ring;
    readonly string _path;


    public TapWriter(FrameRing ring, string path) { _ring = ring; _path = path; }

    public void Run(CancellationToken ct)
    {
        using var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        Span<byte> h = stackalloc byte[12];
        while (true)
        {
            if (_ring.TryPeek(out ReadOnlySpan<byte> p, out long ns))
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(h, p.Length);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(h[4..], ns);
                fs.Write(h);
                fs.Write(p);
                _ring.Release();
            }
            else if (ct.IsCancellationRequested) break;
            else Thread.Sleep(1);
        }
    }

    public static long Replay(string path, Engine e)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var h = new byte[12];
        var buf = new byte[1 << 21];
        long n = 0;
        while (fs.Read(h, 0, 12) == 12)
        {
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(h);
            long ns = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(4));

            fs.ReadExactly(buf, 0, len);
            e.OnFrame(buf[0], buf.AsSpan(1, len - 1), ns);
            n++;
        }
        return n;
    }
}

public readonly record struct SymbolSpec(string Symbol, long PriceTick8, long QtyStep8, long MinQty8);

public static class Rest
{
    public static async Task<SymbolSpec> GetSpec(HttpClient h, string symbol, CancellationToken ct, string host = "https://api.gemini.com")
    {
        await using var s = await h.GetStreamAsync($"{host}/v1/symbols/details/{symbol.ToUpperInvariant()}", ct);
        using var d = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        return Parse(symbol, d.RootElement);
    }

    public static SymbolSpec Parse(string symbol, JsonElement root)
    {
        static double Num(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.Parse(e.GetString()!, CultureInfo.InvariantCulture);
        long tick = Fixed8.Increment(Num(root.GetProperty("quote_increment")));
        long step = Fixed8.Increment(Num(root.GetProperty("tick_size")));
        JsonElement mo = root.GetProperty("min_order_size");
        string mos = mo.ValueKind == JsonValueKind.Number ? mo.GetRawText() : mo.GetString()!;
        Fixed8.TryParse(Encoding.ASCII.GetBytes(mos), out long min);
        return new SymbolSpec(symbol, tick, step, min);
    }
}
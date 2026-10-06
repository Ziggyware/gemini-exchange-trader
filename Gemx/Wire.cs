using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Gemx;

public enum Kind : byte { None, Ticker, Depth, Order, Balance, Ack }
public enum Status : byte { None, New, Open, Filled, Partial, Canceled, Rejected, Modified }

public struct Msg
{
    public Kind Kind;
    public int Sym;
    public long E, FirstId, LastId;
    public long BidPx, BidQty, AskPx, AskQty;
    public long OrderId;
    public ulong Cid;
    public Status St;
    public bool Sell, Maker;
    public long Px, Qty, Rem, Exec, LastPx;
    public long ReqId, ExchOrderId;
    public int Code;
    public long BalAvail, BalTotal;
    public bool HasBal;
    // quote-currency (e.g. USD) balance of the same update; only set when the frame carried it
    public long QuoteAvail, QuoteTotal;
    public bool HasQuote;
    // reject/ack text said the balance was too small to open the order
    public bool NoFunds;
    public int NBid, NAsk;
}

public static class Cid
{
    public static ulong Parse(ReadOnlySpan<byte> s)

    {
        if (s.Length != 17 || s[0] != (byte)'z') return 0;
        ulong v = 0;
        for (int i = 1; i < 17; i++)
        {
            int c = s[i];
            int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
            if (d < 0) return 0;
            v = (v << 4) | (uint)d;
        }
        return v;
    }

    public static void Format(ulong v, Span<byte> dst)
    {
        dst[0] = (byte)'z';
        for (int i = 16; i >= 1; i--)
        {
            uint d = (uint)(v & 15);
            dst[i] = (byte)(d < 10 ? '0' + d : 'a' + d - 10);
            v >>= 4;
        }
    }
}

public sealed class SymbolTable
{
    readonly byte[][] _n;
    public SymbolTable(params string[] names) => _n = names.Select(Encoding.ASCII.GetBytes).ToArray();

    public int Find(ReadOnlySpan<byte> s)
    {

        for (int i = 0; i < _n.Length; i++)
            if (Ascii.EqualsIgnoreCase(s, _n[i])) return i;
        return -1;
    }
}

public sealed class FrameParser
{
    readonly SymbolTable _syms;
    readonly byte[] _base;
    readonly byte[]? _quote;
    public readonly long[] BidPx, BidQty, AskPx, AskQty;

    public FrameParser(SymbolTable syms, string baseAsset, string? quoteAsset = null, int maxLevels = 1 << 15)
    {
        _syms = syms;
        _base = Encoding.ASCII.GetBytes(baseAsset);
        _quote = string.IsNullOrWhiteSpace(quoteAsset) ? null : Encoding.ASCII.GetBytes(quoteAsset.Trim());
        BidPx = new long[maxLevels];
        BidQty = new long[maxLevels];
        AskPx = new long[maxLevels];
        AskQty = new long[maxLevels];
    }

    static bool Int(ref Utf8JsonReader r, out long v)
    {
        v = 0;
        if (r.TokenType == JsonTokenType.Number) return r.TryGetInt64(out v);
        if (r.TokenType != JsonTokenType.String) return false;
        ReadOnlySpan<byte> s = r.ValueSpan;
        if (s.Length == 0 || s.Length > 18) return false;
        foreach (byte c in s)
        {
            uint d = (uint)(c - (byte)'0');

            if (d > 9) return false;
            v = v * 10 + d;
        }
        return true;
    }

    static bool Dec(ref Utf8JsonReader r, out long v)
    {
        v = 0;
        return (r.TokenType == JsonTokenType.String || r.TokenType == JsonTokenType.Number) && Fixed8.TryParse(r.ValueSpan, out v);
    }

    static int Levels(ref Utf8JsonReader r, long[] px, long[] qty)
    {
        int n = 0;
        while (r.Read() && r.TokenType == JsonTokenType.StartArray)
        {
            if (!r.Read() || !Dec(ref r, out long p)) return -1;
            if (!r.Read() || !Dec(ref r, out long q)) return -1;
            if (!r.Read() || r.TokenType != JsonTokenType.EndArray) return -1;
            if (n == px.Length) return -1;
            px[n] = p;
            qty[n] = q;
            n++;
        }
        return n;
    }

    bool Balances(ref Utf8JsonReader r, ref Msg m)
    {
        bool found = false;
        while (r.Read() && r.TokenType == JsonTokenType.StartObject)
        {
            bool match = false, qmatch = false;
            long f = 0, c = 0;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                ReadOnlySpan<byte> k = r.ValueSpan;
                r.Read();
                if (k.Length == 1 && k[0] == (byte)'a')
                {
                    if (r.TokenType == JsonTokenType.String)
                    {
                        match = Ascii.EqualsIgnoreCase(r.ValueSpan, _base);
                        qmatch = _quote != null && Ascii.EqualsIgnoreCase(r.ValueSpan, _quote);
                    }
                }
                else if (k.Length == 1 && k[0] == (byte)'f') { if (!Dec(ref r, out f)) return false; }
                else if (k.Length == 1 && k[0] == (byte)'c') { if (!Dec(ref r, out c)) return false; }
                else r.Skip();
            }
            if (match) { m.BalAvail = f; m.BalTotal = c; m.HasBal = true; found = true; }
            if (qmatch) { m.QuoteAvail = f; m.QuoteTotal = c; m.HasQuote = true; }
        }
        if (!found)
        {
            // zero balance - still valid
            m.BalAvail = 0;
            m.BalTotal = 0;
            m.HasBal = true;
        }
        // an update that never mentions the quote asset carries no information about it
        return true;
    }

    // case-insensitive "needle in haystack" over raw bytes; both sides are folded with the same
    // mask, so digits, spaces and punctuation compare exactly as written.
    static bool ContainsNoCase(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || hay.Length < needle.Length) return false;
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && (hay[i + j] | 0x20) == (needle[j] | 0x20)) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryParse(ReadOnlySpan<byte> f, out Msg m)
    {
        m = default;
        try
        {
            m.Sym = -1;
            var r = new Utf8JsonReader(f);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
            bool depth = false, order = false, bal = false, hasId = false, tb = false, ta = false;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                ReadOnlySpan<byte> k = r.ValueSpan;
                if (!r.Read()) return false;
                if (k.Length == 1)
                {
                    switch ((char)k[0])
                    {

                        case 'e':
                            if (r.TokenType == JsonTokenType.String)
                            {
                                if (r.ValueTextEquals("depthUpdate"u8)) depth = true;
                                else if (r.ValueTextEquals("orderUpdate"u8)) order = true;
                                else if (r.ValueTextEquals("balanceUpdate"u8)) bal = true;
                            }
                            break;
                        case 'E': if (!Int(ref r, out m.E)) return false; break;
                        case 'U': if (!Int(ref r, out m.FirstId)) return false; break;
                        case 'u': if (!Int(ref r, out m.LastId)) return false; break;
                        case 's': if (r.TokenType == JsonTokenType.String) m.Sym = _syms.Find(r.ValueSpan); break;
                        case 'i': if (!Int(ref r, out m.OrderId)) return false; break;
                        case 'c': if (r.TokenType == JsonTokenType.String) m.Cid = Cid.Parse(r.ValueSpan); break;
                        case 'S': m.Sell = r.TokenType == JsonTokenType.String && r.ValueSpan.Length > 0 && r.ValueSpan[0] == (byte)'S'; break;
                        case 'X':
                            if (r.TokenType == JsonTokenType.String && r.ValueSpan.Length > 0)
                                m.St = (char)r.ValueSpan[0] switch
                                {
                                    'N' => Status.New,
                                    'O' => Status.Open,
                                    'F' => Status.Filled,
                                    'P' => Status.Partial,
                                    'C' => Status.Canceled,
                                    'R' => Status.Rejected,
                                    'M' => Status.Modified,
                                    _ => Status.None
                                };
                            break;
                        case 'p': if (!Dec(ref r, out m.Px)) return false; break;
                        case 'q': if (!Dec(ref r, out m.Qty)) return false; break;
                        case 'z': if (!Dec(ref r, out m.Rem)) return false; break;
                        case 'Z': if (!Dec(ref r, out m.Exec)) return false; break;
                        case 'L': if (!Dec(ref r, out m.LastPx)) return false; break;
                        case 'm': m.Maker = r.TokenType == JsonTokenType.True; break;
                        case 'b':
                            if (r.TokenType == JsonTokenType.StartArray) { m.NBid = Levels(ref r, BidPx, BidQty); if (m.NBid < 0) return false; }
                            else { if (!Dec(ref r, out m.BidPx)) return false; tb = true; }

                            break;
                        case 'a':
                            if (r.TokenType == JsonTokenType.StartArray) { m.NAsk = Levels(ref r, AskPx, AskQty); if (m.NAsk < 0) return false; }
                            else { if (!Dec(ref r, out m.AskPx)) return false; ta = true; }
                            break;
                        case 'B':
                            if (r.TokenType == JsonTokenType.StartArray) { if (!Balances(ref r, ref m)) return false; }
                            else if (!Dec(ref r, out m.BidQty)) return false;
                            break;
                        case 'A': if (!Dec(ref r, out m.AskQty)) return false; break;
                        default: r.Skip(); break;
                    }
                }
                else if (k.SequenceEqual("id"u8)) { hasId = true; if (!Int(ref r, out m.ReqId)) return false; }
                else if (k.SequenceEqual("status"u8)) { if (Int(ref r, out long c)) m.Code = (int)c; else r.Skip(); }
                else if (k.SequenceEqual("reason"u8) || k.SequenceEqual("message"u8))
                {
                    // "Insufficient funds" / "InsufficientFunds" / "insufficient balance"...
                    if (r.TokenType == JsonTokenType.String && ContainsNoCase(r.ValueSpan, "insufficient"u8)) m.NoFunds = true;
                }
                else if (k.SequenceEqual("result"u8))
                {
                    if (r.TokenType == JsonTokenType.StartObject)
                    {
                        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                        {
                            ReadOnlySpan<byte> k2 = r.ValueSpan;
                            r.Read();
                            if (k2.SequenceEqual("orderId"u8)) { if (!Int(ref r, out m.ExchOrderId)) m.ExchOrderId = 0; }
                            else r.Skip();
                        }
                    }
                    else r.Skip();
                }
                else r.Skip();
            }
            m.Kind = depth ? Kind.Depth : order ? Kind.Order : bal ? Kind.Balance : hasId ? Kind.Ack : tb && ta ? Kind.Ticker : Kind.None;
        }
        catch { return false; }
        return true;
    }
}
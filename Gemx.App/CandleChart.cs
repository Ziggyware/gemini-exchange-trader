using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Gemx.App;

public enum TF { M1 = 60, M5 = 300, M15 = 900, M30 = 1800, H1 = 3600, D1 = 86400, W1 = 604800, M1_30D = 2592000 }

public struct Candle
{
    public long StartNs;
    public double O, H, L, C;
    public double V;
    public int Ticks;
}

public sealed class Aggregator
{
    static readonly TF[] Tfs = Enum.GetValues<TF>();

    readonly Dictionary<TF, List<Candle>> _books = new();
    readonly Dictionary<TF, Candle> _cur = new();

    public Aggregator()
    {
        foreach (TF tf in Tfs) { _books[tf] = new List<Candle>(500); _cur[tf] = new Candle(); }
    }

    public IReadOnlyList<Candle> Get(TF tf) => _books[tf];
    public Candle Current(TF tf) => _cur[tf];

    // W1 starts Monday 00:00 UTC and M1_30D starts on the 1st of the calendar month, matching CandleHistory.Aggregate*.
    static long StartNs(TF tf, long ns)
    {
        long sec = ns / 1_000_000_000L;
        if (tf == TF.W1)
        {
            long day = sec / 86400;               // epoch day 0 is a Thursday
            return (day - (day + 3) % 7) * 86400L * 1_000_000_000L;
        }
        if (tf == TF.M1_30D)
        {
            var d = DateTimeOffset.FromUnixTimeSeconds(sec);
            return new DateTimeOffset(d.Year, d.Month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds() * 1_000_000_000L;
        }
        long span = (long)tf * 1_000_000_000L;
        return ns / span * span;
    }

    public void LoadHistory(TF tf, List<Candle> hist)
    {
        var book = new List<Candle>(500);
        if (hist.Count > 0)
        {
            int n = hist.Count - 1;                // the newest history candle becomes the live slot, not a book entry
            int s = Math.Max(0, n - 500);
            book.AddRange(hist.GetRange(s, n - s));
            Candle last = hist[n];
            _cur.TryGetValue(tf, out Candle c);
            if (c.Ticks > 0 && c.StartNs > last.StartNs) book.Add(last);   // live data already moved past it
            else
            {
                if (c.Ticks > 0 && c.StartNs == last.StartNs)
                {
                    last.H = Math.Max(last.H, c.H);
                    last.L = Math.Min(last.L, c.L);
                    last.C = c.C;
                }
                _cur[tf] = last;
            }
        }
        _books[tf] = book;
    }

    public void Push(long recvNs, double micro)
    {
        if (micro <= 0) return;
        if (recvNs <= 0) recvNs = Gemx.Clock.NowNs();
        foreach (TF tf in Tfs)
        {
            long start = StartNs(tf, recvNs);
            ref Candle cur = ref CollectionsMarshal.GetValueRefOrAddDefault(_cur, tf, out _);
            if (cur.StartNs != start)
            {
                if (cur.StartNs != 0 && cur.Ticks > 0) { var b = _books[tf]; b.Add(cur); if (b.Count > 500) b.RemoveAt(0); }
                cur = new Candle { StartNs = start, O = micro, H = micro, L = micro, C = micro, Ticks = 1 };
            }
            else
            {
                if (cur.Ticks == 0) cur = new Candle { StartNs = start, O = micro, H = micro, L = micro, C = micro, Ticks = 1 };
                else { if (micro > cur.H) cur.H = micro; if (micro < cur.L) cur.L = micro; cur.C = micro; cur.Ticks++; }
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------
// One timeframe, one full size chart: candles, EMA, volume, crosshair, pan and zoom.
// ---------------------------------------------------------------------------------------------
internal sealed class CandleStrip : UiControl
{
    readonly TF _tf;
    IReadOnlyList<Candle> _hist = Array.Empty<Candle>();
    Candle _live;
    int _dec = 2;
    int _visible = 90;
    int _offset;
    bool _dragging;
    Point _dragStart;
    PointF _mouse = new(-1, -1);
    bool _over;
    bool _log;
    float _phase;

    const int MA = 20;

    public CandleStrip(TF tf)
    {
        _tf = tf;
        Surface = Pal.Card;
        MinimumSize = new Size(160, 110);
        MouseWheel += OnWheel;
        MouseEnter += (_, _) => { Focus(); _over = true; };
        MouseLeave += (_, _) => { _over = false; _mouse = new PointF(-1, -1); Invalidate(); };
        TabStop = true;
    }

    public void SetData(IReadOnlyList<Candle> hist, Candle live, int dec)
    {
        _hist = hist;
        _live = live;
        _dec = dec;
        Invalidate();
    }

    public void ResetView()
    {
        _visible = 90;
        _offset = 0;
        Invalidate();
    }

    // ---------------------------------------------------------------- interaction
    void OnWheel(object? s, MouseEventArgs e)
    {
        _visible = Math.Clamp(_visible + (e.Delta > 0 ? -10 : 10), 12, 300);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, Total - _visible));
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragStart = e.Location;
        Cursor = Cursors.SizeWE;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = new PointF(e.X, e.Y);
        if (_dragging)
        {
            int step = Math.Max(2, (int)(_dxSize));
            int dx = _dragStart.X - e.X;
            if (Math.Abs(dx) >= step)
            {
                int dir = Math.Sign(dx);
                _offset = Math.Clamp(_offset + dir * Math.Max(1, Math.Abs(dx) / step), 0, Math.Max(0, Total - _visible));
                _dragStart = e.Location;
            }
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        ResetView();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: _offset = Math.Clamp(_offset + 5, 0, Math.Max(0, Total - _visible)); break;
            case Keys.Right: _offset = Math.Clamp(_offset - 5, 0, Math.Max(0, Total - _visible)); break;
            case Keys.Home: _offset = Math.Max(0, Total - _visible); break;
            case Keys.End: _offset = 0; break;
            case Keys.OemPlus: case Keys.Add: _visible = Math.Clamp(_visible - 10, 12, 300); break;
            case Keys.OemMinus: case Keys.Subtract: _visible = Math.Clamp(_visible + 10, 12, 300); break;
            case Keys.L: _log = !_log; break;
            default: return;
        }
        e.Handled = true;
        Invalidate();
    }

    protected override bool Animate(float dt)
    {
        _phase += dt;
        return _over;
    }

    // ---------------------------------------------------------------- data helpers
    int Total => _hist.Count + (_live.Ticks > 0 ? 1 : 0);

    Candle At(int i) => i < _hist.Count ? _hist[i] : _live;

    float _dxSize = 6;

    // ---------------------------------------------------------------- painting
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float headH = S(21), axisH = S(15), gutter = S(76);
        var head = new RectangleF(0, 0, Width, headH);
        float bodyTop = headH + S(2);
        float bodyBottom = Height - axisH;
        var area = new RectangleF(S(4), bodyTop, Math.Max(20, Width - S(6) - gutter), Math.Max(20, bodyBottom - bodyTop));

        int total = Total;
        if (total == 0)
        {
            DrawFrame(g, area);
            g.DrawString(Fmt.Tf(_tf) + " — no ticks yet", Fonts.UiSmall, Cache.Brush(Pal.TextFaint), new RectangleF(area.X, area.Y, area.Width, area.Height), Gfx.SfCenter);
            DrawHeader(g, head, 0, 0, 0, 0, 0);
            return;
        }

        int visible = Math.Min(_visible, total);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, total - visible));
        int start = Math.Max(0, total - visible - _offset);
        int end = Math.Min(total, start + visible);
        int count = Math.Max(1, end - start);
        _dxSize = area.Width / count;

        // window extremes
        double hi = double.MinValue, lo = double.MaxValue, maxVol = 0;
        bool hasVol = false;
        for (int i = start; i < end; i++)
        {
            Candle c = At(i);
            if (c.H > hi) hi = c.H;
            if (c.L < lo) lo = c.L;
            if (c.V > maxVol) maxVol = c.V;
            if (c.V > 0) hasVol = true;
        }
        if (hi < lo) { hi = lo + 1; }
        double pad = (hi - lo) * 0.08;
        if (pad <= 0) pad = Math.Max(1e-6, hi * 0.0005);
        double yLo = lo - pad, yHi = hi + pad;
        if (_log && yLo <= 0) _log = false;

        float volH = hasVol ? Math.Min(area.Height * 0.22f, S(70)) : 0;
        var price = new RectangleF(area.X, area.Y, area.Width, Math.Max(20, area.Height - volH - (hasVol ? S(6) : 0)));
        var vol = new RectangleF(area.X, price.Bottom + S(6), area.Width, volH);

        DrawFrame(g, area);
        DrawGrid(g, price, yLo, yHi, gutter);
        DrawVolume(g, vol, start, end, maxVol, price, yLo, yHi);

        float step = price.Width / count;
        float bw = Math.Clamp(step * 0.66f, 1.5f, S(22));

        // EMA of closes, oldest to newest
        DrawEma(g, price, start, end, yLo, yHi, step);

        for (int i = start; i < end; i++)
        {
            Candle c = At(i);
            float x = price.Right - (end - 1 - i) * step - step / 2f;
            float yo = Yp(price, c.O, yLo, yHi), yc = Yp(price, c.C, yLo, yHi);
            float yh = Yp(price, c.H, yLo, yHi), yl = Yp(price, c.L, yLo, yHi);
            bool up = c.C >= c.O;
            Color ink = up ? Pal.UpLit : Pal.DownLit;
            Color dim = up ? Pal.Up : Pal.Down;

            bool hovered = _over && _mouse.X >= x - step / 2f && _mouse.X < x + step / 2f;
            if (hovered) g.FillRectangle(Cache.Brush(Pal.Alpha(Pal.Hover, 70)), x - step / 2f, price.Y, step, price.Height);

            using (var wick = new Pen(Pal.Alpha(dim, 235), Math.Max(1f, bw * 0.12f)))
                g.DrawLine(wick, x, yh, x, yl);

            float top = Math.Min(yo, yc), bh = Math.Max(S(1.6f), Math.Abs(yo - yc));
            var body = new RectangleF(x - bw / 2f, top, bw, bh);
            using (GraphicsPath bp = Gfx.Round(body, Math.Min(S(1.6f), bw * 0.22f)))
            {
                Grad.FillPathV(g, bp, body, Pal.Mix(ink, Color.Black, 0.05), Pal.Mix(ink, Color.Black, 0.35));
                using var edge = new Pen(Pal.Alpha(ink, 235), 1f);
                g.DrawPath(edge, bp);
            }

            // the candle still being built gets a breathing outline
            if (_live.Ticks > 0 && i == total - 1)
            {
                float a = 0.45f + 0.35f * (float)Math.Sin(_phase * 3.2f);
                using var live = new Pen(Pal.Alpha(Pal.TextHi, (int)(200 * a)), 1f) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(live, body.X - S(1.5f), body.Y - S(1.5f), body.Width + S(3), body.Height + S(3));
            }
        }

        // last price line and tag
        Candle lastC = At(total - 1);
        float ly = Yp(price, lastC.C, yLo, yHi);
        bool lastUp = lastC.C >= lastC.O;
        Color lastInk = lastUp ? Pal.UpLit : Pal.DownLit;
        using (var dash = Cache.DashPen(Pal.Alpha(lastInk, 120), 1f, DashStyle.Dash))
            g.DrawLine(dash, price.Left, ly, price.Right, ly);
        DrawPriceTag(g, new RectangleF(price.Right + S(1), ly - S(8), gutter - S(3), S(16)), lastInk, lastC.C.ToString("N" + _dec, Fmt.Inv));

        DrawTimeAxis(g, price, start, end, step);
        DrawHeader(g, head, start, end, yLo, yHi, maxVol);
        DrawCrosshair(g, price, start, end, step, yLo, yHi);
    }

    float Yp(RectangleF r, double v, double lo, double hi)
    {
        if (_log && v > 0 && lo > 0)
            return (float)(r.Bottom - (Math.Log(v) - Math.Log(lo)) / Math.Max(1e-9, Math.Log(hi) - Math.Log(lo)) * r.Height);
        double span = hi - lo;
        if (span <= 0) span = 1;
        return (float)(r.Bottom - (v - lo) / span * r.Height);
    }

    void DrawFrame(Graphics g, RectangleF area)
    {
        Gfx.FillRound(g, area, S(4), Color.FromArgb(12, 0, 0, 0));
        Gfx.StrokeRound(g, area, S(4), Pal.Alpha(Pal.LineSoft, 150));
    }

    void DrawGrid(Graphics g, RectangleF price, double lo, double hi, float gutter)
    {
        double step = Gfx.NiceStep(hi - lo, Math.Max(2, (int)(price.Height / S(30))));
        int first = (int)Math.Ceiling(lo / step);
        int last = (int)Math.Floor(hi / step);
        for (int i = first; i <= last; i++)
        {
            double v = i * step;
            float y = Yp(price, v, lo, hi);
            Gfx.HairH(g, y, price.Left, price.Right, Pal.Alpha(Pal.LineSoft, 115));
            Gfx.HairV(g, price.Right + S(1), y, y + S(3), Pal.Alpha(Pal.Line, 170));
            g.DrawString(v.ToString(HiPrec, Fmt.Inv), Fonts.MonoTiny, Cache.Brush(Pal.TextFaint),
                new RectangleF(price.Right + S(4), y - S(6), gutter, S(12)), Gfx.SfTop);
        }
    }

    string HiPrec => "N" + Math.Max(2, _dec);

    void DrawVolume(Graphics g, RectangleF vol, int start, int end, double maxVol, RectangleF price, double lo, double hi)
    {
        if (vol.Height < 8 || maxVol <= 0) return;
        Gfx.HairH(g, vol.Bottom + 0.5f, vol.Left, vol.Right, Pal.Alpha(Pal.LineSoft, 150));
        Gfx.Tracked(g, "VOL", Fonts.UiTiny, Pal.TextFaint, vol.X + S(2), vol.Y - S(1), 0.7f);
        g.DrawString(Fmt.Count((long)maxVol), Fonts.MonoTiny, Cache.Brush(Pal.TextFaint), vol, Gfx.SfTopRight);
        float step = vol.Width / Math.Max(1, end - start);
        float bw = Math.Clamp(step * 0.66f, 1.5f, S(22));
        for (int i = start; i < end; i++)
        {
            Candle c = At(i);
            if (c.V <= 0) continue;
            float x = vol.Right - (end - 1 - i) * step - step / 2f;
            float h = (float)(c.V / maxVol * (vol.Height - S(4)));
            var b = new RectangleF(x - bw / 2f, vol.Bottom - h, bw, Math.Max(1f, h));
            using GraphicsPath bp = Gfx.Round(b, Math.Min(S(1.4f), bw * 0.2f));
            g.FillPath(Cache.Brush(Pal.Alpha(c.C >= c.O ? Pal.Up : Pal.Down, 120)), bp);
        }
    }

    void DrawEma(Graphics g, RectangleF price, int start, int end, double lo, double hi, float step)
    {
        int n = _hist.Count + (_live.Ticks > 0 ? 1 : 0);
        if (n < MA + 2) return;
        double k = 2.0 / (MA + 1);
        double ema = 0;
        var pts = new List<PointF>(end - start + 2);
        for (int i = start; i < end; i++)
        {
            if (i == start)
            {
                double seed = 0;
                int from = Math.Max(0, start - MA);
                for (int j = from; j < start + 1; j++) seed += At(j).C;
                ema = seed / (start - from + 1);
            }
            else ema = At(i).C * k + ema * (1 - k);
            float x = price.Right - (end - 1 - i) * step - step / 2f;
            pts.Add(new PointF(x, Yp(price, ema, lo, hi)));
        }
        if (pts.Count < 2) return;
        using var pen = new Pen(Pal.Alpha(Pal.Violet, 190), 1.2f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts.ToArray());
        g.DrawString("EMA" + MA, Fonts.MonoTiny, Cache.Brush(Pal.Alpha(Pal.Violet, 200)), pts[^1].X + S(3), pts[^1].Y - S(6), Gfx.SfTop);
    }

    void DrawTimeAxis(Graphics g, RectangleF price, int start, int end, float step)
    {
        int every = Math.Max(1, (int)Math.Round(price.Width / S(90)));
        int idx = 0;
        for (int i = start; i < end; i++, idx++)
        {
            if (idx % every != 0) continue;
            float x = price.Right - (end - 1 - i) * step - step / 2f;
            Gfx.HairV(g, x, price.Bottom, price.Bottom + S(3), Pal.Alpha(Pal.Line, 150));
            g.DrawString(Stamp(At(i).StartNs), Fonts.MonoTiny, Cache.Brush(Pal.TextFaint),
                new RectangleF(x - S(34), price.Bottom + S(2), S(68), S(12)), Gfx.SfCenter);
        }
    }

    string Stamp(long ns)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000).UtcDateTime;
        return _tf switch
        {
            TF.M1 or TF.M5 or TF.M15 or TF.M30 or TF.H1 => t.ToString("HH:mm", Fmt.Inv),
            TF.D1 => t.ToString("dd MMM", Fmt.Inv),
            _ => t.ToString("MMM yy", Fmt.Inv)
        };
    }

    void DrawPriceTag(Graphics g, RectangleF r, Color ink, string text)
    {
        Gfx.FillRound(g, r, S(3), Pal.Mix(ink, Color.Black, 0.55));
        Gfx.StrokeRound(g, r, S(3), Pal.Alpha(ink, 210));
        g.FillRectangle(Cache.Brush(ink), r.X, r.Y + S(3), S(2), r.Height - S(6));
        g.DrawString(text, Fonts.MonoTiny, Cache.Brush(Pal.Mix(ink, Pal.TextHi, 0.5)), new RectangleF(r.X + S(5), r.Y, r.Width - S(7), r.Height), Gfx.SfMid);
    }

    void DrawHeader(Graphics g, RectangleF head, int start, int end, double lo, double hi, double maxVol)
    {
        float x = S(2);
        var badge = new RectangleF(x, head.Y + S(3), S(30), S(16));
        Gfx.FillRound(g, badge, S(4), Pal.Alpha(Pal.Accent, 40));
        Gfx.StrokeRound(g, badge, S(4), Pal.Alpha(Pal.Accent, 150));
        g.DrawString(Fmt.Tf(_tf), Fonts.MonoTiny, Cache.Brush(Pal.TextHi), badge, Gfx.SfCenter);
        x = badge.Right + S(8);

        if (end > start)
        {
            Candle c = At(end - 1);
            Candle prev = end - 1 > 0 ? At(end - 2) : c;
            bool up = c.C >= prev.C;
            Color ink = up ? Pal.UpLit : Pal.DownLit;
            string px = c.C.ToString("N" + _dec, Fmt.Inv);
            g.DrawString(px, Fonts.MonoMid, Cache.Brush(ink), x, head.Y + S(2), Gfx.SfTop);
            x += Gfx.Width(g, px, Fonts.MonoMid) + S(6);
            double chg = prev.C > 0 ? (c.C - prev.C) / prev.C * 100 : 0;
            string pct = (up ? "▲ " : "▼ ") + Math.Abs(chg).ToString("F2", Fmt.Inv) + "%";
            g.DrawString(pct, Fonts.MonoTiny, Cache.Brush(Pal.Alpha(ink, 220)), x, head.Y + S(6), Gfx.SfTop);
            x += Gfx.Width(g, pct, Fonts.MonoTiny) + S(10);

            // window stats
            string range = $"H {hi.ToString("N" + _dec, Fmt.Inv)}  L {lo.ToString("N" + _dec, Fmt.Inv)}  {Fmt.TfLong(_tf)}";
            g.DrawString(range, Fonts.MonoTiny, Cache.Brush(Pal.TextDim), x, head.Y + S(6), Gfx.SfTop);
            x += Gfx.Width(g, range, Fonts.MonoTiny) + S(10);
        }

        float right = head.Right - S(4);
        if (_log)
        {
            var tag = new RectangleF(right - S(34), head.Y + S(3), S(32), S(16));
            Gfx.FillRound(g, tag, S(4), Pal.Alpha(Pal.Violet, 60));
            Gfx.StrokeRound(g, tag, S(4), Pal.Alpha(Pal.Violet, 170));
            g.DrawString("LOG", Fonts.MonoTiny, Cache.Brush(Pal.Violet), tag, Gfx.SfCenter);
            right -= S(40);
        }
        if (_offset > 0 || _visible != 90)
        {
            var tag = new RectangleF(right - S(52), head.Y + S(3), S(50), S(16));
            Gfx.FillRound(g, tag, S(4), Pal.Alpha(Pal.Warn, 45));
            Gfx.StrokeRound(g, tag, S(4), Pal.Alpha(Pal.Warn, 150));
            g.DrawString("reset view", Fonts.MonoTiny, Cache.Brush(Pal.Warn), tag, Gfx.SfCenter);
            right -= S(58);
        }
        if (_over && x < right - S(60))
            g.DrawString("wheel zoom · drag pan · dbl reset · L log", Fonts.MonoTiny, Cache.Brush(Pal.TextFaint),
                new RectangleF(x, head.Y, right - x, head.Height), Gfx.SfMid);
    }

    void DrawCrosshair(Graphics g, RectangleF price, int start, int end, float step, double lo, double hi)
    {
        if (!_over || end <= start) return;
        float mx = Math.Clamp(_mouse.X, price.Left, price.Right);
        int idx = (int)Math.Floor((price.Right - mx) / Math.Max(0.5f, step));
        int i = Math.Clamp(end - 1 - idx, start, end - 1);
        Candle c = At(i);
        float cx = price.Right - (end - 1 - i) * step - step / 2f;
        float my = Math.Clamp(_mouse.Y, price.Top, price.Bottom);
        if (_mouse.Y < price.Top || _mouse.Y > price.Bottom) my = Yp(price, c.C, lo, hi);

        using (var dash = Cache.DashPen(Pal.Alpha(Pal.LineBright, 180), 1f, DashStyle.Dash))
        {
            g.DrawLine(dash, cx, price.Top, cx, price.Bottom);
            g.DrawLine(dash, price.Left, my, price.Right, my);
        }
        double pv = _log && lo > 0
            ? Math.Exp(Math.Log(lo) + (price.Bottom - my) / price.Height * (Math.Log(hi) - Math.Log(lo)))
            : lo + (price.Bottom - my) / price.Height * (hi - lo);
        DrawPriceTag(g, new RectangleF(price.Right + S(1), my - S(8), Math.Max(S(30), Width - price.Right - S(5)), S(16)), Pal.Accent, pv.ToString("N" + _dec, Fmt.Inv));

        string when = DateTimeOffset.FromUnixTimeMilliseconds(c.StartNs / 1_000_000).ToLocalTime().ToString("MMM dd HH:mm", Fmt.Inv);
        float tw = S(112);
        var tr = new RectangleF(Math.Clamp(cx - tw / 2f, 0, Math.Max(0, Width - tw)), price.Bottom + S(1), tw, S(13));
        Gfx.FillRound(g, tr, S(3), Pal.Mix(Pal.Accent, Color.Black, 0.45));
        g.DrawString(when, Fonts.MonoTiny, Cache.Brush(Pal.TextHi), tr, Gfx.SfCenter);

        string[][] rows =
        {
            new[] { "open", c.O.ToString("N" + _dec, Fmt.Inv) },
            new[] { "high", c.H.ToString("N" + _dec, Fmt.Inv) },
            new[] { "low", c.L.ToString("N" + _dec, Fmt.Inv) },
            new[] { "close", c.C.ToString("N" + _dec, Fmt.Inv) },
            new[] { "chg", (c.O > 0 ? (c.C - c.O) / c.O * 100 : 0).ToString("F2", Fmt.Inv) + "%" },
            new[] { "ticks", c.Ticks.ToString("N0", Fmt.Inv) },
            new[] { "volume", c.V > 0 ? Fmt.Count((long)c.V) : "-" }
        };
        float rowH = S(14.5f), cw = S(160), ch = rows.Length * rowH + S(8);
        float bx = cx + S(14);
        if (bx + cw > Width - S(6)) bx = cx - cw - S(14);
        bx = Math.Max(S(2), bx);
        float by = Math.Clamp(_mouse.Y - ch / 2f, price.Top + S(2), Math.Max(price.Top + S(2), price.Bottom - ch - S(2)));
        var card = new RectangleF(bx, by, cw, ch);
        Gfx.DropShadow(g, card, S(6), 95, S(3));
        Gfx.FillRound(g, card, S(6), Pal.Alpha(Pal.Mix(Pal.Bg, Color.Black, 0.25), 246));
        Gfx.StrokeRound(g, card, S(6), Pal.Alpha(Pal.Line, 205));
        for (int r = 0; r < rows.Length; r++)
        {
            float ry = card.Y + S(4) + r * rowH;
            g.DrawString(rows[r][0], Fonts.MonoTiny, Cache.Brush(Pal.TextDim), card.X + S(8), ry, Gfx.SfTop);
            Color ink = r switch { 1 => Pal.UpLit, 2 => Pal.DownLit, 3 => Pal.TextHi, _ => Pal.Text };
            g.DrawString(rows[r][1], Fonts.MonoTiny, Cache.Brush(ink), new RectangleF(card.X, ry, card.Width - S(8), S(12)), Gfx.SfTopRight);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Hosts one strip per timeframe and shows the selected one.
// ---------------------------------------------------------------------------------------------
internal sealed class CandleSet : UserControl
{
    public static readonly TF[] Order = { TF.M1, TF.M5, TF.M15, TF.M30, TF.H1, TF.D1, TF.W1, TF.M1_30D };

    readonly Aggregator _agg = new();
    readonly Dictionary<TF, CandleStrip> _strips = new();
    public Action<string>? Log;
    int _dec = 2;

    public CandleSet()
    {
        DoubleBuffered = true;
        BackColor = Pal.Card;
        foreach (TF tf in Order)
        {
            var s = new CandleStrip(tf) { Dock = DockStyle.Fill, Visible = false };
            _strips[tf] = s;
            Controls.Add(s);
        }
        _strips[TF.M1].Visible = true;
    }

    public TF Selected { get; private set; } = TF.M1;

    public void ShowTf(TF tf)
    {
        Selected = tf;
        foreach (var kv in _strips) kv.Value.Visible = kv.Key == tf;
    }

    public void ResetViews()
    {
        foreach (var kv in _strips) kv.Value.ResetView();
    }

    public async Task LoadHistoryAsync(string symbol, int dec)
    {
        _dec = dec;
        async Task<List<Candle>> Get(TF tf)
        {
            try { return await CandleHistory.FetchAsync(symbol, CandleHistory.ToGemini(tf)); }
            catch (Exception ex) { Log?.Invoke($"candles {tf}: {ex.Message}"); return new List<Candle>(); }
        }

        TF[] tfs = { TF.M1, TF.M5, TF.M15, TF.M30, TF.H1, TF.D1 };
        List<Candle>[] res = await Task.WhenAll(tfs.Select(Get));
        for (int i = 0; i < tfs.Length; i++) _agg.LoadHistory(tfs[i], res[i]);
        List<Candle> daily = res[5];
        _agg.LoadHistory(TF.W1, CandleHistory.AggregateWeek(daily));
        _agg.LoadHistory(TF.M1_30D, CandleHistory.AggregateMonth(daily));

        PushStrips();
    }

    public void Push(long recvNs, double micro, int dec)
    {
        _dec = dec;
        _agg.Push(recvNs, micro);
        PushStrips();
    }

    public void Clear()
    {
        foreach (var kv in _strips) kv.Value.SetData(Array.Empty<Candle>(), new Candle(), _dec);
    }

    void PushStrips()
    {
        foreach (var kv in _strips) kv.Value.SetData(_agg.Get(kv.Key), _agg.Current(kv.Key), _dec);
    }
}

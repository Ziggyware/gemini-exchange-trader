using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Gemx.App;

// ---------------------------------------------------------------------------------------------
// Flow chart. Best bid/ask, our live quotes, the intent behind them, micro price and feed lag.
// Prices are carried as 1e-8 fixed point exactly as the engine publishes them.
//
// X axis: sample index. The newest sample sits on the right edge and history scrolls left, so an
// early session fills in from the right like a real tape rather than stretching.
// ---------------------------------------------------------------------------------------------
internal sealed class QuoteChart : UiControl
{
    const int Cap = 600;

    readonly long[] _bb = new long[Cap];   // best bid
    readonly long[] _ba = new long[Cap];   // best ask
    readonly long[] _qb = new long[Cap];   // our live bid
    readonly long[] _qa = new long[Cap];   // our live ask
    readonly long[] _ib = new long[Cap];   // intended bid
    readonly long[] _ia = new long[Cap];   // intended ask
    readonly long[] _mc = new long[Cap];   // micro price
    readonly double[] _lag = new double[Cap];

    readonly List<(long Seq, long Px8, bool Sell)> _fills = new(64);
    readonly List<PointF> _pts = new(Cap + 2);
    readonly List<PointF> _top = new(Cap + 2);
    readonly List<PointF> _bot = new(Cap + 2);

    long _seq;
    int _head, _count, _dec = 2;
    long _tick8 = 1;
    double _maxLagMs = 100;

    int _visible = 300;
    int _offset;
    bool _dragging;
    int _dragX, _dragOffset;
    PointF _mouse = new(-1, -1);
    bool _over;

    RectangleF _plot, _lagRect;
    bool _hasLagPane;
    double _lo, _hi;
    int _start, _end, _newest;
    float _dx = 1;
    float _phase;

    public double SampleSeconds { get; set; } = 0.1;
    public int Dec { get => _dec; set => _dec = value; }
    public long Tick8 { get => _tick8; set => _tick8 = Math.Max(1, value); }
    public double MaxLagMs { get => _maxLagMs; set => _maxLagMs = Math.Max(1, value); }

    public QuoteChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Surface = Pal.Card;
        MinimumSize = new Size(280, 170);
        TabStop = true;
    }

    public void Reset(int priceDecimals)
    {
        _dec = priceDecimals;
        _head = 0;
        _count = 0;
        _seq = 0;
        _offset = 0;
        _visible = 300;
        _fills.Clear();
        Invalidate();
    }

    public void Push(long bestBid8, long bestAsk8, long quoteBid8, long quoteAsk8, long intendedBid8, long intendedAsk8, double micro, double lagMs)
    {
        _bb[_head] = bestBid8;
        _ba[_head] = bestAsk8;
        _qb[_head] = quoteBid8;
        _qa[_head] = quoteAsk8;
        _ib[_head] = intendedBid8;
        _ia[_head] = intendedAsk8;
        _mc[_head] = micro > 0 ? (long)Math.Round(micro * 1e8) : 0;
        _lag[_head] = lagMs;
        _head = (_head + 1) % Cap;
        if (_count < Cap) _count++;
        _seq++;
        while (_fills.Count > 0 && _seq - _fills[0].Seq > Cap) _fills.RemoveAt(0);
        Invalidate();
    }

    /// Marks a fill so it shows as a directional arrow on the price it printed at.
    /// Returns the view to the default zoom and window without discarding history.
    public void ResetView()
    {
        _visible = 300;
        _offset = 0;
        _dragOffset = 0;
        Invalidate();
    }

    public void MarkFill(bool sell, long px8)
    {
        _fills.Add((_seq, px8, sell));
        Invalidate();
    }

    int Index(int i) => ((_head - _count + i) % Cap + Cap) % Cap;

    int Window => Math.Min(Math.Max(60, _visible), Cap);

    // ---------------------------------------------------------------- interaction
    // take keyboard/wheel focus on hover: the candles pane above also listens for the wheel, and
    // WinForms routes it to the focused control
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _over = true; Focus(); }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _over = false;
        _mouse = new PointF(-1, -1);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = new PointF(e.X, e.Y);
        if (_dragging)
        {
            int step = Math.Max(4, (int)(_dx * 3));
            int delta = (int)((e.X - _dragX) / (float)step);
            if (delta != 0)
            {
                int maxOffset = Math.Max(0, _count - 2);
                int next = Math.Clamp(_dragOffset + delta, 0, maxOffset);
                if (next != _dragOffset)
                {
                    _dragOffset = next;
                    _dragX += delta * step;
                }
            }
        }
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !_plot.Contains(e.X, e.Y)) return;
        _dragging = true;
        _dragX = e.X;
        _dragOffset = _offset;
        Cursor = Cursors.SizeWE;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        _offset = _dragOffset;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        _visible = 300;
        _offset = 0;
        _dragOffset = 0;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _visible = Math.Clamp(_visible + (e.Delta > 0 ? -20 : 20), 60, Cap);
        Invalidate();
    }

    protected override bool Animate(float dt)
    {
        _phase += dt;
        return _over || _fills.Count > 0;
    }

    // ---------------------------------------------------------------- layout
    void Layout()
    {
        float pad = S(8);
        float top = S(20);
        float gutter = S(_dec > 4 ? 86 : 76);
        float bottom = S(17);
        float lagH = Height > S(230) ? S(46) : 0;
        _hasLagPane = lagH > 0;
        float plotBottom = Height - bottom - lagH - (lagH > 0 ? S(5) : 0);
        _plot = new RectangleF(pad, top, Math.Max(40, Width - pad - gutter), Math.Max(30, plotBottom - top));
        _lagRect = new RectangleF(_plot.X, _plot.Bottom + S(5), _plot.Width, lagH);
    }

    static float MapY(RectangleF r, double v, double lo, double hi)
    {
        double span = hi - lo;
        if (span <= 0) span = 1;
        return (float)(r.Bottom - (v - lo) / span * r.Height);
    }

    float X(int k) => _plot.Right - (_newest - k) * _dx;

    /// Rebuilds the visible window and the price range that fits it.
    void Project()
    {
        int window = Window;
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _count - 2));
        _newest = _count - 1 - _offset;
        _start = Math.Max(0, _newest - window + 1);
        _end = _newest + 1;
        _dx = _plot.Width / Math.Max(1f, window - 1f);

        double lo = double.MaxValue, hi = double.MinValue;
        for (int k = _start; k < _end; k++)
        {
            int i = Index(k);
            long[] all = { _bb[i], _ba[i], _qb[i], _qa[i], _ib[i], _ia[i] };
            foreach (long v in all)
            {
                if (v <= 0) continue;
                double d = v / 1e8;
                if (d < lo) lo = d;
                if (d > hi) hi = d;
            }
        }
        if (lo > hi) { lo = 0; hi = 1; }
        double span = hi - lo;
        if (span < 1e-9) span = Math.Max(1e-6, hi * 0.0005);
        _lo = lo - span * 0.10;
        _hi = hi + span * 0.10;
    }

    // ---------------------------------------------------------------- painting
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Layout();
        if (_count < 2) { DrawEmpty(g); return; }
        Project();

        DrawBackdrop(g);
        DrawSpreadBand(g);
        Series(g, _bb, Pal.Up, 1.2f, false, DashStyle.Solid, true);
        Series(g, _ba, Pal.Down, 1.2f, false, DashStyle.Solid, true);
        Series(g, _mc, Pal.Violet, 1f, false, DashStyle.Dot);
        Series(g, _ib, Pal.Alpha(Pal.UpLit, 130), 1f, true, DashStyle.Dash);
        Series(g, _ia, Pal.Alpha(Pal.DownLit, 130), 1f, true, DashStyle.Dash);
        Series(g, _qb, Pal.UpLit, 2.2f, true, DashStyle.Solid, true);
        Series(g, _qa, Pal.DownLit, 2.2f, true, DashStyle.Solid, true);
        DrawHeads(g);
        DrawFills(g);
        if (_hasLagPane) DrawLag(g);
        DrawLegend(g);
        DrawTags(g);
        DrawCrosshair(g);
    }

    void DrawEmpty(Graphics g)
    {
        Gfx.HairH(g, _plot.Bottom - 0.5f, _plot.Left, _plot.Right, Pal.LineSoft);
        Gfx.HairV(g, _plot.Left - 0.5f, _plot.Top, _plot.Bottom, Pal.LineSoft);
        using var big = new Font(Fonts.Mono.FontFamily, 30f, FontStyle.Bold);
        g.DrawString("GEMX", big, Cache.Brush(Color.FromArgb(16, 255, 255, 255)), _plot, Gfx.SfCenter);
        g.DrawString("awaiting the first tick — start a session to stream quotes", Fonts.UiSmall, Cache.Brush(Pal.TextFaint),
            new RectangleF(_plot.X, _plot.Y + _plot.Height / 2f + S(6), _plot.Width, S(16)), Gfx.SfCenter);
    }

    void DrawBackdrop(Graphics g)
    {
        Gfx.FillRound(g, _plot, S(4), Color.FromArgb(12, 0, 0, 0));
        Gfx.StrokeRound(g, _plot, S(4), Pal.Alpha(Pal.LineSoft, 160));

        double step = Gfx.NiceStep(_hi - _lo, Math.Max(2, (int)(_plot.Height / S(32))));
        int first = (int)Math.Ceiling(_lo / step);
        int last = (int)Math.Floor(_hi / step);
        for (int i = first; i <= last; i++)
        {
            double v = i * step;
            float y = MapY(_plot, v, _lo, _hi);
            Gfx.HairH(g, y, _plot.Left, _plot.Right, Pal.Alpha(Pal.LineSoft, 120));
            Gfx.HairV(g, _plot.Right + S(1), y, y + S(3), Pal.Alpha(Pal.Line, 170));
            g.DrawString(v.ToString("N" + _dec, Fmt.Inv), Fonts.MonoTiny, Cache.Brush(Pal.TextFaint),
                new RectangleF(_plot.Right + S(4), y - S(6), Width - _plot.Right - S(6), S(12)), Gfx.SfTop);
        }

        // one vertical rule every 10 seconds of samples
        int every = Math.Max(1, (int)Math.Round(10.0 / Math.Max(0.02, SampleSeconds)));
        for (int k = _newest; k >= _start; k -= every)
        {
            float x = X(k);
            if (x < _plot.Left) break;
            Gfx.HairV(g, x, _plot.Top, _plot.Bottom, Pal.Alpha(Pal.LineSoft, 95));
            double secs = (_newest - k) * SampleSeconds;
            string lab = secs < 0.05 ? "now" : "-" + secs.ToString("F0", Fmt.Inv) + "s";
            g.DrawString(lab, Fonts.MonoTiny, Cache.Brush(secs < 0.05 ? Pal.TextDim : Pal.TextFaint),
                new RectangleF(x - S(24), _plot.Bottom + S(2), S(48), S(12)), Gfx.SfCenter);
        }
        Gfx.HairH(g, _plot.Bottom + 0.5f, _plot.Left, _plot.Right, Pal.Alpha(Pal.Line, 150));
    }

    /// Shades the region between the best bid and the best ask — the market's own spread.
    void DrawSpreadBand(Graphics g)
    {
        bool run = false;
        for (int k = _start; k <= _end; k++)
        {
            bool ok = false;
            float bx = 0, by = 0, ay = 0;
            if (k < _end)
            {
                int i = Index(k);
                long bb = _bb[i], ba = _ba[i];
                if (bb > 0 && ba > bb)
                {
                    ok = true;
                    bx = X(k);
                    by = MapY(_plot, bb / 1e8, _lo, _hi);
                    ay = MapY(_plot, ba / 1e8, _lo, _hi);
                }
            }
            if (ok)
            {
                if (!run) { _top.Clear(); _bot.Clear(); run = true; }
                _top.Add(new PointF(bx, by));
                _bot.Add(new PointF(bx, ay));
            }
            else if (run) { FillRun(g); run = false; }
        }
        if (run) FillRun(g);
    }

    void FillRun(Graphics g)
    {
        if (_top.Count < 2) { _top.Clear(); _bot.Clear(); return; }
        using var path = new GraphicsPath();
        path.AddLines(_top.ToArray());
        var back = new PointF[_bot.Count];
        for (int i = 0; i < _bot.Count; i++) back[i] = _bot[_bot.Count - 1 - i];
        path.AddLines(back);
        path.CloseFigure();
        Grad.FillPathV(g, path, _plot, Pal.Alpha(Pal.Accent, 30), Pal.Alpha(Pal.Accent, 8));
        _top.Clear();
        _bot.Clear();
    }

    void Series(Graphics g, long[] a, Color c, float w, bool step, DashStyle dash, bool glow = false)
    {
        _pts.Clear();
        bool run = false;
        for (int k = _start; k < _end; k++)
        {
            long v = a[Index(k)];
            if (v <= 0)
            {
                if (run) { Stroke(g, c, w, dash, glow); _pts.Clear(); run = false; }
                continue;
            }
            float x = X(k);
            float y = MapY(_plot, v / 1e8, _lo, _hi);
            if (step && _pts.Count > 0) _pts.Add(new PointF(x, _pts[^1].Y));
            _pts.Add(new PointF(x, y));
            run = true;
        }
        if (run) Stroke(g, c, w, dash, glow);
        _pts.Clear();
    }

    void Stroke(Graphics g, Color c, float w, DashStyle dash, bool glow)
    {
        if (_pts.Count == 0) return;
        var pts = _pts.ToArray();
        if (glow && pts.Length > 2)
        {
            using var wide = new Pen(Pal.Alpha(c, 34), w * 4f) { LineJoin = LineJoin.Round };
            g.DrawLines(wide, pts);
        }
        using var pen = new Pen(c, w) { LineJoin = LineJoin.Round, DashStyle = dash, StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (pts.Length == 1) g.DrawLine(pen, pts[0].X - 1, pts[0].Y, pts[0].X + 1, pts[0].Y);
        else g.DrawLines(pen, pts);
    }

    /// Pulsing dots on the live end of our own quotes — the part of the chart that is us.
    void DrawHeads(Graphics g)
    {
        int i = Index(_newest);
        float pulse = 0.75f + 0.25f * (float)Math.Sin(_phase * 3.4f);
        if (_qb[i] > 0)
        {
            float y = MapY(_plot, _qb[i] / 1e8, _lo, _hi);
            Gfx.Dot(g, X(_newest), y, S(2.6f), Pal.UpLit, true);
            Gfx.Dot(g, X(_newest), y, S(1.5f) * pulse + S(1.2f), Pal.Alpha(Pal.UpLit, 120));
        }
        if (_qa[i] > 0)
        {
            float y = MapY(_plot, _qa[i] / 1e8, _lo, _hi);
            Gfx.Dot(g, X(_newest), y, S(2.6f), Pal.DownLit, true);
            Gfx.Dot(g, X(_newest), y, S(1.5f) * pulse + S(1.2f), Pal.Alpha(Pal.DownLit, 120));
        }
    }

    void DrawFills(Graphics g)
    {
        if (_fills.Count == 0) return;
        long baseSeq = _seq - _newest;
        foreach (var f in _fills)
        {
            long k = f.Seq - baseSeq;
            if (k < _start || k > _newest) continue;
            float x = X((int)k);
            float y = MapY(_plot, f.Px8 / 1e8, _lo, _hi);
            Color c = f.Sell ? Pal.DownLit : Pal.UpLit;
            float r = S(4.5f);
            using var tri = new GraphicsPath();
            if (f.Sell) tri.AddPolygon(new[] { new PointF(x, y + r), new PointF(x - r, y - r * 0.7f), new PointF(x + r, y - r * 0.7f) });
            else tri.AddPolygon(new[] { new PointF(x, y - r), new PointF(x - r, y + r * 0.7f), new PointF(x + r, y + r * 0.7f) });
            g.FillPath(Cache.Brush(Pal.Alpha(c, 235)), tri);
            using var pen = new Pen(Pal.Alpha(Pal.Bg, 210), 1f);
            g.DrawPath(pen, tri);
        }
    }

    void DrawLag(Graphics g)
    {
        if (_lagRect.Height < 12) return;
        var r = _lagRect;
        Gfx.FillRound(g, r, S(4), Color.FromArgb(9, 0, 0, 0));
        Gfx.StrokeRound(g, r, S(4), Pal.Alpha(Pal.LineSoft, 150));
        Gfx.Tracked(g, "FEED LAG", Fonts.UiTiny, Pal.TextFaint, r.X + S(6), r.Y + S(2), 0.7f);

        double hi = Math.Max(_maxLagMs * 1.5, 1);
        double peak = 0, last = 0;
        for (int k = _start; k < _end; k++)
        {
            double v = _lag[Index(k)];
            if (v > hi) hi = v;
            if (v > peak) peak = v;
        }
        last = _lag[Index(_newest)];

        _pts.Clear();
        for (int k = _start; k < _end; k++)
        {
            double v = _lag[Index(k)];
            float x = X(k);
            float y = (float)(r.Bottom - S(3) - Math.Min(1.0, v / hi) * (r.Height - S(14)));
            _pts.Add(new PointF(x, y));
        }
        if (_pts.Count > 1)
        {
            var line = _pts.ToArray();
            using var path = new GraphicsPath();
            path.AddLines(line);
            path.AddLine(line[^1].X, line[^1].Y, line[^1].X, r.Bottom - S(2));
            path.AddLine(line[^1].X, r.Bottom - S(2), line[0].X, r.Bottom - S(2));
            path.CloseFigure();
            Grad.FillPathV(g, path, r, Pal.Alpha(Pal.Warn, 76), Pal.Alpha(Pal.Warn, 4));
            using var pen = new Pen(Pal.Warn, 1.2f) { LineJoin = LineJoin.Round };
            g.DrawLines(pen, line);
            Gfx.Dot(g, line[^1].X, line[^1].Y, S(2f), last > _maxLagMs ? Pal.DownLit : Pal.UpLit, true);
        }

        float ty = (float)(r.Bottom - S(3) - Math.Min(1.0, _maxLagMs / hi) * (r.Height - S(14)));
        using (var dash = Cache.DashPen(Pal.Alpha(Pal.Danger, 200), 1f, DashStyle.Dash))
            g.DrawLine(dash, r.Left + S(2), ty, r.Right - S(2), ty);
        g.DrawString("max " + _maxLagMs.ToString("F0", Fmt.Inv) + " ms", Fonts.MonoTiny, Cache.Brush(Pal.Alpha(Pal.Danger, 215)),
            new RectangleF(r.Right - S(90), ty - S(11), S(88), S(11)), Gfx.SfTopRight);

        Color ink = last > _maxLagMs ? Pal.DownLit : last > _maxLagMs * 0.7 ? Pal.Warn : Pal.UpLit;
        string head = last.ToString("F2", Fmt.Inv) + " ms   peak " + peak.ToString("F1", Fmt.Inv) + " ms";
        g.DrawString(head, Fonts.MonoSmall, Cache.Brush(ink), new RectangleF(r.X + S(64), r.Y, r.Width - S(70), S(14)), Gfx.SfTopRight);
    }

    void DrawLegend(Graphics g)
    {
        int i = Index(_newest);
        long bb = _bb[i], ba = _ba[i];
        long spread = ba > bb && bb > 0 ? ba - bb : 0;
        double ticks = spread > 0 ? spread / (double)_tick8 : 0;
        double mid = bb > 0 && ba > bb ? (bb + ba) / 2.0 / 1e8 : 0;

        float x = _plot.Left + S(2);
        float y = S(2);

        // pane identity: the stacked layout shows this chart permanently, so it names itself
        Gfx.Tracked(g, "QUOTE FLOW", Fonts.UiTinyBold, Pal.TextHi, x, y + S(2.5f), 0.9f);
        x += Gfx.TrackedWidth(g, "QUOTE FLOW", Fonts.UiTinyBold, 0.9f) + S(12);

        (string Name, Color Ink, bool Dash)[] legend =
        {
            ("best bid", Pal.Up, false),
            ("best ask", Pal.Down, false),
            ("our bid", Pal.UpLit, false),
            ("our ask", Pal.DownLit, false),
            ("intent", Pal.Alpha(Pal.UpLit, 150), true),
            ("micro", Pal.Violet, false)
        };
        foreach (var (name, ink, dash) in legend)
        {
            var sw = new RectangleF(x, y + S(6.5f), S(11), S(2.4f));
            if (dash)
            {
                using var dp = Cache.DashPen(Pal.Alpha(ink, 230), 1.6f, DashStyle.Dash);
                g.DrawLine(dp, sw.X, sw.Y + 1, sw.Right, sw.Y + 1);
            }
            else g.FillRectangle(Cache.Brush(Pal.Alpha(ink, 235)), sw);
            g.DrawString(name, Fonts.MonoTiny, Cache.Brush(Pal.TextDim), x + S(14), y + S(2.5f), Gfx.SfTop);
            x += S(14) + Gfx.Width(g, name, Fonts.MonoTiny) + S(11);
            if (x > _plot.Right - S(160)) break;
        }

        string right = spread > 0
            ? $"spread {Fmt.Price(spread, _dec)}   {ticks:F0}t   mid {mid.ToString("N" + _dec, Fmt.Inv)}   {Window * SampleSeconds:F0}s window"
            : "no touch yet";
        g.DrawString(right, Fonts.MonoSmall, Cache.Brush(spread > 0 ? Pal.Text : Pal.TextDim),
            new RectangleF(_plot.Left, y, _plot.Width - S(2), S(14)), Gfx.SfTopRight);
    }

    /// Price tags in the right gutter, nudged so they never overlap.
    void DrawTags(Graphics g)
    {
        int i = Index(_newest);
        var tags = new List<(long Px, Color Ink, string Label)>(4);
        if (_bb[i] > 0) tags.Add((_bb[i], Pal.Up, "BID "));
        if (_ba[i] > 0) tags.Add((_ba[i], Pal.Down, "ASK "));
        if (_qb[i] > 0) tags.Add((_qb[i], Pal.UpLit, "OUR "));
        if (_qa[i] > 0) tags.Add((_qa[i], Pal.DownLit, "OUR "));
        if (tags.Count == 0) return;

        float w = Math.Max(S(34), Width - _plot.Right - S(6));
        var placed = new List<RectangleF>(4);
        foreach (var (px, ink, label) in tags)
        {
            float y = MapY(_plot, px / 1e8, _lo, _hi);
            var r = new RectangleF(_plot.Right + S(4), y - S(7), w, S(15));
            for (int guard = 0; guard < 40; guard++)
            {
                bool hit = false;
                foreach (var p in placed)
                    if (r.Top < p.Bottom + 1f && r.Bottom > p.Top - 1f) { r.Y = p.Bottom + 1.5f; hit = true; }
                if (!hit) break;
            }
            if (r.Bottom > _plot.Bottom + S(10)) r.Y = _plot.Bottom + S(10) - r.Height;
            if (r.Top < _plot.Top - S(8)) r = new RectangleF(r.X, _plot.Top - S(8), r.Width, r.Height);
            placed.Add(r);

            Gfx.FillRound(g, r, S(3), Pal.Mix(ink, Color.Black, 0.58));
            Gfx.StrokeRound(g, r, S(3), Pal.Alpha(ink, 200));
            g.FillRectangle(Cache.Brush(ink), r.X, r.Y + S(3), S(2), r.Height - S(6));
            g.DrawString(label + Fmt.Price(px, _dec), Fonts.MonoTiny, Cache.Brush(Pal.Mix(ink, Pal.TextHi, 0.5)),
                new RectangleF(r.X + S(6), r.Y, r.Width - S(8), r.Height), Gfx.SfMid);
            using var conn = Cache.DashPen(Pal.Alpha(ink, 80), 1f, DashStyle.Dot);
            g.DrawLine(conn, X(_newest), MapY(_plot, px / 1e8, _lo, _hi), r.X, r.Y + r.Height / 2f);
        }
    }

    void DrawCrosshair(Graphics g)
    {
        if (!_over || _count < 2) return;
        float mx = Math.Clamp(_mouse.X, _plot.Left, _plot.Right);
        float my = Math.Clamp(_mouse.Y, _plot.Top, _plot.Bottom);
        int k = (int)Math.Round((_plot.Right - mx) / Math.Max(0.001f, _dx));
        k = Math.Clamp(k, 0, _newest - _start);
        int sample = Math.Max(_start, _newest - k);
        float sx = X(sample);

        using (var dash = Cache.DashPen(Pal.Alpha(Pal.LineBright, 190), 1f, DashStyle.Dash))
        {
            g.DrawLine(dash, sx, _plot.Top, sx, _plot.Bottom + (_hasLagPane ? _lagRect.Height + S(5) : 0));
            g.DrawLine(dash, _plot.Left, my, _plot.Right, my);
        }
        double pv = _lo + (_plot.Bottom - my) / _plot.Height * (_hi - _lo);
        var pr = new RectangleF(_plot.Right + S(4), my - S(7), Math.Max(S(34), Width - _plot.Right - S(6)), S(15));
        Gfx.FillRound(g, pr, S(3), Pal.Mix(Pal.Accent, Color.Black, 0.45));
        g.DrawString(pv.ToString("N" + _dec, Fmt.Inv), Fonts.MonoTiny, Cache.Brush(Pal.TextHi), pr, Gfx.SfCenter);
        string tl = "-" + ((_newest - sample) * SampleSeconds).ToString("F1", Fmt.Inv) + "s";
        var tr = new RectangleF(sx - S(22), _plot.Bottom + S(2), S(44), S(13));
        Gfx.FillRound(g, tr, S(3), Pal.Mix(Pal.Accent, Color.Black, 0.45));
        g.DrawString(tl, Fonts.MonoTiny, Cache.Brush(Pal.TextHi), tr, Gfx.SfCenter);

        // readout card next to the cursor
        int i = Index(sample);
        long bb = _bb[i], ba = _ba[i], qb = _qb[i], qa = _qa[i], ib = _ib[i], ia = _ia[i], mc = _mc[i];
        var lines = new List<string[]>
        {
            new[] { "time", Fmt.TimeOfDay(DateTime.Now.AddSeconds(-(_newest - sample) * SampleSeconds)) },
            new[] { "best bid", bb > 0 ? Fmt.Price(bb, _dec) : "-" },
            new[] { "best ask", ba > 0 ? Fmt.Price(ba, _dec) : "-" },
            new[] { "spread", ba > bb && bb > 0 ? Fmt.Price(ba - bb, _dec) + "  " + ((ba - bb) / (double)_tick8).ToString("F0", Fmt.Inv) + "t" : "-" },
            new[] { "our bid", qb > 0 ? Fmt.Price(qb, _dec) : "-" },
            new[] { "our ask", qa > 0 ? Fmt.Price(qa, _dec) : "-" },
            new[] { "intent", (ib > 0 ? Fmt.Price(ib, _dec) : "-") + " / " + (ia > 0 ? Fmt.Price(ia, _dec) : "-") },
            new[] { "micro", mc > 0 ? Fmt.Price(mc, _dec) : "-" },
            new[] { "lag", _lag[i].ToString("F2", Fmt.Inv) + " ms" }
        };
        float rowH = S(14.5f);
        float maxH = _plot.Height - S(10);
        while (lines.Count > 3 && lines.Count * rowH + S(10) > maxH) lines.RemoveAt(lines.Count - 1);

        float cw = S(198);
        float ch = lines.Count * rowH + S(10);
        float cx = sx + S(14);
        if (cx + cw > _plot.Right) cx = sx - cw - S(14);
        cx = Math.Max(_plot.Left + S(2), cx);
        float cy0 = Math.Clamp(my + S(12), _plot.Top + S(2), Math.Max(_plot.Top + S(2), _plot.Bottom - ch - S(4)));
        var card = new RectangleF(cx, cy0, cw, ch);
        Gfx.DropShadow(g, card, S(6), 100, S(3));
        Gfx.FillRound(g, card, S(6), Pal.Alpha(Pal.Mix(Pal.Bg, Color.Black, 0.25), 246));
        Gfx.StrokeRound(g, card, S(6), Pal.Alpha(Pal.Line, 210));
        for (int l = 0; l < lines.Count; l++)
        {
            float ly = card.Y + S(5) + l * rowH;
            g.DrawString(lines[l][0], Fonts.MonoTiny, Cache.Brush(l == 0 ? Pal.TextFaint : Pal.TextDim), card.X + S(8), ly, Gfx.SfTop);
            g.DrawString(lines[l][1], Fonts.MonoTiny, Cache.Brush(l == 0 ? Pal.TextDim : Pal.TextHi),
                new RectangleF(card.X, ly, card.Width - S(8), S(12)), Gfx.SfTopRight);
        }
    }
}

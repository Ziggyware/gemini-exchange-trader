using System.Drawing.Drawing2D;

namespace Gemx.App;

internal struct DepthInput
{
    public int Dec;
    public long Tick8, BestBid8, BestAsk8, BestBidQty8, BestAskQty8, OurBid8, OurAsk8;
    public double Micro;
    public Slot BidSlot, AskSlot;
    public bool BookSynced;
}

// ---------------------------------------------------------------------------------------------
// Depth ladder. Levels come from the L2 book when a depth stream is configured; the top of book
// always comes from the ticker so the tool still reads correctly with only bookTicker.
// ---------------------------------------------------------------------------------------------
internal sealed class DepthLadder : UiControl
{
    const int MaxLevels = 12;

    readonly long[] _bPx = new long[MaxLevels], _bQty = new long[MaxLevels], _aPx = new long[MaxLevels], _aQty = new long[MaxLevels];
    readonly float[] _bAnim = new float[MaxLevels], _aAnim = new float[MaxLevels];
    int _levels = 5;
    DepthInput _in;
    long _maxQty;
    int _hover = -1;
    float _phase;
    float _highlight;           // row click flash
    int _highlightRow = -1;
    public Action<string>? CopyNote;

    public DepthLadder()
    {
        Surface = Pal.Card;
        Font = Fonts.Mono;
    }

    /// Requested depth. Wheel over the ladder raises or lowers it.
    public int Levels { get; set; } = 6;

    /// Copies the engine book into the ladder's own arrays; the engine thread mutates the book.
    public void ScanBook(L2Book? book)
    {
        int n = Math.Clamp(Levels, 2, MaxLevels);
        for (int i = 0; i < n; i++)
        {
            _bPx[i] = _bQty[i] = _aPx[i] = _aQty[i] = 0;
            if (book == null) continue;
            try
            {
                if (book.Bids.Level(i, out long bp, out long bq)) { _bPx[i] = bp; _bQty[i] = bq; }
                if (book.Asks.Level(i, out long ap, out long aq)) { _aPx[i] = ap; _aQty[i] = aq; }
            }
            catch
            {
                // a torn read of the live book: keep whatever we already copied
                break;
            }
        }
        _levels = n;
    }

    public void Update(in DepthInput input)
    {
        _in = input;
        // top of book always reflects the ticker, whether or not depth is streaming
        if (_in.BestBid8 > 0) { _bPx[0] = _in.BestBid8; _bQty[0] = _in.BestBidQty8; }
        if (_in.BestAsk8 > 0) { _aPx[0] = _in.BestAsk8; _aQty[0] = _in.BestAskQty8; }
        _maxQty = 1;
        for (int i = 0; i < MaxLevels; i++)
        {
            if (_bQty[i] > _maxQty) _maxQty = _bQty[i];
            if (_aQty[i] > _maxQty) _maxQty = _aQty[i];
        }
        Invalidate();
    }

    protected override bool Animate(float dt)
    {
        _phase += dt;
        bool dirty = false;
        for (int i = 0; i < MaxLevels; i++)
        {
            float tb = _maxQty <= 0 ? 0 : (float)(_bQty[i] / (double)_maxQty);
            float ta = _maxQty <= 0 ? 0 : (float)(_aQty[i] / (double)_maxQty);
            if (Math.Abs(_bAnim[i] - tb) > 0.0015f) { _bAnim[i] += (tb - _bAnim[i]) * Math.Min(1f, dt * 9f); dirty = true; }
            else if (_bAnim[i] != tb) { _bAnim[i] = tb; dirty = true; }
            if (Math.Abs(_aAnim[i] - ta) > 0.0015f) { _aAnim[i] += (ta - _aAnim[i]) * Math.Min(1f, dt * 9f); dirty = true; }
            else if (_aAnim[i] != ta) { _aAnim[i] = ta; dirty = true; }
        }
        if (_highlight > 0) { _highlight = Math.Max(0, _highlight - dt * 1.6f); dirty = true; }
        return dirty;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int row = RowAt(e.Y);
        if (row != _hover) { _hover = row; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int row = RowAt(e.Y);
        if (row < 0) return;
        long px = PriceAt(row);
        if (px <= 0) return;
        try { Clipboard.SetText(Fmt.Price(px, _in.Dec)); CopyNote?.Invoke("copied " + Fmt.Price(px, _in.Dec)); }
        catch { }
        _highlightRow = row;
        _highlight = 1f;
    }

    // ---------------------------------------------------------------- geometry
    float RowH => S(18);
    float HeadH => S(15);
    float SpreadH => S(30);

    int VisibleRows
    {
        get
        {
            float body = Height - HeadH - SpreadH - S(4);
            int per = (int)Math.Floor(body / 2f / RowH);
            return Math.Clamp(per, 2, MaxLevels);
        }
    }

    float BlockTop => HeadH;
    float AskTop => BlockTop;
    float SpreadTop => AskTop + VisibleRows * RowH;
    float BidTop => SpreadTop + SpreadH;

    int RowAt(float y)
    {
        int per = VisibleRows;
        // asks are painted farthest-first, so the bottom line of that block is level 0
        if (y >= AskTop && y < AskTop + per * RowH) return per - 1 - (int)((y - AskTop) / RowH);
        if (y >= BidTop && y < BidTop + per * RowH) return 100 + (int)((y - BidTop) / RowH);
        return -1;
    }

    long PriceAt(int row)
    {
        if (row >= 100)
        {
            int i = row - 100;
            return i >= 0 && i < _levels ? _bPx[i] : 0;
        }
        return row >= 0 && row < _levels ? _aPx[row] : 0;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // wheel walks the depth window one level at a time
        Levels = Math.Clamp(Levels + (e.Delta > 0 ? 1 : -1), 2, MaxLevels);
        Invalidate();
    }

    // ---------------------------------------------------------------- painting
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int per = VisibleRows;
        int dec = _in.Dec;

        // column captions
        float cw = Width;
        float priceRight = cw * 0.40f;
        float sizeRight = cw * 0.58f;
        float barX = cw * 0.62f;
        float barW = cw - barX - S(4);
        Gfx.Tracked(g, "PRICE", Fonts.UiTiny, Pal.TextFaint, priceRight - S(46), S(1.5f), 0.7f);
        Gfx.Tracked(g, "SIZE", Fonts.UiTiny, Pal.TextFaint, sizeRight - S(34), S(1.5f), 0.7f);
        Gfx.Tracked(g, "DEPTH", Fonts.UiTiny, Pal.TextFaint, barX, S(1.5f), 0.7f);
        if (!_in.BookSynced)
            g.DrawString(_levels + " lvls · ticker only", Fonts.MonoTiny, Cache.Brush(Pal.TextFaint), new RectangleF(0, S(1.5f), cw - S(2), S(11)), Gfx.SfTopRight);

        // asks, farthest first
        for (int i = per - 1; i >= 0; i--)
            DrawRow(g, i, false, AskTop + (per - 1 - i) * RowH, priceRight, sizeRight, barX, barW, dec);

        // bids, nearest first
        for (int i = 0; i < per; i++)
            DrawRow(g, i, true, BidTop + i * RowH, priceRight, sizeRight, barX, barW, dec);

        DrawSpread(g, priceRight, barX, barW, per);

        // side labels on the outer edges
        Gfx.Tracked(g, "ASKS", Fonts.UiTinyBold, Pal.Alpha(Pal.Down, 150), S(2), AskTop - S(1) + RowH * 0.1f, 0.7f);
        Gfx.Tracked(g, "BIDS", Fonts.UiTinyBold, Pal.Alpha(Pal.Up, 150), S(2), BidTop + RowH * 0.1f, 0.7f);
    }

    void DrawRow(Graphics g, int i, bool bid, float y, float priceRight, float sizeRight, float barX, float barW, int dec)
    {
        long px = bid ? _bPx[i] : _aPx[i];
        long qty = bid ? _bQty[i] : _aQty[i];
        float norm = bid ? _bAnim[i] : _aAnim[i];
        bool placeholder = px == 0 && i > 0;
        Color side = bid ? Pal.Up : Pal.Down;
        bool ours = px > 0 && ((bid && px == _in.OurBid8) || (!bid && px == _in.OurAsk8));
        int rowId = bid ? 100 + i : i;
        bool hovered = _hover == rowId;

        var row = new RectangleF(0, y, Width, RowH);
        if (ours)
            g.FillRectangle(Cache.Brush(Pal.Alpha(side, 26)), row);
        else if (hovered)
            g.FillRectangle(Cache.Brush(Pal.Alpha(Pal.Hover, 90)), row);
        else if (i % 2 == 1)
            g.FillRectangle(Cache.Brush(Color.FromArgb(12, 255, 255, 255)), row);
        if (_highlight > 0 && _highlightRow == rowId)
            g.FillRectangle(Cache.Brush(Pal.Alpha(Pal.Accent, (int)(70 * _highlight))), row);

        // depth bar
        if (norm > 0.004f && barW > 4)
        {
            float w = Math.Max(S(1.5f), barW * norm);
            var b = new RectangleF(barX, y + S(2.5f), w, RowH - S(5f));
            using (GraphicsPath bp = Gfx.Round(b, S(2.5f)))
            {
                Grad.FillPathH(g, bp, b, Pal.Alpha(side, 105), Pal.Alpha(side, 26));
                using var edge = new Pen(Pal.Alpha(side, 150), 1f);
                g.DrawLine(edge, b.Right - 0.5f, b.Y, b.Right - 0.5f, b.Bottom);
            }
        }
        else if (placeholder && barW > 4)
        {
            // empty level: show the tick lattice so the ladder still reads as a grid
            using var dot = new Pen(Pal.Alpha(Pal.LineBright, 40), 1f) { DashStyle = DashStyle.Dot };
            g.DrawLine(dot, barX, y + RowH / 2f, barX + barW, y + RowH / 2f);
        }

        // prices: projected from the touch when a level is missing so the tick grid stays readable
        long shown = px;
        if (shown <= 0 && i > 0 && _in.Tick8 > 0)
        {
            long anchor = bid ? _in.BestBid8 : _in.BestAsk8;
            if (anchor > 0) shown = bid ? anchor - i * _in.Tick8 : anchor + i * _in.Tick8;
        }
        string pxs = shown > 0 ? Fmt.Price(shown, dec) : "-";
        Color pxInk = placeholder ? Pal.Alpha(side, 90) : ours ? Pal.Mix(side, Pal.TextHi, 0.45) : Pal.Mix(side, Pal.Text, 0.55);
        g.DrawString(pxs, Fonts.MonoSmall, Cache.Brush(pxInk), new RectangleF(0, y, priceRight - S(4), RowH), Gfx.SfRight);

        string qs = qty > 0 ? Fmt.Qty(qty / 1e8, 3) : "";
        g.DrawString(qs, Fonts.MonoTiny, Cache.Brush(qty > 0 ? Pal.Text : Pal.TextFaint), new RectangleF(0, y, sizeRight - S(4), RowH), Gfx.SfRight);

        if (ours)
        {
            var chip = new RectangleF(sizeRight + S(2), y + (RowH - S(11)) / 2f, S(26), S(11));
            Gfx.FillRound(g, chip, S(3), Pal.Alpha(side, 60));
            Gfx.StrokeRound(g, chip, S(3), Pal.Alpha(side, 190));
            g.DrawString("OURS", Fonts.MonoTiny, Cache.Brush(Pal.Mix(side, Pal.TextHi, 0.5)), chip, Gfx.SfCenter);
        }

        if (hovered)
        {
            double sideTotal = 0;
            for (int k = 0; k < _levels; k++) sideTotal += bid ? _bQty[k] : _aQty[k];
            double pct = sideTotal > 0 ? (qty / sideTotal) * 100 : 0;
            string note = qty > 0 ? $"{Fmt.Qty(qty / 1e8, 4)}  {pct:F1}% of side" : "no data";
            g.DrawString(note, Fonts.MonoTiny, Cache.Brush(Pal.Text), new RectangleF(barX, y, barW, RowH), Gfx.SfRight);
        }

        if (bid) Gfx.HairH(g, y + RowH - 0.5f, S(4), Width - S(4), Pal.Alpha(Pal.LineSoft, 90));
    }

    void DrawSpread(Graphics g, float priceRight, float barX, float barW, int per)
    {
        var r = new RectangleF(S(2), SpreadTop + S(2), Width - S(4), SpreadH - S(4));
        Gfx.FillRound(g, r, S(6), Pal.Mix(Pal.Bg, Color.Black, 0.05f));
        Gfx.StrokeRound(g, r, S(6), Pal.Alpha(Pal.Line, 150));

        long bid = _in.BestBid8, ask = _in.BestAsk8;
        bool two = bid > 0 && ask > bid;
        long spread = two ? ask - bid : 0;
        double ticks = _in.Tick8 > 0 ? spread / (double)_in.Tick8 : 0;
        double mid = two ? (bid + ask) / 2.0 / 1e8 : 0;
        double bp = mid > 0 ? spread / 1e8 / mid * 10000 : 0;

        float cy = r.Y + r.Height / 2f;
        Gfx.Tracked(g, "SPREAD", Fonts.UiTiny, Pal.TextFaint, r.X + S(8), cy - S(6f), 0.7f);
        string sp = two ? Fmt.Price(spread, _in.Dec) : "-";
        g.DrawString(sp, Fonts.MonoBold, Cache.Brush(two ? Pal.TextHi : Pal.TextDim), r.X + S(48), cy - S(7f), Gfx.SfTop);
        string meta = two ? $"{ticks:F0}t · {bp:F1}bp" : "no touch";
        g.DrawString(meta, Fonts.MonoTiny, Cache.Brush(Pal.TextDim), r.X + S(48) + Gfx.Width(g, sp, Fonts.MonoBold) + S(7), cy - S(5.5f), Gfx.SfTop);

        // mid / micro nudge indicator
        if (two)
        {
            float gw = S(58), gx = r.Right - gw - S(8);
            g.DrawString("mid", Fonts.MonoTiny, Cache.Brush(Pal.TextFaint), gx, cy - S(14), Gfx.SfTop);
            g.DrawString(Fmt.Price(mid, _in.Dec), Fonts.MonoSmall, Cache.Brush(Pal.Text), new RectangleF(gx, cy - S(6), gw, S(12)), Gfx.SfTopRight);
            if (_in.Micro > 0)
            {
                double off = _in.Micro - mid;
                double tickOff = _in.Tick8 > 0 ? off / (_in.Tick8 / 1e8) : 0;
                Color ink = Math.Abs(tickOff) < 0.05 ? Pal.TextFaint : off > 0 ? Pal.UpLit : Pal.DownLit;
                string s = $"{Fmt.Signed(off, _in.Dec)} ({Fmt.Signed(tickOff, 2)}t)";
                g.DrawString(s, Fonts.MonoTiny, Cache.Brush(ink), new RectangleF(gx - S(70), cy + S(2), gw + S(70), S(12)), Gfx.SfTopRight);
            }
        }

        // visible book imbalance
        double sb = 0, sa = 0;
        for (int i = 0; i < _levels; i++) { sb += _bQty[i]; sa += _aQty[i]; }
        if (sb + sa > 0)
        {
            float ibW = S(88), ix = r.X + S(6), iy = r.Bottom - S(7);
            var ib = new RectangleF(ix, iy, ibW, S(4));
            Gfx.FillRound(g, ib, S(2), Pal.Alpha(Pal.Bg, 220));
            double frac = sb / (sb + sa);
            var fillB = new RectangleF(ib.X, ib.Y, (float)(ibW * frac), ib.Height);
            if (frac > 0.001) Gfx.FillRound(g, fillB, S(2), Pal.Alpha(Pal.Up, 190));
            var fillA = new RectangleF(ib.X + (float)(ibW * frac), ib.Y, (float)(ibW * (1 - frac)), ib.Height);
            if (frac < 0.999) Gfx.FillRound(g, fillA, S(2), Pal.Alpha(Pal.Down, 190));
            string txt = $"imb {(frac - 0.5) * 200:F0}%  ·  {Fmt.Qty(sb / 1e8, 2)} vs {Fmt.Qty(sa / 1e8, 2)}";
            g.DrawString(txt, Fonts.MonoTiny, Cache.Brush(Pal.TextDim), ib.Right + S(6), iy - S(4.5f), Gfx.SfTop);
        }
        else
        {
            g.DrawString("depth stream disabled — add {symbol}@depth to MdSubs", Fonts.MonoTiny, Cache.Brush(Pal.TextFaint), r.X + S(6), r.Bottom - S(12), Gfx.SfTop);
        }
    }
}

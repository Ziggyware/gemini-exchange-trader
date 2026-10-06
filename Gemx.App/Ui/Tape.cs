using System.Drawing.Drawing2D;

namespace Gemx.App;

internal enum TapeKind { Fill, Order, Alert, Kill, Info, Boot }

// ---------------------------------------------------------------------------------------------
// Activity tape. Newest event sits at the top so the eye never has to chase a scrollbar.
// ---------------------------------------------------------------------------------------------
internal sealed class TapeView : UiControl
{
    const int MaxRows = 400;

    struct Row
    {
        public TapeKind Kind;
        public bool Sell;
        public string Time, Tag, Price, Qty, Note;
        public float Flash;
    }

    readonly List<Row> _rows = new(MaxRows + 32);
    int _scroll;
    int _hover = -1;
    float _phase;
    public Action<string>? Note;

    public TapeView()
    {
        Surface = Pal.Card;
        TabStop = true;
    }

    public int Count => _rows.Count;

    public float RowH => S(19);
    public float HeadH => S(15);

    public void Add(TapeKind kind, bool sell, string tag, string price, string qty, string note)
    {
        if (_scroll > 0) _scroll++;      // keep the reader's place while new rows land
        _rows.Insert(0, new Row
        {
            Kind = kind,
            Sell = sell,
            Time = Fmt.TimeOfDayMs(DateTime.Now),
            Tag = tag,
            Price = price,
            Qty = qty,
            Note = note,
            Flash = 1f
        });
        if (_rows.Count > MaxRows) _rows.RemoveRange(MaxRows, _rows.Count - MaxRows);
        Invalidate();
    }

    public string LastText()
    {
        if (_rows.Count == 0) return "";
        Row r = _rows[0];
        return $"{r.Time} {r.Tag} {r.Price} {r.Qty} {r.Note}".Trim();
    }

    public void Clear()
    {
        _rows.Clear();
        _scroll = 0;
        Invalidate();
    }

    int VisibleRows => Math.Max(1, (int)((Height - HeadH) / RowH));

    protected override bool Animate(float dt)
    {
        _phase += dt;
        bool dirty = false;
        for (int i = 0; i < _rows.Count; i++)
        {
            Row r = _rows[i];
            if (r.Flash <= 0) continue;
            r.Flash = Math.Max(0, r.Flash - dt * 1.1f);
            _rows[i] = r;
            dirty = true;
        }
        return dirty;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int max = Math.Max(0, _rows.Count - VisibleRows);
        _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * 2, 0, max);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = (int)((e.Y - HeadH) / RowH);
        i = e.Y < HeadH ? -1 : i + _scroll;
        if (i != _hover) { _hover = i; Invalidate(); }
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
        int i = (int)((e.Y - HeadH) / RowH);
        i = e.Y < HeadH ? -1 : i + _scroll;
        if (i < 0 || i >= _rows.Count) return;
        Row r = _rows[i];
        try
        {
            Clipboard.SetText($"{r.Time} {r.Tag} {r.Price} {r.Qty} {r.Note}".Trim());
            Note?.Invoke("row copied");
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float timeW = S(58), tagW = S(48), priceW = S(84), qtyW = S(56);
        float noteX = timeW + tagW + priceW + qtyW + S(10);

        Gfx.Tracked(g, "TIME", Fonts.UiTiny, Pal.TextFaint, S(10), S(1.5f), 0.7f);
        Gfx.Tracked(g, "EVENT", Fonts.UiTiny, Pal.TextFaint, timeW + S(4), S(1.5f), 0.7f);
        g.DrawString("PRICE", Fonts.UiTiny, Cache.Brush(Pal.TextFaint), new RectangleF(timeW + tagW, S(1.5f), priceW - S(6), S(11)), Gfx.SfTopRight);
        g.DrawString("SIZE", Fonts.UiTiny, Cache.Brush(Pal.TextFaint), new RectangleF(timeW + tagW + priceW, S(1.5f), qtyW - S(6), S(11)), Gfx.SfTopRight);
        Gfx.Tracked(g, "DETAIL", Fonts.UiTiny, Pal.TextFaint, noteX, S(1.5f), 0.7f);
        Gfx.HairH(g, HeadH - 0.5f, 0, Width, Pal.Alpha(Pal.LineSoft, 170));

        if (_rows.Count == 0)
        {
            float cy = HeadH + (Height - HeadH) / 2f;
            Icons.Draw(g, Glyph.Wave, new RectangleF(Width / 2f - S(11), cy - S(22), S(22), S(22)), Pal.Alpha(Pal.TextFaint, 120), S(1.5f));
            g.DrawString("quiet — fills, rejects and breaker trips land here", Fonts.UiSmall, Cache.Brush(Pal.TextFaint), new RectangleF(0, cy + S(2), Width, S(16)), Gfx.SfCenter);
            return;
        }

        int first = _scroll;
        int shown = VisibleRows;
        for (int k = 0; k < shown; k++)
        {
            int i = first + k;
            if (i >= _rows.Count) break;
            DrawRow(g, _rows[i], HeadH + k * RowH, i == _hover, timeW, tagW, priceW, qtyW, noteX);
        }

        if (_scroll > 0)
        {
            string msg = $"scrolled back {_scroll} — wheel or click to return";
            var r = new RectangleF(Width - S(210), HeadH + S(4), S(204), S(16));
            Gfx.FillRound(g, r, S(6), Pal.Alpha(Pal.AccentDeep, 200));
            g.DrawString(msg, Fonts.MonoTiny, Cache.Brush(Color.FromArgb(230, 240, 255)), r, Gfx.SfCenter);
        }
    }

    void DrawRow(Graphics g, Row r, float y, bool hovered, float timeW, float tagW, float priceW, float qtyW, float noteX)
    {
        Color accent = r.Kind switch
        {
            TapeKind.Fill => r.Sell ? Pal.Down : Pal.Up,
            TapeKind.Kill => Pal.Danger,
            TapeKind.Alert => Pal.Warn,
            TapeKind.Order => Pal.Info,
            TapeKind.Boot => Pal.Violet,
            _ => Pal.Neutral
        };
        var row = new RectangleF(0, y, Width, RowH);
        if (r.Flash > 0) g.FillRectangle(Cache.Brush(Pal.Alpha(accent, (int)(58 * r.Flash))), row);
        else if (hovered) g.FillRectangle(Cache.Brush(Pal.Alpha(Pal.Hover, 100)), row);

        g.FillRectangle(Cache.Brush(Pal.Alpha(accent, r.Flash > 0 ? 230 : 110)), 0, y + S(1.5f), S(2.5f), RowH - S(3f));

        g.DrawString(r.Time, Fonts.MonoTiny, Cache.Brush(Pal.TextFaint), new RectangleF(S(8), y, timeW, RowH), Gfx.SfMid);

        // event tag chip
        float tw = Gfx.Width(g, r.Tag, Fonts.MonoTiny) + S(10);
        var chip = new RectangleF(timeW + S(2), y + (RowH - S(13)) / 2f, tw, S(13));
        Gfx.FillRound(g, chip, S(3.5f), Pal.Alpha(accent, r.Flash > 0 ? 90 : 45));
        Gfx.StrokeRound(g, chip, S(3.5f), Pal.Alpha(accent, 150));
        g.DrawString(r.Tag, Fonts.MonoTiny, Cache.Brush(Pal.Mix(accent, Pal.TextHi, 0.3)), chip, Gfx.SfCenter);

        if (r.Price.Length > 0)
            g.DrawString(r.Price, Fonts.MonoSmall, Cache.Brush(r.Kind == TapeKind.Fill ? Pal.Mix(accent, Pal.TextHi, 0.35) : Pal.Text), new RectangleF(timeW + tagW, y, priceW - S(6), RowH), Gfx.SfRight);
        if (r.Qty.Length > 0)
            g.DrawString(r.Qty, Fonts.MonoTiny, Cache.Brush(Pal.TextDim), new RectangleF(timeW + tagW + priceW, y, qtyW - S(6), RowH), Gfx.SfRight);

        var noteRect = new RectangleF(noteX, y, Math.Max(S(10), Width - noteX - S(8)), RowH);
        g.DrawString(r.Note, Fonts.UiSmall, Cache.Brush(hovered ? Pal.TextHi : Pal.Text), noteRect, Gfx.SfMid);

        Gfx.HairH(g, y + RowH - 0.5f, S(6), Width - S(4), Pal.Alpha(Pal.LineSoft, 90));
    }
}

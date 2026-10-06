using System.Drawing.Drawing2D;

namespace Gemx.App;

// ---------------------------------------------------------------------------------------------
// Rolling sample buffer plus its renderer. Shared by the metric tiles and the gauges so a
// number always comes with its recent shape.
// ---------------------------------------------------------------------------------------------
internal sealed class Spark
{
    readonly double[] _b;
    int _head, _count;

    public Spark(int cap) { _b = new double[Math.Max(4, cap)]; }

    public int Count => _count;

    public void Clear() { _head = 0; _count = 0; }

    public void Push(double v)
    {
        _b[_head] = v;
        _head = (_head + 1) % _b.Length;
        if (_count < _b.Length) _count++;
    }

    public double At(int i) => _b[((_head - _count + i) % _b.Length + _b.Length) % _b.Length];

    public double Last => _count == 0 ? 0 : At(_count - 1);

    public double MinMax(out double lo, out double hi)
    {
        lo = double.MaxValue; hi = double.MinValue;
        for (int i = 0; i < _count; i++) { double v = At(i); if (v < lo) lo = v; if (v > hi) hi = v; }
        if (_count == 0) { lo = 0; hi = 1; }
        return hi - lo;
    }

    public void Draw(Graphics g, RectangleF r, Color c, bool area = true, double? fixLo = null, double? fixHi = null, bool zeroLine = true)
    {
        if (r.Width < 4 || r.Height < 4) return;
        // baseline so an empty tile still looks deliberate
        Gfx.HairH(g, r.Bottom - 0.5f, r.Left, r.Right, Pal.LineSoft);
        if (_count < 2)
        {
            g.FillEllipse(Cache.Brush(Pal.Alpha(c, 90)), r.Right - 3.5f, r.Top + r.Height / 2f - 1.5f, 3f, 3f);
            return;
        }

        MinMax(out double lo, out double hi);
        if (fixLo.HasValue) lo = fixLo.Value;
        if (fixHi.HasValue) hi = fixHi.Value;
        double span = hi - lo;
        if (span <= 1e-12) { lo -= 0.5; hi += 0.5; span = 1; }
        double head = span * 0.12;
        lo -= head; hi += head; span = hi - lo;

        var pts = new PointF[_count];
        float dx = r.Width / (_count - 1f);
        for (int i = 0; i < _count; i++)
        {
            double v = At(i);
            float y = (float)(r.Bottom - (v - lo) / span * r.Height);
            pts[i] = new PointF(r.Left + i * dx, y);
        }

        float baseY = (zeroLine && lo < 0 && hi > 0) ? (float)(r.Bottom - (0 - lo) / span * r.Height) : r.Bottom;
        if (area)
        {
            using var path = new GraphicsPath();
            path.AddLines(pts);
            path.AddLine(pts[_count - 1].X, pts[_count - 1].Y, pts[_count - 1].X, baseY);
            path.AddLine(pts[_count - 1].X, baseY, pts[0].X, baseY);
            path.CloseFigure();
            Grad.FillPathV(g, path, r, Pal.Alpha(c, 78), Pal.Alpha(c, 4));
            if (baseY > r.Top && baseY < r.Bottom) Gfx.HairH(g, baseY, r.Left, r.Right, Pal.Alpha(Pal.LineBright, 90));
        }

        Gfx.GlowLine(g, pts, c, 1.1f);
        using var pen = new Pen(c, 1.3f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, pts);
        Gfx.Dot(g, pts[_count - 1].X, pts[_count - 1].Y, 1.7f, c, true);
    }
}

// ---------------------------------------------------------------------------------------------
// Brand mark. Drawn, not shipped as a bitmap, so it stays sharp at every DPI.
// ---------------------------------------------------------------------------------------------
internal sealed class LogoMark : UiControl
{
    public string Symbol = "";

    public LogoMark()
    {
        Surface = Pal.Bg;
        Height = 30;
        Width = 260;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cy = Height / 2f;
        float s = S(20);

        // diamond badge with a nested candle motif
        var box = new RectangleF(S(2), cy - s / 2f, s, s);
        using (var d = new GraphicsPath())
        {
            d.AddPolygon(new[]
            {
                new PointF(box.X + box.Width / 2f, box.Y),
                new PointF(box.Right, box.Y + box.Height / 2f),
                new PointF(box.X + box.Width / 2f, box.Bottom),
                new PointF(box.X, box.Y + box.Height / 2f)
            });
            Grad.FillPathV(g, d, box, Pal.Mix(Pal.Accent, Color.White, 0.25), Pal.AccentDeep);
            using var edge = new Pen(Pal.Alpha(Pal.Accent, 150), 1f);
            g.DrawPath(edge, d);
            g.FillRectangle(Cache.Brush(Color.FromArgb(230, 255, 255, 255)), box.X + box.Width * 0.36f, box.Y + box.Height * 0.26f, box.Width * 0.10f, box.Height * 0.48f);
            g.FillRectangle(Cache.Brush(Color.FromArgb(200, 255, 255, 255)), box.X + box.Width * 0.54f, box.Y + box.Height * 0.34f, box.Width * 0.10f, box.Height * 0.40f);
        }

        float x = box.Right + S(9);
        Gfx.Tracked(g, "GEMX", Fonts.Title, Pal.TextHi, x, cy - S(10), 1.1f);
        x += Gfx.TrackedWidth(g, "GEMX", Fonts.Title, 1.1f) + S(11);

        if (Symbol.Length > 0 && x < Width - S(60))
        {
            float tw = Gfx.Width(g, Symbol, Fonts.MonoSmall);
            var r = new RectangleF(x, cy - S(10), tw + S(30), S(20));
            Gfx.FillRound(g, r, S(5), Pal.Alpha(Pal.Raise, 210));
            Gfx.StrokeRound(g, r, S(5), Pal.Line);
            Gfx.Dot(g, r.X + S(9), cy, S(2.6f), Pal.Accent, true);
            g.DrawString(Symbol, Fonts.MonoSmall, Cache.Brush(Pal.TextHi), new RectangleF(r.X + S(16), r.Y, r.Width - S(19), r.Height), Gfx.SfMid);
            x = r.Right + S(12);
            float sw2 = Gfx.TrackedWidth(g, "MAKER TERMINAL", Fonts.UiTinyBold, 0.9f);
            if (x + sw2 < Width - S(6))
                Gfx.Tracked(g, "MAKER TERMINAL", Fonts.UiTinyBold, Pal.TextFaint, x, cy - S(4.5f), 0.9f);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Primary trade-state chip: dot, headline and reason.
// ---------------------------------------------------------------------------------------------
internal sealed class StateChip : UiControl
{
    string _text = "IDLE";
    string _detail = "";
    StateTone _tone = StateTone.Idle;
    float _shown;
    float _phase;
    bool _pulse;

    public StateChip()
    {
        Surface = Pal.Bg;
        Height = 30;
        Width = 190;
    }

    public void Set(string text, string detail, StateTone tone, bool pulse)
    {
        bool changed = text != _text || tone != _tone;
        _text = text; _detail = detail; _tone = tone; _pulse = pulse;
        int w = TextRenderer.MeasureText(_text, Fonts.UiBold).Width + TextRenderer.MeasureText(_detail, Fonts.MonoTiny).Width + (int)S(62);
        Width = Math.Max((int)S(120), w);
        if (changed) _shown = 0f;
    }

    protected override bool Animate(float dt)
    {
        bool dirty = false;
        _phase += dt;
        if (_shown < 1f) { _shown = Math.Min(1f, _shown + dt * 5f); dirty = true; }
        if (_pulse) dirty = true;
        return dirty;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color c = Pal.StateColor(_tone);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (GraphicsPath p = Gfx.Round(r, Height / 2f))
        {
            int edge = (int)(150 * (0.4f + 0.6f * _shown));
            Grad.FillPathV(g, p, r, Pal.Alpha(c, 34), Pal.Alpha(c, 12));
            using var pen = new Pen(Pal.Alpha(c, edge), 1f);
            g.DrawPath(pen, p);
        }

        float cy = Height / 2f;
        float dot = S(4.6f) * (0.82f + 0.18f * (float)Math.Sin(_phase * 2.4f));
        if (_tone == StateTone.Idle) dot = S(4.6f);
        Gfx.Dot(g, S(15), cy, dot, c, _pulse);

        float x = S(27);
        Color ink = Pal.Mix(c, Pal.TextHi, 0.35f + 0.25f * (1f - _shown));
        Gfx.Tracked(g, _text, Fonts.UiBold, ink, x, cy - S(7.5f), 0.9f);
        x += Gfx.TrackedWidth(g, _text, Fonts.UiBold, 0.9f) + S(8);
        if (_detail.Length > 0 && x < Width - S(20))
            g.DrawString(_detail, Fonts.MonoTiny, Cache.Brush(Pal.TextDim), x, cy - S(6f), Gfx.SfTop);
    }
}

// ---------------------------------------------------------------------------------------------
// Status bar pill: compact subsystem lamp with an optional reading.
// ---------------------------------------------------------------------------------------------
internal sealed class StatusPill : UiControl
{
    public readonly string Label;
    string _value = "";
    StateTone _tone = StateTone.Idle;
    bool _pulse;
    float _phase;

    public StatusPill(string label)
    {
        Label = label;
        Surface = Pal.Bg;
        Height = 20;
        Width = 74;
    }

    public void Set(StateTone tone, string? value = null, bool pulse = false)
    {
        _tone = tone;
        _value = value ?? "";
        _pulse = pulse;
        int w = TextRenderer.MeasureText(Label, Fonts.UiTinyBold).Width + (int)S(26);
        if (_value.Length > 0) w += TextRenderer.MeasureText(_value, Fonts.MonoTiny).Width + (int)S(6);
        Width = Math.Max((int)S(46), w);
        Invalidate();
    }

    protected override bool Animate(float dt)
    {
        _phase += dt;
        return _pulse || _tone == StateTone.Warm;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color c = Pal.StateColor(_tone);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (GraphicsPath p = Gfx.Round(r, S(5)))
        {
            Grad.FillPathV(g, p, r, Pal.Alpha(c, 30), Pal.Alpha(Pal.Panel, 200));
            using var pen = new Pen(Pal.Alpha(c, _tone == StateTone.Idle ? 60 : 120), 1f);
            g.DrawPath(pen, p);
        }

        float cy = Height / 2f;
        float rad = S(3.1f) * (_tone == StateTone.Warm ? 0.85f + 0.15f * (float)Math.Sin(_phase * 3.1f) : 1f);
        Gfx.Dot(g, S(10), cy, rad, c, _pulse);
        Gfx.Tracked(g, Label, Fonts.UiTinyBold, Pal.Mix(c, Pal.TextHi, _tone == StateTone.Idle ? 0.0 : 0.25), S(17), cy - S(6f), 0.8f);

        if (_value.Length > 0)
        {
            float vx = Width - S(8) - Gfx.Width(g, _value, Fonts.MonoTiny);
            g.DrawString(_value, Fonts.MonoTiny, Cache.Brush(Pal.Text), vx, cy - S(5.5f), Gfx.SfTop);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Owner-drawn push button with three visual weights.
// ---------------------------------------------------------------------------------------------
internal sealed class ToolButton : UiControl
{
    public enum Kind { Primary, Danger, Ghost, Subtle }

    readonly Glyph _glyph;
    readonly Kind _kind;
    string _text;
    bool _over;
    float _hover;
    float _press;
    public bool Checked;
    public string Tip = "";
    public bool Compact;

    public ToolButton(string text, Glyph glyph, Kind kind = Kind.Ghost)
    {
        _text = text; _glyph = glyph; _kind = kind;
        Surface = Pal.Bg;
        Height = 26;
        TabStop = false;
        Width = 90;
    }

    public void SetText(string text)
    {
        _text = text;
        Width = CalcWidth();
        Parent?.PerformLayout();
        Invalidate();
    }

    int CalcWidth()
    {
        float h = S(Compact ? 20 : 26);
        float content = 0;
        if (_text.Length > 0) content += TextRenderer.MeasureText(_text, Fonts.UiBold).Width;
        if (_glyph != Glyph.None)
        {
            content += S(14);
            if (_text.Length > 0) content += S(6);
        }
        return (int)Math.Max(h, content + S(22));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Height = (int)S(Compact ? 20 : 26);
        Width = CalcWidth();
        Parent?.PerformLayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool on = Enabled;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float radius = S(6);

        Color face, face2, ink, border;
        switch (_kind)
        {
            case Kind.Primary:
                face = Pal.Mix(Pal.Up, Color.Black, 0.15); face2 = Pal.Mix(Pal.Up, Color.Black, 0.42);
                ink = Color.FromArgb(240, 255, 250); border = Pal.Alpha(Pal.UpLit, 190);
                break;
            case Kind.Danger:
                face = Pal.Mix(Pal.Down, Color.Black, 0.10); face2 = Pal.Mix(Pal.Down, Color.Black, 0.45);
                ink = Color.FromArgb(255, 240, 242); border = Pal.Alpha(Pal.DownLit, 200);
                break;
            case Kind.Subtle:
                face = Pal.Mix(Pal.Raise, Color.Black, 0.10); face2 = Pal.Mix(Pal.Panel, Color.Black, 0.05);
                ink = Pal.Text; border = Pal.LineSoft;
                break;
            default:
                face = Pal.Raise; face2 = Pal.Mix(Pal.Raise, Color.Black, 0.28);
                ink = Pal.TextHi; border = Pal.Line;
                break;
        }
        if (Checked) { face = Pal.Mix(face, Pal.Accent, 0.30); face2 = Pal.Mix(face2, Pal.Accent, 0.16); border = Pal.Alpha(Pal.Accent, 190); ink = Pal.TextHi; }
        if (_hover > 0) { face = Pal.Mix(face, Color.White, 0.10 * _hover); face2 = Pal.Mix(face2, Color.White, 0.08 * _hover); }
        if (_press > 0) { face = Pal.Mix(face, Color.Black, 0.22 * _press); face2 = Pal.Mix(face2, Color.Black, 0.10 * _press); }
        if (!on) { face = Pal.Mix(face, Pal.Panel, 0.7); face2 = Pal.Mix(face2, Pal.Panel, 0.7); ink = Pal.TextFaint; border = Pal.LineSoft; }

        if (_kind != Kind.Subtle) Gfx.DropShadow(g, r, radius, _kind == Kind.Ghost ? 40 : 70, S(2));
        using (GraphicsPath p = Gfx.Round(r, radius))
        {
            Grad.FillPathV(g, p, r, face, face2);
            using var pen = new Pen(border, 1f);
            g.DrawPath(pen, p);
            // 1px inner highlight along the top edge
            using var top = new Pen(Color.FromArgb(on ? 34 : 12, 255, 255, 255), 1f);
            g.DrawLine(top, r.X + radius * 0.8f, r.Y + 1.0f, r.Right - radius * 0.8f, r.Y + 1.0f);
        }

        float shift = _press * S(1);
        float avail = Width - S(14);
        float iconW = _glyph == Glyph.None ? 0 : S(13);
        float textW = _text.Length > 0 ? Gfx.Width(g, _text, Fonts.UiBold) : 0;
        float gap = iconW > 0 && textW > 0 ? S(6) : 0;
        float x = (Width - (iconW + gap + textW)) / 2f;
        float cy = Height / 2f + shift;

        if (iconW > 0)
        {
            Icons.Draw(g, _glyph, new RectangleF(x, cy - iconW / 2f, iconW, iconW), ink, S(1.5f));
            x += iconW + gap;
        }
        if (textW > 0)
            g.DrawString(_text, Fonts.UiBold, Cache.Brush(ink), new RectangleF(x, cy - S(9), avail, S(18)), Gfx.SfMid);
    }

    protected override bool Animate(float dt)
    {
        bool dirty = false;
        float target = _over ? 1f : 0f;
        if (Math.Abs(_hover - target) > 0.01f) { _hover += (target - _hover) * Math.Min(1f, dt * 14f); dirty = true; }
        if (_press > 0) { _press = Math.Max(0, _press - dt * 6f); dirty = true; }
        return dirty;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _over = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _over = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _press = 1f; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

// ---------------------------------------------------------------------------------------------
// Segmented selector used for the chart timeframes.
// ---------------------------------------------------------------------------------------------
internal sealed class Segments : UiControl
{
    readonly string[] _items;
    int _sel;
    int _hover = -1;
    float _phase;
    public bool LiveDot;
    public Action<int>? Changed;

    public Segments(params string[] items)
    {
        _items = items;
        Surface = Pal.Panel;
        Height = 28;
        TabStop = true;
    }

    public int Selected
    {
        get => _sel;
        set { if (value >= 0 && value < _items.Length && value != _sel) { _sel = value; Changed?.Invoke(_sel); Invalidate(); } }
    }

    float CellW => _items.Length == 0 ? 0 : (Width - S(6)) / _items.Length;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = Hit(e.X);
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Selected = Hit(e.X);
    }

    int Hit(int x)
    {
        if (CellW <= 0) return -1;
        int i = (int)((x - S(3)) / CellW);
        return i < 0 || i >= _items.Length ? -1 : i;
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Selected = Math.Max(0, _sel - 1);
        else if (e.KeyCode == Keys.Right) Selected = Math.Min(_items.Length - 1, _sel + 1);
        else return;
        e.Handled = true;
    }

    protected override bool Animate(float dt)
    {
        _phase += dt;
        return LiveDot;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(0.5f, 1f, Width - 1, Height - 2);
        using (GraphicsPath tp = Gfx.Round(track, S(7)))
        {
            Grad.FillPathV(g, tp, track, Pal.Mix(Pal.Panel, Color.Black, 0.25), Pal.Panel);
            using var pen = new Pen(Pal.LineSoft, 1f);
            g.DrawPath(pen, tp);
        }

        float cw = CellW;
        for (int i = 0; i < _items.Length; i++)
        {
            var cell = new RectangleF(S(3) + i * cw, S(3), cw - S(2), Height - S(6));
            bool sel = i == _sel;
            if (sel)
            {
                var selRect = RectangleF.Inflate(cell, -S(1), 0);
                Gfx.DropShadow(g, selRect, S(6), 60, S(2));
                using (GraphicsPath sp = Gfx.Round(selRect, S(6)))
                {
                    Grad.FillPathV(g, sp, selRect, Pal.Raise, Pal.Mix(Pal.Raise, Color.Black, 0.3));
                    using var pen = new Pen(Pal.Alpha(Pal.Accent, 130), 1f);
                    g.DrawPath(pen, sp);
                    using var bar = new Pen(Pal.Accent, S(2f));
                    g.DrawLine(bar, selRect.X + S(5), selRect.Bottom - S(2.4f), selRect.Right - S(5), selRect.Bottom - S(2.4f));
                }
            }
            else if (i == _hover)
            {
                Gfx.FillRound(g, RectangleF.Inflate(cell, -S(1), 0), S(6), Pal.Alpha(Pal.Hover, 120));
            }

            Color ink = sel ? Pal.TextHi : Pal.TextDim;
            float tx = cell.X + cell.Width / 2f;
            if (sel)
            {
                Gfx.Tracked(g, _items[i], Fonts.UiTinyBold, ink, tx - Gfx.TrackedWidth(g, _items[i], Fonts.UiTinyBold, 0.8f) / 2f, cell.Y + cell.Height / 2f - S(6f), 0.8f);
                if (LiveDot)
                {
                    float d = S(3f) * (0.75f + 0.25f * (float)Math.Sin(_phase * 4f));
                    Gfx.Dot(g, cell.X + S(6), cell.Y + cell.Height / 2f, d, Pal.UpLit);
                }
            }
            else
            {
                Gfx.Tracked(g, _items[i], Fonts.UiTiny, ink, tx - Gfx.TrackedWidth(g, _items[i], Fonts.UiTiny, 0.8f) / 2f, cell.Y + cell.Height / 2f - S(5.5f), 0.8f);
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Alert banner: appears only when something deserves attention, so the surface stays quiet.
// ---------------------------------------------------------------------------------------------
internal sealed class AlertBanner : UiControl
{
    string _title = "";
    string _detail = "";
    StateTone _tone = StateTone.Info;
    float _phase;
    float _enter;
    public Action? Dismissed;

    public AlertBanner()
    {
        Surface = Pal.Panel;
        Height = 32;
        Visible = false;
    }

    public void Show(string title, string detail, StateTone tone)
    {
        _title = title; _detail = detail; _tone = tone; _enter = 0f;
        Visible = true;
        Invalidate();
    }

    public void Dismiss() { Visible = false; Dismissed?.Invoke(); }

    protected override bool Animate(float dt)
    {
        _phase += dt;
        bool dirty = false;
        if (_enter < 1f) { _enter = Math.Min(1f, _enter + dt * 6f); dirty = true; }
        if (_tone == StateTone.Bad) dirty = true;
        return dirty;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.X > Width - S(28)) { Visible = false; Dismissed?.Invoke(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color c = Pal.StateColor(_tone);
        float slide = (1f - _enter) * S(10);
        var r = new RectangleF(1.5f, 1.5f + slide, Width - 3, Height - 4);

        using (GraphicsPath p = Gfx.Round(r, S(7)))
        {
            g.SetClip(p);
            Grad.FillPathV(g, p, r, Pal.Alpha(c, 46), Pal.Alpha(Pal.Card, 250));
            if (_tone == StateTone.Bad)
            {
                float sweep = (float)((_phase * 0.35f) % 1.0);
                float sx = r.X + sweep * (r.Width + S(160)) - S(160);
                using var brush = new LinearGradientBrush(new RectangleF(sx, r.Y, S(160), r.Height), Pal.Alpha(c, 0), Pal.Alpha(c, 26), 0f);
                g.FillRectangle(brush, new RectangleF(sx, r.Y, S(160), r.Height));
            }
            g.ResetClip();
            using var pen = new Pen(Pal.Alpha(c, 160), 1f);
            g.DrawPath(pen, p);
            using var bar = new SolidBrush(c);
            g.FillRectangle(bar, r.X + S(3), r.Y + S(5), S(3), r.Height - S(10));
        }

        Icons.Draw(g, _tone == StateTone.Bad ? Glyph.Warn : _tone == StateTone.Warm ? Glyph.Bolt : Glyph.Check, new RectangleF(r.X + S(14), r.Y + (r.Height - S(13)) / 2f, S(13), S(13)), c, S(1.5f));
        float x = r.X + S(34);
        g.DrawString(_title, Fonts.UiBold, Cache.Brush(Pal.Mix(c, Pal.TextHi, 0.4)), x, r.Y + (r.Height - S(15)) / 2f, Gfx.SfTop);
        float tw = Gfx.Width(g, _title, Fonts.UiBold);
        if (_detail.Length > 0)
            g.DrawString(_detail, Fonts.MonoSmall, Cache.Brush(Pal.Text), new RectangleF(x + tw + S(10), r.Y, r.Width - tw - S(60), r.Height), Gfx.SfMid);
        Icons.Draw(g, Glyph.Close, new RectangleF(r.Right - S(22), r.Y + (r.Height - S(11)) / 2f, S(11), S(11)), Pal.TextFaint, S(1.3f));
    }
}

// ---------------------------------------------------------------------------------------------
// Headline metric: label, big value, caption and a sparkline of its own history.
// ---------------------------------------------------------------------------------------------
internal sealed class MetricTile : UiControl
{
    public string Heading = "";
    public string Unit = "";
    public string Caption = "";
    public Color Accent = Pal.Accent;
    public int SparkCap = 120;

    string _value = "-";
    Color _valueInk = Pal.TextHi;
    float _flash;
    Color _flashInk = Pal.Up;
    readonly Spark _spark;

    public MetricTile(string heading, int sparkCap = 120)
    {
        Heading = heading; SparkCap = sparkCap;
        _spark = new Spark(sparkCap);
        Surface = Pal.Card;
        Height = 60;
    }

    public void SetValue(string text, Color? ink = null, int flashDir = 0)
    {
        if (text != _value)
        {
            if (flashDir > 0) { _flash = 1f; _flashInk = Pal.Up; }
            else if (flashDir < 0) { _flash = 1f; _flashInk = Pal.Down; }
        }
        _value = text;
        _valueInk = ink ?? Pal.TextHi;
    }

    public void SetCaption(string s) => Caption = s;

    public void Push(double v) => _spark.Push(v);

    public void Clear() { _spark.Clear(); _value = "-"; }

    protected override bool Animate(float dt)
    {
        if (_flash > 0) { _flash = Math.Max(0, _flash - dt * 1.6f); return true; }
        return false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = Rect;
        if (_flash > 0)
            Gfx.FillRound(g, r, S(7), Pal.Alpha(_flashInk, (int)(26 * _flash)));

        // left accent rail
        using (var rail = Gfx.Round(new RectangleF(S(4), S(9), S(2.4f), Height - S(18)), S(1.2f)))
            g.FillPath(Cache.Brush(Pal.Alpha(Accent, 200)), rail);

        float x = S(13);
        Gfx.Tracked(g, Heading.ToUpperInvariant(), Fonts.UiTinyBold, Pal.TextDim, x, S(8), 0.9f);
        if (Unit.Length > 0)
        {
            float uw = Gfx.TrackedWidth(g, Unit.ToUpperInvariant(), Fonts.UiTiny, 0.8f);
            Gfx.Tracked(g, Unit.ToUpperInvariant(), Fonts.UiTiny, Pal.TextFaint, x + Gfx.TrackedWidth(g, Heading.ToUpperInvariant(), Fonts.UiTinyBold, 0.9f) + S(7), uw > 0 ? S(8.6f) : S(8f), 0.8f);
        }

        // value, monospaced so digits never jitter
        g.DrawString(_value, Fonts.MonoBig, Cache.Brush(_valueInk), x - S(1), S(20), Gfx.SfTop);

        float sparkW = Math.Min(S(SparkCap / 2.2f), Width * 0.42f);
        var sr = new RectangleF(Width - sparkW - S(10), Height - S(26), sparkW, S(18));
        _spark.Draw(g, sr, Pal.Alpha(_valueInk, 220), true);

        if (Caption.Length > 0)
            g.DrawString(Caption, Fonts.MonoTiny, Cache.Brush(Pal.TextFaint), x, Height - S(15), Gfx.SfTop);

        Gfx.HairH(g, Height - S(1), S(8), Width - S(8), Pal.Alpha(Pal.LineSoft, 120));
    }
}

// ---------------------------------------------------------------------------------------------
// Compact key/value grid used for footers and the diagnostics card.
// ---------------------------------------------------------------------------------------------
internal sealed class StatGrid : UiControl
{
    readonly int _cols;
    readonly int _rows;
    readonly string[] _keys;
    readonly string[] _vals;
    readonly Color[] _inks;

    public int RowHeight = 26;

    public StatGrid(int cols, int rows)
    {
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        _keys = new string[_cols * _rows];
        _vals = new string[_cols * _rows];
        _inks = new Color[_cols * _rows];
        for (int i = 0; i < _keys.Length; i++) { _keys[i] = ""; _vals[i] = "-"; _inks[i] = Pal.Text; }
        Surface = Pal.Card;
        Height = _rows * RowHeight;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RowHeight = (int)S(26);
        Height = _rows * RowHeight;
    }

    public void Set(int i, string key, string value, Color? ink = null)
    {
        if (i < 0 || i >= _keys.Length) return;
        _keys[i] = key; _vals[i] = value; _inks[i] = ink ?? Pal.Text;
        Invalidate();
    }

    public void SetRow(int row, params (string Key, string Value, Color Ink)[] items)
    {
        for (int c = 0; c < _cols && c < items.Length; c++)
            Set(row * _cols + c, items[c].Key, items[c].Value, items[c].Ink);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cw = Width / (float)_cols;
        float rh = Height / (float)_rows;
        for (int i = 0; i < _keys.Length; i++)
        {
            if (_keys[i].Length == 0) continue;
            int row = i / _cols, col = i % _cols;
            float x = col * cw;
            float y = row * rh;
            Gfx.Tracked(g, _keys[i].ToUpperInvariant(), Fonts.UiTiny, Pal.TextFaint, x, y + rh / 2f - S(10), 0.8f);
            g.DrawString(_vals[i], Fonts.MonoSmall, Cache.Brush(_inks[i]), new RectangleF(x, y + rh / 2f + S(0.5f), cw - S(10), S(14)), Gfx.SfTop);
            if (col > 0) Gfx.HairV(g, x - S(7), y + S(4), y + rh - S(6), Pal.Alpha(Pal.LineSoft, 140));
            if (row > 0 && col == 0) Gfx.HairH(g, y - S(0.5f), 0, Width, Pal.Alpha(Pal.LineSoft, 110));
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Labelled meter with threshold and peak markers.
// ---------------------------------------------------------------------------------------------
internal sealed class MeterBar : UiControl
{
    public readonly string Heading;
    string _value = "-";
    string _note = "";
    double _target, _shown;
    Color _ink = Pal.Up;
    Color _peakInk = Pal.TextDim;
    double? _threshold;
    double? _peak;

    public MeterBar(string heading)
    {
        Heading = heading;
        Surface = Pal.Card;
        Height = 32;
    }

    public void Set(string value, double norm, Color ink, double? threshold = null, double? peak = null, string? note = null, Color? peakInk = null)
    {
        _value = value; _target = Gfx.Clamp01(norm); _ink = ink; _threshold = threshold; _peak = peak;
        if (note != null) _note = note;
        if (peakInk.HasValue) _peakInk = peakInk.Value;
    }

    protected override bool Animate(float dt)
    {
        if (Math.Abs(_target - _shown) > 0.0015) { _shown += (_target - _shown) * Math.Min(1.0, dt * 7.0); return true; }
        if (_shown != _target) { _shown = _target; return true; }
        return false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = S(2), w = Width - S(4);
        Gfx.Tracked(g, Heading.ToUpperInvariant(), Fonts.UiTiny, Pal.TextDim, x, S(1.5f), 0.85f);
        g.DrawString(_value, Fonts.MonoSmall, Cache.Brush(_ink), new RectangleF(x, S(1), w, S(14)), Gfx.SfTopRight);
        if (_note.Length > 0)
        {
            float vw = Gfx.Width(g, _value, Fonts.MonoSmall);
            g.DrawString(_note, Fonts.MonoTiny, Cache.Brush(Pal.TextFaint), new RectangleF(x, S(1), w - vw - S(8), S(14)), Gfx.SfTopRight);
        }

        float h = S(8);
        float top = Math.Max(S(16), Height - S(12));
        var track = new RectangleF(x, top, w, h);
        Gfx.FillRound(g, track, h / 2f, Pal.Mix(Pal.Bg, Color.Black, 0.1));
        Gfx.StrokeRound(g, track, h / 2f, Pal.Alpha(Pal.Line, 160));

        float fw = (float)(w * _shown);
        if (fw > 1.5f)
        {
            var fill = new RectangleF(x, top, fw, h);
            using var fp = Gfx.Round(fill, h / 2f);
            Grad.FillPathH(g, fp, fill, Pal.Mix(_ink, Color.Black, 0.35), Pal.Mix(_ink, Color.White, 0.25));
        }

        // segment ticks every 10%
        for (int i = 1; i < 10; i++)
            Gfx.HairV(g, x + w * i / 10f, top + 1, top + h - 1, Pal.Alpha(Pal.Bg, 190));

        if (_threshold.HasValue)
        {
            float tx = x + w * (float)Gfx.Clamp01(_threshold.Value);
            Gfx.HairV(g, tx, top - S(2), top + h + S(2), Pal.Alpha(Pal.Warn, 200));
            using var tri = new GraphicsPath();
            tri.AddPolygon(new[]
            {
                new PointF(tx, top - S(2)),
                new PointF(tx - S(2.6f), top - S(5.4f)),
                new PointF(tx + S(2.6f), top - S(5.4f))
            });
            g.FillPath(Cache.Brush(Pal.Warn), tri);
        }

        if (_peak.HasValue)
        {
            float px = x + w * (float)Gfx.Clamp01(_peak.Value);
            Gfx.HairV(g, px, top + h, top + h + S(3), Pal.Alpha(_peakInk, 190));
            using var tri = new GraphicsPath();
            tri.AddPolygon(new[]
            {
                new PointF(px, top + h + S(4)),
                new PointF(px - S(2.6f), top + h + S(0.4f)),
                new PointF(px + S(2.6f), top + h + S(0.4f))
            });
            g.FillPath(Cache.Brush(Pal.Alpha(_peakInk, 200)), tri);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Inventory gauge: symmetric around flat, with limit ticks, a danger zone and a value flag.
// ---------------------------------------------------------------------------------------------
internal sealed class InventoryGauge : UiControl
{
    double _target, _shown;
    long _pos8, _maxPos8 = 1, _base8, _qty8;
    bool _blocked;
    float _phase;

    public InventoryGauge()
    {
        Surface = Pal.Card;
        Height = 50;
    }

    public void Set(long pos8, long maxPos8, long base8, long qty8, bool blocked)
    {
        _pos8 = pos8; _maxPos8 = Math.Max(1, maxPos8); _base8 = base8; _qty8 = qty8; _blocked = blocked;
        _target = Math.Max(-1, Math.Min(1, pos8 / (double)_maxPos8));
    }

    protected override bool Animate(float dt)
    {
        _phase += dt;
        if (Math.Abs(_target - _shown) > 0.0015) { _shown += (_target - _shown) * Math.Min(1.0, dt * 8.0); return true; }
        if (_shown != _target) { _shown = _target; return true; }
        return _blocked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float pad = S(2);
        float w = Width - pad * 2;
        float mid = pad + w / 2f;
        float trackY = S(24), trackH = S(11);

        Gfx.Tracked(g, "SHORT", Fonts.UiTinyBold, Pal.DownLit, pad, S(2), 0.8f);
        float lw = Gfx.TrackedWidth(g, "LONG", Fonts.UiTinyBold, 0.8f);
        Gfx.Tracked(g, "LONG", Fonts.UiTinyBold, Pal.UpLit, pad + w - lw, S(2), 0.8f);

        string flat = "flat";
        Gfx.Tracked(g, flat, Fonts.UiTiny, Pal.TextFaint, mid - Gfx.TrackedWidth(g, flat, Fonts.UiTiny, 0.8f) / 2f, S(3), 0.8f);

        var track = new RectangleF(pad, trackY, w, trackH);
        Gfx.FillRound(g, track, trackH / 2f, Pal.Mix(Pal.Bg, Color.Black, 0.2));
        Gfx.StrokeRound(g, track, trackH / 2f, Pal.Alpha(Pal.Line, 150));

        // danger zone beyond 80% of the limit on both sides
        float dz = w * 0.5f * 0.2f;
        using (var dp = Gfx.Round(new RectangleF(track.Right - dz, trackY, dz, trackH), trackH / 2f))
            g.FillPath(Cache.Brush(Pal.Alpha(Pal.Down, 34)), dp);
        using (var dp = Gfx.Round(new RectangleF(track.X, trackY, dz, trackH), trackH / 2f))
            g.FillPath(Cache.Brush(Pal.Alpha(Pal.Down, 34)), dp);

        for (int i = 1; i < 8; i++)
        {
            if (i == 4) continue;
            float tx = pad + w * i / 8f;
            Gfx.HairV(g, tx, trackY + S(1.5f), trackY + trackH - S(1.5f), Pal.Alpha(Pal.Line, i % 4 == 0 ? 220 : 120));
        }

        float halfW = w / 2f;
        float vx = mid + (float)(_shown * halfW * 0.92);
        Color ink = Math.Abs(_shown) > 0.9 ? Pal.Danger : Math.Abs(_shown) > 0.6 ? Pal.Warn : Pal.Up;
        float bx = Math.Min(mid, vx), bw = Math.Abs(vx - mid);
        if (bw > 1f)
        {
            var fill = new RectangleF(bx, trackY, bw, trackH);
            using (var fp = Gfx.Round(fill, trackH / 2f))
            {
                if (_shown >= 0) Grad.FillPathH(g, fp, fill, ink, Pal.Mix(ink, Color.White, 0.35));
                else Grad.FillPathH(g, fp, fill, Pal.Mix(ink, Color.White, 0.35), ink);
            }
        }
        Gfx.HairV(g, mid, trackY - S(3), trackY + trackH + S(3), Pal.Alpha(Pal.LineBright, 220));

        // value flag
        float fx = Math.Max(pad + S(20), Math.Min(track.Right - S(20), vx));
        var flag = new RectangleF(fx - S(24), trackY + trackH + S(3), S(48), S(14));
        Gfx.FillRound(g, flag, S(4), Pal.Mix(ink, Color.Black, 0.55));
        Gfx.StrokeRound(g, flag, S(4), Pal.Alpha(ink, 170));
        g.DrawString((_pos8 / 1e8).ToString("F6", Fmt.Inv), Fonts.MonoTiny, Cache.Brush(Pal.Mix(ink, Color.White, 0.5)), flag, Gfx.SfCenter);
        using (var tri = new GraphicsPath())
        {
            tri.AddPolygon(new[]
            {
                new PointF(fx, trackY + trackH + S(1)),
                new PointF(fx - S(3), trackY + trackH + S(4)),
                new PointF(fx + S(3), trackY + trackH + S(4))
            });
            g.FillPath(Cache.Brush(Pal.Alpha(ink, 220)), tri);
        }

        if (_blocked)
        {
            float a = 0.45f + 0.35f * (float)Math.Sin(_phase * 5f);
            Gfx.StrokeRound(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(6), Pal.Alpha(Pal.Down, (int)(180 * a)), S(1.2f));
            Gfx.Tracked(g, "LIMIT BLOCKED", Fonts.UiTinyBold, Pal.Alpha(Pal.DownLit, (int)(230 * a)), pad, Height - S(12), 0.8f);
        }
        else
        {
            string lim = "-" + (_maxPos8 / 1e8).ToString("F6", Fmt.Inv);
            g.DrawString(lim, Fonts.MonoTiny, Cache.Brush(Pal.TextFaint), pad, Height - S(11), Gfx.SfTop);
            string lim2 = "+" + (_maxPos8 / 1e8).ToString("F6", Fmt.Inv);
            g.DrawString(lim2, Fonts.MonoTiny, Cache.Brush(Pal.TextFaint), new RectangleF(pad, Height - S(11), w, S(12)), Gfx.SfTopRight);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// One side of our live quote: price, lifecycle state, age and distance to the touch.
// ---------------------------------------------------------------------------------------------
internal sealed class QuoteRow : UiControl
{
    readonly bool _sell;
    long _px8, _touch8, _tick8, _intended8, _ageNs, _nowNs;
    int _dec = 2;
    Slot _slot;
    bool _blocked;
    bool _over;
    float _hover, _phase;

    public QuoteRow(bool sell)
    {
        _sell = sell;
        Surface = Pal.Card;
        Height = 34;
    }

    public void Set(long px8, long intended8, long touch8, long tick8, int dec, Slot slot, long sentNs, long nowNs, bool blocked)
    {
        _px8 = px8; _intended8 = intended8; _touch8 = touch8; _tick8 = Math.Max(1, tick8); _dec = dec;
        _slot = slot; _nowNs = nowNs; _blocked = blocked;
        _ageNs = sentNs == 0 || nowNs <= sentNs ? 0 : nowNs - sentNs;
        Invalidate();
    }

    protected override bool Animate(float dt)
    {
        _phase += dt;
        bool dirty = false;
        float target = _over ? 1f : 0f;
        if (Math.Abs(target - _hover) > 0.02f) { _hover += (target - _hover) * Math.Min(1f, dt * 12f); dirty = true; }
        if (_slot == Slot.PlacePending || _slot == Slot.CancelPending || _blocked) dirty = true;
        return dirty;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _over = true; }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _over = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color side = _sell ? Pal.Down : Pal.Up;
        if (_hover > 0.01f) Gfx.FillRound(g, new RectangleF(0, 1, Width, Height - 2), S(5), Pal.Alpha(Pal.Hover, (int)(70 * _hover)));

        // side chip
        var chip = new RectangleF(S(2), (Height - S(15)) / 2f, S(38), S(15));
        using (GraphicsPath cp = Gfx.Round(chip, S(4)))
        {
            Grad.FillPathV(g, cp, chip, Pal.Alpha(side, 60), Pal.Alpha(side, 22));
            using var pen = new Pen(Pal.Alpha(side, 180), 1f);
            g.DrawPath(pen, cp);
        }
        Gfx.TrackedCentered(g, _sell ? "SELL" : "BUY", Fonts.UiTinyBold, Pal.Mix(side, Pal.TextHi, 0.35), chip.X + chip.Width / 2f, chip.Y + S(3.2f), 0.8f);

        float x = chip.Right + S(9);
        bool live = _px8 > 0;
        Color priceInk = !live ? Pal.TextFaint : _blocked ? Pal.Warn : Pal.TextHi;
        string px = live ? Fmt.Price(_px8, _dec) : "-";
        g.DrawString(px, Fonts.MonoMid, Cache.Brush(priceInk), x, (Height - S(18)) / 2f - S(1), Gfx.SfTop);
        float pxw = Gfx.Width(g, px, Fonts.MonoMid);
        if (!live && _intended8 > 0)
        {
            string w = "want " + Fmt.Price(_intended8, _dec);
            g.DrawString(w, Fonts.MonoTiny, Cache.Brush(Pal.TextFaint), x + pxw + S(7), (Height - S(9)) / 2f, Gfx.SfTop);
        }

        // lifecycle pill
        string state = _slot switch
        {
            Slot.PlacePending => "SENDING",
            Slot.Live => "LIVE",
            Slot.CancelPending => "CANCEL",
            _ => "IDLE"
        };
        Color st = _slot switch
        {
            Slot.PlacePending => Pal.Info,
            Slot.Live => Pal.UpLit,
            Slot.CancelPending => Pal.Warn,
            _ => Pal.Neutral
        };
        float sw = Gfx.Width(g, state, Fonts.MonoTiny) + S(12);
        var pill = new RectangleF(Width - sw - S(2), (Height - S(15)) / 2f, sw, S(15));
        Gfx.FillRound(g, pill, S(7.5f), Pal.Alpha(st, _slot == Slot.Idle ? 20 : 42));
        Gfx.StrokeRound(g, pill, S(7.5f), Pal.Alpha(st, _slot == Slot.Idle ? 60 : 150));
        g.DrawString(state, Fonts.MonoTiny, Cache.Brush(Pal.Mix(st, Pal.TextHi, 0.25)), pill, Gfx.SfCenter);
        // gentle breathing dot while a command is in flight
        if (_slot == Slot.PlacePending || _slot == Slot.CancelPending)
            Gfx.Dot(g, pill.X + S(4), pill.Y + pill.Height / 2f, S(1.8f) * (0.7f + 0.3f * (float)Math.Sin(_phase * 6f)), st, true);

        // distance to the touch, in ticks
        float barW = S(78), barX = pill.X - barW - S(10);
        if (barX > x + pxw + S(6))
        {
            var bar = new RectangleF(barX, Height / 2f - S(2.5f), barW, S(5));
            Gfx.FillRound(g, bar, S(2.5f), Pal.Alpha(Pal.Bg, 200));
            long dist = live && _touch8 > 0 ? Math.Abs(_touch8 - _px8) / _tick8 : 0;
            double norm = live ? Gfx.Clamp01(dist / 12.0) : 0;
            if (live)
            {
                var fill = new RectangleF(bar.X, bar.Y, (float)(barW * norm), bar.Height);
                using var fp = Gfx.Round(fill, S(2.5f));
                g.FillPath(Cache.Brush(Pal.Alpha(side, 170)), fp);
                Gfx.Dot(g, bar.X + (float)(barW * norm), bar.Y + bar.Height / 2f, S(2.2f), Pal.Mix(side, Color.White, 0.4f));
            }
            string d = live ? dist + "t" : "-";
            g.DrawString(d, Fonts.MonoTiny, Cache.Brush(Pal.TextDim), new RectangleF(barX, bar.Y - S(11), barW, S(11)), Gfx.SfTopRight);
        }

        // age colouring: stale quotes are the thing that bites a maker
        if (_ageNs > 0)
        {
            double secs = _ageNs / 1e9;
            Color ageInk = secs > 4 ? Pal.DownLit : secs > 2 ? Pal.Warn : Pal.TextFaint;
            string age = Fmt.Age(_nowNs, _nowNs - _ageNs);
            float ax = Math.Min(barX > 0 ? barX - S(6) : Width - S(60), Width - S(40));
            g.DrawString(age, Fonts.MonoTiny, Cache.Brush(ageInk), new RectangleF(ax - S(44), (Height - S(9)) / 2f, S(44), S(12)), Gfx.SfTopRight);
        }

        if (_blocked)
            Gfx.HairH(g, Height - S(1), 0, Width, Pal.Alpha(Pal.Down, 130));
        else
            Gfx.HairH(g, Height - S(1), 0, Width, Pal.Alpha(Pal.LineSoft, 150));
    }
}

// ---------------------------------------------------------------------------------------------
// Card container: rounded surface, header with accent glyph, optional collapse.
// ---------------------------------------------------------------------------------------------
internal sealed class Card : Panel
{
    public readonly string Heading;
    public readonly string GlyphName;
    public Color Accent = Pal.Accent;
    public string Caption = "";
    public string Hint = "";
    public bool Collapsible = true;
    public bool Expanded = true;
    public int PrefHeight = 120;
    public Action<Card>? Toggled;

    readonly Panel _body = new() { Dock = DockStyle.Fill };

    public Card(string heading, string glyphName, Color accent, int prefHeight)
    {
        Heading = heading; GlyphName = glyphName; Accent = accent;
        PrefHeight = prefHeight;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        BackColor = Pal.Panel;
        _body.BackColor = Pal.Card;
        Controls.Add(_body);
        UpdatePadding();
    }

    public Panel Body => _body;

    protected float Dpi => DeviceDpi / 96f;
    protected float S(float v) => v * DeviceDpi / 96f;

    float HeaderH => S(30);

    public int CurrentHeight => Expanded ? (int)S(PrefHeight) : (int)S(32);

    void UpdatePadding()
    {
        Padding = new Padding((int)S(12), (int)HeaderH + (Expanded ? (int)S(2) : 0), (int)S(12), (int)S(10));
        _body.Visible = Expanded;
    }

    public void SetExpanded(bool on)
    {
        Expanded = on;
        UpdatePadding();
        Height = CurrentHeight;
        Toggled?.Invoke(this);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Collapsible && e.Y <= HeaderH) SetExpanded(!Expanded);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = Collapsible && e.Y <= HeaderH ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float radius = S(9);
        Gfx.DropShadow(g, r, radius, 55, S(2));
        using (GraphicsPath p = Gfx.Round(r, radius))
        {
            g.FillPath(Cache.Brush(Pal.Card), p);
            // header tint, clipped to the rounded silhouette
            var hdr0 = new RectangleF(r.X, r.Y, r.Width, HeaderH);
            var st = g.Save();
            g.SetClip(p, CombineMode.Intersect);
            using (var brush = new LinearGradientBrush(hdr0, Pal.Alpha(Accent, 34), Pal.Alpha(Accent, 0), 90f))
                g.FillRectangle(brush, hdr0);
            Gfx.HairH(g, hdr0.Bottom, hdr0.X + S(6), hdr0.Right - S(6), Pal.Alpha(Pal.LineSoft, 170));
            g.Restore(st);
            // inner top highlight
            using var hl = new Pen(Color.FromArgb(20, 255, 255, 255), 1f);
            g.DrawLine(hl, r.X + radius * 0.9f, r.Y + 1f, r.Right - radius * 0.9f, r.Y + 1f);
            using var pen = new Pen(Pal.LineSoft, 1f);
            g.DrawPath(pen, p);
        }

        var hdr = new RectangleF(r.X, r.Y, r.Width, HeaderH);
        var chip = new RectangleF(hdr.X + S(10), hdr.Y + (hdr.Height - S(17)) / 2f, S(17), S(17));
        using (GraphicsPath cp = Gfx.Round(chip, S(5)))
        {
            Grad.FillPathV(g, cp, chip, Pal.Alpha(Accent, 220), Pal.Alpha(Accent, 110));
            using var pen = new Pen(Pal.Alpha(Accent, 230), 1f);
            g.DrawPath(pen, cp);
        }
        Icons.Draw(g, GlyphName switch
        {
            "gauge" => Glyph.Gauge,
            "bolt" => Glyph.Bolt,
            "layers" => Glyph.Layers,
            "target" => Glyph.Target,
            "wave" => Glyph.Wave,
            "depth" => Glyph.Depth,
            "pulse" => Glyph.Pulse,
            "clock" => Glyph.Clock,
            _ => Glyph.Link
        }, chip, Color.FromArgb(235, 250, 255), S(1.4f));

        Gfx.Tracked(g, Heading.ToUpperInvariant(), Fonts.UiTinyBold, Pal.TextHi, chip.Right + S(7), hdr.Y + (hdr.Height - S(11)) / 2f, 1.0f);

        float right = hdr.Right - S(10);
        if (Collapsible)
        {
            Icons.Draw(g, Expanded ? Glyph.ChevronUp : Glyph.ChevronDown, new RectangleF(right - S(12), hdr.Y + (hdr.Height - S(12)) / 2f, S(12), S(12)), Pal.TextDim, S(1.4f));
            right -= S(18);
        }
        if (Caption.Length > 0)
        {
            float left = chip.Right + S(7) + Gfx.TrackedWidth(g, Heading.ToUpperInvariant(), Fonts.UiTinyBold, 1.0f) + S(8);
            float cw = right - left;
            if (cw > S(24))
                g.DrawString(Caption, Fonts.MonoTiny, Cache.Brush(Pal.TextDim), new RectangleF(left, hdr.Y, cw, hdr.Height), Gfx.SfRight);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Vertical stack that lays cards out by their preferred height.
// ---------------------------------------------------------------------------------------------
internal sealed class CardStack : Panel
{
    public int Gap = 9;
    public int MarginY = 8;

    public CardStack()
    {
        AutoScroll = true;
        BackColor = Pal.Panel;
    }

    public void Add(Card c)
    {
        c.Toggled = _ => Relayout();
        Controls.Add(c);
        Relayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    public void Relayout()
    {
        int y = MarginY;
        int w = ClientSize.Width - 12;
        if (w < 40) return;
        foreach (Control c in Controls)
        {
            if (c is Card card)
            {
                card.Bounds = new Rectangle(6, y, w, card.CurrentHeight);
                y += card.CurrentHeight + Gap;
            }
            else
            {
                c.Bounds = new Rectangle(6, y, w, c.Height);
                y += c.Height + Gap;
            }
        }
    }
}

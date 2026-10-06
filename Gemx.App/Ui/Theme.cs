using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace Gemx.App;

// ---------------------------------------------------------------------------------------------
// Design tokens. Every colour, radius and hairline used by the terminal comes from here so the
// surface stays visually consistent no matter which control paints.
// ---------------------------------------------------------------------------------------------
internal static class Pal
{
    // surfaces, darkest to lightest
    public static readonly Color Bg = Color.FromArgb(9, 12, 17);
    public static readonly Color Panel = Color.FromArgb(15, 19, 26);
    public static readonly Color Card = Color.FromArgb(20, 25, 34);
    public static readonly Color CardTop = Color.FromArgb(24, 30, 41);
    public static readonly Color Raise = Color.FromArgb(30, 38, 51);
    public static readonly Color Hover = Color.FromArgb(38, 48, 64);

    // strokes
    public static readonly Color Line = Color.FromArgb(36, 46, 61);
    public static readonly Color LineSoft = Color.FromArgb(27, 35, 47);
    public static readonly Color LineBright = Color.FromArgb(58, 72, 94);

    // type
    public static readonly Color TextHi = Color.FromArgb(233, 239, 247);
    public static readonly Color Text = Color.FromArgb(172, 184, 200);
    public static readonly Color TextDim = Color.FromArgb(120, 133, 151);
    public static readonly Color TextFaint = Color.FromArgb(83, 95, 113);

    // semantics
    public static readonly Color Accent = Color.FromArgb(56, 189, 248);
    public static readonly Color AccentDeep = Color.FromArgb(14, 116, 178);
    public static readonly Color Violet = Color.FromArgb(167, 139, 250);
    public static readonly Color Up = Color.FromArgb(34, 197, 94);
    public static readonly Color UpLit = Color.FromArgb(74, 222, 128);
    public static readonly Color Down = Color.FromArgb(239, 68, 68);
    public static readonly Color DownLit = Color.FromArgb(248, 113, 113);
    public static readonly Color Warn = Color.FromArgb(251, 191, 36);
    public static readonly Color Danger = Color.FromArgb(244, 63, 94);
    public static readonly Color Info = Color.FromArgb(96, 165, 250);
    public static readonly Color Neutral = Color.FromArgb(113, 128, 148);

    public static Color Alpha(Color c, int a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t)
    {
        if (t < 0) t = 0; if (t > 1) t = 1;
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    // state vocabulary shared by the state chip, pills and alerts
    public static Color StateColor(StateTone t) => t switch
    {
        StateTone.Good => UpLit,
        StateTone.Warm => Warn,
        StateTone.Bad => DownLit,
        StateTone.Idle => TextDim,
        _ => Info
    };
}

internal enum StateTone { Idle, Info, Good, Warm, Bad }

// ---------------------------------------------------------------------------------------------
// Cached fonts and number formatting.
// ---------------------------------------------------------------------------------------------
internal static class Fonts
{
    public static readonly Font Ui = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font UiBold = new("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font UiSmall = new("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font UiSmallBold = new("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font UiTiny = new("Segoe UI", 7.25f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font UiTinyBold = new("Segoe UI", 7.25f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font Title = new("Segoe UI", 11.5f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font Mono = new("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font MonoBold = new("Consolas", 9f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font MonoSmall = new("Consolas", 8f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font MonoTiny = new("Consolas", 7.25f, FontStyle.Regular, GraphicsUnit.Point);
    // status-bar pills: one step larger than the old 7.25 pt micro type so readings stay legible
    public static readonly Font Pill = new("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font PillValue = new("Consolas", 8f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font MonoMid = new("Consolas", 11f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font MonoBig = new("Consolas", 15f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font MonoHuge = new("Consolas", 19f, FontStyle.Regular, GraphicsUnit.Point);
}

internal static class Fmt
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // price with thousands separators, e.g. 96,412.31
    public static string Price(long x8, int dec)
    {
        if (x8 == 0) return "-";
        return (x8 / 1e8).ToString("N" + dec, Inv);
    }

    public static string Price(double px, int dec) => px == 0 ? "-" : px.ToString("N" + dec, Inv);

    // plain decimal, fixed width so columns line up, e.g. 0.000100
    public static string Qty(long x8, int dec) => (x8 / 1e8).ToString("F" + dec, Inv);

    public static string Qty(double q, int dec) => q.ToString("F" + dec, Inv);

    public static string Signed(double v, int dec)
    {
        string s = Math.Abs(v).ToString("F" + dec, Inv);
        return v > 0 ? "+" + s : v < 0 ? "-" + s : s;
    }

    public static string SignedUsd(double v) => (v >= 0 ? "+$" : "-$") + Math.Abs(v).ToString("N2", Inv);

    // 1,234,567 -> 1.23M ; keeps busy counters readable in tight space
    public static string Count(long n)
    {
        double a = Math.Abs((double)n);
        string s;
        if (a >= 1e9) s = (n / 1e9).ToString("F2", Inv) + "B";
        else if (a >= 1e6) s = (n / 1e6).ToString("F2", Inv) + "M";
        else if (a >= 1e4) s = (n / 1e3).ToString("F1", Inv) + "k";
        else s = n.ToString("N0", Inv);
        return s;
    }

    public static string CountExact(long n) => n.ToString("N0", Inv);

    // sub-millisecond lags deserve to be shown in microseconds
    public static string Lag(long ns)
    {
        double ms = ns / 1e6;
        if (Math.Abs(ms) < 1) return (ns / 1e3).ToString("F0", Inv) + " us";
        if (Math.Abs(ms) < 1000) return ms.ToString("F2", Inv) + " ms";
        return (ms / 1000).ToString("F2", Inv) + " s";
    }

    public static string Bytes(long b)
    {
        if (b >= 1 << 20) return (b / (double)(1 << 20)).ToString("F1", Inv) + " MB";
        if (b >= 1 << 10) return (b / (double)(1 << 10)).ToString("F1", Inv) + " KB";
        return b + " B";
    }

    public static string Age(long nowNs, long sentNs)
    {
        if (sentNs == 0 || nowNs <= sentNs) return "-";
        double s = (nowNs - sentNs) / 1e9;
        if (s < 1) return (s * 1000).ToString("F0", Inv) + " ms";
        if (s < 60) return s.ToString("F1", Inv) + " s";
        return (s / 60).ToString("F1", Inv) + " m";
    }

    public static string TimeOfDay(long ns)
        => new DateTimeOffset(ns / 100, TimeSpan.Zero).ToLocalTime().ToString("HH:mm:ss", Inv);

    public static string TimeOfDay(DateTime t) => t.ToString("HH:mm:ss", Inv);

    public static string TimeOfDayMs(DateTime t) => t.ToString("HH:mm:ss.fff", Inv);

    public static string Clock(DateTime t) => t.ToString("HH:mm:ss", Inv);

    public static string Tf(TF tf) => tf switch
    {
        TF.M1 => "1m",
        TF.M5 => "5m",
        TF.M15 => "15m",
        TF.M30 => "30m",
        TF.H1 => "1h",
        TF.D1 => "1d",
        TF.W1 => "1w",
        TF.M1_30D => "1M",
        _ => "?"
    };

    public static string TfLong(TF tf) => tf switch
    {
        TF.M1 => "1 minute",
        TF.M5 => "5 minutes",
        TF.M15 => "15 minutes",
        TF.M30 => "30 minutes",
        TF.H1 => "1 hour",
        TF.D1 => "1 day",
        TF.W1 => "1 week",
        TF.M1_30D => "1 month",
        _ => "?"
    };

    // 92400 -> "1d 1h" style uptime
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return $"{t.Days}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return t.TotalSeconds.ToString("F1", Inv) + "s";
    }
}

// ---------------------------------------------------------------------------------------------
// Brush / pen / gradient pooling. Painting happens 10-30 times a second across a dozen controls,
// so nothing in the paint path allocates a GDI+ object per frame if it can be pooled.
// ---------------------------------------------------------------------------------------------
internal static class Cache
{
    static readonly Dictionary<int, SolidBrush> _brushes = new(64);
    static readonly Dictionary<long, Pen> _pens = new(64);

    public static SolidBrush Brush(Color c)
    {
        int k = c.ToArgb();
        if (!_brushes.TryGetValue(k, out SolidBrush? b)) { b = new SolidBrush(c); _brushes[k] = b; }
        return b;
    }

    public static Pen Pen(Color c, float w = 1f)
    {
        long k = ((long)c.ToArgb() << 16) | (long)(w * 10);
        if (!_pens.TryGetValue(k, out Pen? p))
        {
            p = new Pen(c, w) { LineJoin = LineJoin.Round };
            _pens[k] = p;
        }
        return p;
    }

    public static Pen DashPen(Color c, float w = 1f, DashStyle style = DashStyle.Dash)
    {
        // dashed pens are rare enough that a per-call object is not worth pooling
        return new Pen(c, w) { DashStyle = style, LineJoin = LineJoin.Round };
    }
}

internal static class Grad
{
    static readonly Dictionary<(int, int, int, int), LinearGradientBrush> _v = new(32);
    static readonly Dictionary<(int, int, int, int), LinearGradientBrush> _h = new(32);

    // pooled brushes are native resources: release them before dropping the cache
    static void Drop(Dictionary<(int, int, int, int), LinearGradientBrush> pool)
    {
        foreach (var kv in pool) kv.Value.Dispose();
        pool.Clear();
    }

    public static LinearGradientBrush Vertical(RectangleF r, Color top, Color bottom)
    {
        int w = (int)Math.Max(1, Math.Round(r.Width)), h = (int)Math.Max(1, Math.Round(r.Height));
        var k = (w, h, top.ToArgb(), bottom.ToArgb());
        if (!_v.TryGetValue(k, out LinearGradientBrush? b))
        {
            if (_v.Count > 128) Drop(_v);
            b = new LinearGradientBrush(new RectangleF(0, 0, w, h), top, bottom, 90f);
            _v[k] = b;
        }
        return b;
    }

    public static LinearGradientBrush Horizontal(RectangleF r, Color left, Color right)
    {
        int w = (int)Math.Max(1, Math.Round(r.Width)), h = (int)Math.Max(1, Math.Round(r.Height));
        var k = (w, h, left.ToArgb(), right.ToArgb());
        if (!_h.TryGetValue(k, out LinearGradientBrush? b))
        {
            if (_h.Count > 128) Drop(_h);
            b = new LinearGradientBrush(new RectangleF(0, 0, w, h), left, right, 0f);
            _h[k] = b;
        }
        return b;
    }

    // gradient fill of an arbitrary rectangle: caller translates the canvas to the rect origin
    public static void FillV(Graphics g, RectangleF r, Color top, Color bottom)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var gs = g.Save();
        g.TranslateTransform(r.X, r.Y);
        g.FillRectangle(Vertical(r, top, bottom), new RectangleF(0, 0, r.Width, r.Height));
        g.Restore(gs);
    }

    public static void FillH(Graphics g, RectangleF r, Color left, Color right)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var gs = g.Save();
        g.TranslateTransform(r.X, r.Y);
        g.FillRectangle(Horizontal(r, left, right), new RectangleF(0, 0, r.Width, r.Height));
        g.Restore(gs);
    }

    /// Clips to an absolute-space path, paints a cached vertical gradient inside it.
    public static void FillPathV(Graphics g, GraphicsPath path, RectangleF r, Color top, Color bottom)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var gs = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        g.TranslateTransform(r.X, r.Y);
        g.FillRectangle(Vertical(r, top, bottom), new RectangleF(0, 0, r.Width, r.Height));
        g.Restore(gs);
    }

    /// Clips to an absolute-space path, paints a cached horizontal gradient inside it.
    public static void FillPathH(Graphics g, GraphicsPath path, RectangleF r, Color left, Color right)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var gs = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        g.TranslateTransform(r.X, r.Y);
        g.FillRectangle(Horizontal(r, left, right), new RectangleF(0, 0, r.Width, r.Height));
        g.Restore(gs);
    }
}

// ---------------------------------------------------------------------------------------------
// Drawing primitives: rounded geometry, glows, tracked (letter-spaced) type, metrics.
// ---------------------------------------------------------------------------------------------
internal static class Gfx
{
    public static readonly StringFormat Sf = new(StringFormatFlags.NoWrap)
    {
        Trimming = StringTrimming.EllipsisCharacter
    };

    public static readonly StringFormat SfTop = new(StringFormatFlags.NoWrap)
    {
        Trimming = StringTrimming.EllipsisCharacter,
        LineAlignment = StringAlignment.Near
    };

    public static readonly StringFormat SfMid = new(StringFormatFlags.NoWrap)
    {
        LineAlignment = StringAlignment.Center,
        Alignment = StringAlignment.Near
    };

    public static readonly StringFormat SfRight = new(StringFormatFlags.NoWrap)
    {
        LineAlignment = StringAlignment.Center,
        Alignment = StringAlignment.Far
    };

    public static readonly StringFormat SfCenter = new(StringFormatFlags.NoWrap)
    {
        LineAlignment = StringAlignment.Center,
        Alignment = StringAlignment.Center
    };

    public static readonly StringFormat SfTopRight = new(StringFormatFlags.NoWrap)
    {
        LineAlignment = StringAlignment.Near,
        Alignment = StringAlignment.Far
    };

    static readonly Dictionary<(string, string, float, int, int), SizeF> _measure = new(512);

    /// A Graphics used only for measuring. GDI+ scales point fonts by the graphics DPI, so a
    /// measurement surface must match the DPI the text will actually be drawn at — otherwise
    /// width calculations are wrong on any monitor that is not at 96 DPI.
    static readonly Dictionary<int, (Bitmap Bmp, Graphics G)> _measureG = new(4);

    public static Graphics MeasureGraphics(int dpi)
    {
        if (dpi < 96) dpi = 96;
        if (!_measureG.TryGetValue(dpi, out var t))
        {
            var bmp = new Bitmap(1, 1);
            bmp.SetResolution(dpi, dpi);
            t = (bmp, Graphics.FromImage(bmp));
            _measureG[dpi] = t;
        }
        return t.G;
    }

    public static SizeF Measure(Graphics g, string s, Font f, StringFormat? sf = null)
    {
        var key = (s, f.FontFamily.Name, f.Size, (int)f.Style, (int)g.DpiX);
        if (sf == null && _measure.TryGetValue(key, out SizeF cached)) return cached;
        SizeF sz = g.MeasureString(s, f, new SizeF(8192, 512), sf ?? Sf);
        if (sf == null)
        {
            if (_measure.Count > 4096) _measure.Clear();
            _measure[key] = sz;
        }
        return sz;
    }

    public static float Width(Graphics g, string s, Font f) => Measure(g, s, f).Width;

    public static void Str(Graphics g, string s, Font f, Color c, float x, float y)
        => g.DrawString(s, f, Cache.Brush(c), x, y, SfTop);

    public static void Str(Graphics g, string s, Font f, Color c, RectangleF r, StringFormat sf)
        => g.DrawString(s, f, Cache.Brush(c), r, sf);

    public static void StrRight(Graphics g, string s, Font f, Color c, float right, float y)
    {
        float w = Width(g, s, f);
        g.DrawString(s, f, Cache.Brush(c), right - w - 1, y, SfTop);
    }

    public static void StrRightMid(Graphics g, string s, Font f, Color c, float right, float cy)
        => g.DrawString(s, f, Cache.Brush(c), new RectangleF(right - 400, cy - 10, 400, 20), SfRight);

    // letter-spaced uppercase label used for section headers and pills
    public static float TrackedWidth(Graphics g, string s, Font f, float extra)
    {
        float w = 0;
        for (int i = 0; i < s.Length; i++)
        {
            string t = s[i] == ' ' ? "n" : s[i].ToString();
            w += Width(g, t, f) + extra;
        }
        return s.Length > 0 ? w - extra : 0;
    }

    public static void Tracked(Graphics g, string s, Font f, Color c, float x, float y, float extra = 0.6f)
    {
        Brush b = Cache.Brush(c);
        float dx = x;
        for (int i = 0; i < s.Length; i++)
        {
            string t = s[i].ToString();
            g.DrawString(t, f, b, dx, y, SfTop);
            float w = s[i] == ' ' ? Width(g, "n", f) : Width(g, t, f);
            dx += w + extra;
        }
    }

    public static void TrackedCentered(Graphics g, string s, Font f, Color c, float cx, float y, float extra = 0.6f)
        => Tracked(g, s, f, c, cx - TrackedWidth(g, s, f, extra) / 2f, y, extra);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Max(1f, radius * 2f);
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        if (d <= 1.2f)
        {
            p.AddRectangle(r);
            return p;
        }
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, RectangleF r, float radius, Color c)
    {
        using var p = Round(r, radius);
        g.FillPath(Cache.Brush(c), p);
    }

    public static void FillRoundV(Graphics g, RectangleF r, float radius, Color top, Color bottom)
    {
        using var p = Round(r, radius);
        var gs = g.Save();
        g.TranslateTransform(r.X, r.Y);
        g.FillPath(Grad.Vertical(r, top, bottom), p);
        g.Restore(gs);
    }

    public static void StrokeRound(Graphics g, RectangleF r, float radius, Color c, float w = 1f)
    {
        using var p = Round(r, radius);
        g.DrawPath(Cache.Pen(c, w), p);
    }

    public static void Hair(Graphics g, float x0, float y0, float x1, float y1, Color c)
        => g.DrawLine(Cache.Pen(c), x0, y0, x1, y1);

    public static void HairV(Graphics g, float x, float y0, float y1, Color c)
        => g.DrawLine(Cache.Pen(c), x, y0, x, y1);

    public static void HairH(Graphics g, float y, float x0, float x1, Color c)
        => g.DrawLine(Cache.Pen(c), x0, y, x1, y);

    // a soft halo around a polyline: wide translucent stroke, then the crisp stroke on top
    public static void GlowLine(Graphics g, PointF[] pts, Color c, float w)
    {
        if (pts.Length < 2) return;
        using var wide = new Pen(Pal.Alpha(c, 40), w * 3.6f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(wide, pts);
        using var mid = new Pen(Pal.Alpha(c, 70), w * 1.9f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(mid, pts);
    }

    public static void Dot(Graphics g, float cx, float cy, float r, Color c, bool glow = false)
    {
        if (glow)
        {
            g.FillEllipse(Cache.Brush(Pal.Alpha(c, 38)), cx - r * 2.4f, cy - r * 2.4f, r * 4.8f, r * 4.8f);
            g.FillEllipse(Cache.Brush(Pal.Alpha(c, 70)), cx - r * 1.6f, cy - r * 1.6f, r * 3.2f, r * 3.2f);
        }
        g.FillEllipse(Cache.Brush(c), cx - r, cy - r, r * 2, r * 2);
    }

    public static void DropShadow(Graphics g, RectangleF r, float radius, int alpha = 70, float dy = 3f)
    {
        var o = new RectangleF(r.X, r.Y + dy, r.Width, r.Height);
        for (int i = 3; i >= 1; i--)
        {
            var rr = RectangleF.Inflate(o, i, i);
            using var p = Round(rr, radius + i);
            g.FillPath(Cache.Brush(Color.FromArgb(alpha / (i + 1), 0, 0, 0)), p);
        }
    }

    public static PointF[] Points(List<PointF> src) => src.ToArray();

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    public static string Trim(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "\u2026";

    // picks a rounded 1-2-5 grid step so an axis has at most maxSteps lines
    public static double NiceStep(double span, int maxSteps)
    {
        if (span <= 0 || maxSteps < 1) return 1;
        double raw = span / maxSteps;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        double step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return step * mag;
    }
}

// ---------------------------------------------------------------------------------------------
// Vector glyphs. Everything is drawn as paths so there are no image assets to ship and the
// chrome stays crisp at any DPI.
// ---------------------------------------------------------------------------------------------
internal enum Glyph { None, Play, Stop, Skull, Gear, ChevronDown, ChevronUp, ChevronLeft, ChevronRight, Copy, Broom, Collapse, Expand, Pulse, Lock, Bolt, Link, Warn, Close, Wave, Depth, Layers, Gauge, Clock, Target, Check }

internal static class Icons
{
    public static void Draw(Graphics g, Glyph gl, RectangleF r, Color c, float weight = 1.6f)
    {
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        float s = Math.Min(r.Width, r.Height);
        float h = s / 2f;
        using var p = new Pen(c, weight) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        Brush b = Cache.Brush(c);
        switch (gl)
        {
            case Glyph.Play:
                g.FillPolygon(b, new[]
                {
                    new PointF(cx - h * 0.62f, cy - h * 0.82f),
                    new PointF(cx + h * 0.86f, cy),
                    new PointF(cx - h * 0.62f, cy + h * 0.82f)
                });
                break;
            case Glyph.Stop:
                using (var rp = Gfx.Round(new RectangleF(cx - h * 0.66f, cy - h * 0.66f, h * 1.32f, h * 1.32f), h * 0.22f))
                    g.FillPath(b, rp);
                break;
            case Glyph.Skull:
                {
                    float bw = h * 1.5f, bh = h * 1.35f;
                    using (var hp = new GraphicsPath())
                    {
                        hp.AddArc(cx - bw / 2f, cy - bh * 0.72f, bw, bh, 180, 180);
                        hp.AddLine(cx + bw / 2f, cy + bh * 0.06f, cx + bw * 0.34f, cy + bh * 0.06f);
                        hp.AddLine(cx + bw * 0.34f, cy + bh * 0.06f, cx + bw * 0.30f, cy + bh * 0.62f);
                        hp.AddLine(cx + bw * 0.30f, cy + bh * 0.62f, cx - bw * 0.30f, cy + bh * 0.62f);
                        hp.AddLine(cx - bw * 0.30f, cy + bh * 0.62f, cx - bw * 0.34f, cy + bh * 0.06f);
                        hp.CloseFigure();
                        g.FillPath(b, hp);
                    }
                    g.FillEllipse(Cache.Brush(Pal.Bg), cx - h * 0.46f, cy - h * 0.30f, h * 0.34f, h * 0.34f);
                    g.FillEllipse(Cache.Brush(Pal.Bg), cx + h * 0.12f, cy - h * 0.30f, h * 0.34f, h * 0.34f);
                    break;
                }
            case Glyph.Gear:
                {
                    int teeth = 8;
                    using var gp = new GraphicsPath();
                    for (int i = 0; i < teeth; i++)
                    {
                        double a0 = i * 2 * Math.PI / teeth;
                        double a1 = a0 + Math.PI / teeth * 0.62;
                        gp.AddLine(cx + (float)(Math.Cos(a0) * h * 0.95), cy + (float)(Math.Sin(a0) * h * 0.95),
                                   cx + (float)(Math.Cos(a1) * h * 0.95), cy + (float)(Math.Sin(a1) * h * 0.95));
                        gp.AddArc(cx - h * 0.66f, cy - h * 0.66f, h * 1.32f, h * 1.32f, (float)(a1 * 180 / Math.PI), (float)((2 * Math.PI / teeth - (a1 - a0)) * 180 / Math.PI));
                    }
                    gp.CloseFigure();
                    g.DrawPath(p, gp);
                    g.DrawEllipse(p, cx - h * 0.30f, cy - h * 0.30f, h * 0.60f, h * 0.60f);
                    break;
                }
            case Glyph.ChevronDown:
            case Glyph.ChevronUp:
                {
                    float d = gl == Glyph.ChevronDown ? 1 : -1;
                    g.DrawLines(p, new[]
                    {
                        new PointF(cx - h * 0.55f, cy - d * h * 0.26f),
                        new PointF(cx, cy + d * h * 0.30f),
                        new PointF(cx + h * 0.55f, cy - d * h * 0.26f)
                    });
                    break;
                }
            case Glyph.ChevronRight:
                g.DrawLines(p, new[]
                {
                    new PointF(cx - h * 0.26f, cy - h * 0.55f),
                    new PointF(cx + h * 0.30f, cy),
                    new PointF(cx - h * 0.26f, cy + h * 0.55f)
                });
                break;
            case Glyph.ChevronLeft:
                g.DrawLines(p, new[]
                {
                    new PointF(cx + h * 0.26f, cy - h * 0.55f),
                    new PointF(cx - h * 0.30f, cy),
                    new PointF(cx + h * 0.26f, cy + h * 0.55f)
                });
                break;
            case Glyph.Copy:
                g.DrawRectangle(p, cx - h * 0.72f, cy - h * 0.72f, h * 1.05f, h * 1.05f);
                g.DrawRectangle(p, cx - h * 0.28f, cy - h * 0.28f, h * 1.05f, h * 1.05f);
                break;
            case Glyph.Broom:
                g.DrawLine(p, cx + h * 0.62f, cy - h * 0.62f, cx - h * 0.10f, cy + h * 0.06f);
                using (var bp = new GraphicsPath())
                {
                    bp.AddLine(cx - h * 0.62f, cy + h * 0.10f, cx - h * 0.10f, cy + h * 0.06f);
                    bp.AddLine(cx - h * 0.10f, cy + h * 0.06f, cx - h * 0.20f, cy + h * 0.64f);
                    bp.AddLine(cx - h * 0.20f, cy + h * 0.64f, cx - h * 0.70f, cy + h * 0.58f);
                    bp.CloseFigure();
                    g.DrawPath(p, bp);
                }
                break;
            case Glyph.Collapse:
                g.DrawLines(p, new[]
                {
                    new PointF(cx - h * 0.6f, cy - h * 0.5f), new PointF(cx + h * 0.6f, cy - h * 0.5f),
                    new PointF(cx, cy + h * 0.35f)
                });
                g.DrawLine(p, cx - h * 0.55f, cy + h * 0.62f, cx + h * 0.55f, cy + h * 0.62f);
                break;
            case Glyph.Expand:
                g.DrawLines(p, new[]
                {
                    new PointF(cx - h * 0.6f, cy + h * 0.5f), new PointF(cx + h * 0.6f, cy + h * 0.5f),
                    new PointF(cx, cy - h * 0.35f)
                });
                g.DrawLine(p, cx - h * 0.55f, cy - h * 0.62f, cx + h * 0.55f, cy - h * 0.62f);
                break;
            case Glyph.Pulse:
                g.DrawLines(p, new[]
                {
                    new PointF(cx - h * 0.9f, cy),
                    new PointF(cx - h * 0.35f, cy),
                    new PointF(cx - h * 0.12f, cy - h * 0.7f),
                    new PointF(cx + h * 0.16f, cy + h * 0.7f),
                    new PointF(cx + h * 0.42f, cy),
                    new PointF(cx + h * 0.9f, cy)
                });
                break;
            case Glyph.Lock:
                g.DrawRectangle(p, cx - h * 0.6f, cy - h * 0.1f, h * 1.2f, h * 0.8f);
                g.DrawArc(p, cx - h * 0.38f, cy - h * 0.68f, h * 0.76f, h * 0.9f, 180, 180);
                break;
            case Glyph.Bolt:
                g.FillPolygon(b, new[]
                {
                    new PointF(cx + h * 0.25f, cy - h * 0.9f),
                    new PointF(cx - h * 0.55f, cy + h * 0.16f),
                    new PointF(cx - h * 0.05f, cy + h * 0.16f),
                    new PointF(cx - h * 0.25f, cy + h * 0.9f),
                    new PointF(cx + h * 0.55f, cy - h * 0.18f),
                    new PointF(cx + h * 0.06f, cy - h * 0.18f)
                });
                break;
            case Glyph.Link:
                g.DrawArc(p, cx - h * 0.85f, cy - h * 0.45f, h * 0.9f, h * 0.9f, 40, 300);
                g.DrawArc(p, cx - h * 0.05f, cy - h * 0.45f, h * 0.9f, h * 0.9f, 220, 300);
                break;
            case Glyph.Warn:
                g.DrawLines(p, new[]
                {
                    new PointF(cx, cy - h * 0.78f), new PointF(cx + h * 0.88f, cy + h * 0.66f),
                    new PointF(cx - h * 0.88f, cy + h * 0.66f), new PointF(cx, cy - h * 0.78f)
                });
                g.DrawLine(p, cx, cy - h * 0.3f, cx, cy + h * 0.16f);
                g.FillEllipse(b, cx - weight * 0.5f, cy + h * 0.34f, weight, weight);
                break;
            case Glyph.Close:
                g.DrawLine(p, cx - h * 0.55f, cy - h * 0.55f, cx + h * 0.55f, cy + h * 0.55f);
                g.DrawLine(p, cx + h * 0.55f, cy - h * 0.55f, cx - h * 0.55f, cy + h * 0.55f);
                break;
            case Glyph.Wave:
                {
                    using var wp = new GraphicsPath();
                    wp.AddBezier(cx - h * 0.95f, cy, cx - h * 0.5f, cy - h * 0.95f, cx - h * 0.2f, cy + h * 0.95f, cx, cy);
                    wp.AddBezier(cx, cy, cx + h * 0.3f, cy - h * 0.95f, cx + h * 0.6f, cy + h * 0.95f, cx + h * 0.95f, cy);
                    g.DrawPath(p, wp);
                    break;
                }
            case Glyph.Depth:
                for (int i = 0; i < 4; i++)
                {
                    float w = h * (1.5f - i * 0.28f);
                    g.DrawLine(p, cx - w / 2f, cy - h * 0.66f + i * h * 0.44f, cx + w / 2f, cy - h * 0.66f + i * h * 0.44f);
                }
                break;
            case Glyph.Layers:
                g.DrawLines(p, new[]
                {
                    new PointF(cx - h * 0.85f, cy - h * 0.22f), new PointF(cx, cy - h * 0.82f),
                    new PointF(cx + h * 0.85f, cy - h * 0.22f), new PointF(cx, cy + h * 0.38f),
                    new PointF(cx - h * 0.85f, cy - h * 0.22f)
                });
                g.DrawLine(p, cx - h * 0.6f, cy + h * 0.28f, cx, cy + h * 0.78f);
                g.DrawLine(p, cx + h * 0.6f, cy + h * 0.28f, cx, cy + h * 0.78f);
                break;
            case Glyph.Gauge:
                g.DrawArc(p, cx - h * 0.9f, cy - h * 0.9f, h * 1.8f, h * 1.8f, 180, 180);
                g.DrawLine(p, cx, cy + h * 0.05f, cx + h * 0.55f, cy - h * 0.35f);
                g.FillEllipse(b, cx - weight, cy + h * 0.05f - weight, weight * 2, weight * 2);
                break;
            case Glyph.Clock:
                g.DrawEllipse(p, cx - h * 0.82f, cy - h * 0.82f, h * 1.64f, h * 1.64f);
                g.DrawLine(p, cx, cy - h * 0.45f, cx, cy);
                g.DrawLine(p, cx, cy, cx + h * 0.42f, cy + h * 0.18f);
                break;
            case Glyph.Target:
                g.DrawEllipse(p, cx - h * 0.85f, cy - h * 0.85f, h * 1.7f, h * 1.7f);
                g.DrawEllipse(p, cx - h * 0.35f, cy - h * 0.35f, h * 0.7f, h * 0.7f);
                g.DrawLine(p, cx - h * 1.05f, cy, cx - h * 0.85f, cy);
                g.DrawLine(p, cx + h * 0.85f, cy, cx + h * 1.05f, cy);
                break;
            case Glyph.Check:
                g.DrawLines(p, new[]
                {
                    new PointF(cx - h * 0.7f, cy + h * 0.02f),
                    new PointF(cx - h * 0.16f, cy + h * 0.56f),
                    new PointF(cx + h * 0.72f, cy - h * 0.52f)
                });
                break;
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Animation pump. One 33 Hz timer drives every control that opted into IAnimated, so smooth
// motion costs a single message-loop timer rather than one per widget.
// ---------------------------------------------------------------------------------------------
internal interface IAnimated
{
    void Anim(float dt);
}

internal static class Animator
{
    static readonly System.Windows.Forms.Timer Timer = new() { Interval = 33 };
    static readonly List<WeakReference> Items = new(32);
    static long _last;

    static Animator()
    {
        _last = Environment.TickCount64;
        Timer.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            float dt = Math.Min(0.25f, (now - _last) / 1000f);
            _last = now;
            bool pruned = false;
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (Items[i].Target is not Control c || c.IsDisposed)
                {
                    Items.RemoveAt(i);
                    pruned = true;
                    continue;
                }
                if (c is IAnimated a && c.Visible && c.IsHandleCreated) a.Anim(dt);
            }
            if (pruned && Items.Count == 0) Timer.Stop();
        };
    }

    public static void Register(Control c)
    {
        foreach (var w in Items) if (ReferenceEquals(w.Target, c)) return;
        Items.Add(new WeakReference(c));
        if (!Timer.Enabled) { _last = Environment.TickCount64; Timer.Start(); }
    }

    public static void Unregister(Control c)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
            if (Items[i].Target is not Control t || ReferenceEquals(t, c)) Items.RemoveAt(i);
    }
}

// ---------------------------------------------------------------------------------------------
// Base control: double buffered, DPI aware, animator aware, clears to a known surface colour so
// rounded panels never smear.
// ---------------------------------------------------------------------------------------------
internal abstract class UiControl : Control, IAnimated
{
    protected UiControl()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        BackColor = Pal.Panel;
    }

    /// Colour the control clears to before drawing; set it to the colour of whatever sits behind.
    public Color Surface { get; set; } = Pal.Panel;

    /// Return true when the frame changed and the control should repaint itself.
    protected virtual bool Animate(float dt) => false;

    void IAnimated.Anim(float dt)
    {
        if (Animate(dt)) Invalidate();
    }

    protected float Dpi => DeviceDpi / 96f;

    protected float S(float v) => v * DeviceDpi / 96f;

    protected RectangleF Rect => new(0, 0, Width, Height);

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Surface);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Animator.Register(this);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Animator.Unregister(this);
        base.OnHandleDestroyed(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) Animator.Register(this);
    }
}

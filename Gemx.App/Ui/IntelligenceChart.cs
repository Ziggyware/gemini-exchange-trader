using System.Drawing.Drawing2D;
using System.Globalization;

namespace Gemx.App;

// Every advanced-analytics output is represented as a time series. Series are
// independently range-normalised inside each lane so price/variance magnitudes
// cannot flatten bounded probabilities. Each current value remains printed in
// invariant units, preventing the normalisation from hiding its absolute scale.
internal sealed class IntelligenceChart : UiControl
{
    const int Capacity = 240;
    const int SeriesCount = 38;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly double[,] _history = new double[SeriesCount, Capacity];
    readonly int[] _regime = new int[Capacity];
    readonly bool[] _abstain = new bool[Capacity];
    int _head, _count;

    readonly record struct Def(string Name, Color Ink, string Format);
    readonly record struct Lane(string Title, int[] Series);

    static readonly Def[] Defs =
    {
        new("posterior", Pal.Accent, "P0"), new("alpha ρ", Pal.Violet, "P0"), new("leverage", Pal.UpLit, "F2"), new("coherence", Pal.Warn, "P0"),
        new("imbalance", Pal.Accent, "F2"), new("micro", Pal.Violet, "F2"), new("concentration", Pal.Up, "F2"), new("slope", Pal.Warn, "F2"),
        new("convexity", Pal.DownLit, "F2"), new("hysteresis", Pal.Neutral, "F2"), new("transience", Pal.Danger, "P0"), new("gaps", Pal.TextDim, "F1"),
        new("var fast", Pal.Accent, "G2"), new("var med", Pal.Violet, "G2"), new("var slow", Pal.Up, "G2"), new("jump λ", Pal.Warn, "G2"),
        new("jump var", Pal.DownLit, "G2"), new("liq var", Pal.Neutral, "G2"), new("tail", Pal.Danger, "G2"), new("uncertainty", Pal.TextDim, "G2"),
        new("fill", Pal.UpLit, "P0"), new("adverse", Pal.DownLit, "P0"), new("venue", Pal.Accent, "P0"), new("contam", Pal.Danger, "P0"),
        new("inventory", Pal.Warn, "P0"), new("latency", Pal.Violet, "F2"),
        new("spectral Δ", Pal.Accent, "G2"), new("entropy", Pal.Violet, "F2"), new("alpha", Pal.UpLit, "G2"), new("jump score", Pal.Warn, "F2"),
        new("OFI", Pal.Accent, "F2"), new("sigma²", Pal.Violet, "G2"), new("momentum", Pal.UpLit, "G2"), new("z-score", Pal.Warn, "F2"),
        new("spread ratio", Pal.DownLit, "F2"), new("OFI accel", Pal.Neutral, "F2"), new("spread quality", Pal.TextDim, "G2"), new("vol regime", Pal.Danger, "F0"),
    };

    static readonly Lane[] Lanes =
    {
        new("DECISION MANIFOLD", new[] { 0, 1, 2, 3 }),
        new("SIGNAL DECOMPOSITION", new[] { 30, 31, 32, 33, 34, 35, 36, 37 }),
        new("LIQUIDITY TOPOLOGY", new[] { 4, 5, 6, 7, 8, 9, 10, 11 }),
        new("VOLATILITY SURFACE", new[] { 12, 13, 14, 15, 16, 17, 18, 19 }),
        new("EXECUTION STATE", new[] { 20, 21, 22, 23, 24, 25 }),
        new("SPECTRAL / ALPHA", new[] { 26, 27, 28, 29 }),
    };

    public IntelligenceChart()
    {
        Surface = Pal.Card;
        DoubleBuffered = true;
    }

    public void Push(in EngineView v)
    {
        int i = _head;
        Span<double> x = stackalloc double[SeriesCount]
        {
            v.RegimeProbability, v.SignalConfidence, v.LeverageScore, v.ScaleCoherence,
            v.TopologyImbalance, v.TopologyMicro, v.LiquidityConcentration, v.LiquiditySlope,
            v.LiquidityConvexity, v.LiquidityHysteresis, v.LiquidityTransience, v.GapFragility,
            v.FastVariance, v.MediumVariance, v.SlowVariance, v.JumpIntensity,
            v.JumpVariance, v.LiquidityVariance, v.TailLoss, v.ModelUncertainty,
            v.FillProbability, v.AdverseSelection, v.VenueReliability, v.Contamination,
            v.InventoryStress, v.LatencyStress, v.SpectralShift, v.SpectralEntropy, v.FusedAlpha, v.JumpScore,
            v.OfiNorm, v.Sigma2, v.Momentum, v.ZScore, v.SpreadRatio, v.OfiAccel, v.SpreadQuality, v.VolRegime
        };
        for (int s = 0; s < SeriesCount; s++) _history[s, i] = double.IsFinite(x[s]) ? x[s] : 0;
        _regime[i] = v.MarketRegime;
        _abstain[i] = v.AnalyticsAbstain;
        _head = (i + 1) % Capacity;
        if (_count < Capacity) _count++;
        Invalidate();
    }

    static string Regime(int r) => r switch
    {
        0 => "quiet", 1 => "direction", 2 => "reversion", 3 => "withdrawal", 4 => "jump", 5 => "venue", _ => "unknown"
    };

    int Ring(int chronological) => (_head - _count + chronological + Capacity) % Capacity;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Surface);
        float pad = S(4), regimeH = S(42), gap = S(5);
        DrawRegime(g, new RectangleF(pad, pad, Width - 2 * pad, regimeH));
        float y = pad + regimeH + gap;
        float laneH = Math.Max(S(82), (Height - y - pad - gap * (Lanes.Length - 1)) / Lanes.Length);
        foreach (Lane lane in Lanes)
        {
            DrawLane(g, new RectangleF(pad, y, Width - 2 * pad, laneH), lane);
            y += laneH + gap;
        }
    }

    void DrawRegime(Graphics g, RectangleF r)
    {
        Gfx.FillRound(g, r, S(5), Pal.Mix(Pal.Bg, Color.Black, .08));
        Gfx.StrokeRound(g, r, S(5), Pal.LineSoft);
        Gfx.Tracked(g, "REGIME POSTERIOR / DECISION", Fonts.UiTinyBold, Pal.TextDim, r.X + S(5), r.Y + S(3), .8f);
        if (_count == 0) return;
        float top = r.Y + S(18), h = r.Height - S(23), w = r.Width - S(10), x0 = r.X + S(5);
        Color[] inks = { Pal.Up, Pal.Accent, Pal.Violet, Pal.Warn, Pal.DownLit, Pal.Danger };
        for (int n = 0; n < _count; n++)
        {
            int k = Ring(n); float x = x0 + w * n / Math.Max(1, _count);
            float nx = x0 + w * (n + 1) / Math.Max(1, _count);
            g.FillRectangle(Cache.Brush(Pal.Alpha(inks[Math.Clamp(_regime[k], 0, 5)], _abstain[k] ? 235 : 125)), x, top, Math.Max(1, nx - x), h);
            if (_abstain[k]) g.FillRectangle(Cache.Brush(Pal.Alpha(Pal.Danger, 110)), x, top, Math.Max(1, nx - x), S(3));
        }
        int last = Ring(_count - 1);
        string value = Regime(_regime[last]) + (_abstain[last] ? " · ABSTAIN" : " · QUOTE");
        g.DrawString(value, Fonts.MonoTiny, Cache.Brush(_abstain[last] ? Pal.Warn : Pal.TextHi), new RectangleF(r.X, r.Y + S(1), r.Width - S(5), S(13)), Gfx.SfTopRight);
    }

    void DrawLane(Graphics g, RectangleF r, Lane lane)
    {
        Gfx.FillRound(g, r, S(5), Pal.Mix(Pal.Bg, Color.Black, .06));
        Gfx.StrokeRound(g, r, S(5), Pal.LineSoft);
        Gfx.Tracked(g, lane.Title, Fonts.UiTinyBold, Pal.TextDim, r.X + S(5), r.Y + S(3), .8f);
        float legendTop = r.Y + S(16);
        int cols = lane.Series.Length > 4 ? 2 : 1;
        float colW = r.Width / cols;
        for (int n = 0; n < lane.Series.Length; n++)
        {
            int s = lane.Series[n], col = n % cols, row = n / cols;
            double value = _count == 0 ? 0 : _history[s, Ring(_count - 1)];
            string text = Defs[s].Name + " " + value.ToString(Defs[s].Format, Inv);
            float lx = r.X + S(5) + col * colW, ly = legendTop + row * S(11);
            g.FillEllipse(Cache.Brush(Defs[s].Ink), lx, ly + S(3), S(4), S(4));
            g.DrawString(text, Fonts.MonoTiny, Cache.Brush(Pal.TextDim), new RectangleF(lx + S(7), ly, colW - S(10), S(11)), Gfx.SfTop);
        }
        int rows = (lane.Series.Length + cols - 1) / cols;
        float chartTop = legendTop + rows * S(11) + S(3), chartBottom = r.Bottom - S(5);
        var chart = new RectangleF(r.X + S(5), chartTop, r.Width - S(10), Math.Max(S(18), chartBottom - chartTop));
        Gfx.HairH(g, chart.Y + chart.Height / 2, chart.X, chart.Right, Pal.Alpha(Pal.LineSoft, 130));
        if (_count < 2) return;
        foreach (int s in lane.Series) DrawSeries(g, chart, s);
    }

    void DrawSeries(Graphics g, RectangleF r, int series)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        for (int n = 0; n < _count; n++) { double v = _history[series, Ring(n)]; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        double range = hi - lo;
        if (range < Math.Max(1e-12, Math.Abs(hi) * 1e-9)) { lo -= .5; hi += .5; range = 1; }
        using var path = new GraphicsPath();
        for (int n = 0; n < _count; n++)
        {
            double v = _history[series, Ring(n)];
            float x = r.X + r.Width * n / Math.Max(1, _count - 1);
            float y = r.Bottom - (float)((v - lo) / range) * r.Height;
            if (n == 0) path.StartFigure(); else { }
            if (n == 0) path.AddLine(x, y, x, y); else
            {
                double pv = _history[series, Ring(n - 1)];
                float px = r.X + r.Width * (n - 1) / Math.Max(1, _count - 1);
                float py = r.Bottom - (float)((pv - lo) / range) * r.Height;
                path.AddLine(px, py, x, y);
            }
        }
        using var pen = new Pen(Pal.Alpha(Defs[series].Ink, 210), S(1));
        g.DrawPath(pen, path);
    }
}

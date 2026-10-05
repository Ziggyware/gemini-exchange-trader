using System.Drawing.Drawing2D;

namespace Gemx.App;

public sealed class QuoteChart : Control
{
    const int Cap = 600;
    static readonly Color Bg = Color.FromArgb(16, 18, 22), GridC = Color.FromArgb(44, 48, 56), Txt = Color.FromArgb(150, 156, 166);
    static readonly Color CBb = Color.FromArgb(60, 170, 110), CBa = Color.FromArgb(200, 80, 80), CQb = Color.FromArgb(120, 235, 165), CQa = Color.FromArgb(255, 145, 125);
    static readonly Font Mono = new("Consolas", 8.5f);

    readonly double[] _bb = new double[Cap], _ba = new double[Cap], _qb = new double[Cap], _qa = new double[Cap];
    readonly List<PointF> _pts = new(2 * Cap);
    int _head, _count, _dec = 2;

    public double SampleSeconds { get; set; } = 0.1;

    public QuoteChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Bg;
        MinimumSize = new Size(240, 140);
    }

    public void Reset(int priceDecimals)
    {
        _head = 0;
        _count = 0;
        _dec = priceDecimals;
        Invalidate();
    }

    public void Push(double bestBid, double bestAsk, double quoteBid, double quoteAsk, double micro, double intendedBid, double intendedAsk)
    {
        _bb[_head] = bestBid;
        _ba[_head] = bestAsk;
        _ib[_head] = intendedBid; _ia[_head] = intendedAsk;
        _qb[_head] = quoteBid;
        _qa[_head] = quoteAsk;

        _head = (_head + 1) % Cap;
        if (_count < Cap) _count++;
        Invalidate();
    }

    int Index(int i) => (_head - _count + i + Cap) % Cap;

    void Flush(Graphics g, Pen pen)
    {
        if (_pts.Count >= 2) g.DrawLines(pen, _pts.ToArray());
        else if (_pts.Count == 1) g.DrawLine(pen, _pts[0].X - 1, _pts[0].Y, _pts[0].X + 1, _pts[0].Y);
        _pts.Clear();
    }

    readonly double[] _ib = new double[Cap], _ia = new double[Cap]; // intended

    
    void DrawSeries(Graphics g, double[] a, Pen pen, RectangleF plot, double lo, double hi, bool dashed = false)
    {
        if (dashed) pen.DashStyle = DashStyle.Dash;
        float dx = plot.Width / (Cap - 1f);
        _pts.Clear();
        int c = 0;
        for (int i = 0; i < _count; i++)
        {
            double v = a[Index(i)];
            if (v > 0)
            {
                c = c + 1;
                float x = plot.Right - (_count - 1 - i) * dx;
                float y = (float)(plot.Bottom - (v - lo) / (hi - lo) * plot.Height);
                if (_pts.Count > 0) _pts.Add(new PointF(x, _pts[^1].Y));
                _pts.Add(new PointF(x, y));
            }
            else
            {
                if (c > 0)
                {
                    Flush(g, pen);
                    c = 0;
                }
            }
        }
        Flush(g, pen);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Bg);
        var plot = new RectangleF(76, 8, Math.Max(1, Width - 76 - 8), Math.Max(1, Height - 8 - 22));
        using var txt = new SolidBrush(Txt);
        if (_count < 2)
        {
            g.DrawString("waiting for data", Mono, txt, plot.Left + 8, plot.Top + 8);
            return;
        }

        double lo = double.MaxValue, hi = double.MinValue;
        foreach (double[] a in new[] { _bb, _ba, _qb, _qa })
            for (int i = 0; i < _count; i++)
            {
                double v = a[Index(i)];
                if (v > 0) { if (v < lo) lo = v; if (v > hi) hi = v; }
            }
        if (lo > hi) return;
        double span = hi - lo;
        if (span < 1e-9) { lo -= 0.5; hi += 0.5; }
        else { lo -= span * 0.08; hi += span * 0.08; }

        g.SmoothingMode = SmoothingMode.None;
        using (var gp = new Pen(GridC))
            for (int k = 0; k <= 4; k++)
            {
                float y = plot.Top + k * plot.Height / 4f;
                g.DrawLine(gp, plot.Left, y, plot.Right, y);
                g.DrawString((hi - k * (hi - lo) / 4).ToString("F" + _dec), Mono, txt, 4, y - 7);
            }
        g.DrawString($"-{(Cap - 1) * SampleSeconds:F0}s", Mono, txt, plot.Left, plot.Bottom + 4);
        g.DrawString("now", Mono, txt, plot.Right - 24, plot.Bottom + 4);

        using var pBb = new Pen(CBb, 1f);
        using var pBa = new Pen(CBa, 1f);
        using var pQb = new Pen(CQb, 2f);
        using var pQa = new Pen(CQa, 2f);
        DrawSeries(g, _bb, pBb, plot, lo, hi);
        DrawSeries(g, _ba, pBa, plot, lo, hi);
        DrawSeries(g, _qb, pQb, plot, lo, hi);
        DrawSeries(g, _qa, pQa, plot, lo, hi);

        using var pIb = new Pen(Color.FromArgb(60, 120, 235, 165), 1.5f) { DashStyle = DashStyle.Dash };
        using var pIa = new Pen(Color.FromArgb(60, 255, 145, 125), 1.5f) { DashStyle = DashStyle.Dash };
        DrawSeries(g, _ib, pIb, plot, lo, hi, true);
        DrawSeries(g, _ia, pIa, plot, lo, hi, true);

        float lx = plot.Left + 6;
        foreach (var (name, c) in new[] { ("best bid", CBb), ("best ask", CBa), ("our bid", CQb), ("our ask", CQa) })
        {
            using var br = new SolidBrush(c);
            g.DrawString(name, Mono, br, lx, plot.Top + 2);
            lx += g.MeasureString(name, Mono).Width + 8;
        }
    }
}
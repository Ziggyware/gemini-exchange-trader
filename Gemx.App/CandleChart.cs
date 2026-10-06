using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Gemx.App;

public enum TF { M1=60, M5=300, M15=900, M30=1800, H1=3600, D1=86400, W1=604800, M1_30D=2592000 }

public struct Candle
{
    public long StartNs;
    public double O, H, L, C;
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

public sealed class CandleStrip : Control
{
    readonly TF _tf;
    IReadOnlyList<Candle> _hist = Array.Empty<Candle>();
    Candle _live;
    int _dec = 2;
    int _visible = 80; // zoom
    int _offset = 0; // pan
    Point _dragStart;
    bool _dragging;
    static readonly Color Bull = Color.FromArgb(90, 200, 120), Bear = Color.FromArgb(220, 80, 80), Bg = Color.FromArgb(18, 20, 25);
    static readonly SolidBrush BullB = new(Bull), BearB = new(Bear);
    static readonly Pen BullP = new(Bull), BearP = new(Bear), GridP = new(Color.FromArgb(35, 38, 45));
    static readonly Font Small = new("Consolas", 7.5f), Mid = new("Consolas", 8f), Bold = new("Consolas", 8f, FontStyle.Bold);

    public CandleStrip(TF tf)
    {
        _tf = tf; DoubleBuffered = true; MinimumSize = new Size(120, 80);
        MouseWheel += OnWheel; MouseDown += OnDown; MouseMove += OnMove; MouseUp += OnUp; DoubleClick += OnDbl;
        MouseEnter += (_, _) => Focus();   // MouseWheel is only delivered to the focused control
    }

    public void SetData(IReadOnlyList<Candle> hist, Candle live, int dec) { _hist = hist; _live = live; _dec = dec; Invalidate(); }

    void OnWheel(object? s, MouseEventArgs e) { _visible = Math.Clamp(_visible + (e.Delta > 0 ? -10 : 10), 10, 300); Invalidate(); }
    void OnDown(object? s, MouseEventArgs e) { _dragging = true; _dragStart = e.Location; Cursor = Cursors.SizeWE; }
    void OnMove(object? s, MouseEventArgs e)
    {
        if (!_dragging) return;
        int dx = _dragStart.X - e.X;
        int step = Width / Math.Max(1, _visible);
        if (Math.Abs(dx) > step)
        {
            int max = Math.Max(0, _hist.Count + 1 - _visible);
            _offset = Math.Clamp(_offset - Math.Sign(dx), 0, max);
            _dragStart = e.Location;
            Invalidate();
        }
    }
    void OnUp(object? s, MouseEventArgs e) { _dragging = false; Cursor = Cursors.Default; }
    void OnDbl(object? s, EventArgs e) { _visible = 80; _offset = 0; Invalidate(); }

    Candle At(int i) => i < _hist.Count ? _hist[i] : _live;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Bg); g.SmoothingMode = SmoothingMode.None;
        var plot = new RectangleF(6, 2, Width - 8, Height - 20);

        bool hasLive = _live.Ticks > 0;
        int total = _hist.Count + (hasLive ? 1 : 0);
        if (total == 0)
        {
            g.DrawString($"{_tf} live no ticks yet", Mid, Brushes.Gray, 4, 4);
            g.DrawRectangle(Pens.DimGray, Rectangle.Round(plot));
            return;
        }

        int start = Math.Max(0, total - _visible - _offset);
        int end = Math.Min(total, start + _visible);
        int count = end - start;

        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = start; i < end; i++)
        {
            Candle c = At(i);
            if (c.L < lo) lo = c.L;
            if (c.H > hi) hi = c.H;
        }
        double span = hi - lo; if (span < 0.01) span = hi * 0.001 + 0.01; lo -= span * 0.1; hi += span * 0.1;
        double range = hi - lo;

        g.DrawLine(GridP, plot.Left, plot.Top + plot.Height / 2, plot.Right, plot.Top + plot.Height / 2);

        float step = plot.Width / Math.Max(1, count);
        float bw = Math.Max(2, step * 0.65f);
        for (int i = start; i < end; i++)
        {
            Candle c = At(i);
            // newest candle anchored to the right edge
            float x = plot.Right - (end - 1 - i) * step - step / 2;
            float yO = (float)(plot.Bottom - (c.O - lo) / range * plot.Height);
            float yC = (float)(plot.Bottom - (c.C - lo) / range * plot.Height);
            float yH = (float)(plot.Bottom - (c.H - lo) / range * plot.Height);
            float yL = (float)(plot.Bottom - (c.L - lo) / range * plot.Height);
            bool up = c.C >= c.O;
            g.DrawLine(up ? BullP : BearP, x, yH, x, yL);
            float top = Math.Min(yO, yC), h = Math.Max(2, Math.Abs(yO - yC));
            g.FillRectangle(up ? BullB : BearB, x - bw / 2, top, bw, h);
            if (hasLive && i == total - 1) g.DrawRectangle(Pens.White, x - bw / 2, top, bw, h);
        }
        g.DrawString($"{_tf} {count}/{total} [{_visible} vis] wheel=zoom drag=pan dbl=reset", Small, Brushes.Gray, 2, 2);
        g.DrawString($"{hi:F2}", Small, Brushes.Gray, plot.Right + 2, plot.Top);
        g.DrawString($"{lo:F2}", Small, Brushes.Gray, plot.Right + 2, plot.Bottom - 10);
        g.DrawString(At(total - 1).C.ToString("F" + _dec), Bold, Brushes.White, plot.Left + 4, plot.Top + 12);
    }
}

public sealed class MultiTFView : UserControl
{
    readonly Aggregator _agg = new();
    readonly Dictionary<TF, CandleStrip> _strips = new();
    public Action<string>? Log;
    int _dec = 2;

    static TableLayoutPanel Row(params CandleStrip[] strips)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = strips.Length, RowCount = 1 };
        for (int i = 0; i < strips.Length; i++)
        {
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / strips.Length));
            t.Controls.Add(strips[i], i, 0);
        }
        return t;
    }

    public MultiTFView()
    {
        DoubleBuffered = true;
        foreach (TF tf in new[] { TF.M1, TF.M5, TF.M15, TF.M30, TF.H1, TF.D1, TF.W1, TF.M1_30D })
            _strips[tf] = new CandleStrip(tf) { Dock = DockStyle.Fill };

        var root = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        root.Panel1.Controls.Add(Row(_strips[TF.M1], _strips[TF.M5], _strips[TF.M15], _strips[TF.M30]));
        root.Panel2.Controls.Add(Row(_strips[TF.H1], _strips[TF.D1], _strips[TF.W1], _strips[TF.M1_30D]));
        Controls.Add(root);

        // SplitterDistance is validated against the current size, so set it once real layout exists
        bool split = false;
        Resize += (_, _) =>
        {
            if (!split && root.Height > 120) { root.SplitterDistance = root.Height / 2; split = true; }
        };
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

        foreach (var kv in _strips) kv.Value.SetData(_agg.Get(kv.Key), _agg.Current(kv.Key), _dec);
    }

    public void Push(long recvNs, double micro, int dec)
    {
        _dec = dec;
        _agg.Push(recvNs, micro);
        foreach (var kv in _strips) kv.Value.SetData(_agg.Get(kv.Key), _agg.Current(kv.Key), dec);
    }
}
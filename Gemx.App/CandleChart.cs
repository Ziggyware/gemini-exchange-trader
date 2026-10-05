using System.Drawing.Drawing2D;

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
    public void LoadHistory(TF tf, List<Candle> hist)
    {
        _books[tf] = hist.TakeLast(500).ToList();
        if (hist.Count > 0) _cur[tf] = hist.Last(); // start live from last close
    }

    readonly Dictionary<TF, List<Candle>> _books = new();
    readonly Dictionary<TF, Candle> _cur = new();
    public Aggregator()
    {
        foreach(TF tf in Enum.GetValues<TF>()) { _books[tf]=new List<Candle>(500); _cur[tf]=new Candle(); }
    }
    public IReadOnlyList<Candle> Get(TF tf) => _books[tf];
    public Candle Current(TF tf) => _cur[tf];

    public void Push(long recvNs, double micro)
    {
        if (micro <= 0) return;
        if (recvNs <= 0) recvNs = Gemx.Clock.NowNs();
        foreach(TF tf in Enum.GetValues<TF>())
        {
            long span = (long)tf * 1_000_000_000L;
            long start = recvNs / span * span;
            ref var cur = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_cur, tf, out _);
            if (cur.StartNs!= start)
            {
                if (cur.StartNs!= 0 && cur.Ticks>0) { var b=_books[tf]; b.Add(cur); if(b.Count>500) b.RemoveAt(0); }
                cur = new Candle{ StartNs=start, O=micro, H=micro, L=micro, C=micro, Ticks=1 };
            }
            else
            {
                if (cur.Ticks==0) cur = new Candle{ StartNs=start, O=micro, H=micro, L=micro, C=micro, Ticks=1 };
                else { if(micro>cur.H) cur.H=micro; if(micro<cur.L) cur.L=micro; cur.C=micro; cur.Ticks++; }
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
    static readonly Color Bull = Color.FromArgb(90,200,120), Bear = Color.FromArgb(220,80,80), Bg=Color.FromArgb(18,20,25);

    public CandleStrip(TF tf){ _tf=tf; DoubleBuffered=true; MinimumSize=new Size(120,80);
        MouseWheel+=OnWheel; MouseDown+=OnDown; MouseMove+=OnMove; MouseUp+=OnUp; DoubleClick+=OnDbl;
    }

    public void SetData(IReadOnlyList<Candle> hist, Candle live, int dec){ _hist=hist; _live=live; _dec=dec; Invalidate(); }

    void OnWheel(object? s, MouseEventArgs e){ _visible = Math.Clamp(_visible + (e.Delta>0?-10:10), 10, 300); Invalidate(); }
    void OnDown(object? s, MouseEventArgs e){ _dragging=true; _dragStart=e.Location; Cursor=Cursors.SizeWE; }
    void OnMove(object? s, MouseEventArgs e){ if(!_dragging) return; int dx=_dragStart.X- e.X; int step = Width / Math.Max(1,_visible); if(Math.Abs(dx)>step){ _offset=Math.Max(0,_offset - Math.Sign(dx)); _dragStart=e.Location; Invalidate(); } }
    void OnUp(object? s, MouseEventArgs e){ _dragging=false; Cursor=Cursors.Default; }
    void OnDbl(object? s, EventArgs e){ _visible=80; _offset=0; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics; g.Clear(Bg); g.SmoothingMode=SmoothingMode.None;
        var plot = new RectangleF(6, 2, Width-8, Height-20);

        // always draw something even with 1 tick
        var all = new List<Candle>(_hist); if(_live.Ticks>0) all.Add(_live);
        if(all.Count==0){ using var f=new Font("Consolas",8f); g.DrawString($"{_tf} live {(_live.Ticks>0?_live.C.ToString("F"+_dec):"no ticks yet")}", f, Brushes.Gray, 4,4); g.DrawRectangle(Pens.DimGray, Rectangle.Round(plot)); return; }

        int start = Math.Max(0, all.Count - _visible - _offset);
        int end = Math.Min(all.Count, start+_visible);
        var view = all.Skip(start).Take(end - start).ToList(); // oldest -> newest left->right

        if (view.Count==0) view=all.TakeLast(_visible).ToList();

        double lo=view.Min(c=>c.L), hi=view.Max(c=>c.H); double span=hi-lo; if(span<0.01) span=hi*0.001+0.01; lo-=span*0.1; hi+=span*0.1;

        // grid
        using var gridPen=new Pen(Color.FromArgb(35,38,45));
        g.DrawLine(gridPen, plot.Left, plot.Top+plot.Height/2, plot.Right, plot.Top+plot.Height/2);


        float step = plot.Width / Math.Max(1, view.Count);
        float bw = Math.Max(2, step * 0.65f);
        // anchor newest to right edge - time moves left
        for (int i = 0; i < view.Count; i++)
        {
            var c = view[i];
            // i=0 is oldest in view, i=view.Count-1 is newest/live -> at right
            float x = plot.Right - (view.Count - 1 - i) * step - step / 2;


            float yO=(float)(plot.Bottom - (c.O-lo)/(hi-lo)*plot.Height);
            float yC=(float)(plot.Bottom - (c.C-lo)/(hi-lo)*plot.Height);
            float yH=(float)(plot.Bottom - (c.H-lo)/(hi-lo)*plot.Height);
            float yL=(float)(plot.Bottom - (c.L-lo)/(hi-lo)*plot.Height);
            bool up=c.C>=c.O;
            using var br=new SolidBrush(up?Bull:Bear);
            using var pn=new Pen(br.Color);
            g.DrawLine(pn, x, yH, x, yL);
            g.FillRectangle(br, x-bw/2, Math.Min(yO,yC), bw, Math.Max(2, Math.Abs(yO-yC)));
            if(c.Equals(_live)) g.DrawRectangle(Pens.White, x-bw/2, Math.Min(yO,yC), bw, Math.Max(2, Math.Abs(yO-yC)));
        }
        using var font=new Font("Consolas",7.5f);
        g.DrawString($"{_tf} {view.Count}/{all.Count} [{_visible} vis] wheel=zoom drag=pan dbl=reset", font, Brushes.Gray, 2,2);
        g.DrawString($"{hi:F2}", font, Brushes.Gray, plot.Right+2, plot.Top);
        g.DrawString($"{lo:F2}", font, Brushes.Gray, plot.Right+2, plot.Bottom-10);
        g.DrawString($"{all.Last().C.ToString("F"+_dec)}", new Font("Consolas",8f,FontStyle.Bold), Brushes.White, plot.Left+4, plot.Top+12);
    }
}

public sealed class MultiTFView : UserControl
{
    readonly Aggregator _agg=new();
    readonly Dictionary<TF, CandleStrip> _strips=new();
    public MultiTFView()
    {
        DoubleBuffered=true;
        // resizable with nested splitters instead of TableLayout
        var root = new SplitContainer{ Dock=DockStyle.Fill, Orientation=Orientation.Horizontal, SplitterDistance=300 };
        var top = new SplitContainer{ Dock=DockStyle.Fill, Orientation=Orientation.Vertical, SplitterDistance=350 };
        var top2 = new SplitContainer{ Dock=DockStyle.Fill, Orientation=Orientation.Vertical, SplitterDistance=350 };
        var bot = new SplitContainer{ Dock=DockStyle.Fill, Orientation=Orientation.Vertical, SplitterDistance=350 };
        var bot2 = new SplitContainer{ Dock=DockStyle.Fill, Orientation=Orientation.Vertical, SplitterDistance=350 };

        // top row: M1 | M5 | M15 | M30
        var m1=new CandleStrip(TF.M1){Dock=DockStyle.Fill}; var m5=new CandleStrip(TF.M5){Dock=DockStyle.Fill};
        var m15=new CandleStrip(TF.M15){Dock=DockStyle.Fill}; var m30=new CandleStrip(TF.M30){Dock=DockStyle.Fill};
        top.Panel1.Controls.Add(m1); top.Panel2.Controls.Add(top2); top2.Panel1.Controls.Add(m5); top2.Panel2.Controls.Add(m15);
        // hack for 4th - add to form via extra container
        var topRow = new TableLayoutPanel{Dock=DockStyle.Fill, ColumnCount=4};
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25)); topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25)); topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
        topRow.Controls.Add(m1,0,0); topRow.Controls.Add(m5,1,0); topRow.Controls.Add(m15,2,0); topRow.Controls.Add(m30,3,0);

        var h1=new CandleStrip(TF.H1){Dock=DockStyle.Fill}; var d1=new CandleStrip(TF.D1){Dock=DockStyle.Fill};
        var w1=new CandleStrip(TF.W1){Dock=DockStyle.Fill}; var mo=new CandleStrip(TF.M1_30D){Dock=DockStyle.Fill};
        var botRow = new TableLayoutPanel{Dock=DockStyle.Fill, ColumnCount=4};
        botRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25)); botRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
        botRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25)); botRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
        botRow.Controls.Add(h1,0,0); botRow.Controls.Add(d1,1,0); botRow.Controls.Add(w1,2,0); botRow.Controls.Add(mo,3,0);

        root.Panel1.Controls.Add(topRow); root.Panel2.Controls.Add(botRow);
        Controls.Add(root);

        _strips[TF.M1]=m1; _strips[TF.M5]=m5; _strips[TF.M15]=m15; _strips[TF.M30]=m30;
        _strips[TF.H1]=h1; _strips[TF.D1]=d1; _strips[TF.W1]=w1; _strips[TF.M1_30D]=mo;
    }
    public async Task LoadHistoryAsync(string symbol)
    {
        // parallel fetch
        var tasks = new Dictionary<TF, Task<List<Candle>>>();
        foreach (TF tf in new[] { TF.M1, TF.M5, TF.M15, TF.M30, TF.H1, TF.D1 })
            tasks[tf] = CandleHistory.FetchAsync(symbol, CandleHistory.ToGemini(tf));

        await Task.WhenAll(tasks.Values);

        // fill books
        _agg.LoadHistory(TF.M1, await tasks[TF.M1]);
        _agg.LoadHistory(TF.M5, await tasks[TF.M5]);
        _agg.LoadHistory(TF.M15, await tasks[TF.M15]);
        _agg.LoadHistory(TF.M30, await tasks[TF.M30]);
        _agg.LoadHistory(TF.H1, await tasks[TF.H1]);
        var daily = await tasks[TF.D1];
        _agg.LoadHistory(TF.D1, daily);
        _agg.LoadHistory(TF.W1, CandleHistory.AggregateWeek(daily));
        _agg.LoadHistory(TF.M1_30D, CandleHistory.AggregateMonth(daily));

        // push to strips
        foreach (var kv in _strips) kv.Value.SetData(_agg.Get(kv.Key), _agg.Current(kv.Key), 2);
    }
    public void Push(long recvNs, double micro, int dec)
    {
        _agg.Push(recvNs, micro);
        foreach(var kv in _strips) kv.Value.SetData(_agg.Get(kv.Key), _agg.Current(kv.Key), dec);
    }
}
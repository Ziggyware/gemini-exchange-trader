using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Gemx.App;

enum UiState { Idle, Starting, Running, Stopping }

public sealed class MainForm : Form
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly AppSettings _settings = AppSettings.Load();
    readonly PropertyGrid _grid = new() { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true };
    readonly Button _start = new() { Text = "Start", AutoSize = true };
    readonly Button _stop = new() { Text = "Stop", AutoSize = true };
    readonly Button _kill = new() { Text = "KILL", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.Firebrick, ForeColor = Color.White };
    readonly Label _status = new() { AutoSize = true, Margin = new Padding(12, 8, 0, 0) };

    readonly FlowLayoutPanel _pillStrip = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
    readonly Label _pillWarm, _pillPos, _pillLag, _pillBook, _pillHealth, _pillKill;
    readonly InventoryBar _invBar = new() { Dock = DockStyle.Fill, Height = 28 };
    readonly Label _pnlLabel = new() { AutoSize = true, Font = new Font("Consolas", 9f), ForeColor = Color.Gainsboro };
    readonly Label _edgeLabel = new() { AutoSize = true, Font = new Font("Consolas", 8.5f), ForeColor = Color.Silver };
    readonly ListView _ladder = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HeaderStyle = ColumnHeaderStyle.None };
    readonly ListView _tape = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None };
    readonly TableLayoutPanel _metrics = new() { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
    readonly QuoteChart _chart = new() { Dock = DockStyle.Fill };
    readonly MultiTFView _multi = new() { Dock = DockStyle.Fill };

    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, TabStop = false };
    readonly Font _mono = new("Consolas", 9.5f);
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };

    readonly Label _state, _pos, _micro, _bid, _ask, _spread, _qBid, _qAsk, _sigma, _ofi, _lag, _frames, _faults, _cmds, _fills, _trips, _div, _resync, _socks;

    GemxHost? _host;
    UiState _ui = UiState.Idle;
    bool _closing;
    string _pf = "F2", _qf = "F8";
    long _rateTs = Stopwatch.GetTimestamp(), _rateFrames;
    double _fps;
    long _lastFillCount = 0;

    public MainForm()
    {
        Text = "Gemx";
        ClientSize = new Size(1480, 920);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 700);

        _grid.SelectedObject = _settings;
        _multi.Log = Log;
        _metrics.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _metrics.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _state = Metric("State");
        _pos = Metric("Position");
        _micro = Metric("Micro price");
        _bid = Metric("Best bid");
        _ask = Metric("Best ask");
        _spread = Metric("Spread");
        _qBid = Metric("Our bid");
        _qAsk = Metric("Our ask");
        _sigma = Metric("Sigma (per √s)");
        _ofi = Metric("OFI / avg");
        _lag = Metric("Feed lag excess");
        _frames = Metric("Frames");
        _faults = Metric("Parse faults");
        _cmds = Metric("Commands");
        _fills = Metric("Fills");
        _trips = Metric("Breaker trips");
        _div = Metric("Position divergences");
        _resync = Metric("MD resyncs");
        _socks = Metric("Sockets");

        _pillWarm = Pill("WARM");
        _pillPos = Pill("POS");
        _pillLag = Pill("LAG");
        _pillBook = Pill("BOOK");
        _pillHealth = Pill("HEALTH");
        _pillKill = Pill("KILL");
        _pillStrip.Controls.AddRange(new Control[] { _pillWarm, _pillPos, _pillLag, _pillBook, _pillHealth, _pillKill });

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(6, 6, 0, 0) };
        bar.Controls.AddRange(new Control[] { _start, _stop, _kill, _status, _pillStrip });

        _ladder.Columns.Add("Bid Px", 90);
        _ladder.Columns.Add("Bid Qty", 70);
        _ladder.Columns.Add("Ask Px", 90);
        _ladder.Columns.Add("Ask Qty", 70);
        _ladder.Columns.Add("Note", 120);

        _tape.Columns.Add("Time", 90);
        _tape.Columns.Add("Type", 70);
        _tape.Columns.Add("Side", 50);
        _tape.Columns.Add("Price", 90);
        _tape.Columns.Add("Info", 300);

        var rightTop = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        rightTop.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        rightTop.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightTop.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        rightTop.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        rightTop.Controls.Add(_invBar, 0, 0);
        var pnlPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true };
        pnlPanel.Controls.Add(_pnlLabel);
        pnlPanel.Controls.Add(_edgeLabel);
        rightTop.Controls.Add(pnlPanel, 0, 1);
        rightTop.Controls.Add(_metrics, 0, 2);
        rightTop.Controls.Add(_ladder, 0, 3);

        var inner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        inner.Controls.Add(rightTop, 0, 0);
        inner.SetRowSpan(rightTop, 2);

        var chartTabs = new TabControl { Dock = DockStyle.Fill };
        chartTabs.TabPages.Add("Ticks");
        chartTabs.TabPages[0].Controls.Add(_chart);
        chartTabs.TabPages.Add("Candles 1m-1M");
        chartTabs.TabPages[1].Controls.Add(_multi);
        inner.Controls.Add(chartTabs, 1, 0);

        var bottomRight = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        bottomRight.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        bottomRight.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        bottomRight.Controls.Add(_tape, 0, 0);
        bottomRight.Controls.Add(_log, 0, 1);
        inner.Controls.Add(bottomRight, 1, 1);

        _log.Font = _mono;
        _tape.Font = _mono;
        _ladder.Font = _mono;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        root.Controls.Add(bar, 0, 0);
        root.SetColumnSpan(bar, 2);
        root.Controls.Add(_grid, 0, 1);
        root.Controls.Add(inner, 1, 1);
        Controls.Add(root);

        _chart.SampleSeconds = _timer.Interval / 1000.0;
        _start.Click += OnStart;
        _stop.Click += OnStop;
        _kill.Click += OnKill;
        _timer.Tick += OnTimer;
        SetUi(UiState.Idle);
        _timer.Start();
    }

    Label Metric(string name)
    {
        int r = _metrics.RowCount;
        _metrics.RowCount = r + 1;
        _metrics.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var k = new Label { Text = name, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(6, 3, 10, 3), Anchor = AnchorStyles.Left };
        var v = new Label { Text = "-", AutoSize = true, Font = _mono, Margin = new Padding(0, 3, 6, 3), Anchor = AnchorStyles.Left };
        _metrics.Controls.Add(k, 0, r);
        _metrics.Controls.Add(v, 1, r);
        return v;
    }

    static Label Pill(string txt)
    {
        return new Label
        {
            Text = txt,
            Tag = txt,   // immutable base name; Text is derived from it on every update
            AutoSize = false,
            Size = new Size(96, 22),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(4, 2, 0, 2),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8f, FontStyle.Bold)
        };
    }

    static void SetPill(Label pill, bool ok, string? extra = null)
    {
        pill.BackColor = ok ? Color.FromArgb(35, 85, 55) : Color.FromArgb(110, 35, 35);
        pill.ForeColor = ok ? Color.FromArgb(180, 255, 180) : Color.FromArgb(255, 200, 200);
        string name = (string)pill.Tag!;
        pill.Text = extra == null ? name : $"{name} {extra}";
    }

    void SetUi(UiState s)
    {
        _ui = s;
        _start.Enabled = s == UiState.Idle;
        _stop.Enabled = s == UiState.Running;
        _kill.Enabled = s == UiState.Running && _host != null && !_host.Engine.Kill;
        _grid.Enabled = s == UiState.Idle;
        _status.Text = s.ToString();
    }

    void Log(string m) => AppendLog($"{DateTime.Now:HH:mm:ss.fff} {m}\r\n");

    void AppendLog(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(s)); return; }
        if (_log.TextLength > 400_000) _log.Text = _log.Text[^200_000..];
        _log.AppendText(s);
    }

    void DrainLog()
    {
        if (_host is not { } h) return;
        var sb = new StringBuilder();
        int n = 0;
        while (n < 500 && h.Logs.TryDequeue(out string? m)) { sb.Append(m).Append("\r\n"); n++; }
        if (n > 0) AppendLog(sb.ToString());
    }

    string P(long x8) => x8 > 0 ? (x8 / 1e8).ToString(_pf, Inv) : "-";

    static string SockState(FeedSocket s) => s.Current?.State.ToString() ?? "down";

    static string Age(long nowNs, long sentNs, Slot s) =>
        s == Slot.Idle || sentNs == 0 ? "" : $"  age {(nowNs - sentNs) / 1e9:F1}s";

    static (string Text, Color Color, string Reason) Describe(GemxHost h, EngineView v, long maxLagNs)
    {
        if (h.Engine.Kill || v.KillSent) return v.FlushOk ? ("KILLED", Color.Firebrick, "cancel acked") : ("KILLING", Color.Firebrick, "flushing");
        if (h.AuthRejected) return ("AUTH REJECTED", Color.Firebrick, "orders socket 401/403");
        if (v.Breaker) return ("BREAKER", Color.Firebrick, $"tripped, lag {v.LagNs / 1e6:F0}ms");
        if (!v.Warm) return ("WARMING", Color.DarkOrange, $"{v.Samples} samples");
        if (v.LagNs > maxLagNs) return ("LAG", Color.DarkOrange, $"{v.LagNs / 1e6:F0} > {maxLagNs / 1e6:F0}ms");
        if (!v.IsHealthy) return ("UNHEALTHY", Color.DarkOrange, "book/pos");
        if (!v.Quoting) return ("WAITING", Color.DarkOrange, "no signal");
        return ("QUOTING", Color.SeaGreen, $"ok {v.BestBid8 / 1e8:F2}/{v.BestAsk8 / 1e8:F2}");
    }

    void OnTimer(object? sender, EventArgs e)
    {
        DrainLog();
        if (_host is not { } h || !h.Engine.TryReadView(out EngineView v)) return;

        long ts = Stopwatch.GetTimestamp();
        double dt = Stopwatch.GetElapsedTime(_rateTs, ts).TotalSeconds;
        if (dt > 0) _fps += 0.2 * ((v.Frames - _rateFrames) / dt - _fps);
        _rateTs = ts;
        _rateFrames = v.Frames;

        var cfg = h.Engine.Config;
        long maxLagNs = cfg.MaxLagNs;
        var (text, color, reason) = Describe(h, v, maxLagNs);

        _state.Text = $"{text} ({reason})";
        _state.ForeColor = color;
        _pos.Text = (v.Pos8 / 1e8).ToString(_qf, Inv);
        _micro.Text = v.Micro > 0 ? v.Micro.ToString(_pf, Inv) : "-";
        _bid.Text = P(v.BestBid8);
        _ask.Text = P(v.BestAsk8);
        _spread.Text = v.BestAsk8 > v.BestBid8 && v.BestBid8 > 0 ? $"{P(v.BestAsk8 - v.BestBid8)} ({(v.BestAsk8 - v.BestBid8) / (double)cfg.Q.Tick8:F1} ticks)" : "-";
        _qBid.Text = $"{P(v.BidQuote8)}  {v.BidSlot}{Age(v.RecvNs, v.BidSentNs, v.BidSlot)}";
        _qAsk.Text = $"{P(v.AskQuote8)}  {v.AskSlot}{Age(v.RecvNs, v.AskSentNs, v.AskSlot)}";
        _sigma.Text = Math.Sqrt(v.Sigma2).ToString("G4", Inv);
        _ofi.Text = v.OfiNorm.ToString("F3", Inv) + (v.OfiNorm > 0.3 ? " BID HEAVY" : v.OfiNorm < -0.3 ? " ASK HEAVY" : "");
        _lag.Text = $"{v.LagNs / 1e6:F2} ms  (max {v.MaxLagSeenNs / 1e6:F2})";
        _frames.Text = $"{v.Frames:N0}  ({_fps:F0}/s)";
        _faults.Text = v.Fails.ToString("N0", Inv);
        _cmds.Text = $"{v.Cmds:N0}  sent {h.Exec.Sent:N0}  dropped {h.Exec.Dropped:N0}";
        _fills.Text = v.Fills.ToString("N0", Inv);
        _trips.Text = v.Trips.ToString("N0", Inv);
        _div.Text = v.Divergences.ToString("N0", Inv);
        _resync.Text = v.ResyncMd.ToString("N0", Inv);
        _socks.Text = $"md {SockState(h.Md)}  orders {(h.AuthRejected ? "auth rejected" : SockState(h.Orders))}";

        SetPill(_pillWarm, v.Warm);
        SetPill(_pillPos, Math.Abs(v.Pos8) < cfg.MaxPos8 * 0.8);
        SetPill(_pillLag, v.LagNs <= maxLagNs);
        SetPill(_pillBook, v.BestBid8 > 0 && v.BestAsk8 > v.BestBid8);
        SetPill(_pillHealth, v.IsHealthy && v.Quoting);
        SetPill(_pillKill, !v.Killed && !v.KillSent, v.Killed ? "KILLED" : "OK");

        _invBar.Pos8 = v.Pos8;
        _invBar.MaxPos8 = cfg.MaxPos8;
        _invBar.Base8 = v.Base8;
        _invBar.Invalidate();

        if (v.Fills != _lastFillCount)
        {
            long n = v.Fills - _lastFillCount;
            _lastFillCount = v.Fills;
            AddTape(DateTime.Now, "FILL", v.LastFillSell ? "SELL" : "BUY", P(v.LastFillPx8), $"x{n} pos {v.Pos8 / 1e8:G}");
        }
        double pnl = v.Micro > 0 ? v.CashUsd + v.Pos8 / 1e8 * v.Micro : v.CashUsd;
        _pnlLabel.Text = $"Pos {v.Pos8 / 1e8:F6} | cash ${v.CashUsd:F2} | PnL ~ ${pnl:F2} | micro {v.Micro:F2}";
        _pnlLabel.ForeColor = pnl >= 0 ? Color.LightGreen : Color.IndianRed;

        double tick = cfg.Q.Tick8 / 1e8;
        _edgeLabel.Text = $"Edge: fee {cfg.Q.MakerFeeBps}bps | minEdge {cfg.Q.MinEdgeTicks * tick:F4} | sigma√lag {Math.Sqrt(v.Sigma2) * Math.Sqrt(cfg.RttSec):F4} | tick {tick}"
            + ((v.BlockFlags & 1) != 0 ? " | BID blocked" : "") + ((v.BlockFlags & 2) != 0 ? " | ASK blocked" : "");

        // The book is mutated by the engine thread; this read is racy and only populated with a depth stream in MdSubs.
        try
        {
            var book = h.Engine.Book;
            _ladder.BeginUpdate();
            _ladder.Items.Clear();
            for (int i = 4; i >= 0; i--)
            {
                if (!book.Bids.Level(i, out long bp, out long bq)) continue;
                if (!book.Asks.Level(i, out long ap, out long aq)) continue;
                var it = new ListViewItem((bp / 1e8).ToString(_pf));
                it.SubItems.Add((bq / 1e8).ToString("F4"));
                it.SubItems.Add((ap / 1e8).ToString(_pf));
                it.SubItems.Add((aq / 1e8).ToString("F4"));
                string note = "";
                if (v.BidQuote8 == bp) note += "OUR BID ";
                if (v.AskQuote8 == ap) note += "OUR ASK ";
                it.SubItems.Add(note);
                if (note.Length > 0) it.BackColor = Color.FromArgb(40, 60, 40);
                _ladder.Items.Add(it);
            }
            _ladder.EndUpdate();
        }
        catch { /* torn read of the live book */ }

        _chart.Push(v.BestBid8 / 1e8, v.BestAsk8 / 1e8, v.BidQuote8 / 1e8, v.AskQuote8 / 1e8, v.Micro, v.IntendedBid8 / 1e8, v.IntendedAsk8 / 1e8);

        _multi.Push(v.RecvNs, v.Micro > 0 ? v.Micro : (v.BestBid8 + v.BestAsk8) / 2 / 1e8, h.PriceDecimals);
    }

    void AddTape(DateTime t, string type, string side, string price, string info)
    {
        if (_tape.Items.Count > 500) _tape.Items.RemoveAt(0);
        var it = new ListViewItem(t.ToString("HH:mm:ss.fff"));
        it.SubItems.Add(type);
        it.SubItems.Add(side);
        it.SubItems.Add(price);
        it.SubItems.Add(info);
        if (type == "FILL") it.ForeColor = Color.LightGreen;
        if (type == "CANCEL") it.ForeColor = Color.Orange;
        if (type == "TRIP") it.ForeColor = Color.Red;
        _tape.Items.Add(it);
        _tape.EnsureVisible(_tape.Items.Count - 1);
    }

    async void OnStart(object? sender, EventArgs e)
    {
        if (_ui != UiState.Idle) return;
        SetUi(UiState.Starting);
        try
        {
            _settings.Save();
            GemxHost h = await GemxHost.StartAsync(_settings);
            _host = h;
            _pf = "F" + h.PriceDecimals;
            _qf = "F" + h.QtyDecimals;
            _chart.Reset(h.PriceDecimals);
            _fps = 0;
            _rateFrames = 0;
            _rateTs = Stopwatch.GetTimestamp();
            _lastFillCount = 0;
            _tape.Items.Clear();
            SetUi(UiState.Running);

            _ = _multi.LoadHistoryAsync(_settings.Symbol, h.PriceDecimals);
        }
        catch (Exception ex)
        {
            Log("start failed: " + ex.Message);
            SetUi(UiState.Idle);
        }
    }

    async void OnStop(object? sender, EventArgs e) => await StopAsync();

    async Task StopAsync()
    {
        if (_host is not { } h || _ui != UiState.Running) return;
        SetUi(UiState.Stopping);
        try { await h.StopAsync(); }
        catch (Exception ex) { Log("stop failed: " + ex.Message); }
        DrainLog();
        _host = null;
        _state.Text = "STOPPED";
        _state.ForeColor = ForeColor;
        _socks.Text = "-";
        SetUi(UiState.Idle);
    }

    void OnKill(object? sender, EventArgs e)
    {
        if (_host is not { } h) return;
        h.Engine.Kill = true;
        _kill.Enabled = false;
        AddTape(DateTime.Now, "KILL", "", "", "requested");
        Log("kill requested");
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_ui == UiState.Starting || _ui == UiState.Stopping) { e.Cancel = true; return; }
        if (_ui == UiState.Running && !_closing)
        {
            e.Cancel = true;
            _closing = true;
            await StopAsync();
            Close();
            return;
        }
        try { _settings.Save(); } catch (IOException ex) { Log("settings not saved: " + ex.Message); }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _mono.Dispose();
            _pnlLabel.Font.Dispose();
            _edgeLabel.Font.Dispose();
        }
        base.Dispose(disposing);
    }

    sealed class InventoryBar : Control
    {
        static readonly Pen BasePen = new(Color.FromArgb(60, 60, 70));
        static readonly Pen MidPen = new(Color.Gray, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        static readonly Font Label = new("Consolas", 8f);
        static readonly SolidBrush Ok = new(Color.SeaGreen), Warn = new(Color.Orange), Hot = new(Color.Firebrick);

        public long Pos8, MaxPos8 = 1, Base8;
        public InventoryBar() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(28, 30, 35));
            float w = Width, h = Height;
            float mid = w * 0.5f;
            g.DrawLine(BasePen, 0, h / 2, w, h / 2);
            g.DrawLine(MidPen, mid, 0, mid, h);
            if (MaxPos8 <= 0) return;
            double norm = Math.Clamp((double)Pos8 / MaxPos8, -1, 1);
            float x = (float)(mid + norm * mid * 0.9);
            SolidBrush br = Math.Abs(norm) > 0.9 ? Hot : Math.Abs(norm) > 0.6 ? Warn : Ok;
            g.FillRectangle(br, Math.Min(mid, x), 4, Math.Abs(x - mid), h - 8);
            g.DrawString($"POS {Pos8 / 1e8:F6} / {MaxPos8 / 1e8:F6}  base {Base8 / 1e8:F6}", Label, Brushes.White, 4, 4);
        }
    }
}
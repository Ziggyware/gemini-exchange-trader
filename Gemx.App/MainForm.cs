using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Gemx.App;

enum UiState { Idle, Starting, Running, Stopping }

public sealed class MainForm : Form
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------ state
    AppSettings _settings = AppSettings.Load();
    GemxHost? _host;
    UiState _ui = UiState.Idle;
    bool _closing;
    string _pf = "F2", _qf = "F8", _base = "BTC";

    long _rateTs = Stopwatch.GetTimestamp(), _rateFrames;
    long _startedTs;
    double _fps;
    long _lastFills, _lastTrips, _lastResync, _lastDiv, _lastFails;
    string _stateText = "";
    double _lastPnl;
    bool _pnlKnown;

    // ------------------------------------------------------------------ chrome
    readonly LogoMark _logo = new();
    readonly StateChip _chip = new();
    readonly AlertBanner _alert = new();
    readonly ToolButton _btnStart = new("Start", Glyph.Play, ToolButton.Kind.Primary);
    readonly ToolButton _btnStop = new("Stop", Glyph.Stop, ToolButton.Kind.Ghost);
    readonly ToolButton _btnKill = new("Kill", Glyph.Skull, ToolButton.Kind.Danger);
    readonly ToolButton _btnSettings = new("Settings", Glyph.Gear, ToolButton.Kind.Subtle);
    readonly ToolButton _btnSnapshot = new("Snapshot", Glyph.Copy, ToolButton.Kind.Subtle);
    readonly ToolButton _btnFit = new("Fit", Glyph.Target, ToolButton.Kind.Subtle);
    readonly ToolButton _btnLog = new("Log", Glyph.Pulse, ToolButton.Kind.Subtle);
    readonly ToolButton _btnLogCopy = new("Copy", Glyph.Copy, ToolButton.Kind.Subtle);
    readonly ToolButton _btnLogClear = new("Clear", Glyph.Broom, ToolButton.Kind.Subtle);
    readonly ToolButton _btnLogScroll = new("Follow", Glyph.ChevronDown, ToolButton.Kind.Subtle);
    readonly ToolButton _btnSetSave = new("Save", Glyph.Check, ToolButton.Kind.Primary);
    readonly ToolButton _btnSetReset = new("Defaults", Glyph.Broom, ToolButton.Kind.Ghost);
    readonly ToolButton _btnSetClose = new("Close", Glyph.Close, ToolButton.Kind.Ghost);

    readonly Segments _tabs = new("Flow", "1m", "5m", "15m", "30m", "1h", "1d", "1w", "1M");
    readonly QuoteChart _flow = new();
    readonly CandleSet _candles = new();
    readonly DepthLadder _ladder = new();
    readonly TapeView _tape = new();
    readonly LogView _log = new();
    readonly InventoryGauge _gauge = new();
    readonly QuoteRow _rowBid = new(false);
    readonly QuoteRow _rowAsk = new(true);

    readonly MetricTile _tilePos = new("Net position");
    readonly MetricTile _tilePnl = new("Mark-to-market P&L");

    readonly MeterBar _mOfi = new("Order-flow imbalance");
    readonly MeterBar _mSigma = new("Volatility sigma");
    readonly MeterBar _mLag = new("Feed lag excess");
    readonly MeterBar _mWarm = new("Warm-up");

    readonly StatGrid _statPos = new(2, 1);
    readonly StatGrid _statPerf = new(3, 1);
    readonly StatGrid _statQuote = new(3, 2);
    readonly StatGrid _statDiag = new(2, 5);

    readonly CardStack _rail = new();
    readonly Card _cardPos = new("Position", "gauge", Pal.Up, 182);
    readonly Card _cardPerf = new("Performance", "target", Pal.Accent, 140);
    readonly Card _cardQuotes = new("Live quotes", "layers", Pal.Violet, 176);
    readonly Card _cardSignals = new("Signals", "wave", Pal.Warn, 186);
    readonly Card _cardDiag = new("Diagnostics", "depth", Pal.Neutral, 194);

    readonly StatusPill _pillWarm = new("WARM");
    readonly StatusPill _pillPos = new("POS");
    readonly StatusPill _pillLag = new("LAG");
    readonly StatusPill _pillBook = new("BOOK");
    readonly StatusPill _pillHealth = new("HEALTH");
    readonly StatusPill _pillKill = new("KILL");
    readonly StatusPill _pillMd = new("MD");
    readonly StatusPill _pillOrd = new("ORDERS");

    readonly Label _lblRight = new() { AutoSize = true, Font = Fonts.MonoSmall, ForeColor = Pal.TextDim, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 4, 8, 0) };
    readonly Label _lblChart = new() { AutoSize = true, Font = Fonts.MonoTiny, ForeColor = Pal.TextFaint, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 7, 6, 0) };
    readonly Label _lblLog = new() { AutoSize = true, Font = Fonts.MonoTiny, ForeColor = Pal.TextFaint, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 7, 8, 0) };

    readonly PropertyGrid _grid = new();
    readonly Panel _drawer = new() { Dock = DockStyle.Fill, Visible = false };
    readonly ToolTip _tip = new() { InitialDelay = 320, ReshowDelay = 110, AutoPopDelay = 14000 };

    readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    RowStyle _logRow = null!;
    TableLayoutPanel? _root;
    int _pDec = 2, _qDec = 8;
    bool _depthStream;
    long _alertUntil;

    // ------------------------------------------------------------------ construction
    public MainForm()
    {
        Text = "Gemx — maker terminal";
        ClientSize = new Size(1560, 970);
        MinimumSize = new Size(1180, 740);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Pal.Bg;
        ForeColor = Pal.Text;
        DoubleBuffered = true;
        Font = Fonts.Ui;
        KeyPreview = true;

        BuildRail();
        BuildTitle();
        _root = BuildRoot();
        BuildSettingsDrawer();
        Controls.Add(_root);
        _alert.Visible = false;
        _alert.Dismissed += HideAlert;

        _tabs.Changed += ShowChart;
        _btnStart.Click += OnStart;
        _btnStop.Click += OnStop;
        _btnKill.Click += OnKill;
        _btnSettings.Click += (_, _) => ToggleSettings();
        _btnSetClose.Click += (_, _) => ToggleSettings();
        _btnSetSave.Click += (_, _) => { _settings.Save(); AppendLogLine("settings saved"); };
        _btnSetReset.Click += (_, _) => { _settings = new AppSettings(); _grid.SelectedObject = _settings; AppendLogLine("settings reset to defaults (not saved yet)"); };
        _btnSnapshot.Click += (_, _) => Snapshot();
        _btnFit.Click += (_, _) => { _flow.ResetView(); _candles.ResetViews(); };
        _btnLog.Click += (_, _) => ToggleLog();
        _btnLogCopy.Click += (_, _) => _log.CopyAll();
        _btnLogClear.Click += (_, _) => _log.Clear();
        _btnLogScroll.Click += (_, _) =>
        {
            _log.AutoScroll = !_log.AutoScroll;
            _btnLogScroll.Checked = _log.AutoScroll;
            _btnLogScroll.SetText(_log.AutoScroll ? "Follow" : "Frozen");
            if (_log.AutoScroll) _log.ScrollToEnd();
        };
        _btnLogScroll.Checked = true;
        _ladder.CopyNote += m => AppendLogLine(m);
        _tape.Note += m => AppendLogLine(m);
        _candles.Log += m => AppendLogLine("candles: " + m);

        _tip.SetToolTip(_btnStart, "Start a quoting session (F5). Loads the symbol spec, opens both sockets and warms the volatility grid.");
        _tip.SetToolTip(_btnStop, "Stop the session (F6). Requests a session cancel and waits for the acknowledgement.");
        _tip.SetToolTip(_btnKill, "Kill switch (Ctrl+K). Trips the breaker and flushes every open order immediately.");
        _tip.SetToolTip(_btnSettings, "Edit the session settings (Ctrl+,). The running session keeps the settings it started with.");
        _tip.SetToolTip(_pillWarm, "Volatility grid warm-up progress.");
        _tip.SetToolTip(_pillPos, "Inventory against the configured position limit.");
        _tip.SetToolTip(_pillLag, "Feed lag excess against the configured maximum.");
        _tip.SetToolTip(_pillBook, "Top of book is two-sided and not crossed.");
        _tip.SetToolTip(_pillHealth, "Aggregate health: position known, warm, inside the lag budget and quoting.");
        _tip.SetToolTip(_pillKill, "Kill switch state.");
        _tip.SetToolTip(_pillMd, "Market-data socket state.");
        _tip.SetToolTip(_pillOrd, "Authenticated order socket state.");
        _tip.SetToolTip(_ladder, "Depth ladder. Wheel changes how many levels are shown, click a row to copy its price.");
        _tip.SetToolTip(_tape, "Fills, orders and alerts. Click a row to copy it.");
        _tip.SetToolTip(_flow, "Wheel zooms the time window, drag pans, double-click resets.");
        _tip.SetToolTip(_btnSnapshot, "Copy a text snapshot of the live state to the clipboard.");
        _tip.SetToolTip(_btnLogScroll, "Keep the log pinned to the newest line.");

        SetUi(UiState.Idle);
        ShowChart(0);
        _timer.Tick += OnTimer;
        _timer.Start();
    }

    // ------------------------------------------------------------------ layout
    void BuildTitle()
    {
        _logo.Dock = DockStyle.Fill;
        _chip.Dock = DockStyle.Fill;
        _alert.Dock = DockStyle.Fill;

        _btnStart.Margin = new Padding(4, 0, 0, 0);
        _btnStop.Margin = new Padding(4, 0, 0, 0);
        _btnKill.Margin = new Padding(4, 0, 0, 0);
        _btnSettings.Margin = new Padding(4, 0, 0, 0);
    }

    TableLayoutPanel BuildRoot()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Pal.Bg };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        var title = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Pal.Bg, Margin = new Padding(0) };
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 268));
        title.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        title.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        title.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        title.Controls.Add(_logo, 0, 0);
        title.Controls.Add(_chip, 1, 0);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, BackColor = Pal.Bg, Anchor = AnchorStyles.Right };
        tools.Controls.AddRange(new Control[] { _btnStart, _btnStop, _btnKill, _btnSettings });
        title.Controls.Add(tools, 3, 0);
        root.Controls.Add(title, 0, 0);

        var alertRow = new Panel { Dock = DockStyle.Fill, BackColor = Pal.Panel, Padding = new Padding(6, 3, 6, 3) };
        alertRow.Controls.Add(_alert);
        root.Controls.Add(alertRow, 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Pal.Bg, Margin = new Padding(0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 344));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(_rail, 0, 0);
        body.Controls.Add(_drawer, 0, 0);
        body.Controls.Add(BuildContent(), 1, 0);
        root.Controls.Add(body, 0, 2);

        root.Controls.Add(BuildStatusBar(), 0, 3);
        return root;
    }

    Control BuildContent()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Pal.Bg, Margin = new Padding(0) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 348));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // ---- charts column
        var charts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Pal.Bg, Margin = new Padding(6, 6, 6, 6) };
        charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        charts.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        charts.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _logRow = new RowStyle(SizeType.Absolute, 166);
        charts.RowStyles.Add(_logRow);

        var tabRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, BackColor = Pal.Bg, Margin = new Padding(0) };
        tabRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 372));
        tabRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tabRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tabRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tabRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tabs.Dock = DockStyle.Fill;
        _tabs.Margin = new Padding(0, 1, 6, 1);
        tabRow.Controls.Add(_tabs, 0, 0);
        _btnFit.Margin = new Padding(0, 3, 4, 3);
        _btnSnapshot.Margin = new Padding(0, 3, 6, 3);
        tabRow.Controls.Add(_btnFit, 2, 0);
        tabRow.Controls.Add(_btnSnapshot, 3, 0);
        tabRow.Controls.Add(_lblChart, 4, 0);
        charts.Controls.Add(tabRow, 0, 0);

        var host = new Panel { Dock = DockStyle.Fill, BackColor = Pal.Card, Margin = new Padding(0, 0, 0, 0), Padding = new Padding(0) };
        _flow.Dock = DockStyle.Fill;
        _candles.Dock = DockStyle.Fill;
        host.Controls.Add(_flow);
        host.Controls.Add(_candles);
        charts.Controls.Add(host, 0, 1);

        var logWrap = new Panel { Dock = DockStyle.Fill, BackColor = Pal.Panel, Margin = new Padding(0) };
        var logBar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 26, ColumnCount = 6, RowCount = 1, BackColor = Pal.Panel, Margin = new Padding(0), Padding = new Padding(4, 2, 4, 2) };
        logBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (ToolButton c in new[] { _btnLog, _btnLogCopy, _btnLogClear, _btnLogScroll })
        {
            c.Margin = new Padding(0, 0, 4, 0);
            c.Compact = true;
        }
        logBar.Controls.Add(_btnLog, 0, 0);
        logBar.Controls.Add(_btnLogCopy, 1, 0);
        logBar.Controls.Add(_btnLogClear, 2, 0);
        logBar.Controls.Add(_btnLogScroll, 3, 0);
        logBar.Controls.Add(_lblLog, 5, 0);
        _log.Dock = DockStyle.Fill;
        logWrap.Controls.Add(_log);
        logWrap.Controls.Add(logBar);
        logWrap.Paint += (_, e) =>
        {
            Gfx.HairH(e.Graphics, logBar.Bottom - 0.5f, 0, logWrap.Width, Pal.LineSoft);
        };
        charts.Controls.Add(logWrap, 0, 2);

        // ---- right column
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Pal.Bg, Margin = new Padding(0, 6, 6, 6) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 42));

        _ladder.Dock = DockStyle.Fill;
        _ladder.Surface = Pal.Card;
        var depthCard = new Card("Depth", "depth", Pal.Info, 400) { Collapsible = false };
        depthCard.Accent = Pal.Info;
        depthCard.Body.Controls.Add(_ladder);
        right.Controls.Add(depthCard, 0, 0);

        _tape.Dock = DockStyle.Fill;
        var tapeCard = new Card("Activity", "pulse", Pal.Accent, 300) { Collapsible = false, Caption = "newest first" };
        tapeCard.Body.Controls.Add(_tape);
        right.Controls.Add(tapeCard, 0, 1);

        content.Controls.Add(charts, 0, 0);
        content.Controls.Add(right, 1, 0);
        return content;
    }

    void BuildRail()
    {
        _rail.Dock = DockStyle.Fill;
        _rail.Margin = new Padding(0);
        _rail.Gap = 9;

        // POSITION ------------------------------------------------------------
        var posBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Pal.Card, Margin = new Padding(0) };
        posBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        posBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        posBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        posBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _tilePos.Dock = DockStyle.Fill;
        _tilePos.Accent = Pal.Up;
        posBody.Controls.Add(_tilePos, 0, 0);
        _gauge.Dock = DockStyle.Fill;
        posBody.Controls.Add(_gauge, 0, 1);
        _statPos.Dock = DockStyle.Fill;
        _statPos.Surface = Pal.Card;
        posBody.Controls.Add(_statPos, 0, 2);
        _cardPos.Body.Controls.Add(posBody);
        _tip.SetToolTip(_tilePos, "Position measured from the balance seen at the first balance update.");
        _rail.Add(_cardPos);

        // PERFORMANCE ---------------------------------------------------------
        var perfBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Pal.Card, Margin = new Padding(0) };
        perfBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        perfBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        perfBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _tilePnl.Dock = DockStyle.Fill;
        _tilePnl.Accent = Pal.Accent;
        _tilePnl.Unit = "USD";
        perfBody.Controls.Add(_tilePnl, 0, 0);
        _statPerf.Dock = DockStyle.Fill;
        perfBody.Controls.Add(_statPerf, 0, 1);
        _cardPerf.Body.Controls.Add(perfBody);
        _tip.SetToolTip(_tilePnl, "Cash from fills plus open inventory marked at the micro price.");
        _rail.Add(_cardPerf);

        // LIVE QUOTES ---------------------------------------------------------
        var quoteBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Pal.Card, Margin = new Padding(0) };
        quoteBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        quoteBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        quoteBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        quoteBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _rowBid.Dock = DockStyle.Fill;
        _rowAsk.Dock = DockStyle.Fill;
        _statQuote.Dock = DockStyle.Fill;
        quoteBody.Controls.Add(_rowBid, 0, 0);
        quoteBody.Controls.Add(_rowAsk, 0, 1);
        quoteBody.Controls.Add(_statQuote, 0, 2);
        _cardQuotes.Body.Controls.Add(quoteBody);
        _tip.SetToolTip(_rowBid, "Our resting bid: price, lifecycle state, age and distance to the touch in ticks.");
        _tip.SetToolTip(_rowAsk, "Our resting ask: price, lifecycle state, age and distance to the touch in ticks.");
        _rail.Add(_cardQuotes);

        // SIGNALS -------------------------------------------------------------
        var sigBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Pal.Card, Margin = new Padding(0) };
        sigBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) sigBody.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        foreach (var m in new[] { _mOfi, _mSigma, _mLag, _mWarm })
        {
            m.Dock = DockStyle.Fill;
            m.Surface = Pal.Card;
            sigBody.Controls.Add(m);
        }
        _cardSignals.Body.Controls.Add(sigBody);
        _tip.SetToolTip(_mOfi, "Exponentially decayed order-flow imbalance, normalised by the average top-of-book size.");
        _tip.SetToolTip(_mSigma, "Realised volatility from the 100 ms mid grid, shown per square root of a second.");
        _tip.SetToolTip(_mLag, "Clock-aligned feed lag above its running floor. The marker is the worst seen this session.");
        _tip.SetToolTip(_mWarm, "Volatility samples collected against the warm-up requirement.");
        _rail.Add(_cardSignals);

        // DIAGNOSTICS ---------------------------------------------------------
        _statDiag.Dock = DockStyle.Fill;
        _cardDiag.Body.Controls.Add(_statDiag);
        _cardDiag.SetExpanded(false);
        _rail.Add(_cardDiag);
    }

    Control BuildStatusBar()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Pal.Bg, Margin = new Padding(0), Padding = new Padding(6, 3, 6, 3) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var pills = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, BackColor = Pal.Bg, Margin = new Padding(0) };
        foreach (var p in new[] { _pillWarm, _pillPos, _pillLag, _pillBook, _pillHealth, _pillKill, _pillMd, _pillOrd })
        {
            p.Margin = new Padding(0, 0, 5, 0);
            p.Surface = Pal.Bg;
            pills.Controls.Add(p);
        }
        bar.Controls.Add(pills, 0, 0);
        bar.Controls.Add(_lblRight, 2, 0);
        return bar;
    }

    void BuildSettingsDrawer()
    {
        var d = (TableLayoutPanel)_drawer;
        d.ColumnCount = 1;
        d.RowCount = 3;
        d.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        d.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        d.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        d.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        d.BackColor = Pal.Panel;

        var head = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Pal.Panel, Margin = new Padding(0) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var caption = new Label { Dock = DockStyle.Fill, ForeColor = Pal.Text, Font = Fonts.UiSmallBold, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), Text = "SESSION SETTINGS", BackColor = Pal.Panel };
        head.Controls.Add(caption, 0, 0);
        _btnSetReset.Margin = new Padding(0, 5, 4, 5);
        _btnSetClose.Margin = new Padding(0, 5, 6, 5);
        head.Controls.Add(_btnSetReset, 1, 0);
        head.Controls.Add(_btnSetClose, 2, 0);
        head.Paint += (_, e) => Gfx.HairH(e.Graphics, head.Height - 0.5f, 0, head.Width, Pal.Line);

        _grid.Dock = DockStyle.Fill;
        _grid.Margin = new Padding(6, 0, 6, 0);
        _grid.SelectedObject = _settings;
        _grid.ToolbarVisible = false;
        _grid.HelpVisible = true;
        _grid.PropertySort = PropertySort.CategorizedAlphabetical;
        _grid.BackColor = Pal.Card;
        _grid.ForeColor = Pal.Text;
        _grid.ViewBackColor = Pal.Card;
        _grid.ViewForeColor = Pal.Text;
        _grid.ViewBorderColor = Pal.LineSoft;
        _grid.HelpBackColor = Pal.Panel;
        _grid.HelpForeColor = Pal.TextDim;
        _grid.HelpBorderColor = Pal.LineSoft;
        _grid.LineColor = Pal.LineSoft;
        _grid.CategoryForeColor = Pal.Accent;
        _grid.CategorySplitterColor = Pal.LineSoft;
        _grid.DisabledItemForeColor = Pal.TextFaint;
        _grid.SelectedItemWithFocusBackColor = Pal.AccentDeep;
        _grid.SelectedItemWithFocusForeColor = Color.White;
        _grid.Font = Fonts.UiSmall;

        var foot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Pal.Panel, Margin = new Padding(0) };
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Pal.TextFaint,
            Font = Fonts.MonoTiny,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            Text = "GEMX_API_KEY / GEMX_API_SECRET come from the environment",
            BackColor = Pal.Panel
        };
        _btnSetSave.Margin = new Padding(0, 5, 6, 5);
        foot.Controls.Add(hint, 0, 0);
        foot.Controls.Add(_btnSetSave, 1, 0);

        d.Controls.Add(head, 0, 0);
        d.Controls.Add(_grid, 0, 1);
        d.Controls.Add(foot, 0, 2);
    }

    // ------------------------------------------------------------------ chart switching
    void ShowChart(int index)
    {
        bool flow = index == 0;
        _flow.Visible = flow;
        _candles.Visible = !flow;
        if (!flow)
        {
            TF tf = CandleSet.Order[Math.Clamp(index - 1, 0, CandleSet.Order.Length - 1)];
            _candles.ShowTf(tf);
            _lblChart.Text = Fmt.TfLong(tf) + " candles · wheel zoom, drag pan, L for log";
        }
        else
        {
            _lblChart.Text = "quote flow · wheel zoom, drag pan, hover for the readout";
        }
        _tabs.LiveDot = flow && _ui == UiState.Running;
    }

    void ToggleSettings()
    {
        bool show = !_drawer.Visible;
        _drawer.Visible = show;
        _rail.Visible = !show;
        _btnSettings.Checked = show;
        if (show) _grid.SelectedObject = _settings;
    }

    void ToggleLog()
    {
        bool open = _logRow.Height < 40;
        _logRow.Height = open ? 166 : 26;
        _log.Visible = open;
        _btnLog.Checked = open;
    }

    // ------------------------------------------------------------------ ui state
    void SetUi(UiState s)
    {
        _ui = s;
        _btnStart.Enabled = s == UiState.Idle;
        _btnStop.Enabled = s == UiState.Running;
        _btnKill.Enabled = s == UiState.Running && _host != null && !_host.Engine.Kill;
        _btnSettings.Enabled = s == UiState.Idle;
        _grid.Enabled = s == UiState.Idle;
        _tabs.LiveDot = s == UiState.Running && _tabs.Selected == 0;
        if (s == UiState.Idle) _chip.Set("Idle", _host == null ? "no session" : "session stopped", StateTone.Idle, false);
        else if (s == UiState.Starting) _chip.Set("Starting", "spec + sockets", StateTone.Info, true);
        else if (s == UiState.Stopping) _chip.Set("Stopping", "flushing", StateTone.Warm, true);
    }

    string P(long x8) => Fmt.Price(x8, _pDec);

    static string SockState(FeedSocket s) => s.Current?.State.ToString() ?? "down";

    static StateTone SocketTone(FeedSocket s)
    {
        string st = s.Current?.State.ToString() ?? "down";
        return st == "Open" ? StateTone.Good : st == "Connecting" ? StateTone.Warm : StateTone.Bad;
    }

    static (string Text, string Reason, StateTone Tone) Describe(GemxHost h, EngineView v, long maxLagNs, int dec)
    {
        if (h.Engine.Kill || v.KillSent)
            return v.FlushOk ? ("Killed", "session cancel acked", StateTone.Bad) : ("Killing", "flushing orders", StateTone.Bad);
        if (h.AuthRejected) return ("Auth rejected", "orders socket 401/403", StateTone.Bad);
        if (v.Breaker) return ("Breaker", "tripped at lag " + Fmt.Lag(v.LagNs), StateTone.Bad);
        if (!v.Warm) return ("Warming", $"{v.Samples} / {h.Config.WarmupSamples} samples", StateTone.Warm);
        if (v.LagNs > maxLagNs) return ("Lagging", $"{Fmt.Lag(v.LagNs)} over {Fmt.Lag(maxLagNs)}", StateTone.Warm);
        if (!v.IsHealthy) return ("Unhealthy", "book or position unknown", StateTone.Warm);
        if (!v.Quoting) return ("Waiting", "no signal", StateTone.Warm);
        return ("Quoting", $"bid {Fmt.Price(v.BestBid8, dec)}  ask {Fmt.Price(v.BestAsk8, dec)}", StateTone.Good);
    }

    // ------------------------------------------------------------------ logging
    void AppendLogLine(string m)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLogLine(m)); return; }
        _log.Append($"{DateTime.Now:HH:mm:ss.fff} {m}");
        _lblLog.Text = $"{_log.LineCount} lines";
    }

    void DrainLog()
    {
        if (_host is not { } h) return;
        var sb = new StringBuilder();
        int n = 0;
        while (n < 400 && h.Logs.TryDequeue(out string? m)) { sb.Append(m).Append("\r\n"); n++; }
        if (n > 0) _log.Append(sb.ToString().TrimEnd('\r', '\n'));
        _lblLog.Text = $"{_log.LineCount} lines";
    }

    // ------------------------------------------------------------------ alerts
    void ShowAlert(string title, string detail, StateTone tone)
    {
        _alert.Show(title, detail, tone);
        _alertUntil = Environment.TickCount64 + (tone == StateTone.Bad ? 60_000 : 9_000);
        if (_root != null) _root.RowStyles[1].Height = 42;
    }

    void HideAlert()
    {
        _alertUntil = 0;
        _alert.Visible = false;
        if (_root != null) _root.RowStyles[1].Height = 0;
    }

    // ------------------------------------------------------------------ main loop
    void OnTimer(object? sender, EventArgs e)
    {
        DrainLog();
        // timed alerts retire themselves so the surface goes quiet again
        if (_alert.Visible && _alertUntil > 0 && Environment.TickCount64 > _alertUntil) HideAlert();
        if (_host is not { } h || !h.Engine.TryReadView(out EngineView v)) return;

        long ts = Stopwatch.GetTimestamp();
        double dt = Stopwatch.GetElapsedTime(_rateTs, ts).TotalSeconds;
        if (dt > 0.001) _fps += 0.2 * ((v.Frames - _rateFrames) / dt - _fps);
        _rateTs = ts;
        _rateFrames = v.Frames;

        EngineConfig cfg = h.Config;
        int dec = _pDec;
        long tick = cfg.Q.Tick8;
        double tickPx = tick / 1e8;

        var (text, reason, tone) = Describe(h, v, cfg.MaxLagNs, dec);
        bool stateChanged = text != _stateText;
        _stateText = text;
        _chip.Set(text, reason, tone, tone is StateTone.Warm or StateTone.Bad || _ui != UiState.Running);

        if (stateChanged && _ui == UiState.Running)
        {
            if (tone == StateTone.Bad) { ShowAlert(text.ToUpperInvariant(), reason, StateTone.Bad); _tape.Add(TapeKind.Alert, false, "STATE", "", "", text + " — " + reason); }
            else if (tone == StateTone.Warm) { ShowAlert(text, reason, StateTone.Warm); _tape.Add(TapeKind.Order, false, "STATE", "", "", text + " — " + reason); }
            else if (tone == StateTone.Good) { _alert.Dismiss(); HideAlert(); _tape.Add(TapeKind.Boot, false, "STATE", "", "", "quoting — " + reason); }
        }

        // ---- tiles
        double mark = v.Micro > 0 ? v.Micro : v.Mid > 0 ? v.Mid : 0;
        double pnl = mark > 0 ? v.CashUsd + v.Pos8 / 1e8 * mark : v.CashUsd;
        int dir = !_pnlKnown ? 0 : pnl > _lastPnl ? 1 : pnl < _lastPnl ? -1 : 0;
        _lastPnl = pnl;
        _pnlKnown = true;

        double posF = v.Pos8 / 1e8;
        _tilePos.Accent = Math.Abs(v.Pos8) >= cfg.MaxPos8 ? Pal.Warn : Pal.Up;
        _tilePos.SetValue(posF.ToString("F" + _qf, Inv), Math.Abs(v.Pos8) >= cfg.MaxPos8 ? Pal.Warn : Pal.TextHi, 0);
        _tilePos.SetCaption($"limit ±{(cfg.MaxPos8 / 1e8).ToString("F" + _qf, Inv)} · quote {(cfg.QuoteQty8 / 1e8).ToString("F" + _qf, Inv)}");
        _tilePos.Push(posF);

        _tilePnl.SetValue(Fmt.SignedUsd(pnl), pnl >= 0 ? Pal.UpLit : Pal.DownLit, dir);
        _tilePnl.SetCaption($"cash {Fmt.SignedUsd(v.CashUsd)} · mark {mark.ToString("N" + dec, Inv)}");
        _tilePnl.Push(pnl);

        _gauge.Set(v.Pos8, cfg.MaxPos8, v.Base8, cfg.QuoteQty8, v.BlockFlags != 0);
        _statPos.Set(0, "base", (v.Base8 / 1e8).ToString("F" + _qf, Inv), Pal.Text);
        _statPos.Set(1, "avg entry", v.Fills > 0 && Math.Abs(posF) > 1e-12 ? (Math.Abs(v.CashUsd) / Math.Abs(posF)).ToString("N" + dec, Inv) : "-", Pal.TextDim);
        _statPerf.Set(0, "cash", Fmt.SignedUsd(v.CashUsd), v.CashUsd >= 0 ? Pal.Text : Pal.DownLit);
        _statPerf.Set(1, "micro", mark > 0 ? mark.ToString("N" + dec, Inv) : "-", Pal.Text);
        _statPerf.Set(2, "mid", v.Mid > 0 ? v.Mid.ToString("N" + dec, Inv) : "-", Pal.TextDim);

        // ---- quotes card
        _rowBid.Set(v.BidQuote8, v.IntendedBid8, v.BestBid8, tick, dec, v.BidSlot, v.BidSentNs, v.RecvNs, (v.BlockFlags & 1) != 0);
        _rowAsk.Set(v.AskQuote8, v.IntendedAsk8, v.BestAsk8, tick, dec, v.AskSlot, v.AskSentNs, v.RecvNs, (v.BlockFlags & 2) != 0);
        long spread = v.BestAsk8 > v.BestBid8 && v.BestBid8 > 0 ? v.BestAsk8 - v.BestBid8 : 0;
        _statQuote.Set(0, "spread", spread > 0 ? $"{Fmt.Price(spread, dec)}  {(spread / (double)tick):F0}t" : "-", Pal.Text);
        _statQuote.Set(1, "our edge", v.BidQuote8 > 0 && v.AskQuote8 > 0 && spread > 0 ? $"{((v.BestBid8 - v.BidQuote8) / (double)tick):F0}t / {((v.AskQuote8 - v.BestAsk8) / (double)tick):F0}t" : "-", Pal.Text);
        _statQuote.Set(2, "fill", v.Fills > 0 ? $"{(v.LastFillSell ? "sell" : "buy")} {Fmt.Price(v.LastFillPx8, dec)}" : "-", Pal.TextDim);
        _statQuote.Set(3, "maker fee", cfg.Q.MakerFeeBps.ToString("F2", Inv) + " bp", Pal.TextDim);
        _statQuote.Set(4, "min edge", (cfg.Q.MinEdgeTicks * tickPx).ToString("N" + dec, Inv), Pal.TextDim);
        _statQuote.Set(5, "requote", cfg.RequoteTicks + "t", Pal.TextDim);

        // ---- depth ladder
        _ladder.ScanBook(_depthStream ? h.Engine.Book : null);
        _ladder.Update(new DepthInput
        {
            Dec = dec,
            Tick8 = tick,
            BestBid8 = v.BestBid8,
            BestAsk8 = v.BestAsk8,
            BestBidQty8 = v.BestBidQty8,
            BestAskQty8 = v.BestAskQty8,
            OurBid8 = v.BidQuote8,
            OurAsk8 = v.AskQuote8,
            Micro = v.Micro > 0 ? v.Micro : v.Mid,
            BidSlot = v.BidSlot,
            AskSlot = v.AskSlot,
            BookSynced = _depthStream && h.Engine.Book.Synced
        });

        // ---- meters
        double ofi = v.OfiNorm;
        _mOfi.Set(Fmt.Signed(ofi, 3), (ofi + 1) / 2, ofi > 0.3 ? Pal.Up : ofi < -0.3 ? Pal.Down : Pal.Neutral, 0.5, null, ofi > 0.3 ? "bid heavy" : ofi < -0.3 ? "ask heavy" : null);
        double sigma = Math.Sqrt(Math.Max(0, v.Sigma2));
        double sigmaRef = Math.Max(1e-9, 10 * tickPx);
        _mSigma.Set(sigma.ToString("G4", Inv) + " /√s", sigma / sigmaRef, Pal.Violet, null, null, "10 ticks = " + sigmaRef.ToString("G3", Inv));
        double lagMs = v.LagNs / 1e6;
        double lagScale = cfg.MaxLagNs * 1.5 / 1e6;
        _mLag.Set(Fmt.Lag(v.LagNs), lagMs / lagScale, lagMs > cfg.MaxLagNs / 1e6 ? Pal.Down : lagMs > cfg.MaxLagNs / 2e6 ? Pal.Warn : Pal.Up,
            cfg.MaxLagNs / 1e6 / lagScale, v.MaxLagSeenNs / 1e6 / lagScale, "max " + Fmt.Lag(cfg.MaxLagNs), Pal.TextDim);
        int warmPct = (int)Math.Round(100.0 * v.Samples / Math.Max(1, cfg.WarmupSamples));
        _mWarm.Set($"{Math.Min(v.Samples, cfg.WarmupSamples)} / {cfg.WarmupSamples}", Gfx.Clamp01(v.Samples / (double)Math.Max(1, cfg.WarmupSamples)),
            v.Warm ? Pal.Up : Pal.Warn, null, null, warmPct + "%");

        // ---- diagnostics card
        _statDiag.Set(0, "frames", Fmt.Count(v.Frames), Pal.Text);
        _statDiag.Set(1, "rate", _fps.ToString("F1", Inv) + " /s", Pal.Text);
        _statDiag.Set(2, "parse faults", v.Fails.ToString("N0", Inv), v.Fails > 0 ? Pal.Warn : Pal.TextDim);
        _statDiag.Set(3, "commands", Fmt.Count(v.Cmds), Pal.Text);
        _statDiag.Set(4, "fills", v.Fills.ToString("N0", Inv), v.Fills > 0 ? Pal.UpLit : Pal.TextDim);
        _statDiag.Set(5, "breaker trips", v.Trips.ToString("N0", Inv), v.Trips > 0 ? Pal.DownLit : Pal.TextDim);
        _statDiag.Set(6, "md resyncs", v.ResyncMd.ToString("N0", Inv), v.ResyncMd > 0 ? Pal.Warn : Pal.TextDim);
        _statDiag.Set(7, "divergences", v.Divergences.ToString("N0", Inv), v.Divergences > 0 ? Pal.Warn : Pal.TextDim);
        _statDiag.Set(8, "exec sent", h.Exec.Sent.ToString("N0", Inv), Pal.TextDim);
        _statDiag.Set(9, "exec dropped", h.Exec.Dropped.ToString("N0", Inv), h.Exec.Dropped > 0 ? Pal.DownLit : Pal.TextDim);
        _cardDiag.Caption = $"{Fmt.Count(v.Frames)} frames · {v.Cmds} cmds";

        // ---- status pills
        _pillWarm.Set(v.Warm ? StateTone.Good : StateTone.Warm, $"{Math.Min(v.Samples, cfg.WarmupSamples)}/{cfg.WarmupSamples}");
        _pillPos.Set(Math.Abs(v.Pos8) < cfg.MaxPos8 * 0.8 ? StateTone.Good : Math.Abs(v.Pos8) >= cfg.MaxPos8 ? StateTone.Bad : StateTone.Warm, (Math.Abs(v.Pos8) / (double)cfg.MaxPos8 * 100).ToString("F0", Inv) + "%", Math.Abs(v.Pos8) >= cfg.MaxPos8);
        _pillLag.Set(v.LagNs <= cfg.MaxLagNs ? StateTone.Good : StateTone.Bad, lagMs.ToString("F1", Inv) + "ms", v.LagNs > cfg.MaxLagNs);
        _pillBook.Set(v.BestBid8 > 0 && v.BestAsk8 > v.BestBid8 ? StateTone.Good : StateTone.Bad, spread > 0 ? (spread / (double)tick).ToString("F0", Inv) + "t" : "-");
        _pillHealth.Set(v.IsHealthy && v.Quoting ? StateTone.Good : v.Breaker ? StateTone.Bad : StateTone.Warm, v.Breaker ? "BRK" : v.Quoting ? "ok" : "-");
        _pillKill.Set(v.Killed || v.KillSent ? StateTone.Bad : StateTone.Good, v.Killed ? "KILLED" : v.KillSent ? "SENT" : "armed");
        _pillMd.Set(SocketTone(h.Md));
        _pillOrd.Set(h.AuthRejected ? StateTone.Bad : SocketTone(h.Orders));

        // ---- charts
        _flow.Tick8 = tick;
        _flow.MaxLagMs = cfg.MaxLagNs / 1e6;
        _flow.Push(v.BestBid8, v.BestAsk8, v.BidQuote8, v.AskQuote8, v.IntendedBid8, v.IntendedAsk8, mark, v.LagNs / 1e6);
        _candles.Push(v.RecvNs, mark > 0 ? mark : (v.BestBid8 + v.BestAsk8) / 2 / 1e8, dec);

        // ---- events into the tape
        if (v.Fills != _lastFills)
        {
            long n = v.Fills - _lastFills;
            _lastFills = v.Fills;
            _tape.Add(TapeKind.Fill, v.LastFillSell, v.LastFillSell ? "SELL" : "BUY", Fmt.Price(v.LastFillPx8, dec), n > 1 ? "x" + n : Fmt.Qty(cfg.QuoteQty8, h.QtyDecimals), $"position {posF.ToString("F" + _qf, Inv)}");
            _flow.MarkFill(v.LastFillSell, v.LastFillPx8);
            AppendLogLine($"fill {(v.LastFillSell ? "sell" : "buy")} {Fmt.Price(v.LastFillPx8, dec)} x{n} → pos {posF.ToString("F" + _qf, Inv)}");
        }
        if (v.Trips != _lastTrips)
        {
            _lastTrips = v.Trips;
            _tape.Add(TapeKind.Alert, false, "TRIP", "", "", "breaker tripped — lag " + Fmt.Lag(v.LagNs));
            ShowAlert("Breaker tripped", "lag " + Fmt.Lag(v.LagNs) + " — orders pulled", StateTone.Bad);
        }
        if (v.ResyncMd != _lastResync)
        {
            _lastResync = v.ResyncMd;
            _tape.Add(TapeKind.Alert, false, "SYNC", "", "", "market data resync #" + v.ResyncMd);
        }
        if (v.Divergences != _lastDiv)
        {
            _lastDiv = v.Divergences;
            _tape.Add(TapeKind.Alert, false, "DIVR", "", "", "position divergence — balance moved outside fills");
        }
        if (v.Fails > _lastFails + 3)
        {
            _tape.Add(TapeKind.Alert, false, "PARSE", "", "", $"parse faults {v.Fails}");
        }
        _lastFails = v.Fails;

        // ---- footer
        double up = _startedTs == 0 ? 0 : (ts - _startedTs) / (double)Stopwatch.Frequency;
        _lblRight.Text = $"{Fmt.Clock(DateTime.Now)} · up {Fmt.Duration(up)} · {_fps:F1} fr/s · md {SockState(h.Md).ToLowerInvariant()} · orders {(h.AuthRejected ? "rejected" : SockState(h.Orders).ToLowerInvariant())}";
    }

    // ------------------------------------------------------------------ commands
    void Snapshot()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"gemx snapshot {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"state      {_stateText}");
        sb.AppendLine($"symbol     {_settings.Symbol}  price decimals {_pf}  qty decimals {_qf}");
        if (_host is { } h && h.Engine.TryReadView(out EngineView v))
        {
            EngineConfig c = h.Config;
            sb.AppendLine($"touch      bid {P(v.BestBid8)} x {Fmt.Qty(v.BestBidQty8 / 1e8, 4)}   ask {P(v.BestAsk8)} x {Fmt.Qty(v.BestAskQty8 / 1e8, 4)}");
            sb.AppendLine($"micro/mid  {v.Micro:F2} / {v.Mid:F2}");
            sb.AppendLine($"quotes     bid {P(v.BidQuote8)} [{v.BidSlot}]   ask {P(v.AskQuote8)} [{v.AskSlot}]");
            sb.AppendLine($"intent     bid {P(v.IntendedBid8)}   ask {P(v.IntendedAsk8)}   blocks {v.BlockFlags}");
            sb.AppendLine($"position   {Fmt.Qty(v.Pos8 / 1e8, _qDec)}  base {Fmt.Qty(v.Base8 / 1e8, _qDec)}  cash {v.CashUsd:F2}");
            sb.AppendLine($"signals    sigma {Math.Sqrt(Math.Max(0, v.Sigma2)):G4}  ofi {v.OfiNorm:F3}  lag {Fmt.Lag(v.LagNs)}  samples {v.Samples}/{c.WarmupSamples}");
            sb.AppendLine($"counters   frames {v.Frames}  faults {v.Fails}  cmds {v.Cmds}  fills {v.Fills}  trips {v.Trips}  resyncs {v.ResyncMd}  divergences {v.Divergences}");
            sb.AppendLine($"sockets    md {SockState(h.Md)}  orders {(h.AuthRejected ? "auth rejected" : SockState(h.Orders))}  sent {h.Exec.Sent}  dropped {h.Exec.Dropped}");
        }
        try { Clipboard.SetText(sb.ToString()); AppendLogLine("snapshot copied to clipboard"); }
        catch { AppendLogLine("snapshot failed: clipboard unavailable"); }
    }

    async void OnStart(object? sender, EventArgs e)
    {
        if (_ui != UiState.Idle) return;
        if (_drawer.Visible) ToggleSettings();   // the rail carries the live session telemetry
        SetUi(UiState.Starting);
        AppendLogLine("start requested");
        try
        {
            _settings.Save();
            GemxHost h = await GemxHost.StartAsync(_settings);
            _host = h;
            _pf = "F" + h.PriceDecimals;
            _qf = "F" + h.QtyDecimals;
            _pDec = h.PriceDecimals;
            _qDec = h.QtyDecimals;
            _depthStream = _settings.MdSubs.Contains("depth", StringComparison.OrdinalIgnoreCase);
            _logo.Symbol = _settings.Symbol.Trim().ToUpperInvariant();
            _base = _settings.BaseAsset.Trim().ToUpperInvariant();
            _tilePos.Unit = _base;
            _flow.Reset(h.PriceDecimals);
            _flow.SampleSeconds = _timer.Interval / 1000.0;
            _flow.Tick8 = h.Config.Q.Tick8;
            _fps = 0;
            _rateFrames = 0;
            _rateTs = Stopwatch.GetTimestamp();
            _startedTs = _rateTs;
            _lastFills = _lastTrips = _lastResync = _lastDiv = _lastFails = 0;
            _stateText = "";
            _pnlKnown = false;
            _tape.Clear();
            _tape.Add(TapeKind.Boot, false, "BOOT", "", "", $"session started on {_settings.Symbol} @ {_settings.RestHost}");
            HideAlert();
            SetUi(UiState.Running);
            _ = _candles.LoadHistoryAsync(_settings.Symbol, h.PriceDecimals);
        }
        catch (Exception ex)
        {
            AppendLogLine("start failed: " + ex.Message);
            ShowAlert("Start failed", ex.Message, StateTone.Bad);
            _host = null;
            SetUi(UiState.Idle);
        }
    }

    async void OnStop(object? sender, EventArgs e) => await StopAsync();

    async Task StopAsync()
    {
        if (_host is not { } h || _ui != UiState.Running) return;
        _tape.Add(TapeKind.Order, false, "STOP", "", "", "session stop requested");
        SetUi(UiState.Stopping);
        try { await h.StopAsync(); }
        catch (Exception ex) { AppendLogLine("stop failed: " + ex.Message); }
        DrainLog();
        _host = null;
        SetUi(UiState.Idle);
        _tape.Add(TapeKind.Order, false, "STOP", "", "", "session stopped");
        _lblRight.Text = "idle";
    }

    void OnKill(object? sender, EventArgs e)
    {
        if (_host is not { } h) return;
        h.Engine.Kill = true;
        _btnKill.Enabled = false;
        _tape.Add(TapeKind.Kill, false, "KILL", "", "", "kill switch — flushing all orders");
        ShowAlert("Kill switch engaged", "session cancel requested; waiting for the ack", StateTone.Bad);
        AppendLogLine("kill requested");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5: OnStart(this, EventArgs.Empty); return true;
            case Keys.F6: _ = StopAsync(); return true;
            case Keys.Escape when _ui == UiState.Running: _ = StopAsync(); return true;
            case Keys.Control | Keys.K: OnKill(this, EventArgs.Empty); return true;
            case Keys.Control | Keys.L: _log.Clear(); return true;
            case Keys.Control | Keys.Oemcomma: ToggleSettings(); return true;
            case Keys.Control | Keys.S: _settings.Save(); AppendLogLine("settings saved"); return true;
            case Keys.Control | Keys.D: _cardDiag.SetExpanded(!_cardDiag.Expanded); return true;
            case Keys.Control | Keys.J: ToggleLog(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_ui == UiState.Starting || _ui == UiState.Stopping) { e.Cancel = true; return; }
        if (_ui == UiState.Running && !_closing)
        {
            e.Cancel = true;
            _closing = true;
            _ = CloseAfterStop();
            return;
        }
        try { _settings.Save(); } catch (IOException ex) { AppendLogLine("settings not saved: " + ex.Message); }
        base.OnFormClosing(e);
    }

    async Task CloseAfterStop()
    {
        await StopAsync();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}

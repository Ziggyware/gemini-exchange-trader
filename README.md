# Gemx — Gemini maker terminal

A market-making client for Gemini: a lock-free quote engine (`Gemx`), a Windows
desktop terminal that streams a live view of it (`Gemx.App`), and a headless
assertion harness (`Gemx.Check`).

```
Gemx/        quote engine: book, engine, wire codec, rings, signals, transport
Gemx.App/    WinForms terminal (net8.0-windows, PerMonitorV2 DPI)
Gemx.Check/  headless harness — asserts engine/signal/wire behaviour
docs/        design previews
```

## Build & run

```powershell
dotnet build Gemx.App                       # terminal
dotnet run   --project Gemx.App
dotnet run   --project Gemx.Check           # engine harness (no UI)
```

There is no network egress requirement; the harness runs offline. The terminal
connects to Gemini REST (`api.gemini.com`) for candle history and to the
WebSocket endpoints configured in the settings drawer.

---

## The terminal

The dashboard is built from three columns under a single title bar. Everything is
custom-drawn — there is no stock `DataGridView`, no default-looking `PropertyGrid`
in the main view, and no chrome that only exists to decorate.

```
┌─ title ── logo · symbol chip · state chip ──────── start/stop/kill/settings ─┐
│ alert row (collapses to 0 when idle)                                        │
├──────────────┬────────────────────────────────────┬────────────────────────┤
│  left rail   │  centre                            │  right dock            │
│  Position    │  Flow | 1m | 5m | 15m | 30m | 1h…  │  Depth (ladder)        │
│  Performance │  ┌──────────────────────────────┐  │                        │
│  Live quotes │  │  quote-flow chart            │  │  Activity (tape)       │
│  Signals     │  │  or candle strip             │  │                        │
│  Diagnostics │  └──────────────────────────────┘  │                        │
│              │  log strip (collapses to 26 px)    │                        │
├──────────────┴────────────────────────────────────┴────────────────────────┤
│ status bar: warm · pos · lag · book · health · kill · md · orders · clock    │
└─────────────────────────────────────────────────────────────────────────────┘
```

The settings drawer replaces the left rail (it does not float over the charts):
`PropertyGrid` on `AppSettings`, with Save (`Ctrl+S`), Defaults and Close. Starting
a session closes the drawer automatically so the rail is visible while quoting.

### De-cluttering decisions

The previous layout showed a `PropertyGrid`, a 19-row metrics table, six status
pills, the ladder, the tape and the log **at the same time**, so no single number
had any visual weight. The rework:

- **Pills moved into the cards they describe.** The status bar now carries a
  compact `WARM · POS · LAG · BOOK · HEALTH · KILL · MD · ORDERS` strip; the
  verbose statuses (spread/edge/fee) sit inside *Live quotes*, where they belong.
- **Position & P&L use one big number + one gauge**, not a table of seven rows.
- **Diagnostics is collapsed** (`Ctrl+D`) — counters are available, not shouted.
- **The log strip collapses to its button bar** (`Ctrl+J`), giving ~130 px back to
  the chart.
- **The alert banner collapses to zero height** when there is nothing to say, and
  auto-expires (≈60 s for `Bad`, ≈9 s otherwise).
- Wording is shortened to the information-dense part: `Δ 3t` rather than
  "distance to touch: 3 ticks", `2t` rather than "requote threshold: 2 ticks".

### Interaction

| Where | Input | Effect |
|---|---|---|
| Flow chart | wheel | zoom the sample window (60–600) |
| Flow chart | drag | pan back through history |
| Flow chart | double-click | `ResetView()` — keep history, reset zoom/pan |
| Flow chart | hover | per-sample readout card (bid/ask/spread/our quotes/intent/micro/lag) |
| Candles | wheel | visible bars (12–300) |
| Candles | ← → | scroll · `Home`/`End` jump |
| Candles | `+` `-` | zoom · `L` toggles log price scale |
| Candles | hover | crosshair + OHLCV badge |
| Depth | wheel | 2–12 levels · click copies the price |
| Tape | wheel | scroll · click copies the row |
| Log | drag | selects text; buttons mirror Copy/Clear/Follow |
| Global | `F5` / `F6` / `Esc` | start / stop |
| Global | `Ctrl+K` | kill switch (flatten + halt) |
| Global | `Ctrl+L` / `Ctrl+J` / `Ctrl+,` / `Ctrl+S` / `Ctrl+D` | clear log / toggle log / settings / save / diagnostics |

Small details worth noticing: the state chip breathes while quoting, the charts
draw pulsing heads on the newest sample, stale quotes desaturate rather than
disappear, and value tiles flash in the direction of the change.

---

## Rendering architecture

All rendering lives in `Gemx.App/Ui`. The design tokens are in `Theme.cs` and the
drawing helpers are shared, so every control answers the same questions the same
way (hairlines at the DPI-correct width, gradients that never band, text that
never lands on a half pixel).

| File | Responsibility |
|---|---|
| `Ui/Theme.cs` | `Pal` palette (+`Alpha`/`Mix`/`StateColor`), `Fonts` (Segoe UI + Consolas), `Fmt` (invariant formatting), `Cache` (pens/brushes/gradient pools, bounded), `Grad` (gradient fills + eviction that disposes), `Gfx` (hairlines, rounded rects, glow strokes, dots, drop shadows, tracking, measuring), `Glyph`/`Icons`, `Animator` (one shared 33 ms timer), `UiControl` (DPI-aware `Surface`/`S()`/`Rect()`, `Animate()` hook) |
| `Ui/Widgets.cs` | `Spark`, `LogoMark`, `StateChip`, `StatusPill`, `ToolButton`, `Segments`, `AlertBanner`, `MetricTile`, `StatGrid`, `MeterBar`, `InventoryGauge`, `QuoteRow`, `Card`, `CardStack` |
| `Ui/Ladder.cs` | `DepthLadder` — 2–12 levels, synced to the book, own-quote highlighting |
| `Ui/Tape.cs` | `TapeView` — 400-entry ring, per-kind accent rails, hover/scroll/copy |
| `Ui/LogView.cs` | severity-coloured log with ring trim and follow/selection handling |
| `QuoteChart.cs` | spread band, best bid/ask, our quotes (glow + step), intents, micro price, pulsing heads, fill triangles, lag pane, legend, hover card |
| `CandleChart.cs` | `Aggregator` + `CandleStrip` (8 timeframes) and `CandleSet` (tabs + history); EMA20, volume pane, log scale, crosshair, badge |

Design rules that keep it looking deliberate:

1. **Panel count is fixed, content varies.** Cards do not appear and disappear;
   they collapse, so the grid never reflows while you are reading it.
2. **One accent per meaning.** Green/red are only ever price direction or
   position sign; blue is "ours"/accent; violet is the micro price; amber is lag
   and warnings. Nothing is coloured for decoration.
3. **Motion is bounded.** `Animator` drives only what carries information —
   state breathing, value flashes, live heads, hover fades. No idling marquees.
4. **Density where it counts.** Mono for anything numeric or aligned in a column,
   UI font for prose; tabular alignment over italic flourish.
5. **Every element has a resting and an active state** (hover, stale, blocked,
   warning, at-limit) rather than a single static rendering.

### Design preview

`docs/ui-preview.html` is a self-contained, animated HTML mock of the terminal —
open it in a browser to see the intended layout, palette and per-element detail
without building the Windows app. The numbers are simulated; the geometry, type
scale and colour rules match `Ui/Theme.cs`.

# Requirements — Quant Backtesting Platform

Status: **Draft for discussion** · Last updated: 2026-09-25
Related: [design-decisions.md](design-decisions.md) (data pipeline, OLTP/OLAP)

## 1. Purpose and scope

Fantasy Stock is becoming a **quant backtesting application**. Users define
trading strategies, either in the web UI or in Python, and run them against
historical market data. They then judge each strategy on performance KPIs
and risk metrics. A **learning portal** explains the strategies, the KPIs
and risk management, so beginners can learn by experimenting without
risking money.

The product has two tiers:

- **Free tier.** What we build first, covered by this document. Users get
  the whole template library, **one custom strategy built in the UI** and
  **one custom strategy written in Python**. Everything runs on a fixed
  historical period, and users get the introductory learning content.
- **Premium tier.** Adds unlimited custom strategies, testing against
  different historical periods, and the full learning portal. Premium
  requirements are listed here so the free tier is built with the right
  boundaries, but they are not in the first release.

### Decisions and assumptions

- D1. The free tier is a limited version of the same product, not a
  separate one. Free users may **create** strategies, capped at **1 UI
  (visual builder) strategy + 1 Python strategy**. Premium removes the
  cap. *(Decided 2026-09-25.)*
- D2. **No leagues.** The product is organized around individual users
  only. Every strategy, backtest and result belongs to one user. The
  league / invite-code concept from the original pitch is dropped.
  *(Decided 2026-09-25.)*
- D3. **Paper trading stays.** The existing live replay / paper-trading
  path (tick-engine → Kafka → backend, POC 1–3) is kept and becomes the
  base for a later **forward-test** feature (FR-BT-13). It is not part of
  the backtesting MVP scope. *(Decided 2026-09-25.)*
- A1. Nothing in the product is financial advice, and no real orders are
  ever placed.

### Out of scope (all tiers, first release)

- Live or real-money trading and broker integrations
- Options, futures and other derivatives; short selling with borrow costs
- Intraday / tick-level backtests (daily bars only)
- Mobile apps
- Leagues, leaderboards and social features (sharing strategies publicly,
  copy-trading)

## 2. Glossary

| Term | Meaning |
|---|---|
| Strategy | Rules that turn market data into target positions or orders. |
| Template | A pre-built strategy shipped by us, e.g. SMA crossover. Has parameters but no editable logic. |
| Parameter | A tunable input of a strategy, e.g. fast MA = 20. |
| Backtest | One simulation of one strategy + parameters + universe + period + risk settings. |
| Universe | The set of instruments a strategy may trade. |
| Period | The historical date range a backtest runs over. |
| Benchmark | A reference series results are compared with, e.g. SPY buy-and-hold. |
| KPI | A performance or risk metric computed from a backtest (§4.5). |

## 3. Users and personas

| Persona | Goal | Typical tier |
|---|---|---|
| **Curious beginner** | Understand what a "strategy" is; see why one works or fails | Free |
| **Student / learner** | Work through the learning portal and try each concept hands-on | Free → Premium |
| **Hobby quant** | Code their own ideas in Python and test them across market regimes | Free (one strategy) → Premium |
| **Educator** (later) | Use the platform to teach a class | Premium / Team |

## 4. Functional requirements

Priority uses **MoSCoW** (Must / Should / Could / Won't for now). The
**Tier** column says who gets the feature: **F** = free (and therefore
premium too), **P** = premium only.

### 4.1 Accounts and entitlements

| ID | Requirement | Tier | Prio |
|---|---|---|---|
| FR-ACC-01 | Users can register, log in and log out (builds on the existing JWT auth). | F | Must |
| FR-ACC-02 | Every account has a plan (`free`, `premium`). The backend checks the plan on every gated action, not only in the UI. | F | Must |
| FR-ACC-03 | When a free user tries a premium action, they see a clear upgrade prompt, not an error. | F | Must |
| FR-ACC-04 | Usage limits are configurable per plan without a deploy, e.g. backtests per day and saved backtests. | F | Should |
| FR-ACC-05 | Payment / subscription management (e.g. Stripe). | P | Could (post-MVP) |
| FR-ACC-06 | Users can delete their account and export their data (GDPR). | F | Must |

### 4.2 Market data

| ID | Requirement | Tier | Prio |
|---|---|---|---|
| FR-DAT-01 | Daily OHLCV bars for a curated universe: the current 10 US large caps, growing to about 50. | F | Must |
| FR-DAT-02 | Prices are adjusted for splits and dividends, and the UI shows which adjustment is used. | F | Must |
| FR-DAT-03 | Free tier backtests run on **one fixed default period**, e.g. the last 5 years. | F | Must |
| FR-DAT-04 | Free-choice date ranges plus **predefined market regimes**, e.g. 2008 financial crisis, 2020 COVID crash, 2022 rate hikes, 2010s bull market. | P | Must (premium) |
| FR-DAT-05 | Wider universes: more equities, ETFs, crypto. | P | Should |
| FR-DAT-06 | Users can browse an instrument's price chart for the available history. | F | Should |
| FR-DAT-07 | Benchmark series (e.g. SPY) are available to compare backtests against. | F | Must |

### 4.3 Strategy definition

| ID | Requirement | Tier | Prio |
|---|---|---|---|
| FR-STR-01 | **Template library** of at least 5 documented strategies: buy & hold, SMA crossover, RSI mean reversion, momentum (top-N by trailing return), fixed-weight rebalancing (e.g. 60/40). | F | Must |
| FR-STR-02 | Users can change a template's parameters in the UI. Parameters have defined ranges, and invalid values are rejected with a clear explanation. | F | Must |
| FR-STR-03 | Users can pick the universe from the instruments available to their plan. | F | Must |
| FR-STR-04 | Users can save a configured template ("my SMA 20/50 on AAPL") and run it again later. | F | Should |
| FR-STR-05 | **Visual strategy builder**: users compose entry/exit rules from indicators, comparisons and logical operators, with no code. | F (1 strategy) | Must |
| FR-STR-06 | **Custom Python strategies**: users write a strategy class against the documented SDK interface (§4.8), in the browser editor or locally. | F (1 strategy) | Must |
| FR-STR-07 | Strategies are versioned. Every backtest records the exact strategy version and parameters it ran with. | F | Must |
| FR-STR-08 | Users can clone a template into an editable custom strategy. The clone uses the matching slot (UI or Python). | F | Should |
| FR-STR-09 | **Custom strategy quota.** Free: at most **1 UI strategy and 1 Python strategy** at a time. Editing a strategy creates a new version and doesn't use another slot. Deleting a strategy frees its slot. Premium: unlimited (fair use). The server enforces the quota. | F | Must |
| FR-STR-10 | If a premium user downgrades to free, they keep their strategies read-only. They choose which one UI and one Python strategy stay editable and runnable. Nothing is deleted. | P | Should |

### 4.4 Backtest engine

| ID | Requirement | Tier | Prio |
|---|---|---|---|
| FR-BT-01 | Runs a strategy over daily bars in time order and produces orders, fills, positions, cash and equity for each day. | F | Must |
| FR-BT-02 | **No look-ahead.** A decision made on day *t* can only use data up to the close of *t*, and fills at the open of *t+1* by default. The engine enforces this; it is not left to strategy authors. | F | Must |
| FR-BT-03 | Configurable starting capital. The default is $100,000, matching the existing app. | F | Must |
| FR-BT-04 | Transaction costs: commission per trade and/or in bps, plus slippage in bps. Sensible defaults are always on. | F | Must |
| FR-BT-05 | Long-only, cash account. No leverage and no shorting. | F | Must |
| FR-BT-06 | Shorting and leverage, with margin/borrow cost. | P | Could |
| FR-BT-07 | **Deterministic.** The same strategy version, parameters, data version and config always give identical results. | F | Must |
| FR-BT-08 | Backtests run asynchronously. The UI shows queued → running (with progress) → done or failed. | F | Must |
| FR-BT-09 | **Parameter sweeps**: run one strategy over a grid of parameters and compare them. | P | Should |
| FR-BT-10 | **Walk-forward / out-of-sample testing**: optimize on one period, validate on the next, report both. | P | Could |
| FR-BT-11 | **Compare periods**: run the same strategy on several regimes and show the results side by side. | P | Must (premium) |
| FR-BT-12 | Failed backtests show a readable error, including the Python traceback for custom code. | F | Must |
| FR-BT-13 | **Forward test**: run a saved strategy version against the live price feed (paper trading, D3). It places simulated orders as new ticks arrive, and shows live results next to the strategy's backtest KPIs. | P | Could (post-R4) |

### 4.5 KPIs

Every finished backtest computes the following, each against the benchmark
too:

| Group | KPIs | Tier |
|---|---|---|
| Return | Total return, CAGR, annual returns table, monthly returns heatmap | F |
| Risk | Annualized volatility, **max drawdown**, drawdown duration, worst day/month | F |
| Risk-adjusted | **Sharpe**, **Sortino**, Calmar | F |
| Trading | # trades, win rate, avg win / avg loss, profit factor, exposure (% time invested), turnover | F |
| Benchmark-relative | Alpha, beta, correlation, tracking error, information ratio | P |
| Tail risk | VaR (95/99), CVaR / expected shortfall, skew, kurtosis | P |
| Rolling | Rolling Sharpe, rolling volatility, rolling beta | P |

| ID | Requirement | Tier | Prio |
|---|---|---|---|
| FR-KPI-01 | Every KPI shows a short definition and links to its learning portal article. | F | Must |
| FR-KPI-02 | The formulas and conventions (risk-free rate, 252 trading days, arithmetic vs log returns) are documented and tested against a reference implementation. | F | Must |
| FR-KPI-03 | Short or low-trade backtests are flagged, e.g. "fewer than 30 trades — statistics are unreliable". | F | Should |

### 4.6 Risk management

Risk rules sit on top of any strategy and are enforced by the engine, not
by the strategy code.

| ID | Requirement | Tier | Prio |
|---|---|---|---|
| FR-RSK-01 | **Position sizing**: fixed amount, or fixed % of equity. | F | Must |
| FR-RSK-02 | **Stop-loss** and **take-profit** (% from entry). | F | Must |
| FR-RSK-03 | **Max position size** as % of equity, and max number of open positions. | F | Must |
| FR-RSK-04 | Trailing stop. | P | Should |
| FR-RSK-05 | Volatility-targeted sizing (e.g. ATR- or stdev-based). | P | Should |
| FR-RSK-06 | Portfolio-level **max-drawdown circuit breaker**: go flat, and optionally pause for N days. | P | Should |
| FR-RSK-07 | Results show the effect of risk rules: how many exits were triggered by each rule. | F | Should |
| FR-RSK-08 | "With vs without risk rules" comparison of the same backtest. | P | Could |

### 4.7 Results and visualization

| ID | Requirement | Tier | Prio |
|---|---|---|---|
| FR-VIS-01 | Equity curve vs benchmark, with a log-scale toggle. | F | Must |
| FR-VIS-02 | Underwater (drawdown) chart. | F | Must |
| FR-VIS-03 | Price chart with buy/sell markers per instrument. | F | Must |
| FR-VIS-04 | KPI summary panel (§4.5) and a trade list (entry, exit, P/L, holding period, exit reason). | F | Must |
| FR-VIS-05 | Backtest history: a list of past runs with key KPIs, sortable. The free tier keeps the last N runs. | F | Should |
| FR-VIS-06 | Compare up to 4 backtests side by side, with overlaid equity curves and a KPI table. | P | Should |
| FR-VIS-07 | Export results as CSV (trades, daily equity) and a PDF/HTML report. | P | Could |

### 4.8 Python access

| ID | Requirement | Tier | Prio |
|---|---|---|---|
| FR-PY-01 | A pip-installable **Python SDK** that authenticates with an API token. | F | Should |
| FR-PY-02 | The SDK can list templates and instruments, start a backtest of a template with parameters, poll its status, and fetch results as pandas DataFrames. | F | Should |
| FR-PY-03 | The SDK defines a `Strategy` base class, e.g. `on_bar(ctx, bars) -> orders/target_weights`, which custom strategies implement. | F | Must |
| FR-PY-04 | Users can run a custom strategy **locally** against a small sample dataset for fast iteration, then **submit** it to run on the platform against full data. Submitting uses the Python quota (FR-STR-09). | F | Should |
| FR-PY-05 | A browser-based code editor (syntax highlighting, SDK autocomplete) for custom strategies. | F | Should |
| FR-PY-06 | Jupyter notebook examples for every template. | F | Could |
| FR-PY-07 | Every UI action has a public REST API with the same entitlement checks. The UI and the SDK use the same API. | F | Must |

### 4.9 Learning portal

| ID | Requirement | Tier | Prio |
|---|---|---|---|
| FR-LRN-01 | Content is organized into three tracks: **Strategies**, **KPIs**, **Risk Management**. | F | Must |
| FR-LRN-02 | The free tier includes an **introductory module per track**, e.g. "What is a backtest?", "Sharpe & drawdown explained", "Why position sizing matters". | F | Must |
| FR-LRN-03 | Every template strategy has an article: the idea, rules, parameters, when it works or fails, and typical pitfalls. | F | Must |
| FR-LRN-04 | **"Try it" links**: an article opens a pre-filled backtest so the concept can be explored hands-on. | F | Must |
| FR-LRN-05 | Advanced modules: overfitting and data snooping, walk-forward, regime analysis, portfolio construction, tail risk. | P | Must (premium) |
| FR-LRN-06 | Progress tracking: completed articles and quizzes per track. | P | Should |
| FR-LRN-07 | Short quizzes at the end of modules. | P | Could |
| FR-LRN-08 | Content is authored in Markdown in the repo (or a headless CMS) and rendered by the frontend. Publishing it doesn't need a code deploy. | F | Should |
| FR-LRN-09 | A "Common backtesting biases" article (look-ahead, survivorship, overfitting, ignoring costs) is linked from every results page. | F | Must |

### 4.10 Free vs premium: summary

| Capability | Free | Premium |
|---|---|---|
| Template strategies + parameters | ✅ | ✅ |
| Visual strategy builder | 1 strategy | Unlimited |
| Custom Python strategies | 1 strategy | Unlimited |
| Python SDK (templates, custom strategies, results) | ✅ | ✅ |
| Historical period | Fixed default period | Any range + predefined regimes |
| Multi-period comparison, parameter sweeps, walk-forward | — | ✅ |
| Universe | Curated (~10–50 US large caps) | Extended (ETFs, crypto, more equities) |
| Core KPIs + basic risk rules | ✅ | ✅ |
| Advanced KPIs + advanced risk rules | — | ✅ |
| Backtest history | Last N runs | Unlimited (fair use) |
| Compare / export | — | ✅ |
| Learning portal | Intro module per track + template articles | Full portal, progress, quizzes |
| Backtests per day | Low limit (e.g. 20) | High limit (e.g. 500) |

## 5. Non-functional requirements

| ID | Area | Requirement |
|---|---|---|
| NFR-01 | Performance | A template backtest (10 instruments, 5 years daily) finishes in **< 5 s** at p95 on a warm system. |
| NFR-02 | Performance | UI pages render in < 1 s at p95. Chart data for a finished backtest loads in < 500 ms. |
| NFR-03 | Correctness | The engine has a regression suite: known strategies on fixed data must reproduce known equity curves and KPIs exactly. |
| NFR-04 | Reproducibility | Every backtest stores its strategy version, parameters, data snapshot version, engine version and config, and can be re-run bit-for-bit. |
| NFR-05 | **Security** | User Python code runs in an **isolated sandbox**: separate container/microVM, no network, read-only filesystem, CPU/memory/time limits, no access to other users' data or secrets. Never in the API process. Free users can run Python too (FR-STR-06), so anyone who signs up can run code on our servers. The sandbox must be in place before free Python strategies go live. |
| NFR-06 | Security | Entitlements are enforced server-side. JWT auth as today; API tokens for the SDK can be revoked. |
| NFR-07 | Fairness | Per-user rate limits and a job queue with per-plan concurrency, so one user can't starve others. |
| NFR-08 | Scalability | Backtest workers scale horizontally and independently of the API. |
| NFR-09 | Availability | 99% monthly for the free tier (best effort); target 99.5% once premium is paid. |
| NFR-10 | Privacy | GDPR: data minimization, EU hosting, account deletion within 30 days, privacy policy. User strategies are private by default. |
| NFR-11 | Legal | A disclaimer on every results page and at signup: educational use only, not investment advice, past performance does not predict future results. |
| NFR-12 | **Data licensing** | Market data must be licensed for **commercial display**. yfinance / Yahoo data is **not** licensed for this. It is fine for development, but must be replaced by a licensed provider before premium launch. |
| NFR-13 | Observability | Metrics for queue depth, backtest duration, failure rate and sandbox resource kills. Logs are correlated by backtest id. |
| NFR-14 | Accessibility | WCAG 2.1 AA for core flows. Charts have tabular alternatives. |

## 6. Key user flows (free tier)

1. **Learn → try.** A user reads "SMA crossover" in the learning portal →
   clicks *Try it* → the backtest form opens pre-filled → they change
   20/50 to 10/30 → run → view the results and KPIs, each linked back to
   its article.
2. **Tweak and compare.** A user runs RSI mean reversion twice with
   different stop-losses → both appear in the backtest history → (premium
   upsell) "Compare side by side".
3. **Python.** A user runs `pip install` for the SDK → sets an API token →
   `client.run_template("sma_crossover", params={...})` → gets back a
   `DataFrame` of daily equity and a KPI dict.
4. **First custom strategy.** A user clones "RSI mean reversion" into the
   visual builder → adds a volume filter → saves it, which uses the UI
   slot → runs it. Trying to create a second UI strategy shows an upgrade
   prompt that offers to replace the existing one instead.
5. **Python strategy.** A user subclasses `Strategy` locally → iterates
   against sample data → `client.submit(MyStrategy)` uses the Python slot
   → the platform runs it in the sandbox on the default period.

## 7. Impact on architecture

This extends the pipeline in [design-decisions.md](design-decisions.md).
It doesn't replace it.

- **Backtests read historical bars from OLAP.** ClickHouse `market_bars`
  (DD-01, DD-02) becomes the main data source for the product, not just
  for replay. Each backtest records a data snapshot version (NFR-04).
- **OLTP gains new tables:** `plans`/`subscriptions`, `strategies` +
  `strategy_versions`, `backtests` (config, status, engine version),
  `api_tokens`, `learning_progress`.
- **Backtest results:** summary KPIs go in OLTP (for listing and sorting).
  Daily equity and trades per backtest go in OLAP.
- **Job execution:** the API puts a job on `backtests.requested.v1` (Kafka)
  or a dedicated queue. **Backtest workers** in Python (pandas/numpy; one
  engine shared by the SDK and the platform) consume jobs, run templates
  in-process and custom code in a sandbox, and publish
  `backtests.completed.v1`.
- **One engine, two entry points.** The backtest engine is a Python
  package. The same code runs on the workers and in the SDK's local mode
  (FR-PY-04), so local and platform results are identical.
- **No leagues (D2).** The OLTP model is user-centric: cash, holdings,
  strategies and backtests belong directly to `users` (see DD-08).
- **The existing tick-engine / Kafka / live-price path** (paper trading,
  D3) stays as it is. It is the base for forward testing (FR-BT-13): a
  forward-test runner consumes `market.ticks.v1`, calls the same engine's
  `on_bar` per tick batch, and records fills in the per-user `trades`
  table.

A new ADR should record the engine design (event-driven vs vectorized),
the sandbox technology (gVisor, Firecracker, or plain containers with
seccomp) and the job queue choice.

## 8. Release plan

| Release | Contents |
|---|---|
| **R1 — Free tier MVP** | Plans + entitlement checks, curated data on a fixed period, 5 templates with parameters, backtest engine (FR-BT-01–05, 07, 08, 12), core KPIs, basic risk rules, results page, intro learning modules + template articles + *Try it*, disclaimer. |
| **R2 — Free tier complete** | Strategy quota (FR-STR-09), visual builder (1 strategy), Python SDK + sandboxed custom Python strategy (1 strategy), saved configurations, backtest history, API tokens, rate limits. |
| **R3 — Premium beta** | Unlimited custom strategies, any period + regimes, multi-period comparison, advanced KPIs/risk rules, full learning portal. |
| **R4 — Premium launch** | Licensed data provider, payments, downgrade handling (FR-STR-10), parameter sweeps, export. |

## 9. Open questions

1. **Product name.** "Fantasy Stock" no longer fits. Shortlist (trademark,
   domain and PyPI availability not yet checked):
   - **Quantling** *(recommended)*: "a young quant". It says learning and
     quant in one word, reads well as an SDK (`pip install quantling`,
     `from quantling import Strategy`), and has room for a premium
     "Quantling Pro".
   - **Hindsight**: memorable, and backtesting is literally testing in
     hindsight. But it can read as "results are only hindsight", which
     undercuts trust in the numbers.
   - **Backtest Lab** / **StratLab**: descriptive and clear, but generic
     and harder to own as a brand.
   - **Edgefinder**: about finding a trading edge, but promises results
     we can't guarantee (see NFR-11).
2. **Forward-test tier (FR-BT-13).** Premium only, as drafted, or one
   forward test for free users as another hook?
3. **Asset classes.** Are equities only enough for R1? The original pitch
   mentioned crypto, and crypto data is cheaper to license.
4. **Data provider** for commercial use (NFR-12), and its budget.
5. **Pricing** for premium, and whether there is an education/team plan.
6. **Learning content.** Who writes it, and does it need expert review
   before publishing?

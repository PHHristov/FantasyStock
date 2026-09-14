# POC 1 Status — Dockerized Postgres + yfinance import

Branch: `poc/postgres-docker-yfinance` (not yet merged)

## What this covers

From the README's POC section, item 1: get stock data into a database we
control, as groundwork for the Kafka/.NET pieces that build on top of it.

## What's built

- **`docker-compose.yml`** — runs Postgres 16 in a container (`postgres_db`),
  config (user/password/db name) supplied via a local `.env` file (not
  committed — see `.gitignore`). Data persists in a named Docker volume
  across container restarts.
- **`db/init.sql`** — creates the `stock_prices` table automatically the
  first time the container initializes its data directory. Columns:
  `ticker`, `trade_date`, `open`, `high`, `low`, `close`, `volume`, with a
  `UNIQUE (ticker, trade_date)` constraint so re-imports update rather than
  duplicate rows.
- **`scripts/fetch_stock_data.py`** — pulls daily OHLCV data via `yfinance`
  for 10 fixed tickers and upserts it into `stock_prices`.
- **`requirements.txt`** — frozen from the working Python environment
  (`yfinance`, `pandas`, `psycopg2-binary`, `python-dotenv`, + transitive
  deps).

## Current data in the table

- Tickers: `AAPL, MSFT, GOOGL, AMZN, TSLA, META, NVDA, JPM, V, JNJ`
- Range: daily bars for **March 2024** (20 trading days), not just a
  single day — widened from the original "1 day" scope so there's enough
  history for later charting/display work.
- Row count: 200 (10 tickers × 20 trading days).

## How to run it locally

```bash
docker compose up -d          # starts Postgres, runs init.sql on first boot
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
python3 scripts/fetch_stock_data.py
```

Needs a local `.env` (gitignored) with `POSTGRES_USER`, `POSTGRES_PASSWORD`,
`POSTGRES_DB` — not included in the repo, ask Stefan for the values or set
your own.

## Open questions for discussion

1. **Ticker list** — currently hardcoded to 10 large-cap names. Fine for a
   POC; will need a real source of truth once leagues/portfolios exist.
2. **Granularity** — daily only for now. Intraday (hourly/minute) is
   possible via yfinance but Yahoo limits how far back intraday history is
   available (e.g. 1-minute bars: last 7 days only), and would need a
   schema change (`trade_date` → a timestamp column). Worth deciding if/when
   we need that for the live-ticker feature.
3. **Handoff to Kafka (POC item 2)** — this Postgres instance is meant to
   be the source a .NET producer reads from. Need to agree on the read
   pattern (poll on a schedule? trigger-based? one-shot batch per import?).
4. **Credentials approach** — `.env` + `.gitignore` works for local dev;
   will need a real secrets story once this moves beyond one machine.

## Not done yet

- Not pushed as a PR — branch is published, PR opens after this discussion.
- No automated scheduling of the fetch script (run manually today).
- No tests.

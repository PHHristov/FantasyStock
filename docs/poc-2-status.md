# POC 2 Status — Kafka tick engine (Postgres → Kafka)

Branch: `poc/kafka-consumer`

## What this covers

From the README's POC section, item 2: a producer that reads the stock data
already sitting in Postgres (POC item 1) and streams it onto Kafka, so the
future .NET backend (POC item 3) has a live feed to consume instead of
querying Postgres directly.

## What's built

- **`tick-engine/`** — a .NET 8 console app ("the tick engine"). On startup
  it loads every row from `stock_prices`, groups it by ticker, and builds a
  shared calendar of distinct trading days. It then loops forever: on each
  tick it advances one trading day and publishes that day's bar for every
  ticker that has one, so all tickers move in lockstep. When it reaches the
  end of the history it wraps back to the first day and keeps going (a
  `lap` counter in each message says which pass it's on) — this is a
  historical-data replay standing in for a live feed, not a real-time price
  source.
- **Kafka topics** — one topic per ticker, named `stock-ticks.<TICKER>`
  (e.g. `stock-ticks.AAPL`), keyed by ticker symbol. Message value is JSON:
  `{ticker, trade_date, open, high, low, close, volume, lap}`.
- **`docker-compose.yml`** (root) — now also brings up:
  - `kafka` — `apache/kafka:3.8.0`, single-node KRaft mode (no ZooKeeper).
  - `tick-engine` — builds from `tick-engine/Dockerfile`, waits for both
    `db` and `kafka` to report healthy before starting.
  - `db` gained a `pg_isready` healthcheck so `tick-engine` can depend on
    it being actually ready, not just "container started."
  - Whole thing comes up with one `docker compose up -d` from the repo
    root, per the requirement that the app starts as a single stack.

## Configuration (env vars, all have defaults except Postgres credentials)

| Var | Default | Meaning |
|---|---|---|
| `TICK_INTERVAL_SECONDS` | `5` | seconds between ticks (i.e. between trading days) |
| `TOPIC_PREFIX` | `stock-ticks.` | prefix before the ticker symbol in topic names |
| `KAFKA_BOOTSTRAP_SERVERS` | `kafka:9092` | set automatically in compose |
| `POSTGRES_HOST` / `PORT` | `db` / `5432` | set automatically in compose |

`TICK_INTERVAL_SECONDS` and `TOPIC_PREFIX` can be overridden in `.env` or on
the command line, e.g. `TICK_INTERVAL_SECONDS=1 docker compose up -d`.

## How to run it locally

```bash
docker compose up -d          # starts Postgres, Kafka, and the tick engine
```

The tick engine polls Postgres every 5s at startup until `stock_prices` has
rows, so the order you bring things up in doesn't matter — but the table is
still only populated by running `scripts/fetch_stock_data.py` manually
(unchanged from POC 1; this compose file does not run it for you yet).

To watch the feed:

```bash
docker exec -it kafka /opt/kafka/bin/kafka-console-consumer.sh \
  --bootstrap-server localhost:9092 --topic stock-ticks.AAPL --from-beginning
```

## Verification done

Real Kafka/Postgres containers couldn't be pulled from this environment
(Docker Hub is blocked by this sandbox's network policy — should not be an
issue on a normal dev machine), so verification here was:

- `docker compose config` — confirms the compose file is syntactically
  valid and every service, healthcheck, `depends_on` condition, and env
  var resolves correctly.
- The tick engine's core logic (lockstep multi-ticker ticking, per-ticker
  topic/key naming, JSON payload shape, looping across laps, graceful
  shutdown) was unit-verified against stand-in Postgres/Kafka clients with
  canned data — confirmed correct ordering and output.
- Not yet verified: an actual `docker compose up` end-to-end on a machine
  with Docker Hub access, and a real Kafka consumer reading the topics.

## Open questions for discussion

1. **Seeding is still manual.** `docker compose up` starts the whole app,
   but `stock_prices` stays empty until someone runs
   `scripts/fetch_stock_data.py` by hand. Worth adding a one-shot seed
   service to the compose file so a single command does everything?
2. **Tick pacing** — defaulted to 1 trading day every 5 seconds. Fine for a
   demo/dev feed; may want something faster/slower once the backend
   consumer (POC item 3) exists and this gets used for real testing.
3. **Looping forever** — once history is replayed, it starts over from day
   one indefinitely. Is that the right behavior long-term, or should it
   stop after one pass in some environments?
4. **Per-ticker topics vs single topic** — went with one topic per ticker
   per your call; worth revisiting once the consumer side is built, since
   subscribing to 10 topics is more setup than subscribing to 1.

## Not done yet

- Not pushed as a PR.
- No automated topic creation beyond Kafka's own
  `auto.create.topics.enable=true` (fine for a POC, not for production).
- No tests.
- No consumer (that's POC item 3, on the .NET backend).

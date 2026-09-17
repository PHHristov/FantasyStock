# POC 3 Status — Backend (Kafka consumer + REST API + live price feed)

Branch: `poc/kafka-consumer` (same branch as POC 2's tick-engine)

## What this covers

From the README's POC section, item 3: the .NET backend — a Kafka consumer,
two hardcoded no-auth users, buy/sell, and historical data — built as "the
backbone of the app" that everything else (a future frontend, or you
testing directly) connects to.

## What's built

- **`backend/`** — an ASP.NET Core 8 minimal-API app. Three things running
  in one process:
  1. **Kafka consumer** (`KafkaConsumerService`, a hosted background
     service) — subscribes to every `stock-ticks.*` topic via a regex
     subscription and keeps an in-memory "latest price per ticker" cache
     (`PriceCache`) up to date as the tick-engine streams.
  2. **REST API** — reads/writes go through this, backed by Postgres.
  3. **Live price WebSocket** (`GET /ws/prices`) — every tick consumed from
     Kafka is immediately pushed to all connected clients, so a frontend
     doesn't have to poll.
- **New Postgres tables** (`db/02_app_schema.sql`) — `users`, `holdings`,
  `trades`. `docker-compose.yml`'s `db` service now mounts the whole `db/`
  folder (was just `init.sql`), so Postgres runs every `.sql` file in it on
  first boot.
- **`docker-compose.yml`** — added a `backend` service (builds from
  `backend/Dockerfile`, port `8080`, waits for `db` and `kafka` to be
  healthy). The whole app — Postgres, Kafka, tick-engine, backend — now
  comes up with one `docker compose up -d`.

## API

| Method | Path | What |
|---|---|---|
| GET | `/health` | liveness check |
| GET | `/users` | the two hardcoded users and their cash |
| GET | `/users/{id}/portfolio` | cash, holdings, market value per holding (from the live price cache), total value |
| GET | `/users/{id}/trades` | that user's trade history, newest first |
| GET | `/prices` | snapshot of every ticker's latest known price |
| GET | `/prices/{ticker}` | latest known price for one ticker (404 if none yet) |
| POST | `/trades` | `{userId, ticker, side, quantity}` → executes a buy/sell at the latest Kafka-consumed price |
| GET | `/ws/prices` | WebSocket — pushes every tick as JSON as it's consumed |

Swagger UI is at `/swagger` when running (`ASPNETCORE_ENVIRONMENT` isn't
gated in Program.cs, so it's on in the compose setup too — fine for a POC,
worth locking down before this goes anywhere real).

## How trades work

- Execute at the **latest price the backend has consumed from Kafka**
  (`PriceCache`), not a direct Postgres lookup — that's the point of
  routing this through the tick-engine/Kafka pipeline rather than just
  querying `stock_prices`. If no tick has arrived yet for a ticker,
  `POST /trades` returns `400` with a clear message rather than guessing.
- Each trade runs inside a Postgres transaction that does
  `SELECT ... FOR UPDATE` on the user's row, so two concurrent trade
  requests for the same user serialize correctly instead of racing on cash
  or holdings.
- Both users start with **$100,000 cash**, no auth (fixed ids `1` and `2`
  from the seed data).

## Configuration (env vars)

| Var | Default | Meaning |
|---|---|---|
| `KAFKA_BOOTSTRAP_SERVERS` | `kafka:9092` | set automatically in compose |
| `TOPIC_PATTERN` | `^stock-ticks\..*` | regex the consumer subscribes with (leading `^` = regex subscription) |
| `POSTGRES_HOST` / `PORT` | `db` / `5432` | set automatically in compose |
| `POSTGRES_DB` / `USER` / `PASSWORD` | — | required, same `.env` as the rest of the stack |

## How to run it locally

```bash
docker compose up -d     # Postgres + Kafka + tick-engine + backend, all at once
```

**If you already have a Postgres volume from before this change**, the new
`users`/`holdings`/`trades` tables won't appear automatically — Postgres
only runs `docker-entrypoint-initdb.d` scripts on a *fresh* data directory.
Either:

```bash
docker compose down -v && docker compose up -d     # wipes and reseeds everything
```

or apply just the new schema to the existing volume:

```bash
docker exec -i postgres_db psql -U $POSTGRES_USER -d $POSTGRES_DB < db/02_app_schema.sql
```

Then, e.g.:

```bash
curl http://localhost:8080/users
curl -X POST http://localhost:8080/trades \
  -H "Content-Type: application/json" \
  -d '{"userId":1,"ticker":"AAPL","side":"buy","quantity":10}'
curl http://localhost:8080/users/1/portfolio
```

(`stock_prices` and the tick-engine still need seeding per POC 1/2 — this
doesn't change that.)

## Verification done

Same sandbox constraint as POC 2: Docker registries (Docker Hub *and*
Microsoft's own `mcr.microsoft.com`) are both blocked from this
environment, so an actual `docker compose up` couldn't be run here. What
was verified instead:

- **Real compile against the actual ASP.NET Core 8 runtime** (installed
  locally, not a stand-in) — Minimal APIs, dependency injection, the
  WebSocket middleware, `BackgroundService`, CORS, `ILogger`/`IConfiguration`
  all compiled and type-checked for real. Only the three external NuGet
  packages (`Confluent.Kafka`, `Npgsql`, `Swashbuckle.AspNetCore` — blocked
  same as before, no NuGet access in this sandbox) were checked against
  API-accurate stand-ins instead.
- **Actually booted the compiled app** and hit it over HTTP: `/health`,
  `/users`, `/prices`, `/prices/{ticker}` all responded correctly;
  `POST /trades` correctly returned `400` with the right message when no
  price data was available yet, confirming the request → validation →
  error-response path works end to end.
- **Deserialized the tick-engine's actual wire-format JSON** (the exact
  string it was confirmed to emit in POC 2's own test) into `StockTick`,
  confirmed every field round-tripped correctly including the date, ran it
  through `PriceCache` (including a lowercase ticker lookup), and
  re-serialized it to the camelCase JSON shape the API/WebSocket clients
  will actually receive — matched expectations exactly.
- **Not yet verified**: a real end-to-end run with actual Postgres and
  Kafka containers (row-locking behavior under real concurrent trades, the
  Kafka regex subscription against a real broker, the WebSocket feed with a
  real browser client). Worth doing as the very next step once this is
  pulled somewhere with normal registry access.

## Open questions for discussion

1. **Auth** is still nothing — anyone hitting the API can trade as either
   user. Fine for a POC, not fine for anything real.
2. **Swagger is always on.** Should probably be dev-only once this isn't
   just the two of you testing.
3. **Consumer restart behavior** — the backend's Kafka consumer group
   starts from `latest`, so a restarted backend has an empty price cache
   until the tick-engine's next lap comes around (up to
   `TICK_INTERVAL_SECONDS * number of trading days` worst case). Fine given
   the tick-engine loops constantly, but worth knowing if trades fail with
   "no price data yet" right after a backend restart.
4. **CORS is wide open** (`AllowAnyOrigin`) — tighten once there's a real
   frontend origin to lock it to.

## Not done yet

- Not pushed as a PR.
- No tests.
- No auth.
- No frontend yet — this is the backend half of "connect everything up."

# POC 3 Status — Backend (Kafka consumer + REST API + live price feed)

Branch: `poc/kafka-consumer` (same branch as POC 2's tick-engine)

## What this covers

From the README's POC section, item 3: the .NET backend — a Kafka consumer,
user accounts, buy/sell, and historical data — built as "the backbone of
the app" that everything else (a future frontend, or you testing directly)
connects to. Started as two hardcoded no-auth users; real registration/login
was added shortly after (see the API table below) once the rest of the
pipeline was confirmed working.

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

Everything requires `Authorization: Bearer <token>` **except** the rows
marked public below — the app is secure-by-default (an authorization
fallback policy requires an authenticated caller for any endpoint that
doesn't explicitly opt out), so a new endpoint added later is protected
unless someone deliberately marks it `AllowAnonymous()`.

| Method | Path | Auth | What |
|---|---|---|---|
| GET | `/health` | public | liveness check |
| POST | `/auth/register` | public | `{username, password}` → creates a new user (starts with $100,000 cash, same as the seeded ones) and returns a token |
| POST | `/auth/login` | public | `{username, password}` → returns a token |
| GET | `/users` | required | every user and their cash (roster/leaderboard view) |
| GET | `/me/portfolio` | required | cash, holdings, market value per holding, total value — for **the caller**, from the token, not a URL param |
| GET | `/me/trades` | required | the caller's trade history, newest first |
| GET | `/prices` | public | snapshot of every ticker's latest known price |
| GET | `/prices/{ticker}` | public | latest known price for one ticker (404 if none yet) |
| POST | `/trades` | required | `{ticker, side, quantity}` → executes a buy/sell for the caller (from the token) at the latest Kafka-consumed price |
| GET | `/ws/prices` | public | WebSocket — pushes every tick as JSON as it's consumed |

Swagger UI is at `/swagger` when running (`ASPNETCORE_ENVIRONMENT` isn't
gated in Program.cs, so it's on in the compose setup too — fine for a POC,
worth locking down before this goes anywhere real). It has an "Authorize"
button that accepts a bearer token, so protected endpoints can be tried
there too, not just via `curl`.

**Why `/me/...` instead of `/users/{id}/...`:** the old routes (and the
`userId` field `POST /trades` used to take in its body) trusted whatever id
the caller supplied — anyone could trade as `user1` or `user2` with a
single request. The user id now comes exclusively from the verified JWT's
claims (see `backend/Auth/CurrentUser.cs`), never from client input.

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
- All users start with **$100,000 cash**, including the two seeded ones
  (`user1`/`user2`, ids `1`/`2` — note these two have no password set and
  can't log in; they predate auth and are effectively retired, new testing
  should go through `POST /auth/register`).

## Configuration (env vars)

| Var | Default | Meaning |
|---|---|---|
| `KAFKA_BOOTSTRAP_SERVERS` | `kafka:9092` | set automatically in compose |
| `TOPIC_PATTERN` | `^stock-ticks\..*` | regex the consumer subscribes with (leading `^` = regex subscription) |
| `POSTGRES_HOST` / `PORT` | `db` / `5432` | set automatically in compose |
| `POSTGRES_DB` / `USER` / `PASSWORD` | — | required, same `.env` as the rest of the stack |
| `JWT_SIGNING_KEY` | — | required, HMAC-SHA256 signing key for auth tokens (12h expiry, no refresh tokens). Generate one with `openssl rand -base64 32`; same `.env`/gitignored `launchSettings.json` treatment as the Postgres secrets. |

## How to run it locally

```bash
docker compose up -d     # Postgres + Kafka + tick-engine + backend, all at once
```

**If you already have a Postgres volume from before this change**, the new
tables/columns won't appear automatically — Postgres only runs
`docker-entrypoint-initdb.d` scripts on a *fresh* data directory. Either:

```bash
docker compose down -v && docker compose up -d     # wipes and reseeds everything
```

or apply just the new schema files to the existing volume, in order:

```bash
docker exec -i postgres_db psql -U $POSTGRES_USER -d $POSTGRES_DB < db/02_app_schema.sql
docker exec -i postgres_db psql -U $POSTGRES_USER -d $POSTGRES_DB < db/03_auth_schema.sql
```

Then, e.g.:

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"you","password":"pick-something"}' | jq -r .token)

curl http://localhost:8080/users
curl -X POST http://localhost:8080/trades \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"ticker":"AAPL","side":"buy","quantity":10}'
curl http://localhost:8080/me/portfolio -H "Authorization: Bearer $TOKEN"
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
- **Not yet verified**: row-locking behavior under real concurrent trades,
  the WebSocket feed with a real browser client.

**Auth (added after the above) was verified live**, against real
`postgres_db`/`kafka` containers and a running `tick-engine`, not just
compiled: register → login → `/me/portfolio` with/without a token →
`POST /trades` end to end, including the negative cases (no token → `401`,
wrong password → `401`, duplicate username → `400`). One real bug was
caught and fixed in the process: `02_app_schema.sql` seeds `user1`/`user2`
with explicit `id` values, which doesn't advance the `id` column's
underlying sequence — the first real `POST /auth/register` collided with
`user1`'s id and initially surfaced as a misleading "username already
taken". Fixed by fast-forwarding the sequence in `db/03_auth_schema.sql`
and narrowing `AuthService`'s conflict handling to the actual username
constraint, so a different underlying error can't be mislabeled again.

## Open questions for discussion

1. **Swagger is always on.** Should probably be dev-only once this isn't
   just the two of you testing.
2. **Consumer restart behavior** — the backend's Kafka consumer group
   starts from `latest`, so a restarted backend has an empty price cache
   until the tick-engine's next lap comes around (up to
   `TICK_INTERVAL_SECONDS * number of trading days` worst case). Fine given
   the tick-engine loops constantly, but worth knowing if trades fail with
   "no price data yet" right after a backend restart.
3. **CORS is wide open** (`AllowAnyOrigin`) — a Bearer token in a header
   doesn't weaken this the way a cookie would, but still worth tightening
   once there's a real frontend origin to lock it to.
4. **No refresh tokens** — a token just expires after 12h and the user has
   to log in again. Fine for a POC; a real app would want a refresh flow.
5. **`user1`/`user2` are orphaned** — seeded before auth existed, no
   password, can't log in. Leaving them as a known loose end rather than
   backfilling; new accounts go through `/auth/register`.

## Not done yet

- Not pushed as a PR.
- No tests.
- No frontend yet — this is the backend half of "connect everything up."

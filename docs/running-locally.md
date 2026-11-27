# Running FantasyStock locally

Consolidated start/stop reference for the whole stack — Postgres, Kafka,
tick-engine, backend. The per-POC docs (`poc-1..3-status.md`) each cover how
their own piece was built; this one is just "how do I turn it on and off."

## Prerequisites (one-time)

- Docker + Docker Compose, and your user in the `docker` group (`sudo
  usermod -aG docker $USER`, then log out/in) so `docker`/`docker compose`
  work without `sudo`.
- A `.env` in the repo root (gitignored — ask a teammate for the values, or
  make your own for solo local dev):
  ```
  POSTGRES_USER=...
  POSTGRES_PASSWORD=...
  POSTGRES_DB=...
  JWT_SIGNING_KEY=...       # openssl rand -base64 32
  ```
- For **local-dev mode** only (see below): the .NET 8 SDK, and a
  `Properties/launchSettings.json` in both `backend/` and `tick-engine/`
  (gitignored — same secrets as `.env`, plus host-side ports). See
  [Local-dev mode](#local-dev-mode-recommended-while-coding) for the exact
  contents if you don't have one yet.

## Two ways to run it

| | Full Docker | Local-dev |
|---|---|---|
| Postgres, Kafka | container | container |
| tick-engine, backend | container | `dotnet run` / F5 on your machine |
| When to use | quick smoke test, "does it actually work end to end" | day-to-day coding — IDE debugging, breakpoints, fast rebuild loop |

They can't both run at once — both want the same host ports (`8080` for the
backend, `29092` for Kafka's host-side listener). Stop one before starting
the other.

## Full Docker

Everything as containers, one command:

```bash
docker compose up -d
```

Check it actually came up healthy before assuming it works — `tick-engine`
and `backend` both `depends_on` Postgres/Kafka being *healthy*, not just
started, but Kafka in particular takes a few seconds:

```bash
docker compose ps
```

All four services should show `Up`/`healthy` (`db` and `kafka` have
healthchecks; `tick-engine`/`backend` just show `Up` once running). If
something's stuck, `docker compose logs -f <service>` (`db`, `kafka`,
`tick-engine`, or `backend`).

**Stop it:**
```bash
docker compose down          # stops + removes containers, keeps the pgdata volume (data survives)
docker compose down -v       # also wipes the Postgres volume - only if you want a truly fresh db
```

## Local-dev mode (recommended while coding)

Only Postgres + Kafka in Docker; run `tick-engine` and `backend` yourself so
you get breakpoints, fast edit/rebuild, and normal terminal output instead
of `docker compose logs`.

**1. Start just the containers:**
```bash
docker compose up -d db kafka
docker compose ps    # wait for both "healthy"
```

**2. Run tick-engine** (own terminal, or a VS Code Run/Debug config):
```bash
cd tick-engine
dotnet run
```

**3. Run the backend** (separate terminal):
```bash
cd backend
dotnet run
```
Swagger UI: http://localhost:8080/swagger — click **Authorize** and paste a
token (get one via `POST /auth/register` or `/auth/login` first) to try
protected endpoints from the browser instead of only `curl`.

Both processes need a **`Properties/launchSettings.json`** (gitignored —
create it yourself if missing) with host-side values, since containerized
service names (`db`, `kafka:9092`) don't resolve outside the Docker
network:

`backend/Properties/launchSettings.json`:
```json
{
  "profiles": {
    "Backend": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:8080",
      "environmentVariables": {
        "POSTGRES_HOST": "localhost",
        "POSTGRES_PORT": "5433",
        "POSTGRES_DB": "stockdb",
        "POSTGRES_USER": "<from .env>",
        "POSTGRES_PASSWORD": "<from .env>",
        "KAFKA_BOOTSTRAP_SERVERS": "localhost:29092",
        "JWT_SIGNING_KEY": "<from .env>"
      }
    }
  }
}
```

`tick-engine/Properties/launchSettings.json` — same idea, drop
`applicationUrl` and `JWT_SIGNING_KEY` (tick-engine doesn't serve HTTP or
touch auth), add:
```json
"TICK_INTERVAL_SECONDS": "5",
"TOPIC_PREFIX": "stock-ticks."
```

**Stop it:**
- `tick-engine`/`backend`: `Ctrl+C` in their terminal (or the IDE Stop
  button).
- Containers: same as above, `docker compose down` (this also stops `db`/
  `kafka` if you only started those two — Compose operates on whichever
  services are currently up).

## Quick smoke test (either mode)

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"you","password":"pick-something"}' | jq -r .token)

curl http://localhost:8080/me/portfolio -H "Authorization: Bearer $TOKEN"
curl -X POST http://localhost:8080/trades -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" -d '{"ticker":"AAPL","side":"buy","quantity":1}'
```
The trade only works once `tick-engine` has actually produced at least one
tick for that ticker — give it a few seconds after startup.

## Frontend (either mode)

`frontend/` is a plain static page — no npm, no build step. It talks to the
backend on `http://localhost:8080`, so start the backend first (either mode
above), then serve the folder with any static file server:

```bash
python3 -m http.server 5500 --directory frontend
```

Open http://localhost:5500. The market list and chart are public; log in or
create an account in the right-hand panel to see your portfolio and trade.
Stop it with `Ctrl+C`.

- **Charts build up live.** There's no price-history endpoint, so the chart
  fills from the `/ws/prices` WebSocket as ticks arrive, one simulated
  trading day per tick. A full month appears after one tick-engine lap
  (~100s at the default 5s interval). The highlighted point is the day the
  replay is currently on.
- **Logging out** only discards the token in the browser (it's kept in
  `sessionStorage`, so it also goes away when the tab closes). JWTs can't be
  revoked server-side yet; see `poc-3-status.md`.
- Backend somewhere other than `localhost:8080`? Set
  `window.FANTASYSTOCK_API_BASE` in `index.html` before `app.js` loads.
- Chart.js and the fonts load from CDNs, so the page needs internet access.
  Without it, prices still update in the market list; only the chart is
  missing.

## Troubleshooting

- **`permission denied ... docker.sock`** — your user isn't in the `docker`
  group yet, or the group change needs a fresh login to take effect. `sudo
  usermod -aG docker $USER` then log out/in (or `sg docker -c "..."` as a
  same-session workaround without logging out).
- **`address already in use` on 8080 or 5432/5433** — something's already
  bound to that port. `ss -tlnp | grep <port>` shows what; often either a
  locally-installed Postgres squatting on 5432 (that's *why* the container
  uses 5433 instead — see `docker-compose.yml`), or a leftover `dotnet run`
  from a previous session still holding 8080.
- **`Missing required environment variable: ...`** at backend/tick-engine
  startup — `launchSettings.json` is missing or missing a key; see above.
- **Everything 401s, including `/health` or `/swagger`** — if you're on an
  old build, this was a real bug (Swagger's routes aren't minimal-API
  endpoints, so an auth fallback policy rejected them before Swagger's own
  middleware ever ran) — fixed in commit `2f92b63`. Pull latest and rebuild.
- **Postgres tables missing after a fresh `docker compose up`** — the SQL
  files under `db/` only auto-run on a *fresh* volume
  (`docker-entrypoint-initdb.d` behavior). If you're reusing an old volume
  from before a schema change, either `docker compose down -v && docker
  compose up -d` (wipes data) or apply the missing file(s) by hand:
  ```bash
  docker exec -i postgres_db psql -U $POSTGRES_USER -d $POSTGRES_DB < db/<file>.sql
  ```

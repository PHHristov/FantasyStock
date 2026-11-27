-- Backend app schema: two hardcoded no-auth users, their holdings, and their
-- trade history. Independent of stock_prices (01_init.sql) - only runs
-- automatically on a *fresh* Postgres volume (docker-entrypoint-initdb.d
-- scripts only fire once, when the data directory is first created). See
-- docs/poc-3-status.md for how to apply this to an already-initialized db.

CREATE TABLE IF NOT EXISTS users (
    id SERIAL PRIMARY KEY,
    name VARCHAR(50) NOT NULL UNIQUE,
    cash NUMERIC(14, 2) NOT NULL DEFAULT 100000.00
);

INSERT INTO users (id, name, cash) VALUES
    (1, 'user1', 100000.00),
    (2, 'user2', 100000.00)
ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS holdings (
    user_id INT NOT NULL REFERENCES users(id),
    ticker VARCHAR(10) NOT NULL,
    quantity NUMERIC(18, 6) NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, ticker)
);

CREATE TABLE IF NOT EXISTS trades (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(id),
    ticker VARCHAR(10) NOT NULL,
    side VARCHAR(4) NOT NULL CHECK (side IN ('BUY', 'SELL')),
    quantity NUMERIC(18, 6) NOT NULL,
    price NUMERIC(12, 4) NOT NULL,
    executed_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

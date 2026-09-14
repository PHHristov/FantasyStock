# %%
import os

import psycopg2
import yfinance as yf
from dotenv import load_dotenv

TICKERS = ["AAPL", "MSFT", "GOOGL", "AMZN", "TSLA", "META", "NVDA", "JPM", "V", "JNJ"]
START = "2024-03-04"
END = "2024-03-05"  # yfinance's `end` is exclusive, so this window covers just 2024-03-04

# %%
data = yf.download(tickers=TICKERS, start=START, end=END, group_by="ticker")
data

# %%
load_dotenv()

conn = psycopg2.connect(
    host="localhost",
    port=5432,
    dbname=os.environ["POSTGRES_DB"],
    user=os.environ["POSTGRES_USER"],
    password=os.environ["POSTGRES_PASSWORD"],
)
cur = conn.cursor()

rows = []
for ticker in TICKERS:
    ticker_data = data[ticker].dropna(how="all")
    for trade_date, row in ticker_data.iterrows():
        rows.append(
            (
                ticker,
                trade_date.date(),
                float(row["Open"]),
                float(row["High"]),
                float(row["Low"]),
                float(row["Close"]),
                int(row["Volume"]),
            )
        )

cur.executemany(
    """
    INSERT INTO stock_prices (ticker, trade_date, open, high, low, close, volume)
    VALUES (%s, %s, %s, %s, %s, %s, %s)
    ON CONFLICT (ticker, trade_date) DO UPDATE SET
        open = EXCLUDED.open,
        high = EXCLUDED.high,
        low = EXCLUDED.low,
        close = EXCLUDED.close,
        volume = EXCLUDED.volume;
    """,
    rows,
)
conn.commit()
print(f"Inserted/updated {len(rows)} rows.")

cur.close()
conn.close()

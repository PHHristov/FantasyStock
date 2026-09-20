using Backend.Db;
using Backend.Models;
using Npgsql;

namespace Backend.Services;

/// <summary>Thrown for any client-fixable problem with a trade request (bad input, insufficient funds/holdings, unknown user/price).</summary>
public sealed class TradeValidationException(string message) : Exception(message);

/// <summary>
/// Executes buy/sell orders against the latest price in <see cref="PriceCache"/>
/// (fed by Kafka) and persists the result to Postgres. Each trade runs inside
/// a transaction that row-locks the user (`SELECT ... FOR UPDATE`) so two
/// concurrent trades for the same user can't race on cash/holdings.
/// </summary>
public sealed class TradingService
{
    private readonly string _connectionString;
    private readonly PriceCache _priceCache;

    public TradingService(IConfiguration config, PriceCache priceCache)
    {
        _connectionString = ConnectionStrings.Build(config);
        _priceCache = priceCache;
    }

    public async Task<TradeDto> ExecuteAsync(int userId, PlaceTradeRequest request, CancellationToken ct)
    {
        var side = request.Side.Trim().ToUpperInvariant();
        if (side is not ("BUY" or "SELL"))
        {
            throw new TradeValidationException("side must be 'buy' or 'sell'");
        }

        if (request.Quantity <= 0)
        {
            throw new TradeValidationException("quantity must be greater than zero");
        }

        var ticker = request.Ticker.Trim().ToUpperInvariant();
        if (!_priceCache.TryGet(ticker, out var tick))
        {
            throw new TradeValidationException(
                $"no price data yet for '{ticker}' - wait for the tick engine to publish at least one tick for it");
        }

        var price = tick.Close;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Row-lock the user for the rest of this transaction so a concurrent
        // trade for the same user has to wait rather than racing on cash.
        decimal cash;
        await using (var lockCmd = new NpgsqlCommand("SELECT cash FROM users WHERE id = @userId FOR UPDATE", conn))
        {
            lockCmd.Transaction = tx;
            lockCmd.Parameters.AddWithValue("userId", userId);
            var result = await lockCmd.ExecuteScalarAsync(ct);
            if (result is null)
            {
                throw new TradeValidationException($"unknown user id {userId}");
            }

            cash = (decimal)result;
        }

        decimal currentQty = 0;
        await using (var holdingCmd = new NpgsqlCommand(
            "SELECT quantity FROM holdings WHERE user_id = @userId AND ticker = @ticker FOR UPDATE", conn))
        {
            holdingCmd.Transaction = tx;
            holdingCmd.Parameters.AddWithValue("userId", userId);
            holdingCmd.Parameters.AddWithValue("ticker", ticker);
            var result = await holdingCmd.ExecuteScalarAsync(ct);
            if (result is not null)
            {
                currentQty = (decimal)result;
            }
        }

        var notional = price * request.Quantity;

        if (side == "BUY")
        {
            if (notional > cash)
            {
                throw new TradeValidationException($"insufficient cash: need {notional:F2}, have {cash:F2}");
            }

            cash -= notional;
            currentQty += request.Quantity;
        }
        else
        {
            if (request.Quantity > currentQty)
            {
                throw new TradeValidationException(
                    $"insufficient holdings: trying to sell {request.Quantity}, hold {currentQty}");
            }

            cash += notional;
            currentQty -= request.Quantity;
        }

        await using (var updateUserCmd = new NpgsqlCommand("UPDATE users SET cash = @cash WHERE id = @userId", conn))
        {
            updateUserCmd.Transaction = tx;
            updateUserCmd.Parameters.AddWithValue("cash", cash);
            updateUserCmd.Parameters.AddWithValue("userId", userId);
            await updateUserCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var upsertHoldingCmd = new NpgsqlCommand(
            """
            INSERT INTO holdings (user_id, ticker, quantity)
            VALUES (@userId, @ticker, @qty)
            ON CONFLICT (user_id, ticker) DO UPDATE SET quantity = @qty
            """, conn))
        {
            upsertHoldingCmd.Transaction = tx;
            upsertHoldingCmd.Parameters.AddWithValue("userId", userId);
            upsertHoldingCmd.Parameters.AddWithValue("ticker", ticker);
            upsertHoldingCmd.Parameters.AddWithValue("qty", currentQty);
            await upsertHoldingCmd.ExecuteNonQueryAsync(ct);
        }

        int tradeId;
        DateTimeOffset executedAt;
        await using (var insertTradeCmd = new NpgsqlCommand(
            """
            INSERT INTO trades (user_id, ticker, side, quantity, price)
            VALUES (@userId, @ticker, @side, @qty, @price)
            RETURNING id, executed_at
            """, conn))
        {
            insertTradeCmd.Transaction = tx;
            insertTradeCmd.Parameters.AddWithValue("userId", userId);
            insertTradeCmd.Parameters.AddWithValue("ticker", ticker);
            insertTradeCmd.Parameters.AddWithValue("side", side);
            insertTradeCmd.Parameters.AddWithValue("qty", request.Quantity);
            insertTradeCmd.Parameters.AddWithValue("price", price);

            await using var reader = await insertTradeCmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            tradeId = reader.GetInt32(0);
            executedAt = reader.GetFieldValue<DateTimeOffset>(1);
        }

        await tx.CommitAsync(ct);

        return new TradeDto(tradeId, userId, ticker, side, request.Quantity, price, executedAt);
    }
}

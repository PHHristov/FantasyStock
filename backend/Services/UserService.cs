using Backend.Db;
using Backend.Models;
using Npgsql;

namespace Backend.Services;

/// <summary>Read-side queries for users, portfolios and trade history.</summary>
public sealed class UserService
{
    private readonly string _connectionString;
    private readonly PriceCache _priceCache;

    public UserService(IConfiguration config, PriceCache priceCache)
    {
        _connectionString = ConnectionStrings.Build(config);
        _priceCache = priceCache;
    }

    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct)
    {
        var users = new List<UserDto>();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id, name, cash FROM users ORDER BY id", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            users.Add(new UserDto(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2)));
        }

        return users;
    }

    public async Task<PortfolioDto?> GetPortfolioAsync(int userId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        string name;
        decimal cash;
        await using (var userCmd = new NpgsqlCommand("SELECT name, cash FROM users WHERE id = @id", conn))
        {
            userCmd.Parameters.AddWithValue("id", userId);
            await using var reader = await userCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            name = reader.GetString(0);
            cash = reader.GetDecimal(1);
        }

        var holdings = new List<HoldingDto>();
        decimal holdingsValue = 0;
        await using (var holdingsCmd = new NpgsqlCommand(
            "SELECT ticker, quantity FROM holdings WHERE user_id = @id AND quantity <> 0 ORDER BY ticker", conn))
        {
            holdingsCmd.Parameters.AddWithValue("id", userId);
            await using var reader = await holdingsCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var ticker = reader.GetString(0);
                var qty = reader.GetDecimal(1);
                decimal? lastPrice = _priceCache.TryGet(ticker, out var tick) ? tick.Close : null;
                decimal? marketValue = lastPrice is null ? null : lastPrice * qty;

                holdings.Add(new HoldingDto(ticker, qty, lastPrice, marketValue));
                holdingsValue += marketValue ?? 0;
            }
        }

        return new PortfolioDto(userId, name, cash, holdings, holdingsValue, cash + holdingsValue);
    }

    public async Task<IReadOnlyList<TradeDto>> GetTradesAsync(int userId, CancellationToken ct)
    {
        var trades = new List<TradeDto>();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, user_id, ticker, side, quantity, price, executed_at " +
            "FROM trades WHERE user_id = @id ORDER BY executed_at DESC",
            conn);
        cmd.Parameters.AddWithValue("id", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            trades.Add(new TradeDto(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return trades;
    }
}

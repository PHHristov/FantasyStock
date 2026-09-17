using System.Collections.Concurrent;
using Backend.Models;

namespace Backend.Services;

/// <summary>
/// The backend's in-memory view of "the current price" per ticker, kept up
/// to date by <see cref="KafkaConsumerService"/> as it consumes the
/// tick-engine's Kafka stream. This is what trades execute against and what
/// GET /prices reports - it is not read from Postgres on the hot path.
/// </summary>
public sealed class PriceCache
{
    private readonly ConcurrentDictionary<string, StockTick> _prices = new();

    public event Action<StockTick>? OnPriceUpdated;

    public void Update(StockTick tick)
    {
        _prices[tick.Ticker.ToUpperInvariant()] = tick;
        OnPriceUpdated?.Invoke(tick);
    }

    public bool TryGet(string ticker, out StockTick tick) =>
        _prices.TryGetValue(ticker.Trim().ToUpperInvariant(), out tick!);

    public IReadOnlyCollection<StockTick> Snapshot() => _prices.Values.ToList();
}

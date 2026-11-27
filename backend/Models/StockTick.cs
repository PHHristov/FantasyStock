namespace Backend.Models;

/// <summary>
/// Mirrors the JSON payload the tick-engine publishes to Kafka
/// (topic <c>stock-ticks.&lt;TICKER&gt;</c>): one historical daily bar,
/// replayed as a simulated live tick. <see cref="ReceivedAt"/> is stamped
/// locally when the backend consumes the message, not part of the wire
/// payload.
/// </summary>
public sealed record StockTick(
    string Ticker,
    DateOnly TradeDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    int Lap)
{
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
}

namespace Backend.Models;

public sealed record UserDto(int Id, string Name, decimal Cash);

public sealed record HoldingDto(string Ticker, decimal Quantity, decimal? LastPrice, decimal? MarketValue);

public sealed record PortfolioDto(
    int UserId,
    string Name,
    decimal Cash,
    IReadOnlyList<HoldingDto> Holdings,
    decimal HoldingsValue,
    decimal TotalValue);

public sealed record TradeDto(
    int Id,
    int UserId,
    string Ticker,
    string Side,
    decimal Quantity,
    decimal Price,
    DateTimeOffset ExecutedAt);

/// <summary>Request body for POST /trades. Side is "buy" or "sell" (case-insensitive).</summary>
public sealed record PlaceTradeRequest(int UserId, string Ticker, string Side, decimal Quantity);

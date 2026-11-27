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

/// <summary>
/// Request body for POST /trades. No UserId here on purpose - the trading
/// user is derived from the caller's auth token, never from client input.
/// Side is "buy" or "sell" (case-insensitive).
/// </summary>
public sealed record PlaceTradeRequest(string Ticker, string Side, decimal Quantity);

public sealed record RegisterRequest(string Username, string Password);

public sealed record LoginRequest(string Username, string Password);

public sealed record AuthResponseDto(string Token, int UserId, string Username);

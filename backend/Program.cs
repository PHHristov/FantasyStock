using Backend.Models;
using Backend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new DateOnlyJsonConverter());
});

builder.Services.AddSingleton<PriceCache>();
builder.Services.AddSingleton<PriceBroadcaster>();
builder.Services.AddHostedService<KafkaConsumerService>();
builder.Services.AddScoped<TradingService>();
builder.Services.AddScoped<UserService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    // POC-stage, no auth yet: wide open so any local frontend can hit it.
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

// PriceBroadcaster must subscribe to PriceCache updates before the first
// tick arrives, not lazily on the first WebSocket connection - resolving it
// once here forces that construction at startup.
app.Services.GetRequiredService<PriceBroadcaster>();

app.UseCors();
app.UseWebSockets();
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/users", async (UserService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetUsersAsync(ct)));

app.MapGet("/users/{id:int}/portfolio", async (int id, UserService svc, CancellationToken ct) =>
{
    var portfolio = await svc.GetPortfolioAsync(id, ct);
    return portfolio is null
        ? Results.NotFound(new { error = $"no user with id {id}" })
        : Results.Ok(portfolio);
});

app.MapGet("/users/{id:int}/trades", async (int id, UserService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetTradesAsync(id, ct)));

app.MapGet("/prices", (PriceCache cache) => Results.Ok(cache.Snapshot()));

app.MapGet("/prices/{ticker}", (string ticker, PriceCache cache) =>
    cache.TryGet(ticker, out var tick)
        ? Results.Ok(tick)
        : Results.NotFound(new { error = $"no price yet for '{ticker}'" }));

app.MapPost("/trades", async (PlaceTradeRequest request, TradingService svc, CancellationToken ct) =>
{
    try
    {
        var trade = await svc.ExecuteAsync(request, ct);
        return Results.Ok(trade);
    }
    catch (TradeValidationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// One-way live feed: every price update the Kafka consumer picks up gets
// pushed to every connected client as JSON. See PriceBroadcaster.
app.Map("/ws/prices", async (HttpContext context, PriceBroadcaster broadcaster) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await broadcaster.HandleClientAsync(socket, context.RequestAborted);
});

app.Run();

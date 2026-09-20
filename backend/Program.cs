using System.Security.Claims;
using System.Text;
using Backend.Auth;
using Backend.Models;
using Backend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

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
builder.Services.AddScoped<AuthService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Lets Swagger UI's "Authorize" button attach a bearer token to
    // protected-endpoint requests, instead of only being testable via curl.
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
    });
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer",
                },
            },
            Array.Empty<string>()
        },
    });
});

builder.Services.AddCors(options =>
{
    // Auth now runs over a Bearer token in the Authorization header, not
    // cookies, so a wide-open origin policy doesn't weaken it - tighten
    // this once there's a real frontend origin to lock it to.
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var jwtSigningKey = builder.Configuration["JWT_SIGNING_KEY"]
    ?? throw new InvalidOperationException("Missing required environment variable: JWT_SIGNING_KEY");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
            ValidateLifetime = true,
        };
    });

// Secure by default: every endpoint requires an authenticated caller unless
// explicitly marked .AllowAnonymous() below, so a new endpoint added later
// is protected unless someone deliberately opts it out.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();

// PriceBroadcaster must subscribe to PriceCache updates before the first
// tick arrives, not lazily on the first WebSocket connection - resolving it
// once here forces that construction at startup.
app.Services.GetRequiredService<PriceBroadcaster>();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();

app.MapPost("/auth/register", async (RegisterRequest request, AuthService svc, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await svc.RegisterAsync(request, ct));
    }
    catch (AuthValidationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).AllowAnonymous();

app.MapPost("/auth/login", async (LoginRequest request, AuthService svc, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await svc.LoginAsync(request, ct));
    }
    catch (InvalidCredentialsException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
    }
}).AllowAnonymous();

app.MapGet("/users", async (UserService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetUsersAsync(ct)));

app.MapGet("/me/portfolio", async (ClaimsPrincipal user, UserService svc, CancellationToken ct) =>
{
    var portfolio = await svc.GetPortfolioAsync(user.GetUserId(), ct);
    return portfolio is null
        ? Results.NotFound(new { error = "authenticated user no longer exists" })
        : Results.Ok(portfolio);
});

app.MapGet("/me/trades", async (ClaimsPrincipal user, UserService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetTradesAsync(user.GetUserId(), ct)));

app.MapGet("/prices", (PriceCache cache) => Results.Ok(cache.Snapshot()))
    .AllowAnonymous();

app.MapGet("/prices/{ticker}", (string ticker, PriceCache cache) =>
    cache.TryGet(ticker, out var tick)
        ? Results.Ok(tick)
        : Results.NotFound(new { error = $"no price yet for '{ticker}'" }))
    .AllowAnonymous();

app.MapPost("/trades", async (ClaimsPrincipal user, PlaceTradeRequest request, TradingService svc, CancellationToken ct) =>
{
    try
    {
        var trade = await svc.ExecuteAsync(user.GetUserId(), request, ct);
        return Results.Ok(trade);
    }
    catch (TradeValidationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// One-way live feed: every price update the Kafka consumer picks up gets
// pushed to every connected client as JSON. See PriceBroadcaster. Public -
// not user-specific, and a bearer token isn't practical to attach to a
// browser WebSocket handshake anyway.
app.Map("/ws/prices", async (HttpContext context, PriceBroadcaster broadcaster) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await broadcaster.HandleClientAsync(socket, context.RequestAborted);
}).AllowAnonymous();

app.Run();

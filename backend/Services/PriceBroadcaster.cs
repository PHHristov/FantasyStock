using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Backend.Models;

namespace Backend.Services;

/// <summary>
/// Fans out every price update out to connected WebSocket clients
/// (GET /ws/prices), so a frontend can show live-updating prices instead of
/// polling. Registered as a singleton and must be constructed once at
/// startup (see Program.cs) so it subscribes to PriceCache before the first
/// tick arrives.
/// </summary>
public sealed class PriceBroadcaster
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly ILogger<PriceBroadcaster> _logger;

    private static readonly JsonSerializerOptions OutboundJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new DateOnlyJsonConverter() },
    };

    public PriceBroadcaster(PriceCache priceCache, ILogger<PriceBroadcaster> logger)
    {
        _logger = logger;
        priceCache.OnPriceUpdated += tick => _ = BroadcastAsync(tick);
    }

    public async Task HandleClientAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        _clients[id] = socket;
        _logger.LogInformation("WebSocket client {Id} connected ({Count} total)", id, _clients.Count);

        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                // We don't expect inbound messages - this is a one-way feed -
                // but we still need to pump receives so a client-initiated
                // close handshake completes instead of hanging.
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (WebSocketException)
        {
            // client dropped without a clean close handshake
        }
        finally
        {
            _clients.TryRemove(id, out _);
            _logger.LogInformation("WebSocket client {Id} disconnected ({Count} total)", id, _clients.Count);

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    private async Task BroadcastAsync(StockTick tick)
    {
        var json = JsonSerializer.Serialize(tick, OutboundJsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        foreach (var (id, socket) in _clients)
        {
            if (socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }

            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to send to WebSocket client {Id}: {Error}", id, ex.Message);
                _clients.TryRemove(id, out _);
            }
        }
    }
}

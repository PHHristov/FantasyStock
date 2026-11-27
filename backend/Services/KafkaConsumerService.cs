using System.Text.Json;
using Backend.Models;
using Confluent.Kafka;

namespace Backend.Services;

/// <summary>
/// Subscribes to every <c>stock-ticks.*</c> topic the tick-engine publishes
/// (one topic per ticker) via a regex subscription, and feeds each tick into
/// <see cref="PriceCache"/>. This - not a direct query against Postgres - is
/// what keeps the backend's view of "current price" moving, which is the
/// whole point of routing the data through Kafka instead of just reading
/// stock_prices directly.
/// </summary>
public sealed class KafkaConsumerService : BackgroundService
{
    private readonly PriceCache _priceCache;
    private readonly ILogger<KafkaConsumerService> _logger;
    private readonly string _bootstrapServers;
    private readonly string _topicPattern;

    private static readonly JsonSerializerOptions TickJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new DateOnlyJsonConverter() },
    };

    public KafkaConsumerService(PriceCache priceCache, ILogger<KafkaConsumerService> logger, IConfiguration config)
    {
        _priceCache = priceCache;
        _logger = logger;
        _bootstrapServers = config["KAFKA_BOOTSTRAP_SERVERS"] ?? "kafka:9092";
        _topicPattern = config["TOPIC_PATTERN"] ?? "^stock-ticks\\..*";
    }

    // The Confluent.Kafka high-level consumer's Consume() call is blocking,
    // so it runs on its own long-running thread rather than an async loop.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(
            () => Run(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private void Run(CancellationToken stoppingToken)
    {
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _bootstrapServers,
            GroupId = "backend-consumer",
            AutoOffsetReset = AutoOffsetReset.Latest,
        };

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig)
            .SetErrorHandler((_, e) => _logger.LogWarning("Kafka consumer error: {Reason}", e.Reason))
            .Build();

        // A leading '^' subscribes by regex against all matching topics
        // rather than a single fixed topic name - required here since each
        // ticker gets its own topic and the set of tickers isn't known
        // ahead of time.
        consumer.Subscribe(_topicPattern);
        _logger.LogInformation(
            "Kafka consumer subscribed to pattern '{Pattern}' on {Servers}", _topicPattern, _bootstrapServers);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning("Kafka consume error: {Error}", ex.Error.Reason);
                    continue;
                }

                if (result?.Message is null)
                {
                    continue; // poll timeout, nothing new
                }

                StockTick? tick;
                try
                {
                    tick = JsonSerializer.Deserialize<StockTick>(result.Message.Value, TickJsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(
                        "Could not parse tick from topic {Topic}: {Error}", result.Topic, ex.Message);
                    continue;
                }

                if (tick is not null)
                {
                    _priceCache.Update(tick);
                }
            }
        }
        finally
        {
            consumer.Close();
        }
    }
}

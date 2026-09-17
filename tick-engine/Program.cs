using System.Text.Json;
using Confluent.Kafka;
using Npgsql;

namespace TickEngine;

internal sealed record StockBar(
    string Ticker,
    DateOnly TradeDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);

internal sealed record EngineConfig(
    string PostgresHost,
    int PostgresPort,
    string PostgresDb,
    string PostgresUser,
    string PostgresPassword,
    string KafkaBootstrapServers,
    string TopicPrefix,
    TimeSpan TickInterval)
{
    public static EngineConfig FromEnvironment()
    {
        return new EngineConfig(
            PostgresHost: Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "db",
            PostgresPort: int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var port)
                ? port
                : 5432,
            PostgresDb: RequireEnv("POSTGRES_DB"),
            PostgresUser: RequireEnv("POSTGRES_USER"),
            PostgresPassword: RequireEnv("POSTGRES_PASSWORD"),
            KafkaBootstrapServers: Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "kafka:9092",
            TopicPrefix: Environment.GetEnvironmentVariable("TOPIC_PREFIX") ?? "stock-ticks.",
            TickInterval: TimeSpan.FromSeconds(
                double.TryParse(Environment.GetEnvironmentVariable("TICK_INTERVAL_SECONDS"), out var seconds)
                    ? seconds
                    : 5));
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing required environment variable: {name}");
}

/// <summary>
/// Reads the historical daily bars already sitting in Postgres (loaded by
/// scripts/fetch_stock_data.py) and replays them onto Kafka as a simulated
/// live tick feed: one topic per ticker, one message per trading day, paced
/// at a fixed interval, looping back to the start once the history is
/// exhausted.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        EngineConfig config;
        try
        {
            config = EngineConfig.FromEnvironment();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"[tick-engine] configuration error: {ex.Message}");
            return 1;
        }

        Console.WriteLine(
            $"[tick-engine] starting: postgres={config.PostgresHost}:{config.PostgresPort}/{config.PostgresDb}, " +
            $"kafka={config.KafkaBootstrapServers}, topicPrefix='{config.TopicPrefix}', " +
            $"tickInterval={config.TickInterval.TotalSeconds}s");

        using var producer = new ProducerBuilder<string, string>(
                new ProducerConfig { BootstrapServers = config.KafkaBootstrapServers })
            .Build();

        var bars = await LoadBarsWithRetryAsync(config, cts.Token);
        if (bars.Count == 0)
        {
            // Only happens if we were cancelled while still waiting for data.
            Console.WriteLine("[tick-engine] shutting down before any data was found.");
            return 0;
        }

        // Group bars per ticker, and build a shared calendar of distinct trade
        // dates so every tick advances all tickers by one trading day in lockstep.
        var byTicker = bars
            .GroupBy(b => b.Ticker)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.TradeDate).ToList());

        var calendar = bars
            .Select(b => b.TradeDate)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        Console.WriteLine(
            $"[tick-engine] loaded {bars.Count} bar(s) across {byTicker.Count} ticker(s), " +
            $"{calendar.Count} trading day(s) ({calendar[0]:yyyy-MM-dd} .. {calendar[^1]:yyyy-MM-dd}). " +
            $"Ticking every {config.TickInterval.TotalSeconds}s, looping.");

        var dayIndex = 0;
        var lap = 1;

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var date = calendar[dayIndex];
                var producedCount = 0;

                foreach (var (ticker, series) in byTicker)
                {
                    var bar = series.FirstOrDefault(b => b.TradeDate == date);
                    if (bar is null)
                    {
                        continue; // this ticker has no bar for this particular date
                    }

                    var topic = $"{config.TopicPrefix}{ticker}";
                    var payload = JsonSerializer.Serialize(new
                    {
                        ticker = bar.Ticker,
                        trade_date = bar.TradeDate.ToString("yyyy-MM-dd"),
                        open = bar.Open,
                        high = bar.High,
                        low = bar.Low,
                        close = bar.Close,
                        volume = bar.Volume,
                        lap,
                    });

                    await producer.ProduceAsync(
                        topic,
                        new Message<string, string> { Key = ticker, Value = payload },
                        cts.Token);

                    producedCount++;
                }

                Console.WriteLine(
                    $"[tick-engine] lap {lap} tick {dayIndex + 1}/{calendar.Count} ({date:yyyy-MM-dd}): " +
                    $"produced {producedCount} bar(s)");

                dayIndex++;
                if (dayIndex >= calendar.Count)
                {
                    dayIndex = 0;
                    lap++;
                    Console.WriteLine(
                        $"[tick-engine] reached end of history, looping back to {calendar[0]:yyyy-MM-dd} (lap {lap})");
                }

                await Task.Delay(config.TickInterval, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        finally
        {
            producer.Flush(TimeSpan.FromSeconds(5));
            Console.WriteLine("[tick-engine] stopped.");
        }

        return 0;
    }

    private static async Task<List<StockBar>> LoadBarsWithRetryAsync(EngineConfig config, CancellationToken token)
    {
        var connectionString =
            $"Host={config.PostgresHost};Port={config.PostgresPort};Database={config.PostgresDb};" +
            $"Username={config.PostgresUser};Password={config.PostgresPassword}";

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var conn = new NpgsqlConnection(connectionString);
                    await conn.OpenAsync(token);

                    var bars = new List<StockBar>();
                    await using (var cmd = new NpgsqlCommand(
                        "SELECT ticker, trade_date, open, high, low, close, volume " +
                        "FROM stock_prices ORDER BY ticker, trade_date;",
                        conn))
                    await using (var reader = await cmd.ExecuteReaderAsync(token))
                    {
                        while (await reader.ReadAsync(token))
                        {
                            bars.Add(new StockBar(
                                Ticker: reader.GetString(0),
                                TradeDate: DateOnly.FromDateTime(reader.GetDateTime(1)),
                                Open: reader.GetDecimal(2),
                                High: reader.GetDecimal(3),
                                Low: reader.GetDecimal(4),
                                Close: reader.GetDecimal(5),
                                Volume: reader.GetInt64(6)));
                        }
                    }

                    if (bars.Count > 0)
                    {
                        return bars;
                    }

                    Console.WriteLine(
                        "[tick-engine] connected to postgres but stock_prices is empty - waiting for data " +
                        "(run scripts/fetch_stock_data.py against this db), retrying in 5s...");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"[tick-engine] postgres not ready yet ({ex.Message}), retrying in 5s...");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled while waiting for postgres/data - fall through to graceful shutdown
        }

        return new List<StockBar>();
    }
}

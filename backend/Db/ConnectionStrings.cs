namespace Backend.Db;

public static class ConnectionStrings
{
    public static string Build(IConfiguration config)
    {
        var host = config["POSTGRES_HOST"] ?? "db";
        var port = config["POSTGRES_PORT"] ?? "5432";
        var db = config["POSTGRES_DB"]
            ?? throw new InvalidOperationException("Missing required environment variable: POSTGRES_DB");
        var user = config["POSTGRES_USER"]
            ?? throw new InvalidOperationException("Missing required environment variable: POSTGRES_USER");
        var password = config["POSTGRES_PASSWORD"]
            ?? throw new InvalidOperationException("Missing required environment variable: POSTGRES_PASSWORD");

        return $"Host={host};Port={port};Database={db};Username={user};Password={password}";
    }
}

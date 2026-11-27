using Npgsql;

namespace Backend.Db;

/// <summary>
/// Fails fast at startup if the app/auth schemas (db/02_app_schema.sql,
/// db/03_auth_schema.sql) haven't been applied, instead of surfacing as
/// confusing errors on the first register/login/trade request.
/// </summary>
public static class SchemaGuard
{
    private static readonly (string Table, string Column, string Script)[] RequiredColumns =
    [
        ("users", "cash", "02_app_schema.sql"),
        ("holdings", "quantity", "02_app_schema.sql"),
        ("trades", "executed_at", "02_app_schema.sql"),
        ("users", "password_hash", "03_auth_schema.sql"),
    ];

    public static async Task EnsureAppliedAsync(IConfiguration config, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConnectionStrings.Build(config));
        await conn.OpenAsync(ct);

        var missing = new List<string>();
        foreach (var (table, column, script) in RequiredColumns)
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @table AND column_name = @column
                """, conn);
            cmd.Parameters.AddWithValue("table", table);
            cmd.Parameters.AddWithValue("column", column);
            if (await cmd.ExecuteScalarAsync(ct) is null)
            {
                missing.Add($"{table}.{column} (db/{script})");
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Database schema is not fully applied. Missing: " + string.Join(", ", missing)
                + ". Run `docker compose up db-migrate` (or apply the listed scripts with psql) and restart the backend.");
        }
    }
}

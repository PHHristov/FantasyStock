using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Backend.Db;
using Backend.Models;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Backend.Services;

/// <summary>Thrown for a client-fixable problem registering (bad input, username taken).</summary>
public sealed class AuthValidationException(string message) : Exception(message);

/// <summary>Thrown for a failed login. Deliberately the same message regardless of
/// whether the username doesn't exist, has no password set yet, or the password
/// is wrong - never gives an attacker a way to tell those apart.</summary>
public sealed class InvalidCredentialsException() : Exception("invalid username or password");

/// <summary>
/// Registration, login, and JWT issuing. Talks to Postgres directly via
/// Npgsql, same as UserService/TradingService - no EF Core, no ASP.NET
/// Core Identity, just enough to hash a password and hand back a token.
/// </summary>
public sealed class AuthService
{
    private const int MinPasswordLength = 6;

    private readonly string _connectionString;
    private readonly string _signingKey;

    public AuthService(IConfiguration config)
    {
        _connectionString = ConnectionStrings.Build(config);
        _signingKey = config["JWT_SIGNING_KEY"]
            ?? throw new InvalidOperationException("Missing required environment variable: JWT_SIGNING_KEY");
    }

    public async Task<AuthResponseDto> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var username = request.Username.Trim();
        if (username.Length == 0)
        {
            throw new AuthValidationException("username must not be empty");
        }

        if (request.Password.Length < MinPasswordLength)
        {
            throw new AuthValidationException($"password must be at least {MinPasswordLength} characters");
        }

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO users (name, cash, password_hash)
            VALUES (@name, 100000.00, @passwordHash)
            RETURNING id
            """, conn);
        cmd.Parameters.AddWithValue("name", username);
        cmd.Parameters.AddWithValue("passwordHash", passwordHash);

        int userId;
        try
        {
            userId = (int)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
            && ex.ConstraintName == "users_name_key")
        {
            // Scoped to the username's unique constraint specifically - a
            // unique violation on a different constraint (e.g. the id
            // sequence colliding with a seeded row) is a real bug and
            // should surface as one, not get mislabeled as "taken".
            throw new AuthValidationException("username already taken");
        }

        return new AuthResponseDto(IssueToken(userId, username), userId, username);
    }

    public async Task<AuthResponseDto> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var username = request.Username.Trim();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT id, password_hash FROM users WHERE name = @name", conn);
        cmd.Parameters.AddWithValue("name", username);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidCredentialsException();
        }

        var userId = reader.GetInt32(0);
        var passwordHash = reader.IsDBNull(1) ? null : reader.GetString(1);

        // No password set yet (e.g. the seeded user1/user2) - same error as a
        // wrong password, so we don't leak which case it is.
        if (passwordHash is null || !BCrypt.Net.BCrypt.Verify(request.Password, passwordHash))
        {
            throw new InvalidCredentialsException();
        }

        return new AuthResponseDto(IssueToken(userId, username), userId, username);
    }

    private string IssueToken(int userId, string username)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, username),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

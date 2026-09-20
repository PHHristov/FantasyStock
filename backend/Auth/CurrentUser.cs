using System.Security.Claims;

namespace Backend.Auth;

/// <summary>
/// Reads the authenticated user's id out of their JWT claims. Every
/// per-user endpoint (/me/..., POST /trades) uses this instead of trusting
/// a route parameter or request body - the token is the only source of
/// truth for "who's asking".
/// </summary>
public static class CurrentUser
{
    public static int GetUserId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (raw is null || !int.TryParse(raw, out var userId))
        {
            throw new InvalidOperationException(
                "authenticated request has no valid user id claim - this shouldn't happen for a request that passed authorization");
        }

        return userId;
    }
}

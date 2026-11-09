using System.Security.Claims;

namespace FinSightIQ.MarketDataService.Api.Middleware;

/// <summary>
/// Extracts trusted identity claims (X-User-Id and X-User-Role) forwarded by the FinSightIQ API Gateway.
/// </summary>
public sealed class UserContextMiddleware
{
    private const string UserIdHeader = "X-User-Id";
    private const string UserRoleHeader = "X-User-Role";

    private readonly RequestDelegate _next;

    public UserContextMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            string? headerUserId = context.Request.Headers[UserIdHeader].FirstOrDefault();
            string? headerRole = context.Request.Headers[UserRoleHeader].FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(headerUserId) && Guid.TryParse(headerUserId, out _))
            {
                List<Claim> claims = new List<Claim>
                {
                    new Claim("sub", headerUserId),
                    new Claim(ClaimTypes.NameIdentifier, headerUserId)
                };

                if (!string.IsNullOrWhiteSpace(headerRole))
                {
                    claims.Add(new Claim("role", headerRole));
                    claims.Add(new Claim(ClaimTypes.Role, headerRole));
                }

                ClaimsIdentity identity = new ClaimsIdentity(claims, "GatewayTrustedIdentity");
                context.User = new ClaimsPrincipal(identity);
            }
        }

        await _next(context);
    }
}


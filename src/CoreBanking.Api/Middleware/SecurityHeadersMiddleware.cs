namespace CoreBanking.Api.Middleware;

/// <summary>
/// Adds baseline security headers to API responses and propagates X-Request-Id.
/// (The frontend gets its stricter CSP from nginx — see frontend/nginx.conf.)
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = context.Request.Headers["X-Request-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64)
            requestId = Guid.NewGuid().ToString("N");

        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-Request-Id"] = requestId;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
            if (context.Request.Path.StartsWithSegments("/api/auth"))
                h["Cache-Control"] = "no-store";
            return Task.CompletedTask;
        });

        await _next(context);
    }
}

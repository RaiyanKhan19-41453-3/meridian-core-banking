using System.Diagnostics;
using CoreBanking.Api.Services;

namespace CoreBanking.Api.Middleware;

/// <summary>
/// Correlation ID + request timing. Every response carries X-Correlation-Id;
/// logs are structured so they can be shipped to a log aggregator as-is.
/// </summary>
public class RequestObservabilityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestObservabilityMiddleware> _logger;
    private readonly MetricsRegistry _metrics;

    public RequestObservabilityMiddleware(RequestDelegate next,
        ILogger<RequestObservabilityMiddleware> logger, MetricsRegistry metrics)
    {
        _next = next;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        const string header = "X-Correlation-Id";
        var correlationId = context.Request.Headers.TryGetValue(header, out var supplied)
                             && !string.IsNullOrWhiteSpace(supplied)
            ? supplied.ToString()
            : Guid.NewGuid().ToString("N");
        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers[header] = correlationId;

        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            var path = context.Request.Path.Value ?? "/";
            // Normalize ids out of the path to keep metric cardinality bounded.
            var normalized = Normalize(path);
            _metrics.RecordRequest(context.Request.Method, normalized,
                context.Response.StatusCode, sw.Elapsed.TotalMilliseconds);
            _logger.LogInformation("HTTP {Method} {Path} -> {Status} in {ElapsedMs}ms ({CorrelationId})",
                context.Request.Method, path, context.Response.StatusCode,
                Math.Round(sw.Elapsed.TotalMilliseconds, 2), correlationId);
        }
    }

    private static string Normalize(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            if (Guid.TryParse(s, out _)) segments[i] = "{id}";
            else if (s.Length == 36 && s.Contains('-')) segments[i] = "{id}";
            else if (s.Length > 6 && s.All(char.IsDigit)) segments[i] = "{id}";
        }
        return "/" + string.Join('/', segments);
    }
}

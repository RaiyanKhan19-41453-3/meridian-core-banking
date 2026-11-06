using CoreBanking.Api.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Api.Middleware;

/// <summary>
/// Maps every unhandled exception to RFC 7807 problem details.
/// BankException → its status/code; anything else → 500 with no internals leaked.
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        var (status, code, message) = exception switch
        {
            BankException bank => (bank.StatusCode, bank.Code, bank.Message),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.")
        };

        if (status >= 500)
            _logger.LogError(exception, "Unhandled exception at {Path}", httpContext.Request.Path);
        else
            _logger.LogInformation("Rejected request at {Path}: {Code}", httpContext.Request.Path, code);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = message,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        if (_env.IsDevelopment() && status >= 500)
            problem.Extensions["debug"] = exception.Message;

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}

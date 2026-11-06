namespace CoreBanking.Api.Services;

/// <summary>Domain-level error with an HTTP status and stable machine-readable code.</summary>
public class BankException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }

    public BankException(string code, string message, int statusCode = 400)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public static BankException BadRequest(string code, string message) => new(code, message, 400);
    public static BankException Unauthorized(string code, string message) => new(code, message, 401);
    public static BankException Forbidden(string code, string message) => new(code, message, 403);
    public static BankException NotFound(string code, string message) => new(code, message, 404);
    public static BankException Conflict(string code, string message) => new(code, message, 409);
    public static BankException Unprocessable(string code, string message) => new(code, message, 422);
}

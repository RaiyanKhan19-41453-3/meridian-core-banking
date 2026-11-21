namespace CoreBanking.Api.Services;

/// <summary>Safe pagination arithmetic — avoids int overflow in Skip.</summary>
public static class Pagination
{
    /// <summary>
    /// Compute (page - 1) * pageSize as a long and clamp to int.MaxValue.
    /// Callers already clamp page ≥ 1 and pageSize ≥ 1; this defends against a
    /// huge page index wrapping the multiplication negative, which would make
    /// Npgsql emit a negative OFFSET and return HTTP 500.
    /// </summary>
    public static int Skip(int page, int pageSize)
    {
        var skip = ((long)Math.Max(page, 1) - 1) * Math.Max(pageSize, 1);
        return skip >= int.MaxValue ? int.MaxValue : (int)skip;
    }
}

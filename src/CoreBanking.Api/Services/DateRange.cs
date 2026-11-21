namespace CoreBanking.Api.Services;

/// <summary>Inclusive-date-range helpers for timestamp filters.</summary>
public static class DateRange
{
    /// <summary>
    /// Convert an inclusive `toDate` into an exclusive UTC upper bound.
    /// `DateTime.MaxValue.Date.AddDays(1)` throws, so clamp at the max date —
    /// `< MaxValue` still includes every realistic timestamp.
    /// </summary>
    public static DateTime? ExclusiveEnd(DateTime? toDate)
    {
        if (!toDate.HasValue) return null;
        var day = toDate.Value.Date;
        var exclusive = day >= DateTime.MaxValue.Date ? DateTime.MaxValue : day.AddDays(1);
        return DateTime.SpecifyKind(exclusive, DateTimeKind.Utc);
    }
}

namespace CoreBanking.Api.Entities;

public enum BatchJobType
{
    DayEnd,
    Capitalization,
    FeeRun,
    Repayment,
    OverdraftAccrual,
    OverdraftCharge
}

public enum BatchRunStatus
{
    Succeeded,
    Partial,
    Failed
}

/// <summary>
/// Record of one day-end batch execution. Unique (job_type, run_date) — a day
/// runs at most once; the endpoint replays the stored result on repeat calls.
/// </summary>
public class BatchRun
{
    public long Id { get; set; }
    public BatchJobType JobType { get; set; }
    public DateTime RunDate { get; set; }
    public BatchRunStatus Status { get; set; }

    public int ItemsProcessed { get; set; }
    public int ItemsFailed { get; set; }
    public string? Errors { get; set; } // JSON array of { accountId, error }
    public string? Summary { get; set; } // JSON: { accruals, capitalized, feesCharged, ... }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

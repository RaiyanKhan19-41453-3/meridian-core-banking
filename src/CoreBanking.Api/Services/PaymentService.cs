using CoreBanking.Api.Data;
using CoreBanking.Api.Dtos;
using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Services;

/// <summary>
/// Beneficiaries and scheduled payments on an internal sandbox rail. Each
/// execution delegates to the ordinary ledger transfer path, so every payment
/// is double-entry, idempotent, limit-checked and AML-screened like any other
/// money movement. No external rail (ACH/SEPA) is connected — that requires a
/// regulated partner and is intentionally out of scope here.
/// </summary>
public class PaymentService
{
    private readonly AppDbContext _db;
    private readonly LedgerService _ledger;
    private readonly AuditService _audit;

    public PaymentService(AppDbContext db, LedgerService ledger, AuditService audit)
    {
        _db = db;
        _ledger = ledger;
        _audit = audit;
    }

    public async Task<BeneficiaryDto> AddBeneficiaryAsync(Guid userId, AddBeneficiaryRequest req, string? ip)
    {
        await KycGate.RequireVerifiedAsync(_db, userId);
        var number = req.AccountNumber.Trim().ToUpperInvariant();
        if (number.StartsWith("SYS-", StringComparison.OrdinalIgnoreCase))
            throw BankException.BadRequest("system_account", "System accounts cannot be saved as beneficiaries.");
        var exists = await _db.Accounts.AnyAsync(a => a.AccountNumber == number);
        if (!exists) throw BankException.NotFound("account_not_found", "Beneficiary account does not exist.");
        var nickname = req.Nickname.Trim();

        // The (UserId, Nickname) unique index stops duplicates, but without a
        // lock the losing callers hit an unhandled 23505 and return 500.
        await using var tx = await _db.Database.BeginTransactionAsync();
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtext({0}), 4)", $"{userId}:{nickname}");
        if (await _db.Beneficiaries.AnyAsync(b => b.UserId == userId && b.Nickname == nickname))
            throw BankException.Conflict("beneficiary_exists", "A beneficiary with this nickname already exists.");

        var account = await _db.Accounts.AsNoTracking()
            .Where(a => a.AccountNumber == number).Select(a => new { a.Currency }).SingleAsync();
        var beneficiary = new Beneficiary
        {
            UserId = userId,
            Nickname = nickname,
            AccountNumber = number,
            BankCode = string.IsNullOrWhiteSpace(req.BankCode) ? "MERIDIAN" : req.BankCode.Trim().ToUpperInvariant(),
            Currency = account.Currency
        };
        _db.Beneficiaries.Add(beneficiary);
        _audit.Write(userId, "payment.beneficiary_added", nameof(Beneficiary), beneficiary.Id.ToString(),
            new { beneficiary.Nickname, beneficiary.AccountNumber }, ip);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return ToDto(beneficiary);
    }

    public async Task<IReadOnlyList<BeneficiaryDto>> ListBeneficiariesAsync(Guid userId) =>
        await _db.Beneficiaries.AsNoTracking().Where(b => b.UserId == userId)
            .OrderBy(b => b.Nickname).Select(b => new BeneficiaryDto(
                b.Id, b.Nickname, b.AccountNumber, b.BankCode, b.Currency, b.CreatedAt)).ToListAsync();

    public async Task RemoveBeneficiaryAsync(Guid userId, Guid id, string? ip)
    {
        var beneficiary = await _db.Beneficiaries.SingleOrDefaultAsync(b => b.Id == id && b.UserId == userId);
        if (beneficiary is null) throw BankException.NotFound("beneficiary_not_found", "Beneficiary does not exist.");
        if (await _db.ScheduledPayments.AnyAsync(s => s.BeneficiaryId == id && s.Status == ScheduledPaymentStatus.Pending))
            throw BankException.Conflict("beneficiary_in_use", "Cancel pending payments before removing this beneficiary.");
        _db.Beneficiaries.Remove(beneficiary);
        _audit.Write(userId, "payment.beneficiary_removed", nameof(Beneficiary), id.ToString(),
            new { beneficiary.Nickname }, ip);
        await _db.SaveChangesAsync();
    }

    public async Task<ScheduledPaymentDto> ScheduleAsync(Guid userId, SchedulePaymentRequest req, string? ip)
    {
        await KycGate.RequireVerifiedAsync(_db, userId);
        var from = await _db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == req.FromAccountId);
        if (from is null) throw BankException.NotFound("account_not_found", "Source account does not exist.");
        if (from.UserId != userId) throw BankException.Forbidden("not_account_owner", "Not your account.");

        string toNumber;
        if (req.BeneficiaryId.HasValue)
        {
            var beneficiary = await _db.Beneficiaries.AsNoTracking()
                .SingleOrDefaultAsync(b => b.Id == req.BeneficiaryId.Value && b.UserId == userId);
            if (beneficiary is null) throw BankException.NotFound("beneficiary_not_found", "Beneficiary does not exist.");
            toNumber = beneficiary.AccountNumber;
        }
        else
        {
            toNumber = req.ToAccountNumber?.Trim().ToUpperInvariant() ?? string.Empty;
        }
        if (toNumber.Length == 0) throw BankException.BadRequest("missing_destination", "Destination account number is required.");
        if (toNumber.StartsWith("SYS-", StringComparison.OrdinalIgnoreCase))
            throw BankException.BadRequest("system_account", "System accounts are settled only by bank processes.");
        if (toNumber == from.AccountNumber) throw BankException.BadRequest("same_account", "Source and destination are the same account.");

        var to = await _db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.AccountNumber == toNumber);
        if (to is null) throw BankException.NotFound("destination_not_found", "Destination account does not exist.");
        if (to.Currency != from.Currency)
            throw BankException.Unprocessable("fx_not_supported", "Cross-currency payments are not supported.");
        if (req.Amount <= 0m) throw BankException.BadRequest("invalid_amount", "Amount must be positive.");
        if (req.ScheduledDate.Date < DateTime.UtcNow.Date)
            throw BankException.BadRequest("past_date", "Scheduled date cannot be in the past.");

        var payment = new ScheduledPayment
        {
            UserId = userId,
            BeneficiaryId = req.BeneficiaryId,
            FromAccountId = from.Id,
            ToAccountNumber = toNumber,
            Amount = req.Amount,
            Currency = from.Currency,
            Description = req.Description,
            Frequency = req.Frequency,
            ScheduledDate = DateTime.SpecifyKind(req.ScheduledDate.Date, DateTimeKind.Utc)
        };
        _db.ScheduledPayments.Add(payment);
        _audit.Write(userId, "payment.scheduled", nameof(ScheduledPayment), payment.Id.ToString(),
            new { payment.ToAccountNumber, payment.Amount, payment.Frequency, date = payment.ScheduledDate }, ip);
        await _db.SaveChangesAsync();
        return ToDto(payment);
    }

    public async Task<IReadOnlyList<ScheduledPaymentDto>> ListScheduledAsync(Guid userId) =>
        await _db.ScheduledPayments.AsNoTracking().Where(s => s.UserId == userId)
            .OrderByDescending(s => s.ScheduledDate).Select(s => new ScheduledPaymentDto(
                s.Id, s.ToAccountNumber, s.Amount, s.Currency, s.Frequency.ToString(),
                s.ScheduledDate, s.Status.ToString(), s.Rail, s.LastTransactionId, s.LastError)).ToListAsync();

    public async Task CancelScheduledAsync(Guid userId, Guid id, string? ip)
    {
        var payment = await _db.ScheduledPayments.SingleOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (payment is null) throw BankException.NotFound("payment_not_found", "Scheduled payment does not exist.");
        if (payment.Status != ScheduledPaymentStatus.Pending)
            throw BankException.Conflict("payment_not_cancellable", "Only pending payments can be cancelled.");
        payment.Status = ScheduledPaymentStatus.Cancelled;
        _audit.Write(userId, "payment.cancelled", nameof(ScheduledPayment), id.ToString(), null, ip);
        await _db.SaveChangesAsync();
    }

    /// <summary>Execute all payments due on or before `date` through the sandbox rail.</summary>
    public async Task<int> ExecuteDueAsync(DateTime date, string? ip)
    {
        date = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var due = await _db.ScheduledPayments
            .Where(s => s.Status == ScheduledPaymentStatus.Pending && s.ScheduledDate <= date)
            .OrderBy(s => s.ScheduledDate).ToListAsync();

        var executed = 0;
        foreach (var payment in due)
        {
            try
            {
                // Atomically claim this occurrence. Cancel can only touch Pending
                // rows, so a claimed (Processing) payment cannot be cancelled
                // out from under an execution in flight.
                var claimed = await _db.ScheduledPayments
                    .Where(s => s.Id == payment.Id && s.Status == ScheduledPaymentStatus.Pending)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ScheduledPaymentStatus.Processing));
                if (claimed != 1) continue;
                // The claim ran outside the change tracker; align the tracked
                // original value so the final status is actually written back.
                _db.Entry(payment).Property(p => p.Status).OriginalValue =
                    ScheduledPaymentStatus.Processing;

                var key = $"PMT-{payment.Id}-{payment.ScheduledDate:yyyyMMdd}";
                var hash = RequestFingerprint.Compute(new { key, payment.Amount, payment.ToAccountNumber });
                var result = await _ledger.TransferAsync(payment.UserId, payment.FromAccountId,
                    payment.ToAccountNumber, payment.Amount, payment.Description, key, hash, ip);

                payment.LastTransactionId = result.Transaction.Id;
                payment.LastExecutedAt = DateTime.UtcNow;
                payment.LastError = null;
                if (payment.Frequency == PaymentFrequency.Once)
                    payment.Status = ScheduledPaymentStatus.Executed;
                else
                {
                    // Release the claim for the next occurrence.
                    payment.Status = ScheduledPaymentStatus.Pending;
                    payment.ScheduledDate = payment.Frequency == PaymentFrequency.Weekly
                        ? DateTime.SpecifyKind(payment.ScheduledDate.Date.AddDays(7), DateTimeKind.Utc)
                        : DateTime.SpecifyKind(payment.ScheduledDate.Date.AddMonths(1), DateTimeKind.Utc);
                }
                _audit.Write(payment.UserId, "payment.executed", nameof(ScheduledPayment), payment.Id.ToString(),
                    new { key, result.Transaction.Reference }, ip);
                executed++;
            }
            catch (Exception ex)
            {
                payment.Status = ScheduledPaymentStatus.Failed;
                payment.LastError = ex.GetBaseException().Message;
                _audit.Write(payment.UserId, "payment.failed", nameof(ScheduledPayment), payment.Id.ToString(),
                    new { payment.LastError }, ip);
            }
        }
        if (due.Count > 0) await _db.SaveChangesAsync();
        return executed;
    }

    private static BeneficiaryDto ToDto(Beneficiary b) => new(b.Id, b.Nickname, b.AccountNumber,
        b.BankCode, b.Currency, b.CreatedAt);

    private static ScheduledPaymentDto ToDto(ScheduledPayment s) => new(s.Id, s.ToAccountNumber,
        s.Amount, s.Currency, s.Frequency.ToString(), s.ScheduledDate, s.Status.ToString(),
        s.Rail, s.LastTransactionId, s.LastError);
}

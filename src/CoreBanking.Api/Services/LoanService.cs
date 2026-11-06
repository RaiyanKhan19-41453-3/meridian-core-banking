using System.Security.Cryptography;
using CoreBanking.Api.Data;
using CoreBanking.Api.Dtos;
using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreBanking.Api.Services;

/// <summary>
/// Loan origination and servicing. Instant deterministic credit decision at
/// application; schedule is generated at approval; funds move only on
/// explicit acceptance. Repayments are collected by the day-end batch.
/// </summary>
public class LoanService
{
    private readonly AppDbContext _db;
    private readonly GlService _gl;
    private readonly AuditService _audit;
    private readonly AmlService _aml;

    public LoanService(AppDbContext db, GlService gl, AuditService audit, AmlService aml)
    {
        _db = db;
        _gl = gl;
        _audit = audit;
        _aml = aml;
    }

    // ---------- application & decision ----------

    public async Task<Loan> ApplyAsync(Guid userId, ApplyLoanRequest req, string? ip)
    {
        await KycGate.RequireVerifiedAsync(_db, userId);

        var product = await _db.LoanProducts.SingleOrDefaultAsync(p =>
            p.Code == req.LoanProductCode.Trim().ToUpperInvariant() && p.Status == LoanProductStatus.Active);
        if (product is null)
            throw BankException.NotFound("product_not_found", "Loan product does not exist or is retired.");

        if (req.Principal < product.MinPrincipal || req.Principal > product.MaxPrincipal)
            throw BankException.BadRequest("principal_out_of_range",
                $"Principal must be between {product.MinPrincipal:F2} and {product.MaxPrincipal:F2} {product.Currency}.");
        if (req.TermMonths < product.MinTermMonths || req.TermMonths > product.MaxTermMonths)
            throw BankException.BadRequest("term_out_of_range",
                $"Term must be between {product.MinTermMonths} and {product.MaxTermMonths} months.");

        var account = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == req.DisbursementAccountId);
        if (account is null) throw BankException.NotFound("account_not_found", "Disbursement account does not exist.");
        if (account.UserId != userId) throw BankException.Forbidden("not_account_owner", "The disbursement account belongs to another customer.");
        if (account.Status != AccountStatus.Active)
            throw BankException.Unprocessable("account_not_active", "Disbursement account is not active.");
        if (account.Currency != product.Currency)
            throw BankException.Unprocessable("currency_mismatch", $"This product is denominated in {product.Currency}.");

        var hasOpenLoan = await _db.Loans.AnyAsync(l =>
            l.UserId == userId && (l.Status == LoanStatus.Approved || l.Status == LoanStatus.Active));
        if (hasOpenLoan)
            throw BankException.Conflict("active_loan_exists",
                "You already have an open loan. Complete or cancel it before applying again.");

        var profile = await _db.CustomerProfiles.SingleAsync(p => p.UserId == userId);
        var score = CreditScoring.Score(profile, req.Principal);
        var approved = score >= product.MinCreditScore;
        var reason = approved
            ? $"Score {score} ≥ {product.MinCreditScore} ({product.Code} minimum)"
            : $"Score {score} below {product.MinCreditScore} ({product.Code} minimum)";

        var loan = new Loan
        {
            Reference = await GenerateReferenceAsync(),
            UserId = userId,
            LoanProductId = product.Id,
            Principal = req.Principal,
            AnnualInterestRate = product.AnnualInterestRate,
            TermMonths = req.TermMonths,
            OriginationFee = product.OriginationFee,
            CreditScore = score,
            DecisionReason = reason,
            Status = approved ? LoanStatus.Approved : LoanStatus.Rejected,
            DisbursementAccountId = account.Id,
            Purpose = (req.Purpose ?? string.Empty).Trim(),
            AppliedAt = DateTime.UtcNow,
            DecidedAt = DateTime.UtcNow
        };

        _db.Loans.Add(loan);

        if (approved)
        {
            // Schedule is generated at approval so the customer can review it before accepting.
            var firstDue = DateTime.UtcNow.Date.AddMonths(1);
            var rows = Amortization.BuildSchedule(loan.Principal, loan.AnnualInterestRate,
                loan.TermMonths, firstDue);
            foreach (var row in rows)
            {
                loan.Installments.Add(new LoanInstallment
                {
                    InstallmentNo = row.InstallmentNo,
                    DueDate = DateTime.SpecifyKind(row.DueDate.Date, DateTimeKind.Utc),
                    Principal = row.Principal,
                    Interest = row.Interest,
                    Total = row.Total,
                    Status = LoanInstallmentStatus.Pending
                });
            }
        }

        _audit.Write(userId, approved ? "loan.approved" : "loan.rejected", nameof(Loan), loan.Id.ToString(),
            new { loan.Reference, product = product.Code, req.Principal, req.TermMonths, score }, ip);
        await _db.SaveChangesAsync();
        return loan;
    }

    // ---------- acceptance & disbursement ----------

    /// <summary>Accept the approved offer and disburse into the disbursement account.</summary>
    public async Task<Loan> DisburseAsync(Guid callerId, bool callerIsAdmin, Guid loanId, string? ip)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();

        var loan = await _db.Loans
            .Include(l => l.LoanProduct)
            .SingleOrDefaultAsync(l => l.Id == loanId);
        if (loan is null) throw BankException.NotFound("loan_not_found", "Loan does not exist.");
        if (loan.UserId != callerId && !callerIsAdmin)
            throw BankException.Forbidden("not_loan_owner", "This loan belongs to another customer.");

        // Idempotent retry: already disbursed → return current state
        if (loan.Status == LoanStatus.Active || loan.Status == LoanStatus.Completed)
            return loan;
        if (loan.Status != LoanStatus.Approved)
            throw BankException.Conflict("loan_not_approved", $"Loan cannot be disbursed in status {loan.Status}.");

        var idempotencyRef = $"LND-{loan.Reference}";
        if (await _db.Transactions.AnyAsync(t => t.Reference == idempotencyRef))
            throw BankException.Conflict("already_disbursed", "This loan was already disbursed.");

        var clearing = await EnsureClearingAccountAsync(loan.LoanProduct.Currency, "LOANCLR");

        var locked = await LockAccountsAsync(new[] { loan.DisbursementAccountId, clearing.Id });
        var byId = locked.ToDictionary(a => a.Id);
        var customer = byId[loan.DisbursementAccountId];
        var sweep = byId[clearing.Id];

        if (customer.Status != AccountStatus.Active)
            throw BankException.Unprocessable("account_not_active", "Disbursement account is not active.");

        var now = DateTime.UtcNow;
        var txEntry = new Transaction
        {
            Reference = idempotencyRef,
            RequestHash = RequestFingerprint.Compute(new { idempotencyRef }),
            Type = TransactionType.LoanDisbursement,
            Status = TransactionStatus.Posted,
            CreatedBy = callerId,
            Description = $"Loan {loan.Reference} disbursement",
            CreatedAt = now
        };

        var entries = new List<LedgerEntry>
        {
            new() { TransactionId = txEntry.Id, AccountId = sweep.Id, Amount = -loan.Principal,
                    BalanceAfter = sweep.Balance - loan.Principal, PostedAt = now },
            new() { TransactionId = txEntry.Id, AccountId = customer.Id, Amount = loan.Principal,
                    BalanceAfter = customer.Balance + loan.Principal, PostedAt = now },
        };

        _db.Transactions.Add(txEntry);
        _db.LedgerEntries.AddRange(entries);
        await _gl.AttachAsync(txEntry, entries, byId, customer.Currency);

        sweep.Balance -= loan.Principal; sweep.Version += 1;
        customer.Balance += loan.Principal; customer.Version += 1;

        // Origination fee — separate transaction so the statement shows both legs cleanly.
        if (loan.OriginationFee > 0m)
        {
            var feeRef = $"ORG-{loan.Reference}";
            var feeIncome = await EnsureClearingAccountAsync(loan.LoanProduct.Currency, "FEECLR");
            var feeLocked = await LockAccountsAsync(new[] { customer.Id, feeIncome.Id });
            var feeById = feeLocked.ToDictionary(a => a.Id);
            var feeCustomer = feeById[customer.Id];
            var feeSweep = feeById[feeIncome.Id];

            var feeTx = new Transaction
            {
                Reference = feeRef,
                RequestHash = RequestFingerprint.Compute(new { feeRef }),
                Type = TransactionType.Fee,
                Status = TransactionStatus.Posted,
                CreatedBy = callerId,
                Description = $"Origination fee for loan {loan.Reference}",
                CreatedAt = DateTime.UtcNow
            };
            var feeEntries = new List<LedgerEntry>
            {
                new() { TransactionId = feeTx.Id, AccountId = feeCustomer.Id, Amount = -loan.OriginationFee,
                        BalanceAfter = feeCustomer.Balance - loan.OriginationFee, PostedAt = DateTime.UtcNow },
                new() { TransactionId = feeTx.Id, AccountId = feeSweep.Id, Amount = loan.OriginationFee,
                        BalanceAfter = feeSweep.Balance + loan.OriginationFee, PostedAt = DateTime.UtcNow },
            };
            _db.Transactions.Add(feeTx);
            _db.LedgerEntries.AddRange(feeEntries);
            await _gl.AttachAsync(feeTx, feeEntries, feeById, feeCustomer.Currency);
            feeCustomer.Balance -= loan.OriginationFee; feeCustomer.Version += 1;
            feeSweep.Balance += loan.OriginationFee; feeSweep.Version += 1;
            customer = feeCustomer; // reflect fee in disbursement response balance
        }

        loan.Status = LoanStatus.Active;
        loan.DisbursedAt = now;
        loan.DisbursementTransactionId = txEntry.Id;

        _audit.Write(callerId, "loan.disbursed", nameof(Loan), loan.Id.ToString(),
            new { loan.Reference, loan.Principal, loan.OriginationFee, byAdmin = callerIsAdmin }, ip);

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        await _aml.EvaluateAsync(txEntry, new List<Account> { sweep, customer }, ip);
        return loan;
    }

    public async Task<Loan> CancelAsync(Guid callerId, bool callerIsAdmin, Guid loanId, string? ip)
    {
        var loan = await _db.Loans.SingleOrDefaultAsync(l => l.Id == loanId);
        if (loan is null) throw BankException.NotFound("loan_not_found", "Loan does not exist.");
        if (loan.UserId != callerId && !callerIsAdmin)
            throw BankException.Forbidden("not_loan_owner", "This loan belongs to another customer.");
        if (loan.Status != LoanStatus.Approved)
            throw BankException.Conflict("loan_not_cancellable", $"Loan cannot be cancelled in status {loan.Status}.");

        loan.Status = LoanStatus.Cancelled;
        _audit.Write(callerId, "loan.cancelled", nameof(Loan), loan.Id.ToString(),
            new { loan.Reference }, ip);
        await _db.SaveChangesAsync();
        return loan;
    }

    // ---------- repayment (called by the day-end batch) ----------

    /// <summary>
    /// Attempt to collect all due installments. One failed installment never
    /// blocks the others; insufficient funds mark the installment Overdue.
    /// Returns (paid, missed).
    /// </summary>
    public async Task<(int Paid, int Missed)> ProcessRepaymentsAsync(DateTime date,
        BatchCounters counters, Guid? adminId, string? ip)
    {
        date = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var due = await _db.LoanInstallments
            .Where(i => (i.Status == LoanInstallmentStatus.Pending || i.Status == LoanInstallmentStatus.Overdue)
                        && i.DueDate <= date)
            .OrderBy(i => i.DueDate).ThenBy(i => i.InstallmentNo)
            .Include(i => i.Loan)
            .ToListAsync();

        var paid = 0;
        var missed = 0;

        foreach (var installment in due)
        {
            var loan = installment.Loan;
            if (loan.Status != LoanStatus.Active) continue;

            var outcome = await RunRepaymentItem(counters, $"repay:{loan.Reference}:{installment.InstallmentNo}", async () =>
            {
                var clearing = await EnsureClearingAccountAsync("USD", "LOANCLR");
                var locked = await LockAccountsAsync(new[] { loan.DisbursementAccountId, clearing.Id });
                var byId = locked.ToDictionary(a => a.Id);
                var customer = byId[loan.DisbursementAccountId];
                var sweep = byId[clearing.Id];

                var reference = $"LRP-{loan.Reference}-{installment.InstallmentNo}";
                if (await _db.Transactions.AnyAsync(t => t.Reference == reference))
                {
                    installment.Status = LoanInstallmentStatus.Paid; // repair from a crashed run
                    installment.PaidAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    return;
                }

                if (customer.Status != AccountStatus.Active)
                    throw new InvalidOperationException("Repayment account is not active.");
                if (customer.Balance < installment.Total)
                    throw new InsufficientFunds(customer.Balance, installment.Total);

                var now = DateTime.UtcNow;
                var txEntry = new Transaction
                {
                    Reference = reference,
                    RequestHash = RequestFingerprint.Compute(new { reference }),
                    Type = TransactionType.LoanRepayment,
                    Status = TransactionStatus.Posted,
                    CreatedBy = adminId ?? loan.UserId,
                    Description = $"Loan {loan.Reference} installment {installment.InstallmentNo}/{loan.TermMonths}",
                    CreatedAt = now
                };

                var entries = new List<LedgerEntry>
                {
                    new() { TransactionId = txEntry.Id, AccountId = customer.Id, Amount = -installment.Total,
                            BalanceAfter = customer.Balance - installment.Total, PostedAt = now },
                    new() { TransactionId = txEntry.Id, AccountId = sweep.Id, Amount = installment.Total,
                            BalanceAfter = sweep.Balance + installment.Total, PostedAt = now },
                };

                _db.Transactions.Add(txEntry);
                _db.LedgerEntries.AddRange(entries);

                // GL: Dr Customer Deposits (total) · Cr Loans Receivable (principal) · Cr Loan Interest Income (interest)
                var usd = 1.00000000m; // USD v1
                await _gl.AttachRawAsync(txEntry.Id, now, txEntry.Description,
                    new[]
                    {
                        (GlCodes.CustomerDeposits, installment.Total * usd),
                        (GlCodes.LoansReceivable, -installment.Principal * usd),
                        (GlCodes.LoanInterestIncome, -installment.Interest * usd),
                    }, date);

                customer.Balance -= installment.Total; customer.Version += 1;
                sweep.Balance += installment.Total; sweep.Version += 1;

                installment.Status = LoanInstallmentStatus.Paid;
                installment.PaidAt = now;
                installment.PaymentTransactionId = txEntry.Id;

                // Loan completes when every installment is paid
                var remaining = await _db.LoanInstallments
                    .CountAsync(i => i.LoanId == loan.Id && i.Status != LoanInstallmentStatus.Paid && i.Id != installment.Id);
                if (remaining == 0)
                {
                    loan.Status = LoanStatus.Completed;
                    loan.CompletedAt = now;
                }

                _audit.Write(null, "loan.installment_paid", nameof(Loan), loan.Id.ToString(),
                    new { loan.Reference, installment.InstallmentNo, installment.Total,
                          principal = installment.Principal, interest = installment.Interest }, ip);

                await _db.SaveChangesAsync();
            });

            switch (outcome)
            {
                case RepayOutcome.Paid:
                    counters.Processed++;
                    paid++;
                    break;
                case RepayOutcome.InsufficientFunds:
                    await MarkOverdueAsync(installment.Id);
                    await ChargeLateFeeAsync(loan, installment, adminId, ip);
                    missed++;
                    break;
                default:
                    missed++;
                    break;
            }
        }

        return (paid, missed);
    }

    private async Task MarkOverdueAsync(long installmentId)
    {
        await _db.LoanInstallments
            .Where(i => i.Id == installmentId && i.Status == LoanInstallmentStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, LoanInstallmentStatus.Overdue));
    }

    public const decimal LateFeeAmount = 25.00m;

    /// <summary>
    /// Flat late fee ($25) per failed installment per calendar month.
    /// Deterministic reference → never charged twice for the same installment in the same month.
    /// Posts even into overdraft — bank levies are never blocked by the debtor's funds.
    /// </summary>
    private async Task ChargeLateFeeAsync(Loan loan, LoanInstallment installment, Guid? adminId, string? ip)
    {
        try
        {
            _ = adminId;
            var period = DateTime.UtcNow.ToString("yyyyMM");
            var reference = $"LFEE-{loan.Reference}-{installment.InstallmentNo}-{period}";
            if (await _db.Transactions.AnyAsync(t => t.Reference == reference)) return;

            await using var tx = await _db.Database.BeginTransactionAsync();

            var clearing = await EnsureClearingAccountAsync("USD", "FEECLR");
            var locked = await LockAccountsAsync(new[] { loan.DisbursementAccountId, clearing.Id });
            var byId = locked.ToDictionary(a => a.Id);
            var customer = byId[loan.DisbursementAccountId];
            var income = byId[clearing.Id];

            var now = DateTime.UtcNow;
            var txEntry = new Transaction
            {
                Reference = reference,
                RequestHash = RequestFingerprint.Compute(new { reference }),
                Type = TransactionType.Fee,
                Status = TransactionStatus.Posted,
                CreatedBy = adminId ?? loan.UserId,
                Description = $"Late payment fee — loan {loan.Reference} installment {installment.InstallmentNo}",
                CreatedAt = now
            };

            var entries = new List<LedgerEntry>
            {
                new() { TransactionId = txEntry.Id, AccountId = customer.Id, Amount = -LateFeeAmount,
                        BalanceAfter = customer.Balance - LateFeeAmount, PostedAt = now },
                new() { TransactionId = txEntry.Id, AccountId = income.Id, Amount = LateFeeAmount,
                        BalanceAfter = income.Balance + LateFeeAmount, PostedAt = now },
            };

            _db.Transactions.Add(txEntry);
            _db.LedgerEntries.AddRange(entries);
            await _gl.AttachAsync(txEntry, entries, byId, customer.Currency);

            customer.Balance -= LateFeeAmount; customer.Version += 1;
            income.Balance += LateFeeAmount; income.Version += 1;

            _audit.Write(null, "loan.late_fee_charged", nameof(Transaction), txEntry.Id.ToString(),
                new { reference, loan = loan.Reference, installment.InstallmentNo, amount = LateFeeAmount }, ip);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            // A late fee must never break repayment processing — swallow and move on.
        }
    }

    public sealed class InsufficientFunds : Exception
    {
        public decimal Available { get; }
        public decimal Required { get; }
        public InsufficientFunds(decimal available, decimal required)
            : base($"Insufficient funds: available {available:F2}, required {required:F2}")
        {
            Available = available;
            Required = required;
        }
    }

    private async Task<RepayOutcome> RunRepaymentItem(BatchCounters counters, string item, Func<Task> work)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            await work();
            await tx.CommitAsync();
            return RepayOutcome.Paid;
        }
        catch (InsufficientFunds)
        {
            await tx.RollbackAsync();
            _db.ChangeTracker.Clear();
            return RepayOutcome.InsufficientFunds;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _db.ChangeTracker.Clear();
            counters.Failed++;
            counters.Errors.Add(new { item, error = ex.GetBaseException().Message });
            return RepayOutcome.Failed;
        }
    }

    private enum RepayOutcome { Paid, InsufficientFunds, Failed }

    // ---------- helpers ----------

    private async Task<string> GenerateReferenceAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = $"LN-{DateTime.UtcNow:yyyy}-{RandomNumberGenerator.GetInt32(100000, 999999)}";
            if (!await _db.Loans.AnyAsync(l => l.Reference == candidate))
                return candidate;
        }
        throw BankException.Conflict("reference_generation_failed", "Could not allocate a loan reference — please retry.");
    }

    private async Task<Account> EnsureClearingAccountAsync(string currency, string suffix)
    {
        var number = $"SYS-{currency}-{suffix}";
        var existing = await _db.Accounts.SingleOrDefaultAsync(a => a.AccountNumber == number);
        if (existing is not null) return existing;

        var systemUserId = await _db.Users.Where(u => u.Email == "system@meridian.internal")
            .Select(u => u.Id).SingleOrDefaultAsync();
        if (systemUserId == Guid.Empty)
            systemUserId = await _db.Users.OrderBy(u => u.CreatedAt).Select(u => u.Id).FirstAsync();

        var account = new Account
        {
            AccountNumber = number,
            UserId = systemUserId,
            Type = AccountType.Current,
            Currency = currency.Trim().ToUpperInvariant(),
            Status = AccountStatus.Active,
            Balance = 0m,
            Version = 0
        };
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();
        return account;
    }

    private async Task<List<Account>> LockAccountsAsync(Guid[] ids)
    {
        var unique = ids.Distinct().OrderBy(i => i).ToArray();
        var parameters = unique.Select((id, i) => new NpgsqlParameter("p" + i, id)).ToArray();
        var where = string.Join(" OR ", unique.Select((_, i) => $"id = @p{i}"));
        var sql = "SELECT id, account_number, user_id, type, currency, status, balance, version, created_at, product_id " +
                  $"FROM accounts WHERE ({where}) ORDER BY id FOR UPDATE";
        var result = await _db.Accounts.FromSqlRaw(sql, parameters).ToListAsync();
        if (result.Count != unique.Length)
            throw BankException.NotFound("account_not_found", "One or more accounts were not found.");
        return result;
    }
}

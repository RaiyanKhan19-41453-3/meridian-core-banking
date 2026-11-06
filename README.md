# Meridian Core Banking

Core banking system: **.NET 10 Web API · PostgreSQL 16 · React (Vite + TS) · Docker Compose**, JWT auth with refresh-token rotation, TOTP 2FA, double-entry ledger.

## Stack

| Layer     | Tech                                        | Container             |
|-----------|---------------------------------------------|-----------------------|
| Frontend  | React 18, Vite, TypeScript, nginx (security headers + `/api` reverse proxy) | `corebanking-frontend` |
| API       | ASP.NET Core (.NET 10), EF Core + Npgsql, JWT bearer, rate limiting, Swagger | `corebanking-api` |
| Database  | PostgreSQL 16 (named volume `pgdata`)       | `corebanking-postgres` |
| Admin UI  | pgAdmin 4 (optional, `--profile tools`)     | `corebanking-pgadmin` |

## Quick start

```bash
cp .env.example .env       # then set real secrets
docker compose up -d --build
```

- Frontend:  http://localhost:8091
- API:       http://localhost:8090  (Swagger in Development: http://localhost:8090/swagger)
- Postgres:  localhost:55432 (user/db `corebanking`, password from `.env`)
- pgAdmin:   `docker compose --profile tools up -d` → http://localhost:5050

Seeded users (Development only):

| User            | Password   | Role    |
|-----------------|------------|---------|
| demo@bank.com   | Demo123!   | Customer |
| admin@bank.com  | Admin123!  | Admin    |

## Security model

**Authentication**
- BCrypt password hashing; dummy-verify on unknown users (no user-enumeration timing signal)
- Password policy: 8–100 chars, ≥1 digit, ≥1 letter (enforced on register + change)
- **Brute-force lockout**: 5 failed attempts → 15-minute lockout (HTTP 423), attempts counter reset on success — enforced before password verification
- **TOTP 2FA (RFC 6238)**: optional per user; SHA1, 30s step, 6 digits, ±1 window verification window. Login without a code → `totp_required`; wrong code → `invalid_totp` (also counts toward lockout). Setup stores a pending secret; enabled only after confirmation. Disabling requires a valid code and revokes all sessions
- Short-lived **JWT access tokens** (15 min, `ClockSkew` 1m)

**Sessions**
- **Opaque refresh tokens** (48 random bytes, base64url); only **SHA-256 hashes** are stored — raw tokens never touch the DB or logs
- **Single-use rotation**: every refresh issues a new pair and revokes the old one (`replaced_by_id` chain)
- **Reuse detection**: presenting a rotated/revoked token revokes **every refresh token of the user** (theft containment) and returns 409 `refresh_token_reuse`
- IP + user-agent recorded per token; `logout` revokes one token, `logout-all` revokes all
- Changing password or disabling 2FA force-revokes all sessions

**Ledger integrity**
- **Double-entry**: every transaction posts ≥2 immutable `ledger_entries` summing to zero; no UPDATE/DELETE — corrections are reversing transactions
- **Atomic multi-leg postings** inside one DB transaction with explicit `SELECT … FOR UPDATE` row locks (deterministic order — no deadlocks)
- **Cached balance with optimistic concurrency token** (`accounts.version`) — a backstop against lost updates; conflicts → 409 with nothing posted
- **Idempotency**: all money endpoints require `Idempotency-Key`; stored with a **SHA-256 request fingerprint** — replay with the same payload returns the original result; reuse with a different payload → 409; concurrent races resolved by the unique index
- Currency must match across a movement; cross-currency rejected with `fx_not_supported`
- Limits: `Limits:MaxSingleTransaction` (250,000) and `Limits:MaxDailyOutflow` (50,000/day/account, outflows only; overdraft draws count toward the limit)
- Frozen/closed accounts cannot move money; closing requires zero balance; reversals (admin-only) may overdraw and are audit-flagged

**API hardening**
- Per-user (authenticated) / per-IP **rate limiting**: `auth` 10 req/min, `money` 30 req/min → HTTP 429
- **RFC 7807 problem details** everywhere — stable machine-readable `code`, `traceId` correlation; internals never leak in production
- Security headers on all API responses (`nosniff`, `X-Frame-Options: DENY`, `no-referrer`, `Permissions-Policy`, `Cache-Control: no-store` on `/api/auth/*`) + strict **CSP** on the frontend via nginx
- CORS restricted to configured origins; JWT issuer/audience/key validated; money columns `numeric(19,4)`; EF parameterized queries (no SQL injection)
- **Append-only `audit_logs`**: every auth event, money movement, admin action, 2FA change — with IP and JSON payload (never passwords/tokens)
- CSV statement export escapes `=+-@` leading cells (spreadsheet formula injection)
- Health endpoint reports DB reachability; compose healthchecks gate startup order

**Rate limits are configuration-driven** — code defaults stay strict (`auth` 10/min, `money` 30/min); the dev compose overrides them via `RateLimiting__*` env vars so the test automation can run suites. In production, leave the defaults or size to your traffic.

**PII at rest** — KYC documents and ID numbers are stored unencrypted at the column level (demo). Before production: enable Postgres TDE / disk encryption and column encryption (pgcrypto or app-level AES) for `kyc_documents.content` and `customer_profiles.id_number`, plus a retention/purge schedule.

**Secrets**: `.env` is dev-only and gitignored; in production use a secret manager and rotate `JWT_KEY` (≥32 random chars). TLS terminates at the reverse proxy/load balancer in production (compose network is plain HTTP by design).

## Features

**Tier 1 — ledger & security**
- Register / login (JWT), refresh-token rotation & reuse detection, TOTP 2FA, lockout, change password, sign out everywhere
- Open Savings/Current accounts in USD, EUR, GBP, KES, NGN
- Deposit / withdraw / transfer (double-entry, idempotent, row-locked)
- Paginated statement (JSON) + CSV export, owner-or-admin scoped
- Admin: freeze / unfreeze / close accounts (zero-balance close), reverse any posted transaction, audit-log viewer
- Frontend tabs: Overview, Move money, History, Security, Admin (role-gated)

**Tier 2 — products, GL & day-end batch**
- **Product catalogue** — `SAV-STD` (Savings, 2.50% p.a.) and `CUR-PER` (Current, $5/month fee when balance < $1,000 at charge time, waived above)
- **Chart of accounts + general ledger** (USD base, FX-normalized via seeded rates): every money posting also writes a balanced GL journal (`gl_journals`/`gl_entries`) **in the same DB transaction** — subledger and GL can never diverge. Internal customer→customer transfers correctly post no GL movement
- **Daily interest accrual** (balance × rate / 365, `numeric(19,4)`) tracked per account (`interest_accruals`), GL: Dr Interest Expense / Cr Interest Payable
- **Month-end capitalization** of accrued interest (payable → customer deposits, GL posted), idempotent via deterministic references (`INT-<yyyymmdd>-<account>`)
- **Monthly fee charging** on the 1st (`FEE-<yyyymm>-<account>` — may overdraw, audited)
- **Day-end batch** (admin-triggered, `POST /api/admin/batch/day-end`): accrual daily, capitalization on month-end, fees on the 1st, OD accrual + charge on the 1st (Tier 5), loan repayments daily (Tier 4). Runs are **idempotent per (job, date)** — replays return the stored result; items commit per account so one bad account never blocks the batch
- **GL reporting** — `GET /api/admin/gl/trial-balance` (debits == credits, verified) and `GET /api/admin/gl/journals` (paginated, per-leg detail with FX rate)
- Admin UI: batch runner with per-run results + history, trial balance viewer, all auditable

**Tier 3 — KYC onboarding & AML monitoring**
- **KYC dossier per user** (`customer_profiles`) — legal name, DOB (18+), nationality, phone, address, ID type/number, employment, income band. Every registration starts **Pending**; demo `demo@bank.com` and `admin@bank.com` are seeded **Verified**
- **KYC gate**: until our compliance approves, a customer cannot perform **any** money movement or open accounts (`kyc_required`, 403) — profile creation and document upload stay open
- **Document vault** (`kyc_documents`) — PDF/JPEG/PNG ≤ 2 MB, SHA-256 hashed, stored as `bytea`; owner + admin only download, every download audited; delete allowed while not verified
- **Watchlist screening** at registration and every profile save (fictional OFAC-style seed) — a full-token name match raises a `WatchlistMatch` alert, sets risk = High, blocks the user until approved
- **AML rule engine** (post-commit, never rolls back a payment):
  1. Single movement ≥ 10,000 (either direction, CTR-threshold style) → `HighValue`
  2. Outbound ≥ 25,000 in 24h → `Velocity24h` (High) — deduplicated per open alert
  3. Outbound ≥ 5 transactions in 1h → `Burst`
- **Admin review UI** — KYC queue (approve/reject with reason, document browser + audited download), AML alert triage (Open/Reviewed/Dismissed + note), plus day-end batch controls and trial balance
- **Rejected profiles resubmit** by editing and re-save back to Pending; edits while Verified are locked

**Tier 4 — Loans & credit**
- **Loan products** — `PLN-PER` (Personal, 18% p.a., $1k–$50k, 3–36mo, $50 fee, min score 650) and `HML-STD` (Home Loan, 5.2% p.a., $50k–$500k, 60–360mo, $500 fee, min score 700), USD v1
- **Instant credit decision** — deterministic 300–850 score (income band + exposure haircut + profile seasoning), decision + reason stored on the loan; threshold per product
- **EMI amortization** — reducing-balance annuity, fixed rate, schedule generated at approval (`loan_installments`); principals sum exactly to principal
- **Disbursement** with explicit customer/admin acceptance — `Dr 1201 Loans Receivable / Cr 2001 Customer Deposits`, origination fee taken as a separate posting; fund flow, GL and subledger commit atomically; idempotent re-accept
- **Day-end batch repayment** — due installments auto-debit from the disbursement account (no overdraft; insufficient → marked `Overdue`, retried daily); posts principal/interest split (`Dr 2001 / Cr 1201 principal / Cr 6101 interest`) and appears in the statement; deterministic reference per installment → re-runs never double-collect; loan auto-completes when all installments are paid
- **Late-payment fee** ($25, Tier 6) charged on every failed installment per calendar month — deterministic reference per installment-month, never twice in a month; posts even into overdraft; audit-logged
- Admin portfolio view with per-loan overdue/outstanding counts

**Tier 5 — Overdraft & revolving credit**
- **`ODL-VAR` facility** on Current accounts — $500–$25,000 limit, 24% p.a. (on overdrawn balance only), min score 650, USD v1; instant decision; one facility per account (re-apply allowed after closure)
- **No new money-movement code** — withdrawals/transfers just see headroom = balance + overdraft limit; over-draw rejected with `insufficient_funds` (422)
- **Daily interest accrual** (balance × 24% / 365, overdrawn days only) — memo'd to `overdraft_accruals` + real journal (`Dr 1010 Interest Receivable / Cr 6101 Loan Interest Income`)
- **Monthly charge** on the 1st (or forced via admin) — posts `Dr 2001 / Cr 1010`, hits the customer's statement; deterministic reference per facility-month → re-runs never double-charge
- Closure any time the balance is ≥ 0; otherwise repay first; KYC-gated apply/close
- Standalone `BatchJobType` + admin endpoints so ops can re-run accrual/charge jobs for any day independently of the day-end batch

**Tier 6 — Secured overdraft & secured pricing**
- **`ODL-SEC`** — same revolving mechanics as ODL-VAR, priced at **12% p.a.** for $500–$100,000, **no credit score required**, secured by a **pledged Savings balance**
- **Collateral lock** — the pledged portion of the Savings account can't be withdrawn or transferred while the facility is Active (`collateral_locked`, 422); closing the facility releases the pledge
- LTV: limit ≤ **90%** of pledged balance (e.g. $15k limit needs $16,667 pledged); insufficient pledge → same deterministic **Rejected** decision style as ODL-VAR (same table, same workflow, zero new code paths for decisioning/journaling — shared posting engine)
- Product selector on the Loans tab: **Unsecured (ODL-VAR)** vs **Secured (ODL-SEC)** with coverage meter; admin portfolio shows pledge per facility

## Database design (double-entry ledger + GL)

All money columns are `numeric(19,4)`; tables/columns are snake_case.

- **users** — email unique, BCrypt hash, role (Customer|Admin), `totp_secret`, `totp_enabled`, `failed_login_attempts`, `lockout_end`
- **refresh_tokens** — `token_hash` unique-indexed, `expires_at`, `revoked_at`, `replaced_by_id`, IP/user-agent
- **products** — financial product catalogue: `SAV-STD` (Savings, 2.50% p.a.), `CUR-PER` (Current, $5/mo fee under $1,000)
- **loan_products** — loan terms (rate, limits, fee, min score) — used for decisioning
- **accounts** — unique `account_number`, `currency char(3)`, status, cached `balance`, `version` concurrency token, `product_id → products`
- **transactions** — `reference` unique (idempotency key), `request_hash` (fingerprint), `original_transaction_id` → self-FK for reversals, type (Deposit|Withdrawal|Transfer|Fee|Interest|Reversal|LoanRepayment)
- **ledger_entries** — append-only legs, signed amount, `balance_after` snapshot
- **gl_accounts** / **gl_journals** / **gl_entries** — USD-base chart of accounts & journals, debit-positive convention; every money posting writes a balanced journal in the same DB transaction
- **customer_profiles** — one per user; kyc_status (Pending|Verified|Rejected), risk_rating, verified_at/by, rejection_reason
- **kyc_documents** — `bytea` content, SHA-256 hash, doc_type; owner/admin-only, downloads audited
- **watchlist_entries** — seeded OFAC-style fictional entries; full-token match screening
- **aml_alerts** — type (`HighValue|Velocity24h|Burst|WatchlistMatch`), severity, status (Open|Reviewed|Dismissed), JSON details
- **loans** — contracts (reference `LN-*`, credit decision, status)
- **loan_installments** — EMI schedule, per-row Paid/Overdue tracking
- **interest_accruals** — per account per day (savings + overdraft), capitalized monthly
- **batch_runs** — day-end/month-end executions, unique (job_type, run_date); claim → work → finalize/replay
- **fx_rates** — USD per unit of foreign currency (EUR/GBP/KES/NGN seeded)
- **overdraft_facilities** — revolving limit per Current account (status, LTV coverage, type ODL-VAR/ODL-SEC)
- **overdraft_accruals** — per account per day interest memo (charged monthly)

Invariants: `sum(ledger_entries.amount) = 0` per transaction; `accounts.balance = Σ legs` — always updated in the same DB transaction as the legs; `sum(gl_entries.amount) = 0` per journal.

## Implementation roadmap

1. ✅ Setup — compose stack, migrations on startup, health checks
2. ✅ Auth — register/login, JWT, lockout, TOTP 2FA, refresh rotation
3. ✅ Accounts — open/list/statement/CSV
4. ✅ Money movement — deposit/withdraw/transfer, double-entry, idempotency, limits, freeze/close/reverse
5. ✅ Audit — all mutations logged; admin viewer
6. ✅ Products + GL + day-end batch — interest accrual/capitalization, fees, trial-balance reporting
7. ✅ Customer KYC onboarding & AML — profiles, document vault, watchlist screening, rule engine, admin review queue
8. ✅ Loan & credit engine — products, instant decisioning, EMI schedules, day-end auto-repayment, overdue handling, admin portfolio
9. ✅ Overdraft & revolving credit — ODL-VAR facility, utilization-aware balance check, daily accrual + monthly charge, forced ops jobs, admin portfolio
10. ✅ Secured overdraft — ODL-SEC against pledged savings (12% p.a., 90% LTV), collateral lock, same decision/journaling/close engines
11. ✅ Loan late-payment fee — $25 flat per failed installment per calendar month, deterministic reference, audited; hits statement & GL like any other fee
12. 🔲 Overdraft variants: term-deposit-backed OD, fee waiver tiers, automatic limit review; penalty rates on overdue loans
13. 🔲 Payments: beneficiaries, scheduling, sandbox rail → then one real rail (ACH/SEPA Instant) — a regulatory project
14. 🔲 Compliance reporting (CTR/SAR filing), tax statements; sanctions-list refresh automation; nostro/vostro reconciliation
15. 🔲 Production readiness — secret manager, TLS, backups, column-level PII encryption, retention policy, Testcontainers suite, CI (dependency scanning, migrations check), observability

## Development

```bash
docker compose up -d --build          # rebuild everything
docker compose logs -f api            # follow API logs
docker compose down -v                # wipe ALL data (fresh seed)

# Six E2E verification suites — run in order on a fresh volume
# (wait for healthy first: curl http://localhost:8090/health → 200)
for f in verify verify-tier2 verify-tier3 verify-tier4 verify-tier5 verify-tier6; do
  /home/shoue/core-banking/scripts/$f.sh
done
```

**Verification (all six suites on a clean volume = 145/145 green)**
| Suite | Checks |
|------|--------|
| `verify.sh` | 25 — auth, 2FA, lockout, refresh rotation, idempotency, freeze/close/reverse, limits |
| `verify-tier2.sh` | 17 — products, GL journals, trial balance, day-end (accrual/capitalization/fees), statement |
| `verify-tier3.sh` | 27 — KYC flow, document vault, watchlist, AML rules, admin review |
| `verify-tier4.sh` | 22 — loans (decisioning, EMI, disburse, batch repayments, overdue + late fee, admin portfolio) |
| `verify-tier5.sh` | 29 — ODL-VAR (approve, draw, accrual, monthly charge, close, AML alert) |
| `verify-tier6.sh` | 25 — ODL-SEC (pledge, collateral lock, close releases) + late fee posting, GL balanced |

Each suite deliberately seeds its own unique user with randomized emails and **per-run unique idempotency-key suffixes** — safe to re-run against the same volume without wiping.


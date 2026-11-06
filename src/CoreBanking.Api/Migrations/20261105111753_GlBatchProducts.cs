using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CoreBanking.Api.Migrations
{
    /// <inheritdoc />
    public partial class GlBatchProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "product_id",
                table: "accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "batch_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    run_date = table.Column<DateTime>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    items_processed = table.Column<int>(type: "integer", nullable: false),
                    items_failed = table.Column<int>(type: "integer", nullable: false),
                    errors = table.Column<string>(type: "text", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_batch_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fx_rates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    from_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    to_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    as_of = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fx_rates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gl_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gl_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gl_journals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    business_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    usd_rate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gl_journals", x => x.id);
                    table.ForeignKey(
                        name: "fk_gl_journals_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "interest_accruals",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accrual_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    capitalized = table.Column<bool>(type: "boolean", nullable: false),
                    capitalized_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    capitalization_transaction_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interest_accruals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    annual_interest_rate = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    monthly_fee = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    fee_waiver_min_balance = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gl_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gl_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gl_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_gl_entries_gl_accounts_gl_account_id",
                        column: x => x.gl_account_id,
                        principalTable: "gl_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gl_entries_gl_journals_journal_id",
                        column: x => x.journal_id,
                        principalTable: "gl_journals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_product_id",
                table: "accounts",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_batch_runs_job_type_run_date",
                table: "batch_runs",
                columns: new[] { "job_type", "run_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fx_rates_from_currency_to_currency_as_of",
                table: "fx_rates",
                columns: new[] { "from_currency", "to_currency", "as_of" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gl_accounts_code",
                table: "gl_accounts",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gl_entries_gl_account_id",
                table: "gl_entries",
                column: "gl_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_gl_entries_journal_id",
                table: "gl_entries",
                column: "journal_id");

            migrationBuilder.CreateIndex(
                name: "ix_gl_journals_business_date",
                table: "gl_journals",
                column: "business_date");

            migrationBuilder.CreateIndex(
                name: "ix_gl_journals_number",
                table: "gl_journals",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gl_journals_transaction_id",
                table: "gl_journals",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_interest_accruals_account_id_accrual_date",
                table: "interest_accruals",
                columns: new[] { "account_id", "accrual_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_interest_accruals_capitalized",
                table: "interest_accruals",
                column: "capitalized");

            migrationBuilder.CreateIndex(
                name: "ix_products_code",
                table: "products",
                column: "code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_accounts_products_product_id",
                table: "accounts",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            // ---------- seed products, GL chart of accounts, FX rates; backfill existing accounts ----------
            migrationBuilder.Sql(@"
                INSERT INTO products (id, code, name, type, annual_interest_rate, monthly_fee, fee_waiver_min_balance, created_at)
                VALUES
                  ('11111111-1111-1111-1111-111111111111', 'SAV-STD', 'Savings Standard', 'Savings', 0.025000, 0.00, 0.00, now()),
                  ('22222222-2222-2222-2222-222222222222', 'CUR-PER', 'Current Personal', 'Current', 0.000000, 5.00, 1000.00, now());
            ");

            migrationBuilder.Sql(@"
                INSERT INTO gl_accounts (id, code, name, type)
                VALUES
                  (gen_random_uuid(), '1001', 'Vault Cash & Settlement', 'Asset'),
                  (gen_random_uuid(), '2001', 'Customer Deposits',       'Liability'),
                  (gen_random_uuid(), '2101', 'Interest Payable',        'Liability'),
                  (gen_random_uuid(), '4001', 'Fee & Commission Income', 'Revenue'),
                  (gen_random_uuid(), '5001', 'Interest Expense',        'Expense');
            ");

            migrationBuilder.Sql(@"
                INSERT INTO fx_rates (from_currency, to_currency, rate, as_of)
                VALUES
                  ('USD', 'USD', 1.00000000, '2026-01-01T00:00:00Z'),
                  ('EUR', 'USD', 1.08000000, '2026-01-01T00:00:00Z'),
                  ('GBP', 'USD', 1.27000000, '2026-01-01T00:00:00Z'),
                  ('KES', 'USD', 0.00770000, '2026-01-01T00:00:00Z'),
                  ('NGN', 'USD', 0.00065000, '2026-01-01T00:00:00Z');
            ");

            migrationBuilder.Sql(@"
                UPDATE accounts SET product_id = '11111111-1111-1111-1111-111111111111'
                WHERE type = 'Savings' AND account_number NOT LIKE 'SYS-%';
                UPDATE accounts SET product_id = '22222222-2222-2222-2222-222222222222'
                WHERE type = 'Current' AND account_number NOT LIKE 'SYS-%';
            ");

        } // end Up

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_accounts_products_product_id",
                table: "accounts");

            migrationBuilder.DropTable(
                name: "batch_runs");

            migrationBuilder.DropTable(
                name: "fx_rates");

            migrationBuilder.DropTable(
                name: "gl_entries");

            migrationBuilder.DropTable(
                name: "interest_accruals");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "gl_accounts");

            migrationBuilder.DropTable(
                name: "gl_journals");

            migrationBuilder.DropIndex(
                name: "ix_accounts_product_id",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "accounts");
        }
    }
}

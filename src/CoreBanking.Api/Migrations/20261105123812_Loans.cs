using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CoreBanking.Api.Migrations
{
    /// <inheritdoc />
    public partial class Loans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "loan_products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    min_principal = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    max_principal = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    min_term_months = table.Column<int>(type: "integer", nullable: false),
                    max_term_months = table.Column<int>(type: "integer", nullable: false),
                    annual_interest_rate = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    origination_fee = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    min_credit_score = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loan_products", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "loans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    annual_interest_rate = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    term_months = table.Column<int>(type: "integer", nullable: false),
                    origination_fee = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    credit_score = table.Column<int>(type: "integer", nullable: false),
                    decision_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    disbursement_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    applied_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    disbursed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    disbursement_transaction_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loans", x => x.id);
                    table.ForeignKey(
                        name: "fk_loans_accounts_disbursement_account_id",
                        column: x => x.disbursement_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_loan_products_loan_product_id",
                        column: x => x.loan_product_id,
                        principalTable: "loan_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loan_installments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_no = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateTime>(type: "date", nullable: false),
                    principal = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    interest = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    paid_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payment_transaction_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loan_installments", x => x.id);
                    table.ForeignKey(
                        name: "fk_loan_installments_loans_loan_id",
                        column: x => x.loan_id,
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_loan_installments_loan_id_installment_no",
                table: "loan_installments",
                columns: new[] { "loan_id", "installment_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_loan_installments_status_due_date",
                table: "loan_installments",
                columns: new[] { "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_loan_products_code",
                table: "loan_products",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_loans_disbursement_account_id",
                table: "loans",
                column: "disbursement_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_loan_product_id",
                table: "loans",
                column: "loan_product_id");

            migrationBuilder.CreateIndex(
                name: "ix_loans_reference",
                table: "loans",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_loans_status",
                table: "loans",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_loans_user_id",
                table: "loans",
                column: "user_id");
            // ---------- new GL accounts: Loans Receivable (asset) + Loan Interest Income (revenue) ----------
            migrationBuilder.Sql(@"
                INSERT INTO gl_accounts (id, code, name, type)
                VALUES
                  (gen_random_uuid(), '1201', 'Loans Receivable',    'Asset'),
                  (gen_random_uuid(), '6101', 'Loan Interest Income', 'Revenue');
            ");

            // ---------- seed loan products (USD v1) ----------
            migrationBuilder.Sql(@"
                INSERT INTO loan_products
                  (id, code, name, status, currency, min_principal, max_principal,
                   min_term_months, max_term_months, annual_interest_rate,
                   origination_fee, min_credit_score, created_at)
                VALUES
                  (gen_random_uuid(), 'PLN-PER', 'Personal Loan', 'Active', 'USD',
                   1000.00, 50000.00, 3, 36, 0.180000, 50.00, 650, now()),
                  (gen_random_uuid(), 'HML-STD', 'Home Loan', 'Active', 'USD',
                   50000.00, 500000.00, 60, 360, 0.052000, 500.00, 700, now());
            ");

        } // end Up

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "loan_installments");

            migrationBuilder.DropTable(
                name: "loans");

            migrationBuilder.DropTable(
                name: "loan_products");
        }
    }
}

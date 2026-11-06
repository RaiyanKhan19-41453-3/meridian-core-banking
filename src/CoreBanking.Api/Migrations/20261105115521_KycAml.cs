using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CoreBanking.Api.Migrations
{
    /// <inheritdoc />
    public partial class KycAml : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "aml_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    severity = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    review_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aml_alerts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customer_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_legal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    date_of_birth = table.Column<DateTime>(type: "date", nullable: false),
                    nationality = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    address_line = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    id_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    id_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    employment_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    annual_income_band = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    kyc_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    risk_rating = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    verified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_profiles", x => x.id);
                    table.ForeignKey(
                        name: "fk_customer_profiles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "watchlist_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_watchlist_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "kyc_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_kyc_documents_customer_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "customer_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_aml_alerts_created_at",
                table: "aml_alerts",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_aml_alerts_status",
                table: "aml_alerts",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_aml_alerts_user_id",
                table: "aml_alerts",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_profiles_user_id",
                table: "customer_profiles",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kyc_documents_profile_id",
                table: "kyc_documents",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_watchlist_entries_full_name",
                table: "watchlist_entries",
                column: "full_name");
            // ---------- seed fictional watchlist (demo OFAC-style entries) ----------
            migrationBuilder.Sql(@"
                INSERT INTO watchlist_entries (full_name, source, created_at)
                VALUES
                  ('Viktor Dragov',   'OFAC SDN (demo)', now()),
                  ('Ernesto Delarge', 'OFAC SDN (demo)', now()),
                  ('Sergei Volkov',   'UN (demo)',       now()),
                  ('Natasha Ronov',   'OFAC SDN (demo)', now()),
                  ('Mikhail Petrov',  'EU (demo)',       now()),
                  ('Aleksandr Kovar', 'UN (demo)',       now());
            ");

        } // end Up

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "aml_alerts");

            migrationBuilder.DropTable(
                name: "kyc_documents");

            migrationBuilder.DropTable(
                name: "watchlist_entries");

            migrationBuilder.DropTable(
                name: "customer_profiles");
        }
    }
}

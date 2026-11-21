using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreBanking.Api.Migrations
{
    /// <inheritdoc />
    public partial class ComplianceFilings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "filed_at",
                table: "aml_alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "filed_by",
                table: "aml_alerts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "filed_reference",
                table: "aml_alerts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "filed_at",
                table: "aml_alerts");

            migrationBuilder.DropColumn(
                name: "filed_by",
                table: "aml_alerts");

            migrationBuilder.DropColumn(
                name: "filed_reference",
                table: "aml_alerts");
        }
    }
}

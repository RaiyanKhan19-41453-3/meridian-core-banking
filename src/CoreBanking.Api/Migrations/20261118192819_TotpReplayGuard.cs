using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreBanking.Api.Migrations
{
    /// <inheritdoc />
    public partial class TotpReplayGuard : Migration
    {
        /// <summary>
        /// Adds the last consumed TOTP code to users. The guard prevents an
        /// observed 6-digit code from being replayed inside its validity window.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_totp_code",
                table: "users",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_totp_code",
                table: "users");
        }
    }
}

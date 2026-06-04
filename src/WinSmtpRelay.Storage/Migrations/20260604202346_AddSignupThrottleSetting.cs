using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSignupThrottleSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SignupMaxAttemptsPerIpPerHour",
                table: "PortalSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "PortalSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "SignupMaxAttemptsPerIpPerHour",
                value: 5);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignupMaxAttemptsPerIpPerHour",
                table: "PortalSettings");
        }
    }
}

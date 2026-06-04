using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddPortalSignupFromAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignupFromAddress",
                table: "PortalSettings",
                type: "TEXT",
                maxLength: 320,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "PortalSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "SignupFromAddress",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignupFromAddress",
                table: "PortalSettings");
        }
    }
}

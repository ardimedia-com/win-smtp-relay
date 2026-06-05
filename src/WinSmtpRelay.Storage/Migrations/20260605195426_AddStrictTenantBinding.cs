using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddStrictTenantBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BindTenantToAllowIpRule",
                table: "EmailAuthSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RejectUnresolvedTenant",
                table: "EmailAuthSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "EmailAuthSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "BindTenantToAllowIpRule", "RejectUnresolvedTenant" },
                values: new object[] { false, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BindTenantToAllowIpRule",
                table: "EmailAuthSettings");

            migrationBuilder.DropColumn(
                name: "RejectUnresolvedTenant",
                table: "EmailAuthSettings");
        }
    }
}

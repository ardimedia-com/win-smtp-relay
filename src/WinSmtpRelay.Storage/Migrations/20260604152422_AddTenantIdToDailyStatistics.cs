using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantIdToDailyStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_DailyStatistics",
                table: "DailyStatistics");

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "DailyStatistics",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddPrimaryKey(
                name: "PK_DailyStatistics",
                table: "DailyStatistics",
                columns: new[] { "TenantId", "Date" });

            migrationBuilder.AddForeignKey(
                name: "FK_DailyStatistics_Tenants_TenantId",
                table: "DailyStatistics",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DailyStatistics_Tenants_TenantId",
                table: "DailyStatistics");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DailyStatistics",
                table: "DailyStatistics");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "DailyStatistics");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DailyStatistics",
                table: "DailyStatistics",
                column: "Date");
        }
    }
}

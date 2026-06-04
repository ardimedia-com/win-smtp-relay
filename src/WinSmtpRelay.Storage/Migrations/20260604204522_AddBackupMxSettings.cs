using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupMxSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupMxSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Domains = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    RetryIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxHoldHours = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupMxSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "BackupMxSettings",
                columns: new[] { "Id", "Domains", "Enabled", "MaxHoldHours", "RetryIntervalMinutes", "UpdatedUtc" },
                values: new object[] { 1, "", false, 168, 15, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupMxSettings");
        }
    }
}

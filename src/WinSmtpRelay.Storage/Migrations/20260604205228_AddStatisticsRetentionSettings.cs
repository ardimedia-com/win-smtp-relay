using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddStatisticsRetentionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StatisticsRetentionSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    AggregationTimeUtc = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatisticsRetentionSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "StatisticsRetentionSettings",
                columns: new[] { "Id", "AggregationTimeUtc", "RetentionDays", "UpdatedUtc" },
                values: new object[] { 1, "00:00", 90, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatisticsRetentionSettings");
        }
    }
}

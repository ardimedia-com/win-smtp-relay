using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ChangeTimestampsToDateTimeOffset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "BackupMxSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: "2026-01-01T00:00:00.0000000Z");

            migrationBuilder.UpdateData(
                table: "DnsSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: "2026-01-01T00:00:00.0000000Z");

            migrationBuilder.UpdateData(
                table: "EmailAuthSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: "2026-01-01T00:00:00.0000000Z");

            migrationBuilder.UpdateData(
                table: "PortalSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: "2026-01-01T00:00:00.0000000Z");

            migrationBuilder.UpdateData(
                table: "RateLimitSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: "2026-01-01T00:00:00.0000000Z");

            migrationBuilder.UpdateData(
                table: "StatisticsRetentionSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: "2026-01-01T00:00:00.0000000Z");

            migrationBuilder.UpdateData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedUtc",
                value: "2026-01-01T00:00:00.0000000Z");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "BackupMxSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.UpdateData(
                table: "DnsSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.UpdateData(
                table: "EmailAuthSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.UpdateData(
                table: "PortalSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.UpdateData(
                table: "RateLimitSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.UpdateData(
                table: "StatisticsRetentionSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "UpdatedUtc",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.UpdateData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedUtc",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));
        }
    }
}

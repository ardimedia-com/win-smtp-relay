using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ReportingScheduleLocalTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DailyTimeLocal",
                table: "ReportingSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastDigestSentUtc",
                table: "ReportingSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelfCheckTimeLocal",
                table: "ReportingSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "ReportingSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DailyTimeLocal",
                table: "ReportingSettings");

            migrationBuilder.DropColumn(
                name: "LastDigestSentUtc",
                table: "ReportingSettings");

            migrationBuilder.DropColumn(
                name: "SelfCheckTimeLocal",
                table: "ReportingSettings");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "ReportingSettings");
        }
    }
}

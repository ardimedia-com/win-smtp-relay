using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDnsSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DnsSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicHostname = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    SendingIpAddresses = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SpfIncludes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    SpfAllQualifier = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DmarcReportEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    DmarcPolicy = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DmarcPercentage = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DnsSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "DnsSettings",
                columns: new[] { "Id", "DmarcPercentage", "DmarcPolicy", "DmarcReportEmail", "PublicHostname", "SendingIpAddresses", "SpfAllQualifier", "SpfIncludes", "UpdatedUtc" },
                values: new object[] { 1, 100, "none", null, null, "", "~all", "", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DnsSettings");
        }
    }
}

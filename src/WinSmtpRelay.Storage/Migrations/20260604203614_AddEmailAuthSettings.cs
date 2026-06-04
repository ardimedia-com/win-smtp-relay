using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailAuthSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailAuthSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SpfEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DmarcEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enforcement = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailAuthSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "EmailAuthSettings",
                columns: new[] { "Id", "DmarcEnabled", "Enforcement", "SpfEnabled", "UpdatedUtc" },
                values: new object[] { 1, false, 0, false, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailAuthSettings");
        }
    }
}

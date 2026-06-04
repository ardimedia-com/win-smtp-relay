using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSenderDomainVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VerificationToken",
                table: "AcceptedSenderDomains",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedUtc",
                table: "AcceptedSenderDomains",
                type: "TEXT",
                nullable: true);

            // Give pre-existing rows a random verification token (16 random bytes as lowercase hex).
            migrationBuilder.Sql(
                "UPDATE \"AcceptedSenderDomains\" SET \"VerificationToken\" = lower(hex(randomblob(16))) WHERE \"VerificationToken\" = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerificationToken",
                table: "AcceptedSenderDomains");

            migrationBuilder.DropColumn(
                name: "VerifiedUtc",
                table: "AcceptedSenderDomains");
        }
    }
}

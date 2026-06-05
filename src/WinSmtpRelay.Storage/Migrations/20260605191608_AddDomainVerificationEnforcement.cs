using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainVerificationEnforcement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireRecipientDomainVerification",
                table: "EmailAuthSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireSenderDomainVerification",
                table: "EmailAuthSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "VerificationToken",
                table: "AcceptedDomains",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VerifiedUtc",
                table: "AcceptedDomains",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "EmailAuthSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "RequireRecipientDomainVerification", "RequireSenderDomainVerification" },
                values: new object[] { false, false });

            // Back-fill ownership tokens for any pre-existing recipient domains (32-char lowercase hex,
            // matching AcceptedDomainService.GenerateToken) so their "Verify" action works.
            migrationBuilder.Sql(
                "UPDATE AcceptedDomains SET VerificationToken = lower(hex(randomblob(16))) WHERE VerificationToken = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireRecipientDomainVerification",
                table: "EmailAuthSettings");

            migrationBuilder.DropColumn(
                name: "RequireSenderDomainVerification",
                table: "EmailAuthSettings");

            migrationBuilder.DropColumn(
                name: "VerificationToken",
                table: "AcceptedDomains");

            migrationBuilder.DropColumn(
                name: "VerifiedUtc",
                table: "AcceptedDomains");
        }
    }
}

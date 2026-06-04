using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class GlobalUniqueAcceptedDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AcceptedSenderDomains_TenantId_Domain",
                table: "AcceptedSenderDomains");

            migrationBuilder.DropIndex(
                name: "IX_AcceptedDomains_TenantId_Domain",
                table: "AcceptedDomains");

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedSenderDomains_Domain",
                table: "AcceptedSenderDomains",
                column: "Domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedSenderDomains_TenantId",
                table: "AcceptedSenderDomains",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedDomains_Domain",
                table: "AcceptedDomains",
                column: "Domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedDomains_TenantId",
                table: "AcceptedDomains",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AcceptedSenderDomains_Domain",
                table: "AcceptedSenderDomains");

            migrationBuilder.DropIndex(
                name: "IX_AcceptedSenderDomains_TenantId",
                table: "AcceptedSenderDomains");

            migrationBuilder.DropIndex(
                name: "IX_AcceptedDomains_Domain",
                table: "AcceptedDomains");

            migrationBuilder.DropIndex(
                name: "IX_AcceptedDomains_TenantId",
                table: "AcceptedDomains");

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedSenderDomains_TenantId_Domain",
                table: "AcceptedSenderDomains",
                columns: new[] { "TenantId", "Domain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedDomains_TenantId_Domain",
                table: "AcceptedDomains",
                columns: new[] { "TenantId", "Domain" },
                unique: true);
        }
    }
}

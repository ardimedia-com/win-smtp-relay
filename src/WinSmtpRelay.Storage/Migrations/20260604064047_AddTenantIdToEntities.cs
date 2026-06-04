using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantIdToEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RelayUsers_Username",
                table: "RelayUsers");

            migrationBuilder.DropIndex(
                name: "IX_DkimDomains_Domain",
                table: "DkimDomains");

            migrationBuilder.DropIndex(
                name: "IX_AcceptedSenderDomains_Domain",
                table: "AcceptedSenderDomains");

            migrationBuilder.DropIndex(
                name: "IX_AcceptedDomains_Domain",
                table: "AcceptedDomains");

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "SenderRewriteEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "SendConnectors",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "RelayUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "ReceiveConnectors",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "QueuedMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "IpAccessRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "HeaderRewriteEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "DomainRoutes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "DkimDomains",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "DeliveryLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "AcceptedSenderDomains",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "AcceptedDomains",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_SenderRewriteEntries_TenantId",
                table: "SenderRewriteEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SendConnectors_TenantId",
                table: "SendConnectors",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RelayUsers_TenantId_Username",
                table: "RelayUsers",
                columns: new[] { "TenantId", "Username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceiveConnectors_TenantId",
                table: "ReceiveConnectors",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_QueuedMessages_TenantId",
                table: "QueuedMessages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_IpAccessRules_TenantId",
                table: "IpAccessRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_HeaderRewriteEntries_TenantId",
                table: "HeaderRewriteEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DomainRoutes_TenantId",
                table: "DomainRoutes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DkimDomains_TenantId_Domain",
                table: "DkimDomains",
                columns: new[] { "TenantId", "Domain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLogs_TenantId",
                table: "DeliveryLogs",
                column: "TenantId");

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

            migrationBuilder.AddForeignKey(
                name: "FK_AcceptedDomains_Tenants_TenantId",
                table: "AcceptedDomains",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AcceptedSenderDomains_Tenants_TenantId",
                table: "AcceptedSenderDomains",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DeliveryLogs_Tenants_TenantId",
                table: "DeliveryLogs",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DkimDomains_Tenants_TenantId",
                table: "DkimDomains",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DomainRoutes_Tenants_TenantId",
                table: "DomainRoutes",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HeaderRewriteEntries_Tenants_TenantId",
                table: "HeaderRewriteEntries",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IpAccessRules_Tenants_TenantId",
                table: "IpAccessRules",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_QueuedMessages_Tenants_TenantId",
                table: "QueuedMessages",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ReceiveConnectors_Tenants_TenantId",
                table: "ReceiveConnectors",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RelayUsers_Tenants_TenantId",
                table: "RelayUsers",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SendConnectors_Tenants_TenantId",
                table: "SendConnectors",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SenderRewriteEntries_Tenants_TenantId",
                table: "SenderRewriteEntries",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AcceptedDomains_Tenants_TenantId",
                table: "AcceptedDomains");

            migrationBuilder.DropForeignKey(
                name: "FK_AcceptedSenderDomains_Tenants_TenantId",
                table: "AcceptedSenderDomains");

            migrationBuilder.DropForeignKey(
                name: "FK_DeliveryLogs_Tenants_TenantId",
                table: "DeliveryLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_DkimDomains_Tenants_TenantId",
                table: "DkimDomains");

            migrationBuilder.DropForeignKey(
                name: "FK_DomainRoutes_Tenants_TenantId",
                table: "DomainRoutes");

            migrationBuilder.DropForeignKey(
                name: "FK_HeaderRewriteEntries_Tenants_TenantId",
                table: "HeaderRewriteEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_IpAccessRules_Tenants_TenantId",
                table: "IpAccessRules");

            migrationBuilder.DropForeignKey(
                name: "FK_QueuedMessages_Tenants_TenantId",
                table: "QueuedMessages");

            migrationBuilder.DropForeignKey(
                name: "FK_ReceiveConnectors_Tenants_TenantId",
                table: "ReceiveConnectors");

            migrationBuilder.DropForeignKey(
                name: "FK_RelayUsers_Tenants_TenantId",
                table: "RelayUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_SendConnectors_Tenants_TenantId",
                table: "SendConnectors");

            migrationBuilder.DropForeignKey(
                name: "FK_SenderRewriteEntries_Tenants_TenantId",
                table: "SenderRewriteEntries");

            migrationBuilder.DropIndex(
                name: "IX_SenderRewriteEntries_TenantId",
                table: "SenderRewriteEntries");

            migrationBuilder.DropIndex(
                name: "IX_SendConnectors_TenantId",
                table: "SendConnectors");

            migrationBuilder.DropIndex(
                name: "IX_RelayUsers_TenantId_Username",
                table: "RelayUsers");

            migrationBuilder.DropIndex(
                name: "IX_ReceiveConnectors_TenantId",
                table: "ReceiveConnectors");

            migrationBuilder.DropIndex(
                name: "IX_QueuedMessages_TenantId",
                table: "QueuedMessages");

            migrationBuilder.DropIndex(
                name: "IX_IpAccessRules_TenantId",
                table: "IpAccessRules");

            migrationBuilder.DropIndex(
                name: "IX_HeaderRewriteEntries_TenantId",
                table: "HeaderRewriteEntries");

            migrationBuilder.DropIndex(
                name: "IX_DomainRoutes_TenantId",
                table: "DomainRoutes");

            migrationBuilder.DropIndex(
                name: "IX_DkimDomains_TenantId_Domain",
                table: "DkimDomains");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryLogs_TenantId",
                table: "DeliveryLogs");

            migrationBuilder.DropIndex(
                name: "IX_AcceptedSenderDomains_TenantId_Domain",
                table: "AcceptedSenderDomains");

            migrationBuilder.DropIndex(
                name: "IX_AcceptedDomains_TenantId_Domain",
                table: "AcceptedDomains");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SenderRewriteEntries");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SendConnectors");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "RelayUsers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ReceiveConnectors");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "QueuedMessages");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "IpAccessRules");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "HeaderRewriteEntries");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "DomainRoutes");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "DkimDomains");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "DeliveryLogs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AcceptedSenderDomains");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AcceptedDomains");

            migrationBuilder.CreateIndex(
                name: "IX_RelayUsers_Username",
                table: "RelayUsers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DkimDomains_Domain",
                table: "DkimDomains",
                column: "Domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedSenderDomains_Domain",
                table: "AcceptedSenderDomains",
                column: "Domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedDomains_Domain",
                table: "AcceptedDomains",
                column: "Domain",
                unique: true);
        }
    }
}

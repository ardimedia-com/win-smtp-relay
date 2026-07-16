using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddRejectedSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RejectedSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    ReplyCode = table.Column<int>(type: "INTEGER", nullable: false),
                    SenderDomain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsTrustedSource = table.Column<bool>(type: "INTEGER", nullable: false),
                    Count = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstSeenUtc = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<string>(type: "TEXT", nullable: false),
                    LastBuffer = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RejectedSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RejectedSubmissions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RejectedSubmissions_ClientIp_Reason_ReplyCode_SenderDomain",
                table: "RejectedSubmissions",
                columns: new[] { "ClientIp", "Reason", "ReplyCode", "SenderDomain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RejectedSubmissions_IsTrustedSource",
                table: "RejectedSubmissions",
                column: "IsTrustedSource");

            migrationBuilder.CreateIndex(
                name: "IX_RejectedSubmissions_LastSeenUtc",
                table: "RejectedSubmissions",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RejectedSubmissions_TenantId",
                table: "RejectedSubmissions",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RejectedSubmissions");
        }
    }
}

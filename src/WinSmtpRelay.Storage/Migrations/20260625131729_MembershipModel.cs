using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class MembershipModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminAuditEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredUtc = table.Column<string>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActorEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdminMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: true),
                    Role = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    GrantedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsBreakGlass = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdminMemberships_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditEvents_ActorUserId",
                table: "AdminAuditEvents",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditEvents_OccurredUtc",
                table: "AdminAuditEvents",
                column: "OccurredUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditEvents_TenantId",
                table: "AdminAuditEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminMemberships_TenantId",
                table: "AdminMemberships",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminMemberships_UserId_TenantId",
                table: "AdminMemberships",
                columns: new[] { "UserId", "TenantId" },
                unique: true);

            // Materialise existing access as memberships BEFORE dropping the old columns, so existing
            // admins keep working: each host admin gets a host membership; each tenant-bound admin gets a
            // tenant membership carrying their Identity tenant role (default TenantAdmin). After this, the
            // consent model governs only NEW grants/tenants. CreatedUtc is the fixed-width UTC ISO string
            // the DateTimeOffset converter expects.
            migrationBuilder.Sql(
                "INSERT INTO \"AdminMemberships\" (\"UserId\", \"TenantId\", \"Role\", \"IsBreakGlass\", \"CreatedUtc\") " +
                "SELECT \"Id\", NULL, 'HostAdmin', 0, '2026-06-25T00:00:00.0000000Z' " +
                "FROM \"AspNetUsers\" WHERE \"IsHostAdmin\" = 1;");
            migrationBuilder.Sql(
                "INSERT INTO \"AdminMemberships\" (\"UserId\", \"TenantId\", \"Role\", \"IsBreakGlass\", \"CreatedUtc\") " +
                "SELECT u.\"Id\", u.\"TenantId\", " +
                "COALESCE((SELECT r.\"Name\" FROM \"AspNetUserRoles\" ur JOIN \"AspNetRoles\" r ON r.\"Id\" = ur.\"RoleId\" " +
                "WHERE ur.\"UserId\" = u.\"Id\" AND r.\"Name\" IN ('TenantAdmin','TenantViewer') LIMIT 1), 'TenantAdmin'), " +
                "0, '2026-06-25T00:00:00.0000000Z' " +
                "FROM \"AspNetUsers\" u WHERE u.\"TenantId\" IS NOT NULL;");

            // Old columns are now redundant — access lives in AdminMemberships.
            migrationBuilder.DropColumn(name: "IsHostAdmin", table: "AspNetUsers");
            migrationBuilder.DropColumn(name: "TenantId", table: "AspNetUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminAuditEvents");

            migrationBuilder.DropTable(
                name: "AdminMemberships");

            migrationBuilder.AddColumn<bool>(
                name: "IsHostAdmin",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);
        }
    }
}

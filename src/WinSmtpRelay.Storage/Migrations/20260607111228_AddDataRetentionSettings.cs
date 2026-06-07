using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDataRetentionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataRetentionSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Profile = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StripBodyOnDelivery = table.Column<bool>(type: "INTEGER", nullable: false),
                    MessageHistoryDays = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveryLogDays = table.Column<int>(type: "INTEGER", nullable: false),
                    SuppressionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataRetentionSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "DataRetentionSettings",
                columns: new[] { "Id", "DeliveryLogDays", "MessageHistoryDays", "Profile", "StripBodyOnDelivery", "SuppressionDays", "UpdatedUtc" },
                values: new object[] { 1, 90, 30, "Standard", true, 0, "2026-01-01T00:00:00.0000000Z" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataRetentionSettings");
        }
    }
}

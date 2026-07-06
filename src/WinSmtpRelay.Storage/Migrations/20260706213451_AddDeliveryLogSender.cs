using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryLogSender : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Sender",
                table: "DeliveryLogs",
                type: "TEXT",
                maxLength: 320,
                nullable: true);

            // Backfill existing journal entries from their queued message where it still exists;
            // entries whose message was already purged keep a NULL sender (shown as "-").
            migrationBuilder.Sql(
                """
                UPDATE DeliveryLogs
                SET Sender = (SELECT q.Sender FROM QueuedMessages q WHERE q.Id = DeliveryLogs.QueuedMessageId)
                WHERE Sender IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sender",
                table: "DeliveryLogs");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SuppressionPolicyAndQueueLease : Migration
    {
        // Frozen with this migration (a later change to SuppressionPolicy must not change what it did).
        private const string PolicyCode = "(\"Detail\" LIKE '%5.7.%' OR \"Detail\" LIKE '%4.7.%')";

        private const string PolicyWordsExceptDenied =
            "(\"Detail\" LIKE '%policy%' OR \"Detail\" LIKE '%block%' OR \"Detail\" LIKE '%blacklist%' OR \"Detail\" LIKE '%spam%' "
            + "OR \"Detail\" LIKE '%reputation%' OR \"Detail\" LIKE '%rbl%' OR \"Detail\" LIKE '%spamhaus%' OR \"Detail\" LIKE '%barracuda%' "
            + "OR \"Detail\" LIKE '%not permitted%' OR \"Detail\" LIKE '%prohibited%' OR \"Detail\" LIKE '%authenticat%' OR \"Detail\" LIKE '%spf%' "
            + "OR \"Detail\" LIKE '%dmarc%' OR \"Detail\" LIKE '%dkim%' OR \"Detail\" LIKE '%rate limit%' OR \"Detail\" LIKE '%too many%' "
            + "OR \"Detail\" LIKE '%quota%' OR \"Detail\" LIKE '%temporarily%')";

        // Exchange Online's unknown-recipient answer: "550 5.4.1 Recipient address rejected: Access denied".
        private const string ExchangeUnknownRecipient =
            "(\"Detail\" LIKE '%5.4.1%' AND \"Detail\" LIKE '%recipient address rejected%' AND \"Detail\" NOT LIKE '%5.7.%' "
            + "AND \"Detail\" NOT LIKE '%4.7.%' AND NOT " + PolicyWordsExceptDenied + ")";

        private const string LiftPredicate =
            "\"Reason\" = 0 AND (" + PolicyCode + " OR " + PolicyWordsExceptDenied + " OR \"Detail\" LIKE '%denied%') "
            + "AND NOT " + ExchangeUnknownRecipient;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueuedMessages_NextRetryUtc",
                table: "QueuedMessages");

            // T1074: existing tenants send bounces (DSN) by default.
            migrationBuilder.AddColumn<bool>(
                name: "DsnEnabled",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowPlaintextAuth",
                table: "SendConnectors",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // H6: NextRetryUtc becomes non-nullable so the queue pick is an indexed range filter. Backfill first: a
            // message that never had a retry scheduled is due since it was created.
            migrationBuilder.Sql(
                "UPDATE \"QueuedMessages\" SET \"NextRetryUtc\" = \"CreatedUtc\" WHERE \"NextRetryUtc\" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "NextRetryUtc",
                table: "QueuedMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: "0001-01-01T00:00:00.0000000Z",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailedRecipients",
                table: "QueuedMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseExpiresUtc",
                table: "QueuedMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "QueuedMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // T1075: hard-bounce suppressions expire after 30 days by default. Only the old default (0 = keep for ever)
            // is changed; a retention an operator chose deliberately stays as it is.
            migrationBuilder.Sql(
                "UPDATE \"DataRetentionSettings\" SET \"SuppressionDays\" = 30 WHERE \"SuppressionDays\" = 0;");

            // T1075 one-time clean-up: lift automatic (hard-bounce) suppressions whose stored reason is a policy or
            // blocklist rejection (any x.7.x code, or the policy wording of SuppressionPolicy.PolicyWords — keep the
            // two lists in step). Exchange Online's answer for an unknown recipient ("5.4.1 Recipient address
            // rejected: Access denied") still qualifies under SuppressionPolicy, so it is NOT lifted although it says
            // "denied" — unless it also carries an x.7.x code or other policy wording. Manual and complaint entries
            // are untouched. The number lifted is recorded as one host-level audit event before the rows are deleted,
            // so the operator can see what happened.
            migrationBuilder.Sql(
                "INSERT INTO \"AdminAuditEvents\" (\"OccurredUtc\", \"Action\", \"Detail\") "
                + "SELECT strftime('%Y-%m-%dT%H:%M:%S', 'now') || '.0000000Z', 'suppression.removed', "
                + "'Lifted ' || COUNT(*) || ' automatic suppression(s) caused by a policy or blocklist rejection, not by an unknown address (T1075)' "
                + "FROM \"SuppressionEntries\" WHERE " + LiftPredicate + " HAVING COUNT(*) > 0;");
            migrationBuilder.Sql("DELETE FROM \"SuppressionEntries\" WHERE " + LiftPredicate + ";");

            migrationBuilder.CreateIndex(
                name: "IX_QueuedMessages_Status_NextRetryUtc",
                table: "QueuedMessages",
                columns: new[] { "Status", "NextRetryUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueuedMessages_Status_NextRetryUtc",
                table: "QueuedMessages");

            migrationBuilder.DropColumn(
                name: "DsnEnabled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AllowPlaintextAuth",
                table: "SendConnectors");

            migrationBuilder.DropColumn(
                name: "FailedRecipients",
                table: "QueuedMessages");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresUtc",
                table: "QueuedMessages");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "QueuedMessages");

            migrationBuilder.AlterColumn<string>(
                name: "NextRetryUtc",
                table: "QueuedMessages",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.CreateIndex(
                name: "IX_QueuedMessages_NextRetryUtc",
                table: "QueuedMessages",
                column: "NextRetryUtc");
        }
    }
}

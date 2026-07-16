using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddRejectedSubmissionIgnored : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IgnoredUtc",
                table: "RejectedSubmissions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RejectedSubmissions_IgnoredUtc",
                table: "RejectedSubmissions",
                column: "IgnoredUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RejectedSubmissions_IgnoredUtc",
                table: "RejectedSubmissions");

            migrationBuilder.DropColumn(
                name: "IgnoredUtc",
                table: "RejectedSubmissions");
        }
    }
}

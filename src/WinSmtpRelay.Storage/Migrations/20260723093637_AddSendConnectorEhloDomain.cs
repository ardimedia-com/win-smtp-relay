using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WinSmtpRelay.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSendConnectorEhloDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EhloDomain",
                table: "SendConnectors",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EhloDomain",
                table: "SendConnectors");
        }
    }
}

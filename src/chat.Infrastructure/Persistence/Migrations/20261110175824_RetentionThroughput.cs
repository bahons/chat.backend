using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetentionThroughput : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RefreshSessions_ExpiresAt_Id",
                table: "RefreshSessions",
                columns: new[] { "ExpiresAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveries_CompletedAt_Id",
                table: "PushDeliveries",
                columns: new[] { "CompletedAt", "Id" },
                filter: "\"CompletedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_CompletedAt_Id",
                table: "OutboxMessages",
                columns: new[] { "CompletedAt", "Id" },
                filter: "\"CompletedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccountMails_CompletedAt_Id",
                table: "AccountMails",
                columns: new[] { "CompletedAt", "Id" },
                filter: "\"CompletedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RefreshSessions_ExpiresAt_Id",
                table: "RefreshSessions");

            migrationBuilder.DropIndex(
                name: "IX_PushDeliveries_CompletedAt_Id",
                table: "PushDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_CompletedAt_Id",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_AccountMails_CompletedAt_Id",
                table: "AccountMails");
        }
    }
}

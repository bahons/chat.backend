using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkerReliability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LeaseId",
                table: "PushDeliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "PushDeliveries",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseId",
                table: "OutboxMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "OutboxMessages",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CleanupAfter",
                table: "Attachments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CleanupAttempts",
                table: "Attachments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ObjectsDeletedAt",
                table: "Attachments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetiredAt",
                table: "Attachments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UploadFamilyId",
                table: "Attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountMails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPasswordReset = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountMails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountMails_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_UploadExpiresAt_Id",
                table: "Attachments",
                columns: new[] { "UploadExpiresAt", "Id" },
                filter: "\"ObjectsDeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccountMails_AvailableAt_Id",
                table: "AccountMails",
                columns: new[] { "AvailableAt", "Id" },
                filter: "\"CompletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccountMails_UserId",
                table: "AccountMails",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountMails");

            migrationBuilder.DropIndex(
                name: "IX_Attachments_UploadExpiresAt_Id",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "LeaseId",
                table: "PushDeliveries");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "PushDeliveries");

            migrationBuilder.DropColumn(
                name: "LeaseId",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "CleanupAfter",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "CleanupAttempts",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "ObjectsDeletedAt",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "RetiredAt",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "UploadFamilyId",
                table: "Attachments");
        }
    }
}

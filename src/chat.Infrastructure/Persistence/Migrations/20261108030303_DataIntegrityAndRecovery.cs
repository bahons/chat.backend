using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataIntegrityAndRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $integrity$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Conversations" c JOIN "Users" u ON u."Id" = c."ClientUserId" WHERE u."Kind" <> 'CLIENT')
                        OR EXISTS (SELECT 1 FROM "Attachments" a JOIN "Messages" m ON m."Id" = a."MessageId" WHERE m."ConversationId" <> a."ConversationId")
                        OR EXISTS (SELECT 1 FROM "MessageReadReceipts" r JOIN "Messages" m ON m."Id" = r."LastMessageId" WHERE m."ConversationId" <> r."ConversationId") THEN
                        RAISE EXCEPTION 'Preflight integrity violations require operator reconciliation before migration.';
                    END IF;
                END
                $integrity$;
                """);

            migrationBuilder.Sql("""
                UPDATE "OutboxMessages" SET "Payload" = jsonb_build_object('messageId', "Payload" #> '{message,id}')
                WHERE "EventName" = 'MessageCreated' AND NOT ("Payload" ? 'messageId');

                UPDATE "OutboxMessages" o
                SET "Payload" = jsonb_build_object('messageId', (
                    SELECT CASE WHEN count(*) = 1 THEN min(m."Id"::text) ELSE NULL END
                    FROM "Messages" m
                    WHERE m."ConversationId" = o."ConversationId" AND m."CreatedAt" = o."OccurredAt"))
                WHERE o."EventName" = 'ConversationUpdated' AND NOT (o."Payload" ? 'messageId');
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Attachments_Messages_MessageId",
                table: "Attachments");

            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_Users_ClientUserId",
                table: "Conversations");

            migrationBuilder.DropForeignKey(
                name: "FK_MessageReadReceipts_Messages_LastMessageId",
                table: "MessageReadReceipts");

            migrationBuilder.DropIndex(
                name: "IX_MessageReadReceipts_LastMessageId",
                table: "MessageReadReceipts");

            migrationBuilder.DropIndex(
                name: "IX_Businesses_Country_RegistrationNumber",
                table: "Businesses");

            migrationBuilder.DropIndex(
                name: "IX_BusinessApplications_BusinessId",
                table: "BusinessApplications");

            migrationBuilder.DropIndex(
                name: "IX_Attachments_ConversationId",
                table: "Attachments");

            migrationBuilder.DropIndex(
                name: "IX_Attachments_MessageId",
                table: "Attachments");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReleasedAt",
                table: "DeviceRegistrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientKind",
                table: "Conversations",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "CLIENT");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Users_Id_Kind",
                table: "Users",
                columns: new[] { "Id", "Kind" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Messages_ConversationId_Id",
                table: "Messages",
                columns: new[] { "ConversationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageReadReceipts_ConversationId_LastMessageId",
                table: "MessageReadReceipts",
                columns: new[] { "ConversationId", "LastMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_BusinessId_CreatedAt_Id",
                table: "Conversations",
                columns: new[] { "BusinessId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ClientUserId_ClientKind",
                table: "Conversations",
                columns: new[] { "ClientUserId", "ClientKind" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ClientUserId_CreatedAt_Id",
                table: "Conversations",
                columns: new[] { "ClientUserId", "CreatedAt", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversations_ClientKind",
                table: "Conversations",
                sql: "\"ClientKind\" = 'CLIENT'");

            migrationBuilder.CreateIndex(
                name: "IX_Businesses_Country_RegistrationNumber",
                table: "Businesses",
                columns: new[] { "Country", "RegistrationNumber" },
                unique: true,
                filter: "\"Status\" IN ('VERIFIED', 'SUSPENDED')");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessApplications_BusinessId",
                table: "BusinessApplications",
                column: "BusinessId",
                unique: true,
                filter: "\"ReviewedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_ConversationId_MessageId",
                table: "Attachments",
                columns: new[] { "ConversationId", "MessageId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Attachments_Messages_ConversationId_MessageId",
                table: "Attachments",
                columns: new[] { "ConversationId", "MessageId" },
                principalTable: "Messages",
                principalColumns: new[] { "ConversationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_Users_ClientUserId_ClientKind",
                table: "Conversations",
                columns: new[] { "ClientUserId", "ClientKind" },
                principalTable: "Users",
                principalColumns: new[] { "Id", "Kind" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageReadReceipts_Messages_ConversationId_LastMessageId",
                table: "MessageReadReceipts",
                columns: new[] { "ConversationId", "LastMessageId" },
                principalTable: "Messages",
                principalColumns: new[] { "ConversationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $rollback$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Businesses" GROUP BY "Country", "RegistrationNumber" HAVING count(*) > 1)
                        OR EXISTS (SELECT 1 FROM "BusinessApplications" GROUP BY "BusinessId" HAVING count(*) > 1)
                        OR EXISTS (SELECT 1 FROM "OutboxMessages" WHERE "CompletedAt" IS NULL
                            AND "EventName" IN ('MessageCreated', 'ConversationUpdated') AND "Payload" ? 'messageId') THEN
                        RAISE EXCEPTION 'Use roll-forward or an approved pre-migration restore; current data cannot be downgraded safely.';
                    END IF;
                END
                $rollback$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Attachments_Messages_ConversationId_MessageId",
                table: "Attachments");

            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_Users_ClientUserId_ClientKind",
                table: "Conversations");

            migrationBuilder.DropForeignKey(
                name: "FK_MessageReadReceipts_Messages_ConversationId_LastMessageId",
                table: "MessageReadReceipts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Users_Id_Kind",
                table: "Users");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Messages_ConversationId_Id",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_MessageReadReceipts_ConversationId_LastMessageId",
                table: "MessageReadReceipts");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_BusinessId_CreatedAt_Id",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_ClientUserId_ClientKind",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_ClientUserId_CreatedAt_Id",
                table: "Conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversations_ClientKind",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Businesses_Country_RegistrationNumber",
                table: "Businesses");

            migrationBuilder.DropIndex(
                name: "IX_BusinessApplications_BusinessId",
                table: "BusinessApplications");

            migrationBuilder.DropIndex(
                name: "IX_Attachments_ConversationId_MessageId",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "ReleasedAt",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "ClientKind",
                table: "Conversations");

            migrationBuilder.CreateIndex(
                name: "IX_MessageReadReceipts_LastMessageId",
                table: "MessageReadReceipts",
                column: "LastMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_Businesses_Country_RegistrationNumber",
                table: "Businesses",
                columns: new[] { "Country", "RegistrationNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessApplications_BusinessId",
                table: "BusinessApplications",
                column: "BusinessId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_ConversationId",
                table: "Attachments",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_MessageId",
                table: "Attachments",
                column: "MessageId");

            migrationBuilder.AddForeignKey(
                name: "FK_Attachments_Messages_MessageId",
                table: "Attachments",
                column: "MessageId",
                principalTable: "Messages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_Users_ClientUserId",
                table: "Conversations",
                column: "ClientUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageReadReceipts_Messages_LastMessageId",
                table: "MessageReadReceipts",
                column: "LastMessageId",
                principalTable: "Messages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

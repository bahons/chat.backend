using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CallsAndTimedMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioCodec",
                table: "Attachments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationMilliseconds",
                table: "Attachments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "Attachments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoCodec",
                table: "Attachments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "Attachments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Conversations_Id_ClientUserId_BusinessId",
                table: "Conversations",
                columns: new[] { "Id", "ClientUserId", "BusinessId" });

            migrationBuilder.CreateTable(
                name: "Calls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatorFamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientCallId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatorIsClient = table.Column<bool>(type: "boolean", nullable: false),
                    OperatorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OperatorFamilyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientFamilyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    AwaitingAnswer = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Calls", x => x.Id);
                    table.CheckConstraint("CK_Calls_Kind", "\"Kind\" IN ('AUDIO','VIDEO')");
                    table.CheckConstraint("CK_Calls_Participants", "(\"InitiatorIsClient\" AND \"InitiatorUserId\" = \"ClientUserId\") OR (NOT \"InitiatorIsClient\" AND \"OperatorUserId\" IS NOT NULL AND \"InitiatorUserId\" = \"OperatorUserId\")");
                    table.CheckConstraint("CK_Calls_SelectedOperator", "\"State\" NOT IN ('CONNECTING','ACTIVE') OR (\"OperatorUserId\" IS NOT NULL AND \"OperatorFamilyId\" IS NOT NULL AND \"ClientFamilyId\" IS NOT NULL)");
                    table.CheckConstraint("CK_Calls_State", "\"State\" IN ('RINGING','CONNECTING','ACTIVE','ENDED','MISSED','REJECTED')");
                    table.CheckConstraint("CK_Calls_Terminal", "(\"State\" IN ('RINGING','CONNECTING','ACTIVE') AND \"EndedAt\" IS NULL) OR (\"State\" IN ('ENDED','MISSED','REJECTED') AND \"EndedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Calls_Conversations_ConversationId_ClientUserId_BusinessId",
                        columns: x => new { x.ConversationId, x.ClientUserId, x.BusinessId },
                        principalTable: "Conversations",
                        principalColumns: new[] { "Id", "ClientUserId", "BusinessId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Calls_Users_InitiatorUserId",
                        column: x => x.InitiatorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Calls_Users_OperatorUserId",
                        column: x => x.OperatorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Attachments_MediaDuration",
                table: "Attachments",
                sql: "\"DurationMilliseconds\" IS NULL OR (\"DurationMilliseconds\" > 0 AND \"DurationMilliseconds\" <= 300000)");

            migrationBuilder.CreateIndex(
                name: "IX_Calls_ConversationId",
                table: "Calls",
                column: "ConversationId",
                unique: true,
                filter: "\"State\" IN ('RINGING','CONNECTING','ACTIVE')");

            migrationBuilder.CreateIndex(
                name: "IX_Calls_ConversationId_ClientUserId_BusinessId",
                table: "Calls",
                columns: new[] { "ConversationId", "ClientUserId", "BusinessId" });

            migrationBuilder.CreateIndex(
                name: "IX_Calls_ExpiresAt_Id",
                table: "Calls",
                columns: new[] { "ExpiresAt", "Id" },
                filter: "\"State\" IN ('RINGING','CONNECTING','ACTIVE')");

            migrationBuilder.CreateIndex(
                name: "IX_Calls_InitiatorUserId_ClientCallId",
                table: "Calls",
                columns: new[] { "InitiatorUserId", "ClientCallId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Calls_OperatorUserId",
                table: "Calls",
                column: "OperatorUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Calls");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Conversations_Id_ClientUserId_BusinessId",
                table: "Conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Attachments_MediaDuration",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "AudioCodec",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "DurationMilliseconds",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "VideoCodec",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "Attachments");
        }
    }
}

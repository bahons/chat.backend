using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TeamAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Conversations_Id_BusinessId",
                table: "Conversations",
                columns: new[] { "Id", "BusinessId" });

            migrationBuilder.CreateTable(
                name: "ConversationAssignments",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationAssignments", x => x.ConversationId);
                    table.CheckConstraint("CK_ConversationAssignments_Revision", "\"Revision\" > 0");
                    table.ForeignKey(
                        name: "FK_ConversationAssignments_BusinessMemberships_UserId_Business~",
                        columns: x => new { x.UserId, x.BusinessId },
                        principalTable: "BusinessMemberships",
                        principalColumns: new[] { "UserId", "BusinessId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationAssignments_Conversations_ConversationId_Busine~",
                        columns: x => new { x.ConversationId, x.BusinessId },
                        principalTable: "Conversations",
                        principalColumns: new[] { "Id", "BusinessId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationAssignments_Users_UpdatedBy",
                        column: x => x.UpdatedBy,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamInvitations", x => x.Id);
                    table.CheckConstraint("CK_TeamInvitations_State", "\"Role\" IN ('OWNER','ADMIN','AGENT') AND \"ExpiresAt\" > \"CreatedAt\" AND NOT (\"AcceptedAt\" IS NOT NULL AND \"RevokedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_TeamInvitations_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeamInvitations_Users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_AuthorSide_CreatedAt",
                table: "Messages",
                columns: new[] { "ConversationId", "AuthorSide", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssignments_BusinessId_UserId",
                table: "ConversationAssignments",
                columns: new[] { "BusinessId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssignments_ConversationId_BusinessId",
                table: "ConversationAssignments",
                columns: new[] { "ConversationId", "BusinessId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssignments_UpdatedBy",
                table: "ConversationAssignments",
                column: "UpdatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssignments_UserId_BusinessId",
                table: "ConversationAssignments",
                columns: new[] { "UserId", "BusinessId" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_BusinessId_CreatedAt",
                table: "TeamInvitations",
                columns: new[] { "BusinessId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_CreatedBy",
                table: "TeamInvitations",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_TokenHash",
                table: "TeamInvitations",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationAssignments");

            migrationBuilder.DropTable(
                name: "TeamInvitations");

            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId_AuthorSide_CreatedAt",
                table: "Messages");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Conversations_Id_BusinessId",
                table: "Conversations");
        }
    }
}

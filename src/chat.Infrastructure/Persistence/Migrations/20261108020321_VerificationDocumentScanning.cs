using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VerificationDocumentScanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScanAfter",
                table: "VerificationDocuments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScanStatus",
                table: "VerificationDocuments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "PENDING");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationDocuments_ScanAfter_Id",
                table: "VerificationDocuments",
                columns: new[] { "ScanAfter", "Id" },
                filter: "\"ScanStatus\" = 'PENDING'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VerificationDocuments_ScanStatus",
                table: "VerificationDocuments",
                sql: "\"ScanStatus\" IN ('PENDING', 'READY', 'QUARANTINED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VerificationDocuments_ScanAfter_Id",
                table: "VerificationDocuments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_VerificationDocuments_ScanStatus",
                table: "VerificationDocuments");

            migrationBuilder.DropColumn(
                name: "ScanAfter",
                table: "VerificationDocuments");

            migrationBuilder.DropColumn(
                name: "ScanStatus",
                table: "VerificationDocuments");
        }
    }
}

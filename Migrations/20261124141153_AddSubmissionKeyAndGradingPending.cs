using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    /// <inheritdoc />
    public partial class AddSubmissionKeyAndGradingPending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GradingPending",
                table: "StudentResults",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SubmissionKey",
                table: "StudentResults",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudentResults_StudentId_SubmissionKey",
                table: "StudentResults",
                columns: new[] { "StudentId", "SubmissionKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StudentResults_StudentId_SubmissionKey",
                table: "StudentResults");

            migrationBuilder.DropColumn(
                name: "GradingPending",
                table: "StudentResults");

            migrationBuilder.DropColumn(
                name: "SubmissionKey",
                table: "StudentResults");
        }
    }
}

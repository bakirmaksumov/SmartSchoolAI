using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    /// <inheritdoc />
    public partial class AddAssignmentAudienceAndLateJoin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every assignment that exists today keeps the manual way of giving out work: hand-picked
            // recipients and nothing for students who join later. "Whole class" is never inferred from the
            // current recipient list matching the class — that is no proof the teacher wants newcomers to get it.
            // This migration adds columns and an empty table only; it creates no assignments and no requests.
            migrationBuilder.AddColumn<string>(
                name: "Audience",
                table: "Assignments",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "SelectedStudents");

            migrationBuilder.AddColumn<string>(
                name: "LateJoinPolicy",
                table: "Assignments",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.CreateTable(
                name: "LateJoinRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StudentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecidedById = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LateJoinRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LateJoinRequests_Assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "Assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LateJoinRequests_Users_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LateJoinRequests_AssignmentId_StudentId",
                table: "LateJoinRequests",
                columns: new[] { "AssignmentId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LateJoinRequests_Status",
                table: "LateJoinRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_LateJoinRequests_StudentId",
                table: "LateJoinRequests",
                column: "StudentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LateJoinRequests");

            migrationBuilder.DropColumn(
                name: "Audience",
                table: "Assignments");

            migrationBuilder.DropColumn(
                name: "LateJoinPolicy",
                table: "Assignments");
        }
    }
}

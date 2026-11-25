using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    /// <inheritdoc />
    public partial class AddAttemptActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttemptActivities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssignmentId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    StudentId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    QuestionIndex = table.Column<short>(type: "INTEGER", nullable: true),
                    Kind = table.Column<byte>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<byte>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Flags = table.Column<byte>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttemptActivities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AttemptActivitySummaries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssignmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StudentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Copies = table.Column<int>(type: "INTEGER", nullable: false),
                    Pastes = table.Column<int>(type: "INTEGER", nullable: false),
                    PastesBlocked = table.Column<int>(type: "INTEGER", nullable: false),
                    HintsRequested = table.Column<int>(type: "INTEGER", nullable: false),
                    HintsReceived = table.Column<int>(type: "INTEGER", nullable: false),
                    TutorOpened = table.Column<int>(type: "INTEGER", nullable: false),
                    TutorRequests = table.Column<int>(type: "INTEGER", nullable: false),
                    PurgedBefore = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttemptActivitySummaries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttemptActivities_AssignmentId_StudentId_AttemptNumber_OccurredAt",
                table: "AttemptActivities",
                columns: new[] { "AssignmentId", "StudentId", "AttemptNumber", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AttemptActivities_StudentId_ClientId",
                table: "AttemptActivities",
                columns: new[] { "StudentId", "ClientId" },
                unique: true,
                filter: "ClientId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AttemptActivitySummaries_AssignmentId_StudentId_AttemptNumber",
                table: "AttemptActivitySummaries",
                columns: new[] { "AssignmentId", "StudentId", "AttemptNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttemptActivities");

            migrationBuilder.DropTable(
                name: "AttemptActivitySummaries");
        }
    }
}

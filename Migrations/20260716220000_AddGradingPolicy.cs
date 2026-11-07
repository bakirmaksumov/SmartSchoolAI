using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSchoolAI.Data;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260716220000_AddGradingPolicy")]
    public partial class AddGradingPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GradingType",
                table: "Assignments",
                type: "TEXT",
                nullable: false,
                defaultValue: "Percentage");

            migrationBuilder.AddColumn<int>(
                name: "PassThreshold",
                table: "Assignments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 70);          // matches the previously hard-coded pass mark

            migrationBuilder.AddColumn<bool>(
                name: "AntiCopyPaste",
                table: "Assignments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Unlimited attempts used to be implied by the assignment type. It is now
            // MaxAttempts = 0, so existing classroom practice keeps behaving the same.
            // Type is persisted as a string (HasConversion<string>), hence the literal.
            migrationBuilder.Sql(
                "UPDATE \"Assignments\" SET \"MaxAttempts\" = 0 WHERE \"Type\" = 'ClassroomPractice';");

            // Drop duplicate answers before the unique index can be created, keeping
            // the most recently inserted row for each (result, question).
            migrationBuilder.Sql("""
                DELETE FROM "StudentAnswers"
                WHERE rowid NOT IN (
                    SELECT MAX(rowid) FROM "StudentAnswers"
                    GROUP BY "StudentResultId", "QuestionIndex"
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_StudentAnswers_StudentResultId_QuestionIndex",
                table: "StudentAnswers",
                columns: new[] { "StudentResultId", "QuestionIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StudentAnswers_StudentResultId_QuestionIndex",
                table: "StudentAnswers");

            migrationBuilder.DropColumn(name: "GradingType",   table: "Assignments");
            migrationBuilder.DropColumn(name: "PassThreshold", table: "Assignments");
            migrationBuilder.DropColumn(name: "AntiCopyPaste", table: "Assignments");
        }
    }
}

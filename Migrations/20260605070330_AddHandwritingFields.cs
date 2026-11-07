using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    /// <inheritdoc />
    public partial class AddHandwritingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiSummary",
                table: "StudentAnswers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImagePath",
                table: "StudentAnswers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuestionIndex",
                table: "StepResults",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiSummary",
                table: "StudentAnswers");

            migrationBuilder.DropColumn(
                name: "ImagePath",
                table: "StudentAnswers");

            migrationBuilder.DropColumn(
                name: "QuestionIndex",
                table: "StepResults");
        }
    }
}

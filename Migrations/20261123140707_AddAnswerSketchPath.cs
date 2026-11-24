using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    /// <inheritdoc />
    public partial class AddAnswerSketchPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SketchPath",
                table: "StudentAnswers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SketchPath",
                table: "StudentAnswers");
        }
    }
}

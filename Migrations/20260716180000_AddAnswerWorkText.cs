using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSchoolAI.Data;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260716180000_AddAnswerWorkText")]
    public partial class AddAnswerWorkText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkText",
                table: "StudentAnswers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkText",
                table: "StudentAnswers");
        }
    }
}

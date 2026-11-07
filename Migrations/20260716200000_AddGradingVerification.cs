using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSchoolAI.Data;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260716200000_AddGradingVerification")]
    public partial class AddGradingVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NeedsReview",
                table: "StudentAnswers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "StudentAnswers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AiConfidence",
                table: "StudentAnswers",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "NeedsReview",  table: "StudentAnswers");
            migrationBuilder.DropColumn(name: "ReviewNote",   table: "StudentAnswers");
            migrationBuilder.DropColumn(name: "AiConfidence", table: "StudentAnswers");
        }
    }
}

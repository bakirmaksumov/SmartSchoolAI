using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SmartSchoolAI.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionsAndSkills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Questions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Difficulty = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    GradeLevel = table.Column<int>(type: "INTEGER", nullable: true),
                    AnswerKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Explanation = table.Column<string>(type: "TEXT", nullable: true),
                    Options = table.Column<string>(type: "TEXT", nullable: false),
                    SkillIds = table.Column<string>(type: "TEXT", nullable: false),
                    IsPublic = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Questions", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Skills",
                columns: new[] { "Id", "Code", "GradeLevel", "IsActive", "Level", "Name", "ParentId", "Subject" },
                values: new object[,]
                {
                    { new Guid("a0000000-0000-0000-0000-000000000001"), "MATH", null, true, 0, "Mathematics", null, "Mathematics" },
                    { new Guid("a0000000-0000-0000-0000-000000000002"), "MATH.5", 5, true, 1, "Grade 5", new Guid("a0000000-0000-0000-0000-000000000001"), "Mathematics" },
                    { new Guid("a0000000-0000-0000-0000-000000000003"), "MATH.5.NBT", 5, true, 2, "Number and Operations", new Guid("a0000000-0000-0000-0000-000000000002"), "Mathematics" },
                    { new Guid("a0000000-0000-0000-0000-000000000004"), "MATH.5.NBT.A", 5, true, 3, "Place Value System", new Guid("a0000000-0000-0000-0000-000000000003"), "Mathematics" },
                    { new Guid("a0000000-0000-0000-0000-000000000005"), "MATH.5.NBT.B", 5, true, 3, "Multi-Digit Arithmetic", new Guid("a0000000-0000-0000-0000-000000000003"), "Mathematics" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Questions_CreatedByEmail",
                table: "Questions",
                column: "CreatedByEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_Subject",
                table: "Questions",
                column: "Subject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Questions");

            migrationBuilder.DeleteData(
                table: "Skills",
                keyColumn: "Id",
                keyValue: new Guid("a0000000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "Skills",
                keyColumn: "Id",
                keyValue: new Guid("a0000000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "Skills",
                keyColumn: "Id",
                keyValue: new Guid("a0000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "Skills",
                keyColumn: "Id",
                keyValue: new Guid("a0000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "Skills",
                keyColumn: "Id",
                keyValue: new Guid("a0000000-0000-0000-0000-000000000001"));
        }
    }
}

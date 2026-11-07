using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    /// <inheritdoc />
    public partial class AddTeacherGrading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BonusPoints",
                table: "StudentResults",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "TeacherReviewedAt",
                table: "StudentResults",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "PointsAwarded",
                table: "StudentAnswers",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeacherComment",
                table: "StudentAnswers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TeacherOverridden",
                table: "StudentAnswers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BonusPoints",
                table: "StudentResults");

            migrationBuilder.DropColumn(
                name: "TeacherReviewedAt",
                table: "StudentResults");

            migrationBuilder.DropColumn(
                name: "TeacherComment",
                table: "StudentAnswers");

            migrationBuilder.DropColumn(
                name: "TeacherOverridden",
                table: "StudentAnswers");

            migrationBuilder.AlterColumn<int>(
                name: "PointsAwarded",
                table: "StudentAnswers",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    /// <inheritdoc />
    public partial class AddAiStageCacheAndCallLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GradingJson",
                table: "StudentAnswers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GradingKey",
                table: "StudentAnswers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecognitionKey",
                table: "StudentAnswers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiCallLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CacheKey = table.Column<string>(type: "TEXT", nullable: true),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    Cost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 8, nullable: true),
                    GenerationId = table.Column<string>(type: "TEXT", nullable: true),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Failure = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    StudentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AssignmentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResultId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Questions = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiCallLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiStageResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CacheKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    PromptVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiStageResults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiCallLogs_CacheKey",
                table: "AiCallLogs",
                column: "CacheKey");

            migrationBuilder.CreateIndex(
                name: "IX_AiCallLogs_ResultId",
                table: "AiCallLogs",
                column: "ResultId");

            migrationBuilder.CreateIndex(
                name: "IX_AiStageResults_CacheKey",
                table: "AiStageResults",
                column: "CacheKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiCallLogs");

            migrationBuilder.DropTable(
                name: "AiStageResults");

            migrationBuilder.DropColumn(
                name: "GradingJson",
                table: "StudentAnswers");

            migrationBuilder.DropColumn(
                name: "GradingKey",
                table: "StudentAnswers");

            migrationBuilder.DropColumn(
                name: "RecognitionKey",
                table: "StudentAnswers");
        }
    }
}

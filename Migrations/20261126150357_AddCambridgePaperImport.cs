using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    /// <inheritdoc />
    public partial class AddCambridgePaperImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImagePath",
                table: "Questions",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarkScheme",
                table: "Questions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Marks",
                table: "Questions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceGroupKey",
                table: "Questions",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "Questions",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceLabel",
                table: "Questions",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stem",
                table: "Questions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CambridgeImports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    LastCompletedStep = table.Column<int>(type: "INTEGER", nullable: false),
                    SyllabusCode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Component = table.Column<int>(type: "INTEGER", nullable: true),
                    Variant = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    Session = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    DetailsChecked = table.Column<bool>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SyllabusVersionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PaperFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    PaperSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PaperPages = table.Column<int>(type: "INTEGER", nullable: false),
                    PaperStage = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PaperError = table.Column<string>(type: "TEXT", nullable: true),
                    PaperJson = table.Column<string>(type: "TEXT", nullable: true),
                    MarkSchemeFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: true),
                    MarkSchemeSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MarkSchemePages = table.Column<int>(type: "INTEGER", nullable: false),
                    MarkSchemeStage = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    MarkSchemeError = table.Column<string>(type: "TEXT", nullable: true),
                    MarkSchemeJson = table.Column<string>(type: "TEXT", nullable: true),
                    MarkSchemeDetailsJson = table.Column<string>(type: "TEXT", nullable: true),
                    MarkSchemeDetailsConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    StageStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CambridgeImports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CambridgeImportParts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QuestionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Stem = table.Column<string>(type: "TEXT", nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Marks = table.Column<int>(type: "INTEGER", nullable: true),
                    PaperPage = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    NeedsDrawing = table.Column<bool>(type: "INTEGER", nullable: false),
                    FigureKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    FigureBox = table.Column<string>(type: "TEXT", nullable: true),
                    FigureDescription = table.Column<string>(type: "TEXT", nullable: true),
                    SyllabusTopicId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TopicSource = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AnswerKey = table.Column<string>(type: "TEXT", nullable: true),
                    Criteria = table.Column<string>(type: "TEXT", nullable: true),
                    SchemeMarks = table.Column<int>(type: "INTEGER", nullable: true),
                    SchemePages = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    EditedByTeacher = table.Column<bool>(type: "INTEGER", nullable: false),
                    PendingReplacement = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ReviewReason = table.Column<string>(type: "TEXT", nullable: true),
                    SavedQuestionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CambridgeImportParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CambridgeImportParts_CambridgeImports_ImportId",
                        column: x => x.ImportId,
                        principalTable: "CambridgeImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CambridgeImportParts_SyllabusTopics_SyllabusTopicId",
                        column: x => x.SyllabusTopicId,
                        principalTable: "SyllabusTopics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Questions_CreatedByEmail_SourceKey",
                table: "Questions",
                columns: new[] { "CreatedByEmail", "SourceKey" },
                unique: true,
                filter: "\"SourceKey\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CambridgeImportParts_ImportId_Number",
                table: "CambridgeImportParts",
                columns: new[] { "ImportId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CambridgeImportParts_SyllabusTopicId",
                table: "CambridgeImportParts",
                column: "SyllabusTopicId");

            migrationBuilder.CreateIndex(
                name: "IX_CambridgeImports_OwnerId_PaperSha256",
                table: "CambridgeImports",
                columns: new[] { "OwnerId", "PaperSha256" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CambridgeImportParts");

            migrationBuilder.DropTable(
                name: "CambridgeImports");

            migrationBuilder.DropIndex(
                name: "IX_Questions_CreatedByEmail_SourceKey",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "ImagePath",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "MarkScheme",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "Marks",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "SourceGroupKey",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "SourceLabel",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "Stem",
                table: "Questions");
        }
    }
}

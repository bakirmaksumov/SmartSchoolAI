using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    /// <inheritdoc />
    public partial class AddSyllabusTopics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SyllabusTopicId",
                table: "Questions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SyllabusPrograms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyllabusPrograms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyllabusVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProgramId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FirstExamYear = table.Column<int>(type: "INTEGER", nullable: false),
                    LastExamYear = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SourceSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ImportedById = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyllabusVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyllabusVersions_SyllabusPrograms_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "SyllabusPrograms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyllabusComponents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SourcePage = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyllabusComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyllabusComponents_SyllabusVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "SyllabusVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyllabusTopics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    SourcePage = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyllabusTopics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyllabusTopics_SyllabusComponents_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "SyllabusComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Questions_SyllabusTopicId",
                table: "Questions",
                column: "SyllabusTopicId");

            migrationBuilder.CreateIndex(
                name: "IX_SyllabusComponents_VersionId_Number",
                table: "SyllabusComponents",
                columns: new[] { "VersionId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyllabusPrograms_Code",
                table: "SyllabusPrograms",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyllabusTopics_ComponentId_Order",
                table: "SyllabusTopics",
                columns: new[] { "ComponentId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_SyllabusVersions_ProgramId_FirstExamYear_LastExamYear",
                table: "SyllabusVersions",
                columns: new[] { "ProgramId", "FirstExamYear", "LastExamYear" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyllabusVersions_SourceSha256",
                table: "SyllabusVersions",
                column: "SourceSha256",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Questions_SyllabusTopics_SyllabusTopicId",
                table: "Questions",
                column: "SyllabusTopicId",
                principalTable: "SyllabusTopics",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Questions_SyllabusTopics_SyllabusTopicId",
                table: "Questions");

            migrationBuilder.DropTable(
                name: "SyllabusTopics");

            migrationBuilder.DropTable(
                name: "SyllabusComponents");

            migrationBuilder.DropTable(
                name: "SyllabusVersions");

            migrationBuilder.DropTable(
                name: "SyllabusPrograms");

            migrationBuilder.DropIndex(
                name: "IX_Questions_SyllabusTopicId",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "SyllabusTopicId",
                table: "Questions");
        }
    }
}

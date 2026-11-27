using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    /// <inheritdoc />
    public partial class AddTopicSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TopicError",
                table: "CambridgeImports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TopicStage",
                table: "CambridgeImports",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TopicNote",
                table: "CambridgeImportParts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TopicError",
                table: "CambridgeImports");

            migrationBuilder.DropColumn(
                name: "TopicStage",
                table: "CambridgeImports");

            migrationBuilder.DropColumn(
                name: "TopicNote",
                table: "CambridgeImportParts");
        }
    }
}

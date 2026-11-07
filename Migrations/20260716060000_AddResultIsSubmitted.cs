using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSchoolAI.Data;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260716060000_AddResultIsSubmitted")]
    public partial class AddResultIsSubmitted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSubmitted",
                table: "StudentResults",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Results created before the in-progress model are final submissions.
            migrationBuilder.Sql("UPDATE \"StudentResults\" SET \"IsSubmitted\" = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSubmitted",
                table: "StudentResults");
        }
    }
}

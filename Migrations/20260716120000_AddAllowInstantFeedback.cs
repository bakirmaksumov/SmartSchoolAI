using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSchoolAI.Data;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260716120000_AddAllowInstantFeedback")]
    public partial class AddAllowInstantFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowInstantFeedback",
                table: "Assignments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowInstantFeedback",
                table: "Assignments");
        }
    }
}

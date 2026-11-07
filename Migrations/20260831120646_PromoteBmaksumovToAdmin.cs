using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSchoolAI.Migrations
{
    /// <inheritdoc />
    public partial class PromoteBmaksumovToAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Bootstraps the first Admin account so there's someone who can reset
            // a locked-out teacher's password (students already have their
            // teacher for that; teachers had no equivalent until the Admin Panel).
            // A plain data update, not a schema change — safe to run against an
            // environment where this email doesn't exist yet (matches zero rows).
            migrationBuilder.Sql(@"
                UPDATE Users
                SET RoleId = (SELECT Id FROM Roles WHERE Name = 'Admin')
                WHERE Email = 'bmaksumov@wiut.uz';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE Users
                SET RoleId = (SELECT Id FROM Roles WHERE Name = 'Teacher')
                WHERE Email = 'bmaksumov@wiut.uz';
            ");
        }
    }
}

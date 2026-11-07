using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;

if (args.Length != 1)
    throw new ArgumentException("Pass the isolated E2E SQLite database path.");

var e2eDataDirectory = Path.GetFullPath(
    Environment.GetEnvironmentVariable("E2E_DATA_DIR")
    ?? throw new InvalidOperationException("E2E_DATA_DIR is not set."));
var databasePath = Path.GetFullPath(args[0]);
var expectedDataDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".e2e-data"));

if (!string.Equals(e2eDataDirectory, expectedDataDirectory, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Refusing to seed outside E2E/.e2e-data.");

if (!string.Equals(Path.GetDirectoryName(databasePath), e2eDataDirectory, StringComparison.OrdinalIgnoreCase)
    || !string.Equals(Path.GetFileName(databasePath), "smartschool-e2e.db", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Refusing an unexpected E2E database path.");
}

var regularDatabasePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "smartschool.db"));
if (string.Equals(databasePath, regularDatabasePath, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Refusing to use the application's regular SQLite database.");

if (File.Exists(databasePath))
    throw new InvalidOperationException("The E2E database must be fresh before seeding.");

string RequiredEnvironmentVariable(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"{name} is not set.");

var teacherPassword = RequiredEnvironmentVariable("E2E_TEACHER_PASSWORD");
var studentPassword = RequiredEnvironmentVariable("E2E_STUDENT_PASSWORD");
var adminPassword = RequiredEnvironmentVariable("E2E_ADMIN_PASSWORD");

Directory.CreateDirectory(e2eDataDirectory);

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlite($"Data Source={databasePath}")
    .Options;

await using var db = new AppDbContext(options);
await db.Database.MigrateAsync();

var roles = await db.Roles.ToDictionaryAsync(role => role.Name);
var accounts = new[]
{
    new User
    {
        Email = "e2e.teacher@example.test",
        FullName = "E2E Teacher",
        PasswordHash = PasswordHasher.Hash(teacherPassword),
        RoleId = roles[Role.Teacher].Id,
        IsActive = true
    },
    new User
    {
        Email = "e2e.student@example.test",
        FullName = "E2E Student",
        PasswordHash = PasswordHasher.Hash(studentPassword),
        RoleId = roles[Role.Student].Id,
        IsActive = true
    },
    new User
    {
        Email = "e2e.admin@example.test",
        FullName = "E2E Admin",
        PasswordHash = PasswordHasher.Hash(adminPassword),
        RoleId = roles[Role.Admin].Id,
        IsActive = true
    }
};

db.Users.AddRange(accounts);
await db.SaveChangesAsync();

Console.WriteLine("Prepared a fresh E2E database with three synthetic role accounts.");

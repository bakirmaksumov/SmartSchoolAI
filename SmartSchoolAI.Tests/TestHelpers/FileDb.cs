using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Tests.TestHelpers;

/// <summary>
/// A real SQLite database file built with the application's own migrations. Each <see cref="NewContext"/> opens its
/// own connection, like two HTTP requests would, so transactions, locks and unique keys behave exactly as in production.
/// </summary>
public sealed class FileDb : IDisposable
{
    public string Path { get; }
    public string ConnectionString => $"Data Source={Path};Pooling=False;Default Timeout=30";

    public FileDb(bool migrate = true)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SmartSchoolAI.Tests", $"{Guid.NewGuid()}.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (migrate)
        {
            using var db = NewContext();
            db.Database.Migrate();
        }
    }

    public AppDbContext NewContext()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ConnectionString).Options);

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(ConnectionString);
        c.Open();
        return c;
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    public async Task<User> UserAsync(string role, string name)
    {
        await using var db = NewContext();
        var roleId = await db.Roles.Where(r => r.Name == role).Select(r => r.Id).FirstAsync();
        var user = new User
        {
            Email = $"{name.ToLowerInvariant()}.{Guid.NewGuid():N}@example.test",
            FullName = name, PasswordHash = "x", RoleId = roleId
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<Class> ClassAsync(Guid teacherId, params Guid[] students)
    {
        await using var db = NewContext();
        var cls = new Class
        {
            Name = "Year 12 IT " + Guid.NewGuid().ToString("N")[..6], Subject = "IT", AcademicYear = "2026-2027",
            InviteCode = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), TeacherId = teacherId, MaxStudents = 50
        };
        db.Classes.Add(cls);
        foreach (var s in students)
            db.ClassStudents.Add(new ClassStudent { ClassId = cls.Id, StudentId = s, JoinedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return cls;
    }

    public async Task<Assignment> AssignmentAsync(Guid classId, Guid teacherId, AssignmentType type = AssignmentType.ClassroomPractice,
        AssignmentStatus status = AssignmentStatus.Draft, DateTime? deadline = null,
        AssignmentAudience audience = AssignmentAudience.SelectedStudents, LateJoinPolicy policy = LateJoinPolicy.Manual)
    {
        await using var db = NewContext();
        var a = new Assignment
        {
            Title = "Task " + Guid.NewGuid().ToString("N")[..4], ClassId = classId, TeacherId = teacherId, Type = type,
            Status = status, Deadline = deadline, Audience = audience, LateJoinPolicy = policy,
            Questions = "[{\"id\":\"q1\",\"title\":\"2+2\",\"type\":\"0\",\"answerKey\":\"4\"}]"
        };
        db.Assignments.Add(a);
        await db.SaveChangesAsync();
        return a;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(Path + suffix); } catch (IOException) { /* best effort */ }
    }
}

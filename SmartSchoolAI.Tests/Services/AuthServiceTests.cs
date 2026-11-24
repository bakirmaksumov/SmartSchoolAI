using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;
using SmartSchoolAI.Tests.TestHelpers;

namespace SmartSchoolAI.Tests.Services;

public class AuthServiceTests : IDisposable
{
    private readonly TestDbContextFactory _factory = new();
    private readonly FakeWebHostEnvironment _env = new();
    private readonly AuthService _sut;

    public AuthServiceTests()
    {
        _sut = new AuthService(_factory.Context, _env);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _env.Dispose();
    }

    private async Task<User> SeedUserAsync(string email, string password, string roleName, bool isActive = true)
    {
        var role = await _factory.Context.Roles.FirstAsync(r => r.Name == roleName);
        var user = new User
        {
            Email        = email,
            PasswordHash = PasswordHasher.Hash(password),
            FullName     = "Test User",
            RoleId       = role.Id,
            IsActive     = isActive
        };
        _factory.Context.Users.Add(user);
        await _factory.Context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task ValidateAsync_CorrectCredentials_ReturnsUser()
    {
        await SeedUserAsync("teacher@school.test", "correct-password", Role.Teacher);

        var result = await _sut.ValidateAsync("teacher@school.test", "correct-password");

        Assert.NotNull(result);
        Assert.Equal("teacher@school.test", result!.Email);
    }

    [Fact]
    public async Task ValidateAsync_IsCaseInsensitiveOnEmail()
    {
        await SeedUserAsync("teacher@school.test", "correct-password", Role.Teacher);

        var result = await _sut.ValidateAsync("Teacher@School.Test", "correct-password");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ValidateAsync_WrongPassword_ReturnsNull()
    {
        await SeedUserAsync("teacher@school.test", "correct-password", Role.Teacher);

        var result = await _sut.ValidateAsync("teacher@school.test", "wrong-password");

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateAsync_UnknownEmail_ReturnsNull()
    {
        var result = await _sut.ValidateAsync("nobody@school.test", "whatever");

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateAsync_InactiveUser_ReturnsNull()
    {
        await SeedUserAsync("teacher@school.test", "correct-password", Role.Teacher, isActive: false);

        var result = await _sut.ValidateAsync("teacher@school.test", "correct-password");

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateAsync_UpdatesLastLoginAt()
    {
        var user = await SeedUserAsync("teacher@school.test", "correct-password", Role.Teacher);
        Assert.Null(user.LastLoginAt);

        var result = await _sut.ValidateAsync("teacher@school.test", "correct-password");

        Assert.NotNull(result!.LastLoginAt);
    }

    [Fact]
    public async Task RegisterTeacherAsync_NewEmail_Succeeds()
    {
        var (success, error) = await _sut.RegisterTeacherAsync("new@school.test", "New Teacher", "password123");

        Assert.True(success);
        Assert.Empty(error);
        Assert.True(await _factory.Context.Users.AnyAsync(u => u.Email == "new@school.test"));
    }

    [Fact]
    public async Task RegisterTeacherAsync_DuplicateEmail_Fails()
    {
        await SeedUserAsync("taken@school.test", "password123", Role.Teacher);

        var (success, error) = await _sut.RegisterTeacherAsync("taken@school.test", "Someone Else", "password123");

        Assert.False(success);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task RegisterTeacherAsync_StoresHashedPasswordNotPlainText()
    {
        await _sut.RegisterTeacherAsync("new@school.test", "New Teacher", "super-secret");

        var stored = await _factory.Context.Users.FirstAsync(u => u.Email == "new@school.test");

        Assert.NotEqual("super-secret", stored.PasswordHash);
        Assert.StartsWith("$2", stored.PasswordHash);
    }

    private async Task<Class> SeedClassAsync(string inviteCode, int maxStudents = 50, DateTime? expiresAt = null)
    {
        var teacher = await SeedUserAsync($"owner-{Guid.NewGuid()}@school.test", "password123", Role.Teacher);
        var cls = new Class
        {
            Name             = "5A",
            Subject          = "Math",
            AcademicYear     = "2026",
            InviteCode       = inviteCode,
            MaxStudents      = maxStudents,
            InviteExpiresAt  = expiresAt,
            TeacherId        = teacher.Id
        };
        _factory.Context.Classes.Add(cls);
        await _factory.Context.SaveChangesAsync();
        return cls;
    }

    [Fact]
    public async Task RegisterStudentAsync_ValidInviteCode_EnrollsInClass()
    {
        var cls = await SeedClassAsync("ABC123");

        var (success, error) = await _sut.RegisterStudentAsync(
            "student@school.test", "New Student", "password123", "abc123");

        Assert.True(success);
        Assert.Empty(error);
        var student = await _factory.Context.Users.FirstAsync(u => u.Email == "student@school.test");
        Assert.True(await _factory.Context.ClassStudents
            .AnyAsync(cs => cs.ClassId == cls.Id && cs.StudentId == student.Id));
    }

    [Fact]
    public async Task RegisterStudentAsync_UnknownInviteCode_Fails()
    {
        var (success, error) = await _sut.RegisterStudentAsync(
            "student@school.test", "New Student", "password123", "NOPE99");

        Assert.False(success);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task RegisterStudentAsync_ExpiredInviteCode_Fails()
    {
        await SeedClassAsync("EXP123", expiresAt: DateTime.UtcNow.AddDays(-1));

        var (success, error) = await _sut.RegisterStudentAsync(
            "student@school.test", "New Student", "password123", "EXP123");

        Assert.False(success);
        Assert.Contains("expired", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisterStudentAsync_ClassFull_Fails()
    {
        var cls = await SeedClassAsync("FULL01", maxStudents: 1);
        var existingStudent = await SeedUserAsync("first@school.test", "password123", Role.Student);
        _factory.Context.ClassStudents.Add(new ClassStudent
        {
            ClassId = cls.Id, StudentId = existingStudent.Id, JoinedAt = DateTime.UtcNow
        });
        await _factory.Context.SaveChangesAsync();

        var (success, error) = await _sut.RegisterStudentAsync(
            "second@school.test", "Second Student", "password123", "FULL01");

        Assert.False(success);
        Assert.Contains("full", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JoinClassAsync_AlreadyEnrolled_Fails()
    {
        var cls = await SeedClassAsync("JOIN01");
        var student = await SeedUserAsync("student@school.test", "password123", Role.Student);
        _factory.Context.ClassStudents.Add(new ClassStudent
        {
            ClassId = cls.Id, StudentId = student.Id, JoinedAt = DateTime.UtcNow
        });
        await _factory.Context.SaveChangesAsync();

        var (success, error) = await _sut.JoinClassAsync(student.Id, "JOIN01");

        Assert.False(success);
        Assert.Contains("already", error, StringComparison.OrdinalIgnoreCase);
    }
}

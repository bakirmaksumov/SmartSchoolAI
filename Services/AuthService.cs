using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>
/// Handles all authentication and registration logic.
/// Keeps controllers thin — all business rules live here.
/// </summary>
public class AuthService
{
    private readonly AppDbContext _db;

    // A hash of no real password, verified whenever the email lookup misses.
    // BCrypt's cost factor means a real lookup's Verify() call dominates the
    // response time, so skipping it for unknown emails made "no such account"
    // answer measurably faster than "wrong password" — enough to enumerate
    // registered emails by timing the login form. Always doing the same
    // amount of hashing work closes that side channel.
    private const string DummyHashForTimingParity =
        "$2a$12$C6UzMDM.H6dfI/f/IKcEeO4b0mDF..H8nD1KEG8xkFV/aQ.7dP9O.";

    private readonly IWebHostEnvironment _env;

    public AuthService(AppDbContext db, IWebHostEnvironment env)
    {
        _db  = db;
        _env = env;
    }

    /// <summary>
    /// Validates credentials and updates LastLoginAt on success.
    /// Returns null if credentials are invalid or user is inactive.
    /// </summary>
    public async Task<User?> ValidateAsync(string email, string password)
    {
        var normalizedEmail = email.Trim().ToLower();

        var user = await _db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.IsActive);

        if (user is null)
        {
            PasswordHasher.Verify(password, DummyHashForTimingParity);
            return null;
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash))
            return null;

        // Accounts still on the old SHA-256 hash move to BCrypt here: the plain
        // password is only available at sign-in, so this is the one place it can
        // be re-hashed without asking the user to pick a new one.
        if (PasswordHasher.NeedsUpgrade(user.PasswordHash))
            user.PasswordHash = PasswordHasher.Hash(password);

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return user;
    }

    /// <summary>
    /// Registers a new teacher account.
    /// Returns (true, "") on success or (false, errorMessage) on failure.
    /// </summary>
    public async Task<(bool Success, string Error)> RegisterTeacherAsync(
        string email, string fullName, string password)
    {
        var normalizedEmail = email.Trim().ToLower();

        if (await _db.Users.AnyAsync(u => u.Email == normalizedEmail))
            return (false, "An account with this email already exists.");

        var teacherRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == Role.Teacher);

        if (teacherRole is null)
            return (false, "System error: Teacher role not found. Please contact support.");

        var user = new User
        {
            Email        = normalizedEmail,
            FullName     = fullName.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            RoleId       = teacherRole.Id,
            IsActive     = true
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return (true, string.Empty);
    }

    /// <summary>
    /// Registers a new student account and auto-enrolls them in the class
    /// identified by the invite code. Returns (true, "") on success.
    /// </summary>
    public async Task<(bool Success, string Error)> RegisterStudentAsync(
        string email, string fullName, string password, string inviteCode)
    {
        var normalizedEmail = email.Trim().ToLower();
        var code = inviteCode.Trim().ToUpper();

        if (await _db.Users.AnyAsync(u => u.Email == normalizedEmail))
            return (false, "An account with this email already exists.");

        var cls = await _db.Classes
            .FirstOrDefaultAsync(c => c.InviteCode == code && !c.IsArchived);

        if (cls is null)
            return (false, "Invalid or expired invite code.");

        if (cls.InviteExpiresAt.HasValue && cls.InviteExpiresAt.Value < DateTime.UtcNow)
            return (false, "This invite code has expired. Ask your teacher for a new one.");

        var studentRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == Role.Student);
        if (studentRole is null)
            return (false, "System error: Student role not found.");

        // Hashing is deliberately slow; do it before taking the database write lock below.
        var passwordHash = PasswordHasher.Hash(password);

        // The account, the class membership and the class's late-join rule are one transaction:
        // a full class or any failure leaves no half-created account behind.
        await using var tx = await _db.Database.BeginTransactionAsync();

        var user = new User
        {
            Email        = normalizedEmail,
            FullName     = fullName.Trim(),
            PasswordHash = passwordHash,
            RoleId       = studentRole.Id,
            IsActive     = true
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(); // get user.Id

        var (joined, error) = await AddMembershipAsync(cls, user.Id, "This class is full. Please contact your teacher.");
        if (!joined)
        {
            _db.ChangeTracker.Clear();
            return (false, error);
        }

        await tx.CommitAsync();

        ActivityLog.Record(_env, "StudentEnrolled", "Student", user.Id, $"class={cls.Id}");

        return (true, string.Empty);
    }

    /// <summary>
    /// Sets a new password and clears the "must change password" flag. Used when a
    /// student signs in with a teacher-reset temporary password and is required to
    /// choose their own before continuing. Returns false only if the user is gone.
    /// </summary>
    public async Task<bool> ChangePasswordAsync(Guid userId, string newPassword)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return false;

        user.PasswordHash       = PasswordHasher.Hash(newPassword);
        user.MustChangePassword = false;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Enrolls an existing student in a class by invite code.</summary>
    public async Task<(bool Success, string Error)> JoinClassAsync(Guid studentId, string inviteCode)
    {
        var code = inviteCode.Trim().ToUpper();

        var cls = await _db.Classes
            .FirstOrDefaultAsync(c => c.InviteCode == code && !c.IsArchived);

        if (cls is null)
            return (false, "Invalid or expired invite code.");

        if (cls.InviteExpiresAt.HasValue && cls.InviteExpiresAt.Value < DateTime.UtcNow)
            return (false, "This invite code has expired.");

        await using var tx = await _db.Database.BeginTransactionAsync();

        var (joined, error) = await AddMembershipAsync(cls, studentId, "This class is full.");
        if (!joined)
        {
            _db.ChangeTracker.Clear();
            return (false, error);
        }

        await tx.CommitAsync();

        ActivityLog.Record(_env, "StudentEnrolled", "Student", studentId, $"class={cls.Id}");

        return (true, string.Empty);
    }

    /// <summary>
    /// Makes the student a member of the class and applies the class's late-join rules (assignments given
    /// automatically, or requests for the teacher). Runs inside the caller's transaction — with SQLite a
    /// <c>BEGIN IMMEDIATE</c> one — so the "already joined" and "class full" checks cannot race another join,
    /// and the late-join rule cannot race the teacher assigning the same class.
    /// </summary>
    private async Task<(bool Joined, string Error)> AddMembershipAsync(Class cls, Guid studentId, string fullMessage)
    {
        if (await _db.ClassStudents.AnyAsync(cs => cs.ClassId == cls.Id && cs.StudentId == studentId))
            return (false, "You are already enrolled in this class.");

        var currentCount = await _db.ClassStudents.CountAsync(cs => cs.ClassId == cls.Id);
        if (currentCount >= cls.MaxStudents)
            return (false, fullMessage);

        _db.ClassStudents.Add(new ClassStudent
        {
            ClassId   = cls.Id,
            StudentId = studentId,
            JoinedAt  = DateTime.UtcNow
        });

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The composite key (class, student) refused a second membership: the same join already happened.
            return (false, "You are already enrolled in this class.");
        }

        await new AssignmentDistributionService(_db, NullLogger<AssignmentDistributionService>.Instance)
            .ApplyToNewMemberAsync(cls.Id, studentId);

        return (true, string.Empty);
    }
}

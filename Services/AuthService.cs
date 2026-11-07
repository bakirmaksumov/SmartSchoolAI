using Microsoft.EntityFrameworkCore;
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

        var currentCount = await _db.ClassStudents.CountAsync(cs => cs.ClassId == cls.Id);
        if (currentCount >= cls.MaxStudents)
            return (false, "This class is full. Please contact your teacher.");

        var studentRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == Role.Student);
        if (studentRole is null)
            return (false, "System error: Student role not found.");

        var user = new User
        {
            Email        = normalizedEmail,
            FullName     = fullName.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            RoleId       = studentRole.Id,
            IsActive     = true
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(); // get user.Id

        _db.ClassStudents.Add(new ClassStudent
        {
            ClassId   = cls.Id,
            StudentId = user.Id,
            JoinedAt  = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        // The count check above and this insert aren't atomic — two students
        // hitting the last open seat at the same moment can both pass it. This
        // re-check after the fact closes that gap; if this join lost the race,
        // the whole registration is rolled back rather than leaving a student
        // account that's enrolled nowhere.
        if (!await EnforceCapacityAsync(cls.Id, user.Id, cls.MaxStudents))
        {
            _db.Users.Remove(user);
            await _db.SaveChangesAsync();
            return (false, "This class is full. Please contact your teacher.");
        }

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

        var alreadyJoined = await _db.ClassStudents
            .AnyAsync(cs => cs.ClassId == cls.Id && cs.StudentId == studentId);

        if (alreadyJoined)
            return (false, "You are already enrolled in this class.");

        var currentCount = await _db.ClassStudents.CountAsync(cs => cs.ClassId == cls.Id);
        if (currentCount >= cls.MaxStudents)
            return (false, "This class is full.");

        _db.ClassStudents.Add(new ClassStudent
        {
            ClassId   = cls.Id,
            StudentId = studentId,
            JoinedAt  = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        // See RegisterStudentAsync's identical re-check: the count check above
        // and this insert aren't atomic, so two students racing for the last
        // seat can both get past it.
        if (!await EnforceCapacityAsync(cls.Id, studentId, cls.MaxStudents))
            return (false, "This class is full.");

        ActivityLog.Record(_env, "StudentEnrolled", "Student", studentId, $"class={cls.Id}");

        return (true, string.Empty);
    }

    /// <summary>
    /// Re-checks enrollment against <paramref name="maxStudents"/> AFTER an
    /// insert. Whichever join lands outside the first <paramref name="maxStudents"/>
    /// slots — ordered by JoinedAt as a stable, deterministic tiebreak — is
    /// rolled back here, so at most one side of a same-moment race keeps its seat
    /// instead of both (or, without this check at all, letting the class overfill).
    /// </summary>
    private async Task<bool> EnforceCapacityAsync(Guid classId, Guid studentId, int maxStudents)
    {
        var enrolledIds = await _db.ClassStudents
            .Where(cs => cs.ClassId == classId)
            .OrderBy(cs => cs.JoinedAt).ThenBy(cs => cs.StudentId)
            .Select(cs => cs.StudentId)
            .ToListAsync();

        if (enrolledIds.Count <= maxStudents || enrolledIds.Take(maxStudents).Contains(studentId))
            return true;

        var mine = await _db.ClassStudents
            .FirstOrDefaultAsync(cs => cs.ClassId == classId && cs.StudentId == studentId);
        if (mine is not null)
        {
            _db.ClassStudents.Remove(mine);
            await _db.SaveChangesAsync();
        }
        return false;
    }
}

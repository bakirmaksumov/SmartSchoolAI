using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;
using SmartSchoolAI.Services;
using SmartSchoolAI.ViewModels;
using System.Security.Claims;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// Manages a teacher's classes: list, create, and view details.
/// All queries are scoped to the currently logged-in teacher.
/// </summary>
[Authorize(Roles = "Teacher")]
public class ClassesController : Controller
{
    private readonly AppDbContext                  _db;
    private readonly ClassStatisticsService        _stats;
    private readonly AssignmentDistributionService _distribution;

    public ClassesController(AppDbContext db, ClassStatisticsService stats, AssignmentDistributionService distribution)
    {
        _db           = db;
        _stats        = stats;
        _distribution = distribution;
    }

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET: /Classes?archived=false — active classes by default, archived on the other tab.
    public async Task<IActionResult> Index(bool archived = false)
    {
        var mine = _db.Classes.AccessibleBy(CurrentUserId);

        ViewBag.ActiveCount   = await mine.CountAsync(c => !c.IsArchived);
        ViewBag.ArchivedCount = await mine.CountAsync(c => c.IsArchived);
        ViewBag.ShowArchived  = archived;

        var classes = await mine
            .Where(c => c.IsArchived == archived)
            .Include(c => c.ClassStudents)   // used for the student count on each card
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        return View(classes);
    }

    // GET: /Classes/Create
    public IActionResult Create() => View();

    // POST: /Classes/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string name, int gradeLevel, string subject, string academicYear)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(subject))
        {
            ViewBag.Error = "Name and subject are required.";
            return View();
        }

        var duplicate = await _db.Classes.AnyAsync(c =>
            c.TeacherId    == CurrentUserId &&
            c.Name         == name.Trim() &&
            c.AcademicYear == academicYear);

        if (duplicate)
        {
            ViewBag.Error = "You already have a class with this name in this academic year.";
            return View();
        }

        var newClass = new Class
        {
            TeacherId    = CurrentUserId,
            Name         = name.Trim(),
            GradeLevel   = gradeLevel,
            Subject      = subject.Trim(),
            AcademicYear = academicYear,
            InviteCode   = await GenerateUniqueInviteCodeAsync()
        };

        _db.Classes.Add(newClass);
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Class '{newClass.Name}' created! Invite code: {newClass.InviteCode}";
        return RedirectToAction(nameof(Index));
    }

    // GET: /Classes/Edit/id — settings now live on the class page's Settings tab.
    public IActionResult Edit(Guid id)
        => RedirectToAction(nameof(Details), new { id, tab = "settings" });

    // POST: /Classes/Edit/id
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        Guid id, string name, int gradeLevel, string subject, string academicYear, int maxStudents)
    {
        var cls = await FindOwnedClassAsync(id);
        if (cls is null) return NotFound();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(subject))
        {
            TempData["Error"] = "Name and subject are required.";
            return RedirectToAction(nameof(Details), new { id, tab = "settings" });
        }

        // Same rule as Create — unique name per academic year, ignoring this class.
        var duplicate = await _db.Classes.AnyAsync(c =>
            c.Id           != id &&
            c.TeacherId    == CurrentUserId &&
            c.Name         == name.Trim() &&
            c.AcademicYear == academicYear);

        if (duplicate)
        {
            TempData["Error"] = "You already have a class with this name in this academic year.";
            return RedirectToAction(nameof(Details), new { id, tab = "settings" });
        }

        cls.Name         = name.Trim();
        cls.GradeLevel   = gradeLevel;
        cls.Subject      = subject.Trim();
        cls.AcademicYear = academicYear;
        cls.MaxStudents  = Math.Clamp(maxStudents, 1, 500);
        cls.UpdatedAt    = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Class '{cls.Name}' updated.";
        return RedirectToAction(nameof(Details), new { id, tab = "settings" });
    }

    // POST: /Classes/Archive/id — hide the class; no data is deleted.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Archive(Guid id)
    {
        var cls = await FindOwnedClassAsync(id);
        if (cls is null) return NotFound();

        cls.Archive();
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Class '{cls.Name}' archived. Restore it any time from the Archived tab.";
        return RedirectToAction(nameof(Index));
    }

    // POST: /Classes/Restore/id — bring an archived class back.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(Guid id)
    {
        var cls = await FindOwnedClassAsync(id);
        if (cls is null) return NotFound();

        cls.Restore();
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Class '{cls.Name}' restored.";
        return RedirectToAction(nameof(Index));
    }

    // GET: /Classes/Students — every student across the teacher's classes.
    public async Task<IActionResult> Students(Guid? classId = null, string? search = null, int? page = null)
    {
        var classes = await _db.Classes
            .AccessibleBy(CurrentUserId)
            .Where(c => !c.IsArchived)
            .Include(c => c.Assignments)
            .Include(c => c.ClassStudents)
                .ThenInclude(cs => cs.Student)
            .OrderBy(c => c.Name)
            .ToListAsync();

        var shown = classes.Where(c => classId is null || c.Id == classId).ToList();
        var stats = await _stats.ForStudentsAsync(shown);

        var rows = new List<StudentRosterRowDto>();

        foreach (var cls in shown)
        {
            foreach (var enrolment in cls.ClassStudents)
            {
                if (!Matches(enrolment.Student, search)) continue;

                rows.Add(new StudentRosterRowDto
                {
                    StudentId = enrolment.StudentId,
                    FullName  = enrolment.Student.FullName,
                    Email     = enrolment.Student.Email,
                    JoinedAt  = enrolment.JoinedAt,
                    ClassId   = cls.Id,
                    ClassName = cls.Name,
                    Stats     = stats.TryGetValue((cls.Id, enrolment.StudentId), out var s)
                                    ? s : new ClassStudentStatDto()
                });
            }
        }

        // Ten per page, by name; the class breaks ties so a student in two classes keeps a stable place.
        var ordered  = rows.OrderBy(r => r.FullName).ThenBy(r => r.ClassName).ThenBy(r => r.StudentId).ToList();
        var pageInfo = PageInfo.For(page, ordered.Count);

        return View(new StudentRosterViewModel
        {
            Rows            = ordered.Skip((pageInfo.Page - 1) * pageInfo.PageSize).Take(pageInfo.PageSize).ToList(),
            Page            = pageInfo,
            Classes         = classes,
            SelectedClassId = classId,
            Search          = search
        });
    }

    /// <summary>Free-text filter over a student's name and email.</summary>
    private static bool Matches(User student, string? search) =>
        string.IsNullOrWhiteSpace(search)
        || student.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || student.Email.Contains(search, StringComparison.OrdinalIgnoreCase);

    // GET: /Classes/Details/id?tab=assignments|results|students|settings[&returnUrl=/Assignments/Edit/…]
    public async Task<IActionResult> Details(Guid id, string tab = "assignments", string? returnUrl = null)
    {
        var cls = await _db.Classes
            .AccessibleBy(CurrentUserId)
            .Include(c => c.Assignments)
            .Include(c => c.ClassStudents)
                .ThenInclude(cs => cs.Student)
            .Include(c => c.Teacher)
            .Include(c => c.CoTeachers)
                .ThenInclude(ct => ct.Teacher)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (cls is null) return NotFound();

        var allowed = new[] { "assignments", "results", "students", "settings" };

        return View(new ClassDetailsViewModel
        {
            Class           = cls,
            Tab             = allowed.Contains(tab) ? tab : "assignments",
            StudentStats    = await _stats.ForStudentsAsync(cls),
            AssignmentStats = await _stats.ForAssignmentsAsync(cls),
            PendingRequests = await _distribution.PendingForClassAsync(cls.Id, CurrentUserId),
            // Only a path inside this site: the "back to your draft" link must never lead elsewhere.
            ReturnUrl       = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null
        });
    }

    // POST: /Classes/DecideRequests — the teacher's answer to students who joined after work was given out.
    // decision=approve gives each chosen (student, assignment) pair the work, with a personal due date where
    // the teacher changed it in that row (field "due_{requestId}"); decision=decline closes the chosen
    // requests without giving the work. Requests not chosen stay waiting ("decide later").
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideRequests(Guid classId, List<Guid>? requestIds, string decision)
    {
        if (await FindOwnedClassAsync(classId) is null) return NotFound();

        var ids = (requestIds ?? new()).Distinct().ToList();
        if (ids.Count == 0)
        {
            TempData["Error"] = "Choose at least one student and assignment.";
            return RedirectToAction(nameof(Details), new { id = classId, tab = "students" });
        }

        if (decision == "decline")
        {
            var declined = await _distribution.DeclineAsync(classId, CurrentUserId, ids);
            TempData["Success"] = declined.Declined == 1 ? "1 request declined." : $"{declined.Declined} requests declined.";
            return RedirectToAction(nameof(Details), new { id = classId, tab = "students" });
        }

        var decisions = ids.Select(rid =>
        {
            var raw = Request.Form[$"due_{rid}"].ToString();
            DateTime? due = DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                                              System.Globalization.DateTimeStyles.None, out var parsed)
                ? SchoolTime.ToUtc(parsed) : null;
            return new AssignmentDistributionService.Decision(rid, due);
        }).ToList();

        var outcome = await _distribution.ApproveAsync(classId, CurrentUserId, decisions);

        if (outcome.Approved > 0)
        {
            var students    = outcome.Students == 1 ? "1 student" : $"{outcome.Students} students";
            var assignments = outcome.Assignments == 1 ? "1 assignment" : $"{outcome.Assignments} assignments";
            TempData["Success"] = outcome.Assignments == 1
                ? $"Assigned to {students}."
                : $"Approved {(outcome.Approved == 1 ? "1 request" : $"{outcome.Approved} requests")}: {students} · {assignments}.";
        }
        if (outcome.Notes.Count > 0 || outcome.Approved == 0)
            TempData["Error"] = outcome.Notes.Count > 0
                ? "Not approved: " + string.Join(" ", outcome.Notes)
                : "Nothing was approved.";

        return RedirectToAction(nameof(Details), new { id = classId, tab = "students" });
    }

    // GET: /Classes/StudentWork?studentId=…[&classId=…][&returnUrl=…] — one student's assignments and work
    // across the classes this teacher can see.
    public async Task<IActionResult> StudentWork(Guid studentId, Guid? classId = null, string? returnUrl = null)
    {
        var classes = await _db.Classes
            .AccessibleBy(CurrentUserId)
            .Where(c => c.ClassStudents.Any(cs => cs.StudentId == studentId))
            .OrderBy(c => c.Name)
            .ToListAsync();

        // Only a student in one of this teacher's classes can be looked at.
        if (classes.Count == 0) return NotFound();

        var student = await _db.Users.FirstOrDefaultAsync(u => u.Id == studentId);
        if (student is null) return NotFound();

        var classIds = classes.Select(c => c.Id).ToList();
        if (classId is { } cid && !classIds.Contains(cid)) classId = null;
        var shownClassIds = classId is { } only ? new List<Guid> { only } : classIds;

        var enrollments = await _db.AssignmentStudents
            .Include(x => x.Assignment).ThenInclude(a => a.Class)
            .Where(x => x.StudentId == studentId && shownClassIds.Contains(x.Assignment.ClassId))
            .ToListAsync();

        var pending = await _db.LateJoinRequests
            .Include(r => r.Assignment).ThenInclude(a => a.Class)
            .Where(r => r.StudentId == studentId && r.Status == LateJoinRequestStatus.Pending
                     && shownClassIds.Contains(r.Assignment.ClassId))
            .ToListAsync();

        var assignmentIds = enrollments.Select(e => e.AssignmentId).ToList();
        var results = await _db.StudentResults
            .Include(r => r.StudentAnswers)
            .Where(r => r.StudentId == studentId && assignmentIds.Contains(r.AssignmentId))
            .ToListAsync();

        var now  = DateTime.UtcNow;
        var rows = enrollments.Select(e =>
        {
            var mine      = results.Where(r => r.AssignmentId == e.AssignmentId).OrderByDescending(r => r.AttemptNumber).ToList();
            var latest    = mine.FirstOrDefault(r => r.IsSubmitted);
            var needs     = mine.Any(ReviewRules.Needs);
            var due       = e.DueDateOverride ?? e.Assignment.Deadline;
            var opens     = e.StartDateOverride ?? e.Assignment.AvailableFrom;
            var status    = needs                                                   ? "Needs review"
                          : latest?.TeacherReviewedAt != null                       ? "Reviewed"
                          : latest is not null                                      ? "Submitted"
                          : e.Assignment.Status != AssignmentStatus.Active          ? "Closed"
                          : mine.Any()                                              ? "In progress"
                          : opens.HasValue && opens.Value > now                     ? "Not open yet"
                          : due.HasValue && due.Value < now && !e.Assignment.AllowLateSubmission ? "Overdue"
                          :                                                           "Assigned";
            return new StudentWorkRow
            {
                Assignment  = e.Assignment,
                Due         = due,
                PersonalDue = e.DueDateOverride.HasValue,
                Status      = status,
                Latest      = latest,
                NeedsReview = needs
            };
        })
        .Concat(pending.Where(r => !assignmentIds.Contains(r.AssignmentId)).Select(r => new StudentWorkRow
        {
            Assignment = r.Assignment,
            Due        = r.Assignment.Deadline,
            Status     = "Awaiting approval"
        }))
        .OrderByDescending(r => r.Assignment.CreatedAt)
        .ToList();

        return View(new StudentWorkViewModel
        {
            Student   = student,
            Rows      = rows,
            Classes   = classes,
            ClassId   = classId,
            ReturnUrl = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
                            ? returnUrl
                            : classId is { } back ? $"/Classes/Details/{back}?tab=students" : "/Classes/Students"
        });
    }

    // POST: /Classes/RemoveStudent — Remove a student from a class
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveStudent(Guid classId, Guid studentId, string? returnTo = null)
    {
        var cls = await FindOwnedClassAsync(classId);

        if (cls is null) return NotFound();

        var enrollment = await _db.ClassStudents
            .FirstOrDefaultAsync(cs => cs.ClassId == classId && cs.StudentId == studentId);

        if (enrollment is null)
        {
            TempData["Error"] = "Student not found in this class.";
            return BackToStudents(classId, returnTo);
        }

        _db.ClassStudents.Remove(enrollment);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Student removed from the class.";
        return BackToStudents(classId, returnTo);
    }

    // POST: /Classes/EditStudent — rename a student / fix their email
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditStudent(Guid classId, Guid studentId, string fullName, string email, string? returnTo = null)
    {
        var student = await FindStudentInOwnedClassAsync(classId, studentId);
        if (student is null) return NotFound();

        if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(email))
        {
            TempData["Error"] = "Name and email are required.";
            return BackToStudents(classId, returnTo);
        }

        var normalisedEmail = email.Trim().ToLowerInvariant();

        var emailTaken = await _db.Users
            .AnyAsync(u => u.Id != studentId && u.Email == normalisedEmail);

        if (emailTaken)
        {
            TempData["Error"] = "That email is already used by another account.";
            return BackToStudents(classId, returnTo);
        }

        student.FullName  = fullName.Trim();
        student.Email     = normalisedEmail;
        student.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"{student.FullName}'s details were updated.";
        return BackToStudents(classId, returnTo);
    }

    // POST: /Classes/ResetStudentPassword — give a student a new password
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetStudentPassword(Guid classId, Guid studentId, string newPassword, string? returnTo = null)
    {
        var student = await FindStudentInOwnedClassAsync(classId, studentId);
        if (student is null) return NotFound();

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            TempData["Error"] = "The new password must be at least 8 characters.";
            return BackToStudents(classId, returnTo);
        }

        student.PasswordHash       = PasswordHasher.Hash(newPassword);
        student.MustChangePassword = true;
        student.UpdatedAt          = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Kept out of the generic Success/Error flash keys so the plaintext
        // password isn't casually mixed in with routine "Saved!" banners —
        // it gets its own one-time reveal in _Layout.cshtml instead.
        TempData["ResetPasswordFor"]   = student.FullName;
        TempData["ResetPasswordPlain"] = newPassword;
        return BackToStudents(classId, returnTo);
    }

    // POST: /Classes/AddCoTeacher — grant another teacher full access to this class.
    // Only the actual owner can manage the co-teacher list.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCoTeacher(Guid classId, string email)
    {
        var cls = await FindClassOwnedExclusivelyAsync(classId);
        if (cls is null) return NotFound();

        var normalisedEmail = (email ?? string.Empty).Trim().ToLowerInvariant();

        var teacherRoleId = await _db.Roles
            .Where(r => r.Name == Role.Teacher)
            .Select(r => r.Id)
            .FirstAsync();

        var teacher = await _db.Users
            .FirstOrDefaultAsync(u => u.Email == normalisedEmail && u.RoleId == teacherRoleId);

        if (teacher is null)
        {
            TempData["Error"] = "No teacher account found with that email.";
            return RedirectToAction(nameof(Details), new { id = classId, tab = "settings" });
        }

        if (teacher.Id == cls.TeacherId)
        {
            TempData["Error"] = "That teacher already owns this class.";
            return RedirectToAction(nameof(Details), new { id = classId, tab = "settings" });
        }

        var alreadyAdded = await _db.ClassTeachers
            .AnyAsync(ct => ct.ClassId == classId && ct.TeacherId == teacher.Id);

        if (alreadyAdded)
        {
            TempData["Error"] = $"{teacher.FullName} is already a co-teacher on this class.";
            return RedirectToAction(nameof(Details), new { id = classId, tab = "settings" });
        }

        _db.ClassTeachers.Add(new ClassTeacher { ClassId = classId, TeacherId = teacher.Id });
        await _db.SaveChangesAsync();

        TempData["Success"] = $"{teacher.FullName} added as a co-teacher.";
        return RedirectToAction(nameof(Details), new { id = classId, tab = "settings" });
    }

    // POST: /Classes/RemoveCoTeacher — revoke a co-teacher's access to this class.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveCoTeacher(Guid classId, Guid teacherId)
    {
        var cls = await FindClassOwnedExclusivelyAsync(classId);
        if (cls is null) return NotFound();

        var link = await _db.ClassTeachers
            .FirstOrDefaultAsync(ct => ct.ClassId == classId && ct.TeacherId == teacherId);

        if (link is not null)
        {
            _db.ClassTeachers.Remove(link);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Co-teacher removed.";
        }

        return RedirectToAction(nameof(Details), new { id = classId, tab = "settings" });
    }

    /// <summary>Languages students may translate questions/feedback into, keyed by code.</summary>
    public static readonly IReadOnlyDictionary<string, string> SupportedLanguages = new Dictionary<string, string>
    {
        ["es"] = "Spanish",   ["fr"] = "French",     ["de"] = "German",
        ["ru"] = "Russian",   ["ar"] = "Arabic",     ["zh"] = "Chinese (Simplified)",
        ["uz"] = "Uzbek",     ["tr"] = "Turkish",    ["pt"] = "Portuguese",
        ["hi"] = "Hindi",     ["ja"] = "Japanese",   ["ko"] = "Korean",
    };

    // POST: /Classes/UpdateAccessibility — the two toggle switches on the Settings tab.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAccessibility(Guid classId, bool translationEnabled, bool voiceEnabled)
    {
        var cls = await FindOwnedClassAsync(classId);
        if (cls is null) return NotFound();

        cls.TranslationEnabled = translationEnabled;
        cls.VoiceEnabled       = voiceEnabled;
        cls.UpdatedAt          = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Accessibility settings updated.";
        return RedirectToAction(nameof(Details), new { id = classId, tab = "settings" });
    }

    // POST: /Classes/UpdateLanguages — the "Manage languages" modal.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLanguages(Guid classId, List<string>? languages)
    {
        var cls = await FindOwnedClassAsync(classId);
        if (cls is null) return NotFound();

        var valid = (languages ?? new List<string>())
            .Where(SupportedLanguages.ContainsKey)
            .Distinct()
            .ToList();

        cls.EnabledLanguages = System.Text.Json.JsonSerializer.Serialize(valid);
        cls.UpdatedAt        = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Languages updated.";
        return RedirectToAction(nameof(Details), new { id = classId, tab = "settings" });
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Sends the teacher back where they started: the cross-class roster when the
    /// action came from there, otherwise the class page's Students tab.
    /// </summary>
    private IActionResult BackToStudents(Guid classId, string? returnTo)
    {
        if (returnTo != "roster")
            return RedirectToAction(nameof(Details), new { id = classId, tab = "students" });

        // Back to the same page and filters of the roster (a page left empty shows the one before it).
        var back = Request.HasFormContentType ? Request.Form["returnUrl"].ToString() : null;
        return !string.IsNullOrEmpty(back) && Url.IsLocalUrl(back) &&
               back.StartsWith("/Classes/Students", StringComparison.OrdinalIgnoreCase)
            ? LocalRedirect(back)
            : RedirectToAction(nameof(Students));
    }

    /// <summary>
    /// Loads a student only if they are enrolled in a class this teacher owns or
    /// co-teaches — a teacher must never be able to touch accounts outside those classes.
    /// </summary>
    private async Task<User?> FindStudentInOwnedClassAsync(Guid classId, Guid studentId)
    {
        var enrolled = await _db.ClassStudents
            .AnyAsync(cs => cs.ClassId == classId
                         && cs.StudentId == studentId
                         && (cs.Class.TeacherId == CurrentUserId
                             || cs.Class.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));

        return enrolled
            ? await _db.Users.FirstOrDefaultAsync(u => u.Id == studentId)
            : null;
    }

    private Task<Class?> FindOwnedClassAsync(Guid id)
        => _db.Classes.AccessibleBy(CurrentUserId).FirstOrDefaultAsync(c => c.Id == id);

    /// <summary>
    /// Loads a class only when this teacher is the actual owner (not a co-teacher) —
    /// used to gate managing the co-teacher list itself.
    /// </summary>
    private Task<Class?> FindClassOwnedExclusivelyAsync(Guid id)
        => _db.Classes.FirstOrDefaultAsync(c => c.Id == id && c.TeacherId == CurrentUserId);

    private async Task<string> GenerateUniqueInviteCodeAsync()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = new Random();
        string code;

        do
        {
            code = new string(Enumerable.Repeat(chars, 6)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }
        while (await _db.Classes.AnyAsync(c => c.InviteCode == code));

        return code;
    }
}

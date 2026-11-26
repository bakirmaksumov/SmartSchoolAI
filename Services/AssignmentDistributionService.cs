using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>
/// Who gets an assignment, in one place:
///   - <see cref="AssignAsync"/>: the teacher's single "Assign to N students" action (recipients + publishing);
///   - <see cref="ApplyToNewMemberAsync"/>: the rule for a student who joins the class later;
///   - <see cref="ApproveAsync"/> / <see cref="DeclineAsync"/>: the teacher's answer to waiting requests.
///
/// Access to an assignment only ever comes from an <see cref="AssignmentStudent"/> row. A
/// <see cref="LateJoinRequest"/> grants nothing by itself.
///
/// Concurrency: every operation runs in one database transaction. With SQLite these are
/// <c>BEGIN IMMEDIATE</c> transactions, so assigning, joining and approving run one after another,
/// never interleaved: a student who joins while the teacher assigns is either among the class
/// members the assignment reads, or sees the assignment already active and gets the late-join rule.
/// Unique keys on (assignment, student) back this up — a duplicate can never be stored.
/// </summary>
public class AssignmentDistributionService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AssignmentDistributionService> _logger;

    public AssignmentDistributionService(AppDbContext db, ILogger<AssignmentDistributionService> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── Assign ──────────────────────────────────────────────────────────────────

    /// <param name="Audience">Whole class, or the students in <paramref name="StudentIds"/>.</param>
    /// <param name="LateJoin">For the whole class: what happens to students who join later.</param>
    /// <param name="AvailableFrom">For a draft: when students may start (null = straight away).</param>
    /// <param name="Deadline">For a draft: the due date (null = none).</param>
    public sealed record AssignRequest(
        AssignmentAudience Audience,
        IReadOnlyCollection<Guid> StudentIds,
        LateJoinPolicy LateJoin,
        DateTime? AvailableFrom,
        DateTime? Deadline);

    /// <param name="Recipients">How many students have the assignment after the action.</param>
    /// <param name="Added">How many of them were added by this action.</param>
    public sealed record AssignOutcome(
        bool Success, string? Error, int Recipients, int Added,
        string ClassName, Guid ClassId, DateTime? OpensAt, bool Published);

    /// <summary>
    /// Gives the assignment to its recipients and, for a draft, makes it active — all or nothing.
    /// On any error nothing is changed: the draft and its recipients stay as they were.
    /// A draft's recipients become exactly the chosen ones; an active assignment's are only added to.
    /// </summary>
    public async Task<AssignOutcome> AssignAsync(Guid assignmentId, Guid teacherId, AssignRequest request)
    {
        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();

        var assignment = await _db.Assignments
            .Include(a => a.Class)
            .AccessibleBy(teacherId)
            .FirstOrDefaultAsync(a => a.Id == assignmentId);

        if (assignment is null)
            return Fail("Assignment not found.");

        string? error = assignment.Status switch
        {
            AssignmentStatus.Closed   => "This assignment is closed. Reopen it before assigning it to more students.",
            AssignmentStatus.Archived => "This assignment is archived and cannot be assigned.",
            _                         => null
        };
        if (error is not null) return Fail(error, assignment);

        var isDraft = assignment.Status == AssignmentStatus.Draft;
        if (isDraft && QuestionCount(assignment) == 0)
            return Fail("Add at least one question before assigning.", assignment);

        var members = await _db.ClassStudents
            .Where(cs => cs.ClassId == assignment.ClassId)
            .Select(cs => cs.StudentId)
            .ToListAsync();
        var memberSet = members.ToHashSet();

        HashSet<Guid> target;
        if (request.Audience == AssignmentAudience.EntireClass)
        {
            if (memberSet.Count == 0)
                return Fail($"{assignment.Class.Name} has no students yet. Invite students first.", assignment);
            target = memberSet;
        }
        else
        {
            target = request.StudentIds.ToHashSet();
            if (target.Count == 0)
                return Fail("Choose at least one student.", assignment);

            var outsiders = target.Count(id => !memberSet.Contains(id));
            if (outsiders > 0)
                return Fail(outsiders == 1
                    ? $"One of the chosen students is no longer in {assignment.Class.Name}. Check the list and try again."
                    : $"{outsiders} of the chosen students are no longer in {assignment.Class.Name}. Check the list and try again.",
                    assignment);
        }

        var current = await _db.AssignmentStudents
            .Where(x => x.AssignmentId == assignmentId)
            .ToListAsync();

        // A draft has no work handed in yet, so its recipient list can be replaced. Once active,
        // removing a student is the separate "remove from task" action, never a side effect of assigning.
        if (isDraft && request.Audience == AssignmentAudience.SelectedStudents)
            _db.AssignmentStudents.RemoveRange(current.Where(x => !target.Contains(x.StudentId)));

        var already = current.Select(x => x.StudentId).ToHashSet();
        var toAdd   = target.Where(id => !already.Contains(id)).ToList();
        foreach (var studentId in toAdd)
            _db.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = assignmentId, StudentId = studentId, AssignedAt = now });

        if (isDraft)
        {
            assignment.AvailableFrom = request.AvailableFrom;
            assignment.Deadline      = request.Deadline;
            assignment.Audience      = request.Audience;
            assignment.LateJoinPolicy = request.Audience == AssignmentAudience.EntireClass
                ? NormalisePolicy(request.LateJoin, assignment.Type)
                : LateJoinPolicy.Manual;
        }
        else if (request.Audience == AssignmentAudience.EntireClass)
        {
            assignment.Audience       = AssignmentAudience.EntireClass;
            assignment.LateJoinPolicy = NormalisePolicy(request.LateJoin, assignment.Type);
        }

        // Anyone given the work now no longer needs their waiting request.
        var resolved = await _db.LateJoinRequests
            .Where(r => r.AssignmentId == assignmentId && r.Status == LateJoinRequestStatus.Pending)
            .ToListAsync();
        foreach (var r in resolved.Where(r => target.Contains(r.StudentId)))
        {
            r.Status      = LateJoinRequestStatus.Approved;
            r.DecidedAt   = now;
            r.DecidedById = teacherId;
        }

        if (isDraft)
        {
            try { assignment.Publish(); }
            catch (InvalidOperationException ex) { return Fail(ex.Message, assignment); }
        }

        assignment.UpdatedAt = now;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        var recipients = (isDraft && request.Audience == AssignmentAudience.SelectedStudents)
            ? target.Count
            : already.Count + toAdd.Count;

        return new AssignOutcome(true, null, recipients, toAdd.Count, assignment.Class.Name, assignment.ClassId,
                                 assignment.AvailableFrom is { } from && from > now ? from : null, isDraft);

        AssignOutcome Fail(string message, Assignment? a = null)
        {
            // The transaction is rolled back when it is disposed uncommitted; forget the unsaved edits too,
            // so nothing later in this request can save half of the action by accident.
            var result = new AssignOutcome(false, message, 0, 0, a?.Class?.Name ?? "", a?.ClassId ?? Guid.Empty, null, false);
            _db.ChangeTracker.Clear();
            return result;
        }
    }

    private static LateJoinPolicy NormalisePolicy(LateJoinPolicy chosen, AssignmentType type)
        => chosen == LateJoinPolicy.Manual ? Assignment.SuggestedLateJoinPolicy(type) : chosen;

    private static int QuestionCount(Assignment a)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(a.Questions ?? "[]")?.Count ?? 0; }
        catch (System.Text.Json.JsonException) { return 0; }
    }

    // ── Students who join later ────────────────────────────────────────────────

    /// <summary>
    /// Applies each assignment's late-join rule to a student who has just become a member of the class.
    /// Must run inside the transaction that created the membership (see <see cref="AuthService"/>), so it is
    /// serialised against <see cref="AssignAsync"/>. Only active whole-class assignments that the student could
    /// still complete are considered; drafts, closed work and hand-picked assignments are never extended.
    /// Returns how many assignments were given and how many requests were queued.
    /// </summary>
    public async Task<(int Assigned, int Requested)> ApplyToNewMemberAsync(Guid classId, Guid studentId)
    {
        var now = DateTime.UtcNow;

        var candidates = await _db.Assignments
            .Where(a => a.ClassId == classId
                     && a.Status == AssignmentStatus.Active
                     && a.Audience == AssignmentAudience.EntireClass
                     && a.LateJoinPolicy != LateJoinPolicy.Manual)
            .ToListAsync();

        var eligible = candidates.Where(a => a.AcceptsNewRecipient(now)).ToList();
        if (eligible.Count == 0) return (0, 0);

        var ids = eligible.Select(a => a.Id).ToList();
        var alreadyAssigned = (await _db.AssignmentStudents
            .Where(x => x.StudentId == studentId && ids.Contains(x.AssignmentId))
            .Select(x => x.AssignmentId)
            .ToListAsync()).ToHashSet();
        var alreadyRequested = (await _db.LateJoinRequests
            .IgnoreQueryFilters()
            .Where(r => r.StudentId == studentId && ids.Contains(r.AssignmentId))
            .Select(r => r.AssignmentId)
            .ToListAsync()).ToHashSet();

        int assigned = 0, requested = 0;
        foreach (var a in eligible)
        {
            if (alreadyAssigned.Contains(a.Id)) continue;

            if (a.LateJoinPolicy == LateJoinPolicy.AutoAssign)
            {
                _db.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = a.Id, StudentId = studentId, AssignedAt = now });
                assigned++;
            }
            else if (!alreadyRequested.Contains(a.Id))
            {
                // A request already decided (e.g. declined before the student left and came back) stands.
                _db.LateJoinRequests.Add(new LateJoinRequest { AssignmentId = a.Id, StudentId = studentId, RequestedAt = now });
                requested++;
            }
        }

        await _db.SaveChangesAsync();
        return (assigned, requested);
    }

    // ── Waiting requests ───────────────────────────────────────────────────────

    /// <summary>Requests still waiting in one class, for a teacher who can see that class.</summary>
    public async Task<List<LateJoinRequest>> PendingForClassAsync(Guid classId, Guid teacherId)
    {
        var pending = await _db.LateJoinRequests
            .Include(r => r.Student)
            .Include(r => r.Assignment)
            .Where(r => r.Status == LateJoinRequestStatus.Pending
                     && r.Assignment.ClassId == classId
                     && (r.Assignment.Class.TeacherId == teacherId
                         || r.Assignment.Class.CoTeachers.Any(ct => ct.TeacherId == teacherId)))
            .OrderBy(r => r.RequestedAt)
            .ToListAsync();

        // Only students who are still in the class: someone who left meanwhile has nothing to approve.
        var members = (await _db.ClassStudents.Where(cs => cs.ClassId == classId).Select(cs => cs.StudentId).ToListAsync()).ToHashSet();
        return pending.Where(r => members.Contains(r.StudentId)).ToList();
    }

    public sealed record Decision(Guid RequestId, DateTime? DueOverride);

    public sealed record DecideOutcome(int Approved, int Declined, int Skipped, int Students, int Assignments, List<string> Notes);

    /// <summary>
    /// Gives each chosen request's student the assignment, re-checking everything at this moment:
    /// the request is still waiting, the student is still in the class, the assignment is still active
    /// and can still be done (deadline, or a later personal due date, or late submissions allowed).
    /// A request that no longer qualifies is skipped and stays as it was.
    /// </summary>
    public async Task<DecideOutcome> ApproveAsync(Guid classId, Guid teacherId, IReadOnlyList<Decision> decisions)
    {
        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();

        if (!await CanManageClassAsync(classId, teacherId))
            return new(0, 0, decisions.Count, 0, 0, new() { "Class not found." });

        var ids = decisions.Select(d => d.RequestId).Distinct().ToList();
        var requests = await _db.LateJoinRequests
            .Include(r => r.Assignment)
            .Include(r => r.Student)
            .Where(r => ids.Contains(r.Id) && r.Assignment.ClassId == classId)
            .ToDictionaryAsync(r => r.Id);

        var members = (await _db.ClassStudents.Where(cs => cs.ClassId == classId).Select(cs => cs.StudentId).ToListAsync()).ToHashSet();

        int approved = 0, skipped = 0;
        var notes = new List<string>();
        var pairs = new List<(Guid Student, Guid Assignment)>();

        foreach (var d in decisions.DistinctBy(d => d.RequestId))
        {
            if (!requests.TryGetValue(d.RequestId, out var r)) { skipped++; continue; }

            if (r.Status != LateJoinRequestStatus.Pending)
            {
                skipped++;
                notes.Add($"{r.Student.FullName} — {r.Assignment.Title}: already decided.");
                continue;
            }
            if (!members.Contains(r.StudentId))
            {
                skipped++;
                notes.Add($"{r.Student.FullName} has left the class.");
                continue;
            }
            if (!r.Assignment.AcceptsNewRecipient(now, d.DueOverride))
            {
                skipped++;
                notes.Add(r.Assignment.Status == AssignmentStatus.Active
                    ? $"{r.Assignment.Title} is past its due date — give {r.Student.FullName} a later due date to assign it."
                    : $"{r.Assignment.Title} is no longer active.");
                continue;
            }

            var existing = await _db.AssignmentStudents
                .FirstOrDefaultAsync(x => x.AssignmentId == r.AssignmentId && x.StudentId == r.StudentId);
            if (existing is null)
            {
                _db.AssignmentStudents.Add(new AssignmentStudent
                {
                    AssignmentId    = r.AssignmentId,
                    StudentId       = r.StudentId,
                    AssignedAt      = now,
                    DueDateOverride = d.DueOverride
                });
            }
            else if (d.DueOverride.HasValue)
            {
                existing.DueDateOverride = d.DueOverride;
            }

            r.Status      = LateJoinRequestStatus.Approved;
            r.DecidedAt   = now;
            r.DecidedById = teacherId;
            approved++;
            pairs.Add((r.StudentId, r.AssignmentId));
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return new(approved, 0, skipped,
                   pairs.Select(p => p.Student).Distinct().Count(),
                   pairs.Select(p => p.Assignment).Distinct().Count(), notes);
    }

    /// <summary>Closes the chosen waiting requests without giving the assignment.</summary>
    public async Task<DecideOutcome> DeclineAsync(Guid classId, Guid teacherId, IReadOnlyCollection<Guid> requestIds)
    {
        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();

        if (!await CanManageClassAsync(classId, teacherId))
            return new(0, 0, requestIds.Count, 0, 0, new() { "Class not found." });

        var ids = requestIds.Distinct().ToList();
        var requests = await _db.LateJoinRequests
            .Where(r => ids.Contains(r.Id) && r.Assignment.ClassId == classId)
            .ToListAsync();

        var declined = 0;
        foreach (var r in requests.Where(r => r.Status == LateJoinRequestStatus.Pending))
        {
            r.Status      = LateJoinRequestStatus.Declined;
            r.DecidedAt   = now;
            r.DecidedById = teacherId;
            declined++;
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return new(0, declined, ids.Count - declined, 0, 0, new());
    }

    /// <summary>
    /// Sets the late-join rule on an existing assignment — the explicit switch for work made before the rule
    /// existed. It only affects students who join from now on; nobody currently in the class is added.
    /// Choosing a rule other than <see cref="LateJoinPolicy.Manual"/> makes it a whole-class assignment.
    /// </summary>
    public async Task<bool> SetLateJoinPolicyAsync(Guid assignmentId, Guid teacherId, LateJoinPolicy policy)
    {
        var assignment = await _db.Assignments.AccessibleBy(teacherId).FirstOrDefaultAsync(a => a.Id == assignmentId);
        if (assignment is null) return false;

        assignment.LateJoinPolicy = policy;
        if (policy != LateJoinPolicy.Manual) assignment.Audience = AssignmentAudience.EntireClass;
        assignment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    private Task<bool> CanManageClassAsync(Guid classId, Guid teacherId)
        => _db.Classes.AccessibleBy(teacherId).AnyAsync(c => c.Id == classId);
}

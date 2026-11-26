using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;
using SmartSchoolAI.ViewModels;

namespace SmartSchoolAI.Services;

/// <summary>
/// Builds the teacher's analytics page for one class.
///
/// The unit of every number is one piece of <em>work</em>: one assignment given to one student (a row of
/// AssignmentStudents). Students in the class who were not given an assignment are not counted for it, and retakes
/// do not add work. For each piece of work, exactly one of:
///   Not submitted   — no saved final attempt (a draft in progress is still "not submitted");
///   Awaiting review — submitted, and an attempt is in the teacher's review queue (<see cref="ReviewRules"/>) or the
///                     AI is still marking the newest attempt. A retake waiting for review puts the work here once,
///                     even when an earlier attempt was graded;
///   Graded          — submitted and nothing is waiting. AI marks are final unless the AI flagged an answer, as in
///                     the rest of the app.
/// So Assigned = Not submitted + Awaiting review + Graded and Submitted = Awaiting review + Graded.
/// The score is that of the newest submitted attempt — the gradebook, Student Work, the class statistics and the
/// CSV all use it — and averages include graded work only (a graded 0% counts; missing work does not).
/// Overdue = not submitted and past the student's due date (their personal one when set).
/// The period filter goes by the date each student was given the work, so a late joiner counts from the day they
/// got it. Only stored marks are read: opening analytics never calls the AI.
/// A fixed handful of queries load everything; nothing is queried per student or per question.
/// </summary>
public class AnalyticsService
{
    private readonly AppDbContext _db;

    public AnalyticsService(AppDbContext db) => _db = db;

    /// <summary>Averages below this mark a student on the Students tab.</summary>
    public const int StrugglingBelow = 50;

    /// <summary>A question is suggested for revisiting when its graded answers average below this share of the marks.</summary>
    private const decimal RevisitBelow = 0.7m;
    private const int RevisitMinAnswers = 3;

    public sealed record Request(
        Guid? ClassId, string? Tab, string? Type, string? Period,
        string? Status = null, Guid? AssignmentId = null, Guid? StudentId = null, Guid? QuestionAssignment = null);

    /// <summary>The page for this teacher, or null when the class asked for is not one they teach (owner or co-teacher).</summary>
    public async Task<AnalyticsViewModel?> BuildAsync(Guid teacherId, Request request, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var model = new AnalyticsViewModel
        {
            Classes = await _db.Classes.AccessibleBy(teacherId).Where(c => !c.IsArchived)
                               .OrderBy(c => c.Name).ToListAsync()
        };
        NormaliseFilters(model, request);

        if (request.ClassId is { } asked)
        {
            // Checked on the server before anything of the class is loaded: a class the teacher does not teach is
            // "not found", whether it exists or not.
            if (!model.Classes.Any(c => c.Id == asked))
            {
                var archivedButMine = await _db.Classes.AccessibleBy(teacherId).Where(c => c.Id == asked).FirstOrDefaultAsync();
                if (archivedButMine is null) return null;
                model.Classes.Add(archivedButMine);
            }
            model.SelectedClassId = asked;
        }
        else
        {
            model.SelectedClassId = model.Classes.FirstOrDefault()?.Id;
        }
        if (model.SelectedClassId is not { } classId) return model;

        // 1. The class's published assignments (drafts have no work yet).
        var allAssignments = await _db.Assignments
            .Where(a => a.ClassId == classId && a.Status != AssignmentStatus.Draft)
            .Select(a => new { a.Id, a.Title, a.Type, a.Status, a.Deadline, a.CreatedAt, a.AllowLateSubmission })
            .ToListAsync();
        model.ClassAssignmentCount = allAssignments.Count;

        var assignments = allAssignments
            .Where(a => model.Type == "all"
                     || (model.Type == "homework" ? a.Type == AssignmentType.Homework : a.Type == AssignmentType.ClassroomPractice))
            .OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Title)
            .ToList();
        var ids = assignments.Select(a => a.Id).ToList();

        // 2. Who was given each of them (the work), within the period.
        DateTime? since = model.Period switch { "7d" => now.AddDays(-7), "30d" => now.AddDays(-30), "90d" => now.AddDays(-90), _ => null };
        var given = await _db.AssignmentStudents
            .Where(x => ids.Contains(x.AssignmentId) && (since == null || x.AssignedAt >= since))
            .Select(x => new { x.AssignmentId, x.StudentId, x.AssignedAt, x.DueDateOverride, x.Student.FullName, x.Student.Email })
            .ToListAsync();

        // 3. Every attempt at those assignments, with whether it is in the review queue.
        var attempts = await _db.StudentResults
            .Where(r => ids.Contains(r.AssignmentId))
            .Select(r => new
            {
                r.Id, r.AssignmentId, r.StudentId, r.AttemptNumber, r.IsSubmitted, r.Score, r.GradingPending,
                Flagged = r.IsSubmitted && r.StudentAnswers.Any(a => a.NeedsReview)
            })
            .ToListAsync();
        var attemptsOf = attempts.GroupBy(r => (r.AssignmentId, r.StudentId))
                                 .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.AttemptNumber).ToList());

        var byId = assignments.ToDictionary(a => a.Id);
        model.Work = given.Select(g =>
        {
            var a       = byId[g.AssignmentId];
            var mine    = attemptsOf.TryGetValue((g.AssignmentId, g.StudentId), out var list) ? list : new();
            var latest  = mine.FirstOrDefault(r => r.IsSubmitted);
            var waiting = latest is not null && (mine.Any(r => r.Flagged) || latest.GradingPending);
            var due     = g.DueDateOverride ?? a.Deadline;
            return new AnalyticsWork
            {
                AssignmentId      = a.Id,
                AssignmentTitle   = a.Title,
                AssignmentType    = a.Type,
                StudentId         = g.StudentId,
                StudentName       = g.FullName,
                AssignedAt        = g.AssignedAt,
                Due               = due,
                PersonalDue       = g.DueDateOverride.HasValue,
                Status            = latest is null ? AnalyticsWorkStatus.NotSubmitted
                                  : waiting        ? AnalyticsWorkStatus.AwaitingReview
                                  :                  AnalyticsWorkStatus.Graded,
                LatestResultId    = latest?.Id,
                Score             = latest?.Score,
                SubmittedAttempts = mine.Count(r => r.IsSubmitted),
                DraftOpen         = mine.Any(r => !r.IsSubmitted),
                MarkingPending    = latest?.GradingPending == true,
                Overdue           = latest is null && due is { } d && d < now
            };
        }).ToList();

        // Assignments tab and the score-by-assignment chart: only assignments with work in the filter.
        model.Assignments = assignments
            .Select(a =>
            {
                var work   = model.Work.Where(w => w.AssignmentId == a.Id).ToList();
                var graded = work.Where(w => w.Status == AnalyticsWorkStatus.Graded).ToList();
                return new AnalyticsAssignmentRow
                {
                    Id = a.Id, Title = a.Title, Type = a.Type, Status = a.Status, Deadline = a.Deadline,
                    HasPersonalDue = work.Any(w => w.PersonalDue),
                    Recipients = work.Count,
                    Submitted  = work.Count(w => w.Status != AnalyticsWorkStatus.NotSubmitted),
                    Awaiting   = work.Count(w => w.Status == AnalyticsWorkStatus.AwaitingReview),
                    Graded     = graded.Count,
                    Overdue    = work.Count(w => w.Overdue),
                    AverageScore = Mean(graded.Select(w => w.Score!.Value))
                };
            })
            .Where(r => r.Recipients > 0)
            .ToList();

        // 4. Students: everyone in the class, and anyone else who still holds work here.
        var members = await _db.ClassStudents
            .Where(cs => cs.ClassId == classId)
            .Select(cs => new { cs.StudentId, cs.Student.FullName, cs.Student.Email })
            .ToListAsync();
        var people = members.Select(m => (m.StudentId, m.FullName, m.Email))
            .Concat(given.Select(g => (g.StudentId, g.FullName, g.Email)))
            .DistinctBy(p => p.StudentId)
            .ToList();
        model.Students = people.Select(p =>
        {
            var work      = model.Work.Where(w => w.StudentId == p.StudentId).ToList();
            var graded    = work.Where(w => w.Status == AnalyticsWorkStatus.Graded).ToList();
            var practice  = graded.Where(w => w.AssignmentType == AssignmentType.ClassroomPractice).ToList();
            var homework  = graded.Where(w => w.AssignmentType == AssignmentType.Homework).ToList();
            return new AnalyticsStudentRow
            {
                StudentId = p.StudentId, FullName = p.FullName, Email = p.Email,
                Assigned  = work.Count,
                Submitted = work.Count(w => w.Status != AnalyticsWorkStatus.NotSubmitted),
                Awaiting  = work.Count(w => w.Status == AnalyticsWorkStatus.AwaitingReview),
                Overdue   = work.Count(w => w.Overdue),
                PracticeAverage = Mean(practice.Select(w => w.Score!.Value)), PracticeGraded = practice.Count,
                HomeworkAverage = Mean(homework.Select(w => w.Score!.Value)), HomeworkGraded = homework.Count,
                OverallAverage  = Mean(graded.Select(w => w.Score!.Value))
            };
        })
        .OrderBy(s => s.FullName).ThenBy(s => s.Email)
        .ToList();

        // 5. Answers of the newest submitted attempts (questions tab and the "revisit" suggestion), one query.
        var latestIds = model.Work.Where(w => w.LatestResultId is not null).Select(w => w.LatestResultId!.Value).ToList();
        var answers = latestIds.Count == 0 ? new List<AnswerRow>() : await _db.StudentAnswers
            .Where(a => latestIds.Contains(a.StudentResultId))
            .Select(a => new AnswerRow(a.StudentResultId, a.QuestionIndex, a.AnswerValue, a.IsCorrect, a.PointsAwarded,
                                       a.NeedsReview, a.ImagePath, a.SketchPath))
            .ToListAsync();

        // Question texts for the assignments with answers (and the one picked on the Questions tab), one query.
        model.QuestionAssignmentId = request.QuestionAssignment is { } qa && model.Assignments.Any(a => a.Id == qa)
            ? qa
            : model.Assignments.FirstOrDefault(a => a.Submitted > 0)?.Id ?? model.Assignments.FirstOrDefault()?.Id;
        var resultToWork = model.Work.Where(w => w.LatestResultId is not null).ToDictionary(w => w.LatestResultId!.Value);
        var answeredAssignments = answers.Select(a => resultToWork[a.ResultId].AssignmentId).ToHashSet();
        if (model.QuestionAssignmentId is { } picked) answeredAssignments.Add(picked);
        var questionJson = await _db.Assignments
            .Where(a => answeredAssignments.Contains(a.Id))
            .Select(a => new { a.Id, a.Questions })
            .ToDictionaryAsync(a => a.Id, a => a.Questions);
        var questionsOf = questionJson.ToDictionary(kv => kv.Key, kv => ParseQuestions(kv.Value));

        var rowsByAssignment = questionsOf.ToDictionary(
            kv => kv.Key,
            kv => BuildQuestionRows(kv.Value, answers.Where(a => resultToWork[a.ResultId].AssignmentId == kv.Key), resultToWork));
        if (model.QuestionAssignmentId is { } shown && rowsByAssignment.TryGetValue(shown, out var shownRows))
            model.Questions = shownRows;

        model.Actions = BuildActions(model, rowsByAssignment);
        return model;
    }

    private sealed record AnswerRow(Guid ResultId, int QuestionIndex, string Value, bool? IsCorrect, decimal? Points,
                                    bool NeedsReview, string? ImagePath, string? SketchPath);

    private static void NormaliseFilters(AnalyticsViewModel model, Request r)
    {
        // Old links: ?tab=practice / ?tab=assessment were the type, on the one page there was.
        var tab  = (r.Tab ?? "overview").ToLowerInvariant();
        var type = (r.Type ?? "all").ToLowerInvariant();
        if (tab == "practice")   { tab = "overview"; if (r.Type is null) type = "practice"; }
        if (tab == "assessment") { tab = "overview"; if (r.Type is null) type = "homework"; }

        model.Tab    = tab is "overview" or "assignments" or "students" or "questions" or "work" ? tab : "overview";
        model.Type   = type is "practice" or "homework" ? type : "all";
        model.Period = (r.Period ?? "all").ToLowerInvariant() is var p && p is "7d" or "30d" or "90d" ? p : "all";
        var status   = (r.Status ?? "all").ToLowerInvariant();
        model.WorkStatus = status is "not-submitted" or "submitted" or "awaiting" or "graded" or "overdue" ? status : "all";
        model.WorkAssignmentId = r.AssignmentId;
        model.WorkStudentId    = r.StudentId;
    }

    private static List<QuestionDto> ParseQuestions(string json)
    {
        try { return JsonSerializer.Deserialize<List<QuestionDto>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>A final mark: the teacher's or the AI's, and not waiting in the review queue.</summary>
    private static bool IsGraded(AnswerRow a) => !a.NeedsReview && (a.Points is not null || a.IsCorrect is not null);

    private static decimal PointsOf(AnswerRow a, decimal max) => a.Points ?? (a.IsCorrect == true ? max : 0m);

    private static bool HasContent(AnswerRow a)
        => !string.IsNullOrWhiteSpace(a.Value) || !string.IsNullOrEmpty(a.ImagePath) || !string.IsNullOrEmpty(a.SketchPath);

    private static List<AnalyticsQuestionRow> BuildQuestionRows(
        List<QuestionDto> questions, IEnumerable<AnswerRow> answers, Dictionary<Guid, AnalyticsWork> resultToWork)
    {
        var layout  = QuestionLayout.Compute(questions);
        var byIndex = answers.Where(HasContent).GroupBy(a => a.QuestionIndex).ToDictionary(g => g.Key, g => g.ToList());

        return questions.Select((q, i) =>
        {
            var list   = byIndex.TryGetValue(i, out var l) ? l : new();
            var max    = q.MaxPoints;
            var graded = list.Where(IsGraded).ToList();
            return new AnalyticsQuestionRow
            {
                Index = i, Label = layout.Labels[i], Title = q.Title,
                IsStem = layout.IsStem[i], IsPart = layout.ParentOf[i] >= 0,
                MaxPoints     = max,
                Answered      = list.Count,
                GradedAnswers = graded.Count,
                AveragePoints = graded.Count == 0 ? null : Math.Round(graded.Average(a => PointsOf(a, max)), 2),
                Answers = list.Select(a => new AnalyticsAnswer
                {
                    ResultId    = a.ResultId,
                    StudentId   = resultToWork[a.ResultId].StudentId,
                    StudentName = resultToWork[a.ResultId].StudentName,
                    Value       = a.Value,
                    Points      = IsGraded(a) ? PointsOf(a, max) : null,
                    Graded      = IsGraded(a),
                    NeedsReview = a.NeedsReview,
                    ImagePath   = a.ImagePath,
                    SketchPath  = a.SketchPath
                })
                .OrderBy(a => a.StudentName)
                .ToList()
            };
        }).ToList();
    }

    private static List<AnalyticsAction> BuildActions(
        AnalyticsViewModel m, Dictionary<Guid, List<AnalyticsQuestionRow>> questionRows)
    {
        var actions = new List<AnalyticsAction>();
        if (m.Assigned == 0) return actions;

        if (m.AwaitingReview > 0)
        {
            var marking = m.Work.Count(w => w.Status == AnalyticsWorkStatus.AwaitingReview && w.MarkingPending);
            actions.Add(new AnalyticsAction
            {
                Icon = "bx-task", Tone = "warning",
                Title  = $"Review {Plural(m.AwaitingReview, "submitted assignment")}",
                Detail = marking > 0 ? $"{m.AwaitingReview} awaiting review ({marking} still being marked by the AI)"
                                     : $"{Plural(m.AwaitingReview, "submitted assignment")} {(m.AwaitingReview == 1 ? "needs" : "need")} your review",
                Button = "Open review list", Href = m.Link("work", "awaiting")
            });
        }

        if (m.Overdue > 0)
        {
            var students = m.Work.Where(w => w.Overdue).Select(w => w.StudentId).Distinct().Count();
            actions.Add(new AnalyticsAction
            {
                Icon = "bx-calendar-x", Tone = "danger",
                Title  = "Follow up on overdue work",
                Detail = $"{Plural(m.Overdue, "assignment")} past due, not submitted, from {Plural(students, "student")}",
                Button = "View overdue work", Href = m.Link("work", "overdue")
            });
        }

        // The weakest question with enough graded answers to mean something.
        var weakest = m.Assignments
            .Where(a => questionRows.ContainsKey(a.Id))
            .SelectMany(a => questionRows[a.Id].Where(q => !q.IsStem && q.GradedAnswers >= RevisitMinAnswers && q.MaxPoints > 0
                                                         && q.AveragePoints is not null)
                                               .Select(q => (Assignment: a, Question: q, Share: q.AveragePoints!.Value / q.MaxPoints)))
            .Where(x => x.Share < RevisitBelow)
            .OrderBy(x => x.Share).ThenByDescending(x => x.Question.GradedAnswers)
            .FirstOrDefault();
        if (weakest.Question is not null)
        {
            actions.Add(new AnalyticsAction
            {
                Icon = "bx-bar-chart-alt-2", Tone = "primary",
                Title  = $"Revisit question {weakest.Question.Label} of “{weakest.Assignment.Title}”",
                Detail = $"Average {Marks(weakest.Question.AveragePoints!.Value)} of {Marks(weakest.Question.MaxPoints)} " +
                         $"over {Plural(weakest.Question.GradedAnswers, "graded answer")}",
                Button = "View question",
                Href   = m.Link("questions", questionAssignment: weakest.Assignment.Id) + $"#q-{weakest.Question.Index}"
            });
        }

        // Work given but nothing handed in yet: show what is coming and when it is due.
        if (m.Submitted == 0)
        {
            foreach (var a in m.Assignments.OrderBy(a => a.Deadline ?? DateTime.MaxValue).Take(3))
                actions.Add(new AnalyticsAction
                {
                    Icon = "bx-calendar", Tone = "secondary",
                    Title  = $"Track “{a.Title}”",
                    Detail = $"{a.Submitted} of {a.Recipients} submitted" +
                             (a.Deadline is { } d ? $" · due {SchoolTime.Show(d, "dd MMM, HH:mm")}" : " · no due date") +
                             (a.HasPersonalDue ? " (some students have their own due date)" : ""),
                    Button = $"View {Plural(a.Recipients, "student")}", Href = m.Link("work", assignmentId: a.Id)
                });
        }

        if (actions.Count == 0)
            actions.Add(new AnalyticsAction
            {
                Icon = "bx-check-circle", Tone = "success",
                Title = "All caught up", Detail = "All submitted work is graded and nothing is overdue."
            });
        return actions;
    }

    private static int? Mean(IEnumerable<int> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : (int)Math.Round(list.Average(), MidpointRounding.AwayFromZero);
    }

    public static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    public static string Marks(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSchoolAI.Tests.TestHelpers;

namespace SmartSchoolAI.Tests.Services;

/// <summary>
/// The AddAssignmentAudienceAndLateJoin migration on a database of the previous version full of real-looking data:
/// every row keeps its id, links and values, every existing assignment keeps the manual way of giving out work, and
/// the migration (and a second start) creates no assignment and no request.
/// </summary>
public class AudienceMigrationTests : IDisposable
{
    private const string Previous = "20261123140707_AddAnswerSketchPath";
    private readonly FileDb _db = new(migrate: false);

    public void Dispose() => _db.Dispose();

    private static string G() => Guid.NewGuid().ToString().ToUpperInvariant();
    private const string Now = "2026-11-20 09:00:00";

    /// <summary>Inserts a row, filling any other NOT NULL column without a default with a neutral value.</summary>
    private static void Insert(SqliteConnection c, string table, Dictionary<string, object?> values)
    {
        var columns = new List<(string name, string type, bool notNull, bool hasDefault)>();
        using (var info = c.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var r = info.ExecuteReader();
            while (r.Read()) columns.Add((r.GetString(1), r.GetString(2), r.GetInt32(3) == 1, !r.IsDBNull(4)));
        }
        foreach (var col in columns.Where(x => x.notNull && !x.hasDefault && !values.ContainsKey(x.name)))
            values[col.name] = col.type.ToUpperInvariant() switch { "INTEGER" => 0, "REAL" => 0.0, _ => "" };

        using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO \"{table}\" ({string.Join(",", values.Keys.Select(k => $"\"{k}\""))}) " +
                          $"VALUES ({string.Join(",", values.Keys.Select((_, i) => "$p" + i))})";
        var n = 0;
        foreach (var v in values.Values) cmd.Parameters.AddWithValue("$p" + n++, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static string RoleId(SqliteConnection c, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Roles WHERE Name = $n";
        cmd.Parameters.AddWithValue("$n", name);
        return (string)cmd.ExecuteScalar()!;
    }

    private static List<string> Tables(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY name";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Every row of a table as "col=value|…" over the given columns, sorted — an exact, order-free fingerprint.</summary>
    private static List<string> Rows(SqliteConnection c, string table, IReadOnlyList<string>? onlyColumns = null)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{table}\"";
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
        {
            var parts = new List<string>();
            for (var i = 0; i < r.FieldCount; i++)
            {
                var name = r.GetName(i);
                if (onlyColumns is not null && !onlyColumns.Contains(name)) continue;
                parts.Add($"{name}={(r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))}");
            }
            rows.Add(string.Join("|", parts));
        }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static List<string> Columns(SqliteConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(1));
        return list;
    }

    /// <summary>What each student can open: active assignments they are a recipient of, in a class they are in.</summary>
    private static List<string> Access(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT x.StudentId || '>' || a.Id FROM AssignmentStudents x
            JOIN Assignments a ON a.Id = x.AssignmentId
            JOIN ClassStudents cs ON cs.ClassId = a.ClassId AND cs.StudentId = x.StudentId
            WHERE a.Status = 'Active' AND a.DeletedAt IS NULL ORDER BY 1
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    [Fact]
    public void Migration_KeepsEveryRowAndTheManualWayOfGivingOutWork_AndCreatesNothing()
    {
        // 1. The previous version's schema.
        using (var ctx = _db.NewContext())
            ctx.GetService<IMigrator>().Migrate(Previous);

        // 2. Its data: two classes, hand-picked recipients, a personal due date, a draft answer, two attempts,
        //    a teacher's own grade and a link to an uploaded photo.
        string teacher = G(), coTeacher = G(), s1 = G(), s2 = G(), s3 = G(), s4 = G();
        string classA = G(), classB = G(), active = G(), draft = G(), closed = G(), other = G();
        string attempt1 = G(), attempt2 = G(), draftResult = G();
        using (var c = _db.Open())
        {
            var teacherRole = RoleId(c, "Teacher");
            var studentRole = RoleId(c, "Student");
            foreach (var (id, role, name) in new[] { (teacher, teacherRole, "Teacher"), (coTeacher, teacherRole, "Co"),
                         (s1, studentRole, "S1"), (s2, studentRole, "S2"), (s3, studentRole, "S3"), (s4, studentRole, "S4") })
                Insert(c, "Users", new() { ["Id"] = id, ["Email"] = $"{name.ToLower()}@x.test", ["FullName"] = name, ["PasswordHash"] = "h",
                                           ["RoleId"] = role, ["IsActive"] = 1, ["CreatedAt"] = Now });

            Insert(c, "Classes", new() { ["Id"] = classA, ["Name"] = "Year 12 IT", ["Subject"] = "IT", ["AcademicYear"] = "2026-2027",
                                         ["InviteCode"] = "AAAAAA", ["TeacherId"] = teacher, ["MaxStudents"] = 50, ["CreatedAt"] = Now, ["EnabledLanguages"] = "[]" });
            Insert(c, "Classes", new() { ["Id"] = classB, ["Name"] = "Year 11 Maths", ["Subject"] = "Maths", ["AcademicYear"] = "2026-2027",
                                         ["InviteCode"] = "BBBBBB", ["TeacherId"] = teacher, ["MaxStudents"] = 50, ["CreatedAt"] = Now, ["EnabledLanguages"] = "[]" });
            Insert(c, "ClassTeachers", new() { ["ClassId"] = classA, ["TeacherId"] = coTeacher });
            foreach (var s in new[] { s1, s2, s3 })
                Insert(c, "ClassStudents", new() { ["ClassId"] = classA, ["StudentId"] = s, ["JoinedAt"] = Now });
            Insert(c, "ClassStudents", new() { ["ClassId"] = classB, ["StudentId"] = s4, ["JoinedAt"] = Now });

            void Task(string id, string cls, string status, string type) => Insert(c, "Assignments", new()
            {
                ["Id"] = id, ["Title"] = "Task " + id[..4], ["ClassId"] = cls, ["TeacherId"] = teacher, ["Status"] = status, ["Type"] = type,
                ["Questions"] = "[{\"id\":\"q1\",\"title\":\"2+2\",\"type\":\"0\",\"answerKey\":\"4\",\"marks\":3}]",
                ["SkillIds"] = "[]", ["GradingType"] = "Percentage", ["PassThreshold"] = 70, ["MaxAttempts"] = 2,
                ["Deadline"] = "2026-12-01 10:00:00", ["CreatedAt"] = Now
            });
            Task(active, classA, "Active", "Homework");
            Task(draft, classA, "Draft", "ClassroomPractice");
            Task(closed, classA, "Closed", "ClassroomPractice");
            Task(other, classB, "Active", "ClassroomPractice");

            // Hand-picked: all three of class A have the active one (no proof a fourth should); s1 has a personal due date.
            Insert(c, "AssignmentStudents", new() { ["AssignmentId"] = active, ["StudentId"] = s1, ["AssignedAt"] = Now, ["DueDateOverride"] = "2026-12-05 10:00:00" });
            Insert(c, "AssignmentStudents", new() { ["AssignmentId"] = active, ["StudentId"] = s2, ["AssignedAt"] = Now });
            Insert(c, "AssignmentStudents", new() { ["AssignmentId"] = active, ["StudentId"] = s3, ["AssignedAt"] = Now });
            foreach (var s in new[] { s1, s2, s3 })   // an old draft with three chosen students
                Insert(c, "AssignmentStudents", new() { ["AssignmentId"] = draft, ["StudentId"] = s, ["AssignedAt"] = Now });
            Insert(c, "AssignmentStudents", new() { ["AssignmentId"] = closed, ["StudentId"] = s2, ["AssignedAt"] = Now });
            Insert(c, "AssignmentStudents", new() { ["AssignmentId"] = other, ["StudentId"] = s4, ["AssignedAt"] = Now });

            void Result(string id, string student, int attempt, int submitted, int score) => Insert(c, "StudentResults", new()
            {
                ["Id"] = id, ["AssignmentId"] = active, ["StudentId"] = student, ["AttemptNumber"] = attempt, ["IsSubmitted"] = submitted,
                ["Score"] = score, ["OverallStatus"] = score >= 70 ? "Correct" : "Incorrect", ["SubmittedAt"] = Now, ["CreatedAt"] = Now,
                ["BonusPoints"] = "0.0", ["ImagePath"] = ""
            });
            Result(attempt1, s1, 1, 1, 0);
            Result(attempt2, s1, 2, 1, 100);
            Result(draftResult, s2, 1, 0, 0);
            Insert(c, "StudentAnswers", new() { ["Id"] = G(), ["StudentResultId"] = attempt2, ["QuestionIndex"] = 0, ["QuestionId"] = "q1",
                                                ["AnswerValue"] = "4", ["IsCorrect"] = 1, ["PointsAwarded"] = "3.0", ["TeacherOverridden"] = 1,
                                                ["TeacherComment"] = "Good", ["ImagePath"] = "/uploads/submissions/" + s1 + "/photo.jpg", ["CreatedAt"] = Now });
            Insert(c, "StudentAnswers", new() { ["Id"] = G(), ["StudentResultId"] = draftResult, ["QuestionIndex"] = 0, ["QuestionId"] = "q1",
                                                ["AnswerValue"] = "draft", ["CreatedAt"] = Now });
        }

        // 3. Fingerprint before.
        Dictionary<string, (List<string> columns, List<string> rows)> before;
        List<string> accessBefore;
        using (var c = _db.Open())
        {
            before = Tables(c).ToDictionary(t => t, t => (Columns(c, t), Rows(c, t)));
            accessBefore = Access(c);
        }
        Assert.Equal(4, accessBefore.Count);   // 3 recipients of the active task in class A + 1 in class B

        // 4. The update, then a second start (Program.cs calls Database.Migrate() on every start).
        using (var ctx = _db.NewContext()) ctx.Database.Migrate();
        using (var ctx = _db.NewContext()) ctx.Database.Migrate();

        // 5. Compare: same ids, links and values in every old column of every old table.
        using (var c = _db.Open())
        {
            foreach (var (table, (columns, rows)) in before)
                Assert.True(rows.SequenceEqual(Rows(c, table, columns)), $"table {table} changed");

            Assert.Equal(accessBefore, Access(c));

            // Every existing assignment, active ones included, keeps the manual way.
            Assert.Equal(new[] { "Audience=SelectedStudents|LateJoinPolicy=Manual" },
                         Rows(c, "Assignments", new[] { "Audience", "LateJoinPolicy" }).Distinct());

            // Nothing new was given out or queued.
            Assert.Empty(Rows(c, "LateJoinRequests"));
            Assert.Equal(before["AssignmentStudents"].rows.Count, Rows(c, "AssignmentStudents").Count);
        }

        // 6. The model reads every migrated row back (no unreadable enum values).
        using (var ctx = _db.NewContext())
        {
            var all = ctx.Assignments.IgnoreQueryFilters().ToList();
            Assert.Equal(4, all.Count);
            Assert.All(all, a => Assert.False(a.FollowsNewClassMembers));
        }
    }
}

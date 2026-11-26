using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Data;

/// <summary>
/// Application database context.
/// All entity configurations live here via Fluent API (no data annotations on models).
/// Global soft-delete query filters are applied so deleted records are never returned.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // ── DbSets ────────────────────────────────────────────────────────────────
    public DbSet<User>          Users          { get; set; }
    public DbSet<Role>          Roles          { get; set; }
    public DbSet<Class>         Classes        { get; set; }
    public DbSet<ClassStudent>  ClassStudents  { get; set; }
    public DbSet<ClassTeacher>  ClassTeachers  { get; set; }
    public DbSet<Assignment>    Assignments    { get; set; }
    public DbSet<StudentResult> StudentResults { get; set; }
    public DbSet<StepResult>    StepResults    { get; set; }
    public DbSet<ChatSession>   ChatSessions   { get; set; }
    public DbSet<Skill>         Skills         { get; set; }
    public DbSet<ContentItem>   ContentItems   { get; set; }
    public DbSet<AuditLog>      AuditLogs      { get; set; }
    public DbSet<Question>       Questions       { get; set; }
    public DbSet<StudentAnswer>       StudentAnswers       { get; set; }
    public DbSet<AssignmentStudent>   AssignmentStudents   { get; set; }
    public DbSet<LateJoinRequest>     LateJoinRequests     { get; set; }
    public DbSet<QuestionReport>      QuestionReports      { get; set; }
    public DbSet<SupportTicket>       SupportTickets       { get; set; }
    public DbSet<AiStageResult>       AiStageResults       { get; set; }
    public DbSet<AiCallLog>           AiCallLogs           { get; set; }
    public DbSet<AttemptActivity>        AttemptActivities        { get; set; }
    public DbSet<AttemptActivitySummary> AttemptActivitySummaries { get; set; }
    public DbSet<SyllabusProgram>   SyllabusPrograms   { get; set; }
    public DbSet<SyllabusVersion>   SyllabusVersions   { get; set; }
    public DbSet<SyllabusComponent> SyllabusComponents { get; set; }
    public DbSet<SyllabusTopic>     SyllabusTopics     { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Finished AI stages are looked up by their key; one row per key, so two racing requests cannot both store one.
        modelBuilder.Entity<AiStageResult>(e =>
        {
            e.HasIndex(x => x.CacheKey).IsUnique();
            e.Property(x => x.CacheKey).HasMaxLength(100);
        });
        // A student's actions per attempt: read by attempt, in time order; a repeated delivery is found by its id.
        modelBuilder.Entity<AttemptActivity>(e =>
        {
            e.HasIndex(x => new { x.AssignmentId, x.StudentId, x.AttemptNumber, x.OccurredAt });
            e.HasIndex(x => new { x.StudentId, x.ClientId }).IsUnique().HasFilter("ClientId IS NOT NULL");
            // Ids as 16-byte blobs and times as 8-byte ticks (UTC) instead of 36- and ~27-character text: this table can
            // hold many rows per attempt, and its indexes repeat the ids. (The daily clean-up scans it once; no index.)
            var bytes = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.GuidToBytesConverter();
            e.Property(x => x.AssignmentId).HasConversion(bytes);
            e.Property(x => x.StudentId).HasConversion(bytes);
            e.Property(x => x.ClientId).HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<Guid?, byte[]?>(
                v => v.HasValue ? v.Value.ToByteArray() : null, v => v == null ? null : new Guid(v)));
            var utc = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, long>(
                v => v.ToUniversalTime().Ticks, v => new DateTime(v, DateTimeKind.Utc));
            e.Property(x => x.OccurredAt).HasConversion(utc);
            e.Property(x => x.ReceivedAt).HasConversion(utc);
        });
        modelBuilder.Entity<AttemptActivitySummary>(e =>
            e.HasIndex(x => new { x.AssignmentId, x.StudentId, x.AttemptNumber }).IsUnique());
        modelBuilder.Entity<AiCallLog>(e =>
        {
            e.HasIndex(x => x.CacheKey);
            e.HasIndex(x => x.ResultId);
            e.Property(x => x.Cost).HasPrecision(18, 8);
        });

        ConfigureUser(modelBuilder);
        ConfigureRole(modelBuilder);
        ConfigureClass(modelBuilder);
        ConfigureClassStudent(modelBuilder);
        ConfigureClassTeacher(modelBuilder);
        ConfigureAssignment(modelBuilder);
        ConfigureStudentResult(modelBuilder);
        ConfigureStepResult(modelBuilder);
        ConfigureChatSession(modelBuilder);
        ConfigureSkill(modelBuilder);
        ConfigureContentItem(modelBuilder);
        ConfigureAuditLog(modelBuilder);
        ConfigureQuestion(modelBuilder);
        ConfigureSyllabus(modelBuilder);
        ConfigureStudentAnswer(modelBuilder);
        ConfigureAssignmentStudent(modelBuilder);
        ConfigureLateJoinRequest(modelBuilder);
        ConfigureQuestionReport(modelBuilder);

        SeedRoles(modelBuilder);
        SeedSkills(modelBuilder);
    }

    // ── Entity Configurations ─────────────────────────────────────────────────

    private static void ConfigureUser(ModelBuilder mb)
    {
        mb.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(255).IsRequired();
            e.Property(u => u.PasswordHash).HasMaxLength(255).IsRequired();
            e.Property(u => u.FullName).HasMaxLength(200).IsRequired();
            e.Property(u => u.AvatarUrl).HasMaxLength(500);

            e.HasOne(u => u.Role)
             .WithMany(r => r.Users)
             .HasForeignKey(u => u.RoleId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasQueryFilter(u => u.DeletedAt == null);
        });
    }

    private static void ConfigureRole(ModelBuilder mb)
    {
        mb.Entity<Role>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Name).IsUnique();
            e.Property(r => r.Name).HasMaxLength(50).IsRequired();
        });
    }

    private static void ConfigureClass(ModelBuilder mb)
    {
        mb.Entity<Class>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.InviteCode).IsUnique();
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.Subject).HasMaxLength(100).IsRequired();
            e.Property(c => c.AcademicYear).HasMaxLength(20).IsRequired();
            e.Property(c => c.InviteCode).HasMaxLength(10).IsRequired();

            e.HasOne(c => c.Teacher)
             .WithMany(u => u.TeacherClasses)
             .HasForeignKey(c => c.TeacherId)
             .OnDelete(DeleteBehavior.Restrict);

            // Composite unique: a teacher cannot have two classes with the same name in the same year
            e.HasIndex(c => new { c.TeacherId, c.Name, c.AcademicYear }).IsUnique();

            e.HasQueryFilter(c => c.DeletedAt == null);
        });
    }

    private static void ConfigureClassStudent(ModelBuilder mb)
    {
        mb.Entity<ClassStudent>(e =>
        {
            e.HasKey(cs => new { cs.ClassId, cs.StudentId });

            e.HasOne(cs => cs.Class)
             .WithMany(c => c.ClassStudents)
             .HasForeignKey(cs => cs.ClassId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(cs => cs.Student)
             .WithMany()
             .HasForeignKey(cs => cs.StudentId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureClassTeacher(ModelBuilder mb)
    {
        mb.Entity<ClassTeacher>(e =>
        {
            e.HasKey(ct => new { ct.ClassId, ct.TeacherId });

            e.HasOne(ct => ct.Class)
             .WithMany(c => c.CoTeachers)
             .HasForeignKey(ct => ct.ClassId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(ct => ct.Teacher)
             .WithMany()
             .HasForeignKey(ct => ct.TeacherId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureAssignment(ModelBuilder mb)
    {
        mb.Entity<Assignment>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Title).HasMaxLength(300).IsRequired();
            e.Property(a => a.Type).HasConversion<string>();
            e.Property(a => a.Status).HasConversion<string>();
            e.Property(a => a.GradingType).HasConversion<string>();
            e.Property(a => a.Audience).HasConversion<string>().HasMaxLength(32);
            e.Property(a => a.LateJoinPolicy).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(a => a.ClassId);
            e.HasIndex(a => a.TeacherId);
            e.HasIndex(a => a.Status);

            e.HasOne(a => a.Class)
             .WithMany(c => c.Assignments)
             .HasForeignKey(a => a.ClassId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(a => a.Teacher)
             .WithMany()
             .HasForeignKey(a => a.TeacherId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasQueryFilter(a => a.DeletedAt == null);
        });
    }

    private static void ConfigureStudentResult(ModelBuilder mb)
    {
        mb.Entity<StudentResult>(e =>
        {
            e.HasKey(sr => sr.Id);
            e.Property(sr => sr.OverallStatus).HasConversion<string>();
            e.HasIndex(sr => sr.AssignmentId);
            e.HasIndex(sr => sr.StudentId);
            e.HasIndex(sr => new { sr.AssignmentId, sr.StudentId, sr.AttemptNumber }).IsUnique();
            // One attempt per press of "Submit": a retried request with the same key can never add another.
            e.HasIndex(sr => new { sr.StudentId, sr.SubmissionKey }).IsUnique();

            e.HasOne(sr => sr.Assignment)
             .WithMany(a => a.StudentResults)
             .HasForeignKey(sr => sr.AssignmentId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(sr => sr.Student)
             .WithMany()
             .HasForeignKey(sr => sr.StudentId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasQueryFilter(sr => sr.DeletedAt == null);
        });
    }

    private static void ConfigureStepResult(ModelBuilder mb)
    {
        mb.Entity<StepResult>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Status).HasConversion<string>();

            e.HasOne(s => s.StudentResult)
             .WithMany(sr => sr.StepResults)
             .HasForeignKey(s => s.StudentResultId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(s => s.DeletedAt == null);
        });
    }

    private static void ConfigureQuestionReport(ModelBuilder mb)
    {
        mb.Entity<QuestionReport>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Message).HasMaxLength(2000);

            e.HasOne(r => r.Assignment)
             .WithMany()
             .HasForeignKey(r => r.AssignmentId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.Student)
             .WithMany()
             .HasForeignKey(r => r.StudentId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(r => r.DeletedAt == null);
        });
    }

    private static void ConfigureChatSession(ModelBuilder mb)
    {
        mb.Entity<ChatSession>(e =>
        {
            e.HasKey(cs => cs.Id);
            e.Property(cs => cs.Mode).HasMaxLength(50);

            e.HasOne(cs => cs.StudentResult)
             .WithMany(sr => sr.ChatSessions)
             .HasForeignKey(cs => cs.StudentResultId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(cs => cs.DeletedAt == null);
        });
    }

    private static void ConfigureSkill(ModelBuilder mb)
    {
        mb.Entity<Skill>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Code).IsUnique();
            e.Property(s => s.Name).HasMaxLength(200).IsRequired();
            e.Property(s => s.Code).HasMaxLength(50);
            e.Property(s => s.Subject).HasMaxLength(100);

            e.HasOne(s => s.Parent)
             .WithMany(s => s.Children)
             .HasForeignKey(s => s.ParentId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureAssignmentStudent(ModelBuilder mb)
    {
        mb.Entity<AssignmentStudent>(e =>
        {
            e.HasKey(x => new { x.AssignmentId, x.StudentId });

            e.HasOne(x => x.Assignment)
             .WithMany(a => a.AssignmentStudents)
             .HasForeignKey(x => x.AssignmentId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Student)
             .WithMany()
             .HasForeignKey(x => x.StudentId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureLateJoinRequest(ModelBuilder mb)
    {
        mb.Entity<LateJoinRequest>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);

            // One request per (assignment, student): a repeated join, a double click or two processes
            // racing can never queue the same student twice for the same work.
            e.HasIndex(r => new { r.AssignmentId, r.StudentId }).IsUnique();
            e.HasIndex(r => r.Status);

            e.HasOne(r => r.Assignment)
             .WithMany()
             .HasForeignKey(r => r.AssignmentId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.Student)
             .WithMany()
             .HasForeignKey(r => r.StudentId)
             .OnDelete(DeleteBehavior.Restrict);

            // A deleted assignment's requests are not shown or acted on.
            e.HasQueryFilter(r => r.Assignment.DeletedAt == null);
        });
    }

    private static void ConfigureContentItem(ModelBuilder mb)
    {
        mb.Entity<ContentItem>(e =>
        {
            e.HasKey(ci => ci.Id);
            e.Property(ci => ci.Title).HasMaxLength(300).IsRequired();

            e.HasOne(ci => ci.CreatedBy)
             .WithMany()
             .HasForeignKey(ci => ci.CreatedById)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasQueryFilter(ci => ci.DeletedAt == null);
        });
    }

    private static void ConfigureQuestion(ModelBuilder mb)
    {
        mb.Entity<Question>(e =>
        {
            e.HasKey(q => q.Id);
            e.Property(q => q.Title).HasMaxLength(1000).IsRequired();
            e.Property(q => q.Type).HasConversion<string>();
            e.Property(q => q.Difficulty).HasConversion<string>();
            e.Property(q => q.Subject).HasMaxLength(100);
            e.Property(q => q.AnswerKey).HasMaxLength(500).IsRequired();
            e.Property(q => q.CreatedByEmail).HasMaxLength(255).IsRequired();
            e.HasIndex(q => q.CreatedByEmail);
            e.HasIndex(q => q.Subject);
            e.HasIndex(q => q.SyllabusTopicId);
            // Optional link; a topic that still has questions cannot be removed from the library.
            e.HasOne(q => q.SyllabusTopic).WithMany().HasForeignKey(q => q.SyllabusTopicId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(q => q.DeletedAt == null);
        });
    }

    private static void ConfigureSyllabus(ModelBuilder mb)
    {
        mb.Entity<SyllabusProgram>(e =>
        {
            e.Property(p => p.Code).HasMaxLength(20).IsRequired();
            e.Property(p => p.Title).HasMaxLength(200).IsRequired();
            e.HasIndex(p => p.Code).IsUnique();
        });
        mb.Entity<SyllabusVersion>(e =>
        {
            e.Property(v => v.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.SourceName).HasMaxLength(260).IsRequired();
            e.Property(v => v.SourceUrl).HasMaxLength(500);
            e.Property(v => v.SourceSha256).HasMaxLength(64).IsRequired();
            e.Property(v => v.DocumentVersion).HasMaxLength(40);
            e.Ignore(v => v.Years);
            // One edition per programme and exam years, and one version per file: importing again finds it.
            e.HasIndex(v => new { v.ProgramId, v.FirstExamYear, v.LastExamYear }).IsUnique();
            e.HasIndex(v => v.SourceSha256).IsUnique();
            e.HasOne(v => v.Program).WithMany(p => p.Versions).HasForeignKey(v => v.ProgramId).OnDelete(DeleteBehavior.Cascade);
        });
        mb.Entity<SyllabusComponent>(e =>
        {
            e.Property(c => c.Title).HasMaxLength(200).IsRequired();
            e.HasIndex(c => new { c.VersionId, c.Number }).IsUnique();
            e.HasOne(c => c.Version).WithMany(v => v.Components).HasForeignKey(c => c.VersionId).OnDelete(DeleteBehavior.Cascade);
        });
        mb.Entity<SyllabusTopic>(e =>
        {
            e.Property(t => t.Title).HasMaxLength(200).IsRequired();
            e.Property(t => t.Number).HasMaxLength(20);
            e.HasIndex(t => new { t.ComponentId, t.Order });
            e.HasOne(t => t.Component).WithMany(c => c.Topics).HasForeignKey(t => t.ComponentId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureStudentAnswer(ModelBuilder mb)
    {
        mb.Entity<StudentAnswer>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AnswerValue).IsRequired();
            e.HasOne(x => x.StudentResult)
             .WithMany(r => r.StudentAnswers)
             .HasForeignKey(x => x.StudentResultId)
             .OnDelete(DeleteBehavior.Cascade);

            // One answer row per question. Enforced in the database because the
            // upsert path is a check-then-insert: two concurrent submits (a double
            // click, or a photo evaluation racing the final submit) could both find
            // no row and insert, leaving duplicates that make scoring pick one at random.
            e.HasIndex(x => new { x.StudentResultId, x.QuestionIndex }).IsUnique();

            e.HasQueryFilter(x => x.DeletedAt == null);
        });
    }

    private static void ConfigureAuditLog(ModelBuilder mb)
    {
        mb.Entity<AuditLog>(e =>
        {
            e.HasKey(al => al.Id);
            e.Property(al => al.Action).HasMaxLength(50).IsRequired();
            e.Property(al => al.EntityType).HasMaxLength(100).IsRequired();
            e.HasIndex(al => al.UserId);
            e.HasIndex(al => al.CreatedAt);

            e.HasOne(al => al.User)
             .WithMany(u => u.AuditLogs)
             .HasForeignKey(al => al.UserId)
             .OnDelete(DeleteBehavior.Restrict);
        });
    }

    // ── Seed Data ─────────────────────────────────────────────────────────────

    private static void SeedRoles(ModelBuilder mb)
    {
        mb.Entity<Role>().HasData(
            new Role
            {
                Id          = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                Name        = Role.Admin,
                Permissions = "{\"all\": true}"
            },
            new Role
            {
                Id          = Guid.Parse("10000000-0000-0000-0000-000000000002"),
                Name        = Role.Teacher,
                Permissions = "{\"classes\": true, \"assignments\": true}"
            },
            new Role
            {
                Id          = Guid.Parse("10000000-0000-0000-0000-000000000003"),
                Name        = Role.Student,
                Permissions = "{\"submissions\": true}"
            }
        );
    }

    /// <summary>
    /// Seeds a starter skill tree for Mathematics (Grade 5).
    /// Uses fixed GUIDs so re-running migrations is idempotent.
    /// </summary>
    private static void SeedSkills(ModelBuilder mb)
    {
        mb.Entity<Skill>().HasData(
            new Skill
            {
                Id = Guid.Parse("a0000000-0000-0000-0000-000000000001"),
                Name = "Mathematics", Level = 0, Subject = "Mathematics", Code = "MATH",
                IsActive = true
            },
            new Skill
            {
                Id = Guid.Parse("a0000000-0000-0000-0000-000000000002"),
                Name = "Grade 5", Level = 1, Subject = "Mathematics",
                GradeLevel = 5, Code = "MATH.5", IsActive = true,
                ParentId = Guid.Parse("a0000000-0000-0000-0000-000000000001")
            },
            new Skill
            {
                Id = Guid.Parse("a0000000-0000-0000-0000-000000000003"),
                Name = "Number and Operations", Level = 2, Subject = "Mathematics",
                GradeLevel = 5, Code = "MATH.5.NBT", IsActive = true,
                ParentId = Guid.Parse("a0000000-0000-0000-0000-000000000002")
            },
            new Skill
            {
                Id = Guid.Parse("a0000000-0000-0000-0000-000000000004"),
                Name = "Place Value System", Level = 3, Subject = "Mathematics",
                GradeLevel = 5, Code = "MATH.5.NBT.A", IsActive = true,
                ParentId = Guid.Parse("a0000000-0000-0000-0000-000000000003")
            },
            new Skill
            {
                Id = Guid.Parse("a0000000-0000-0000-0000-000000000005"),
                Name = "Multi-Digit Arithmetic", Level = 3, Subject = "Mathematics",
                GradeLevel = 5, Code = "MATH.5.NBT.B", IsActive = true,
                ParentId = Guid.Parse("a0000000-0000-0000-0000-000000000003")
            }
        );
    }
}

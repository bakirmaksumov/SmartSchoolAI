using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;

// Adds student accounts straight to a SmartSchoolAI SQLite database, each with a random password.
//
//   dotnet run --project scripts/ImportStudents -- --db <path to .db> --list <students.csv> --out <credentials.csv>
//                                                  [--class-code <INVITE>] [--dry-run]
//
// students.csv has a header "name,email" and one student per line. The accounts are created exactly like the
// teacher's "reset password" does it: a random password, and the student must pick their own at first sign-in.
// Emails that already have an account are skipped and reported, never changed. --class-code also enrols every new
// student in that class. The credentials file holds the plain passwords: hand it out, then delete it.

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var dbPath = Option("--db") ?? throw new ArgumentException("--db <database path> is required.");
var listPath = Option("--list") ?? throw new ArgumentException("--list <students.csv> is required.");
var outPath = Option("--out") ?? throw new ArgumentException("--out <credentials.csv> is required.");
var classCode = Option("--class-code")?.Trim().ToUpperInvariant();
var dryRun = args.Contains("--dry-run");

dbPath = Path.GetFullPath(dbPath);
if (!File.Exists(dbPath)) throw new FileNotFoundException("Database not found (it is never created here).", dbPath);
if (File.Exists(outPath) && !dryRun) throw new InvalidOperationException($"{outPath} already exists; refusing to overwrite credentials.");

var emailPattern = new Regex(@"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$");
var wanted = new List<(string Name, string Email)>();
var problems = new List<string>();
var lineNo = 0;
foreach (var raw in File.ReadLines(listPath))
{
    lineNo++;
    var line = raw.Trim();
    if (line.Length == 0 || (lineNo == 1 && line.StartsWith("name,", StringComparison.OrdinalIgnoreCase))) continue;

    var comma = line.LastIndexOf(',');
    if (comma <= 0) { problems.Add($"line {lineNo}: expected name,email"); continue; }
    var name = line[..comma].Trim().Trim('"');
    var email = line[(comma + 1)..].Trim().Trim('"').ToLowerInvariant();
    if (name.Length == 0) problems.Add($"line {lineNo}: empty name");
    else if (!emailPattern.IsMatch(email)) problems.Add($"line {lineNo}: '{email}' is not a valid email");
    else if (wanted.Any(w => w.Email == email)) problems.Add($"line {lineNo}: {email} is listed twice");
    else wanted.Add((name, email));
}

if (problems.Count > 0)
{
    foreach (var p in problems) Console.Error.WriteLine("PROBLEM " + p);
    Console.Error.WriteLine("Nothing was written. Fix the list and run again.");
    return 1;
}

// Readable passwords: no 0/O, 1/l/I. Twelve characters from an unbiased random source, with at least one of each kind.
const string Letters = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ", Digits = "23456789", Symbols = "!@#$%*-_";
string NewPassword()
{
    var all = Letters + Digits + Symbols;
    while (true)
    {
        var chars = Enumerable.Range(0, 12).Select(_ => all[RandomNumberGenerator.GetInt32(all.Length)]).ToArray();
        var text = new string(chars);
        if (text.Any(Letters.Contains) && text.Any(char.IsUpper) && text.Any(Digits.Contains) && text.Any(Symbols.Contains)) return text;
    }
}

var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").Options;
await using var db = new AppDbContext(options);

var studentRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == Role.Student)
    ?? throw new InvalidOperationException("Student role not found; is this the right database?");

Class? cls = null;
if (classCode != null)
{
    cls = await db.Classes.FirstOrDefaultAsync(c => c.InviteCode == classCode && !c.IsArchived)
        ?? throw new InvalidOperationException($"No active class has invite code {classCode}.");
}

var existing = (await db.Users.Where(u => wanted.Select(w => w.Email).Contains(u.Email)).Select(u => u.Email).ToListAsync()).ToHashSet();
var created = new List<(string Name, string Email, string Password)>();

foreach (var (name, email) in wanted)
{
    if (existing.Contains(email)) { Console.WriteLine($"SKIP    {email} already has an account"); continue; }

    var password = NewPassword();
    var user = new User
    {
        Email = email,
        FullName = name,
        PasswordHash = PasswordHasher.Hash(password),
        RoleId = studentRole.Id,
        MustChangePassword = true,
        IsActive = true
    };
    created.Add((name, email, password));
    if (dryRun) { Console.WriteLine($"WOULD   create {email}"); continue; }

    db.Users.Add(user);
    if (cls != null) db.ClassStudents.Add(new ClassStudent { ClassId = cls.Id, Student = user, JoinedAt = DateTime.UtcNow });
}

if (!dryRun && created.Count > 0)
{
    if (cls != null)
    {
        var seats = await db.ClassStudents.CountAsync(cs => cs.ClassId == cls.Id);
        if (seats + created.Count > cls.MaxStudents)
            throw new InvalidOperationException($"Class {cls.Name} has {seats} of {cls.MaxStudents} seats taken; {created.Count} more will not fit. Nothing was written.");
    }
    await db.SaveChangesAsync();

    var csv = new StringBuilder("name,email,password\n");
    foreach (var c in created) csv.Append('"').Append(c.Name.Replace("\"", "\"\"")).Append("\",").Append(c.Email).Append(',').Append(c.Password).Append('\n');
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    await File.WriteAllTextAsync(outPath, csv.ToString(), new UTF8Encoding(true));
    foreach (var c in created) Console.WriteLine($"CREATED {c.Email}");
}

Console.WriteLine($"{(dryRun ? "Dry run: " : "")}{created.Count} to create, {wanted.Count - created.Count} skipped, database {dbPath}");
if (!dryRun && created.Count > 0) Console.WriteLine($"Passwords written to {Path.GetFullPath(outPath)}. Hand them out, then delete that file.");
return 0;

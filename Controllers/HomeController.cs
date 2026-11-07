using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using System.Security.Claims;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// Renders the teacher's dashboard with real-time statistics from the database.
/// </summary>
[Authorize]
public class HomeController : Controller
{
    private readonly AppDbContext _db;

    public HomeController(AppDbContext db)
    {
        _db = db;
    }

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> Index()
    {
        if (User.IsInRole("Student"))
            return RedirectToAction("Index", "Student");

        var teacherId = CurrentUserId;

        ViewBag.TotalStudents = await _db.ClassStudents
            .Where(cs => cs.Class.TeacherId == teacherId
                      || cs.Class.CoTeachers.Any(ct => ct.TeacherId == teacherId))
            .Select(cs => cs.StudentId)
            .Distinct()
            .CountAsync();

        ViewBag.ActiveAssignments = await _db.Assignments
            .AccessibleBy(teacherId)
            .CountAsync(a => a.Status == AssignmentStatus.Active);

        ViewBag.SubmissionsToday = await _db.StudentResults
            .Where(sr => sr.IsSubmitted
                      && (sr.Assignment.TeacherId == teacherId
                       || sr.Assignment.Class.TeacherId == teacherId
                       || sr.Assignment.Class.CoTeachers.Any(ct => ct.TeacherId == teacherId))
                      && sr.SubmittedAt.Date == DateTime.UtcNow.Date)
            .CountAsync();

        var scores = await _db.StudentResults
            .Where(sr => sr.IsSubmitted
                      && (sr.Assignment.TeacherId == teacherId
                       || sr.Assignment.Class.TeacherId == teacherId
                       || sr.Assignment.Class.CoTeachers.Any(ct => ct.TeacherId == teacherId)))
            .Select(sr => sr.Score)
            .ToListAsync();

        ViewBag.AverageScore = scores.Count > 0 ? (int)scores.Average() : 0;

        ViewBag.RecentAssignments = await _db.Assignments
            .Include(a => a.Class)
            .Include(a => a.StudentResults)
            .AccessibleBy(teacherId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(10)
            .Select(a => new
            {
                a.Id,
                a.Title,
                ClassName        = a.Class.Name,
                Type             = a.Type.ToString(),
                Status           = a.Status.ToString().ToLower(),
                a.Deadline,
                SubmissionsCount = a.StudentResults.Count(r => r.IsSubmitted),
                TotalStudents    = a.Class.ClassStudents.Count
            })
            .ToListAsync();

        return View();
    }

    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> SubmitSupportTicket([FromBody] SupportTicketRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Message))
            return BadRequest(new { success = false, message = "Message is required." });

        var ticket = new SupportTicket
        {
            UserName = User.Identity?.Name ?? "Student/Guest",
            UserEmail = User.FindFirstValue(ClaimTypes.Email) ?? "student@eduschool.ai",
            Message = request.Message,
            CreatedAt = DateTime.UtcNow
        };

        _db.SupportTickets.Add(ticket);
        await _db.SaveChangesAsync();

        // Отправляем Email ТОЛЬКО в крайнем случае (если это критический баг/падение системы)
        if (request.IsCritical)
        {
            try
            {
                await SendEmailNotificationAsync("maksumov1988@gmail.com", ticket.UserName, ticket.UserEmail, ticket.Message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Email error: " + ex.Message);
            }
        }

        return Json(new { success = true, message = "Ticket recorded successfully." });
    }

    private async Task SendEmailNotificationAsync(string recipientEmail, string userName, string userEmail, string messageBody)
    {
        using var client = new System.Net.Mail.SmtpClient("smtp.gmail.com", 587)
        {
            Credentials = new System.Net.NetworkCredential("maksumov1988@gmail.com", "app_password_here"),
            EnableSsl = true
        };

        var mail = new System.Net.Mail.MailMessage
        {
            From = new System.Net.Mail.MailAddress("noreply@eduschool.ai", "EduSchool Support Bot"),
            Subject = $"🚨 [EduSchool Support Bug Report] from {userName}",
            Body = $"New Support / Bug Report Received:\n\nUser: {userName}\nEmail: {userEmail}\nTime: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n\nMessage:\n{messageBody}",
            IsBodyHtml = false
        };
        mail.To.Add(recipientEmail);

        await client.SendMailAsync(mail);
    }

    public IActionResult Error() => View();
}

public class SupportTicketRequest
{
    public string Message { get; set; } = string.Empty;
    public bool IsCritical { get; set; }
}


namespace SmartSchoolAI.Models;

public class SupportTicket : BaseEntity
{
    public string UserEmail { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsResolved { get; set; } = false;
}

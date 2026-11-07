namespace SmartSchoolAI.ViewModels;

public class ProfileViewModel
{
    public string  FullName  { get; set; } = string.Empty;
    public string  FirstName { get; set; } = string.Empty;
    public string  LastName  { get; set; } = string.Empty;
    public string  Email     { get; set; } = string.Empty;
    public string? School    { get; set; }
    public string? Bio       { get; set; }
    public string? AvatarUrl { get; set; }
    public string  Role      { get; set; } = string.Empty;
}

using System.Globalization;

namespace SmartSchoolAI.Services;

/// <summary>
/// The one place where dates cross between the school's clock and storage.
///
/// Every date is stored in UTC. A teacher types times on the school's clock (setting <c>School:TimeZone</c>,
/// an IANA id, default <c>Asia/Tashkent</c>), and every page shows them on that same clock — whatever time
/// zone the server itself runs in (a Docker container is UTC, a Windows server is usually local time). So
/// "27 Nov 17:00" means 17:00 in Tashkent everywhere: in the form, after saving again, in a personal due
/// date, and on the student's screen.
/// </summary>
public static class SchoolTime
{
    public const string DefaultZoneId = "Asia/Tashkent";

    public static TimeZoneInfo Zone { get; private set; } = Resolve(DefaultZoneId);
    public static string ZoneId { get; private set; } = DefaultZoneId;

    /// <summary>Called once at start-up with the configured zone.</summary>
    public static void Configure(string? zoneId)
    {
        ZoneId = string.IsNullOrWhiteSpace(zoneId) ? DefaultZoneId : zoneId.Trim();
        Zone   = Resolve(ZoneId);
    }

    private static TimeZoneInfo Resolve(string id)
    {
        // .NET 8 accepts IANA ids on Windows (via ICU) and Linux alike; the Windows name is a fallback.
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            throw new InvalidOperationException($"Unknown time zone '{id}' in setting School:TimeZone.", ex);
        }
    }

    /// <summary>A time the teacher typed (school clock, from a datetime-local field) as UTC for storage.</summary>
    public static DateTime? ToUtc(DateTime? schoolTime)
        => schoolTime is { } t
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), Zone)
            : null;

    /// <summary>A stored UTC time on the school's clock.</summary>
    public static DateTime ToSchool(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>Value for a datetime-local input, so saving the form again keeps exactly the same time.</summary>
    public static string Input(DateTime? utc)
        => utc is { } t ? ToSchool(t).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) : "";

    /// <summary>A stored UTC time as text on the school's clock.</summary>
    public static string Show(DateTime? utc, string format = "dd MMM yyyy HH:mm", string empty = "—")
        => utc is { } t ? ToSchool(t).ToString(format, CultureInfo.InvariantCulture) : empty;

    /// <summary>e.g. "Tashkent time (UTC+05:00)" — shown next to date fields so the clock is never in doubt.</summary>
    public static string Label
    {
        get
        {
            var offset = Zone.GetUtcOffset(DateTime.UtcNow);
            var city   = ZoneId.Contains('/') ? ZoneId[(ZoneId.LastIndexOf('/') + 1)..].Replace('_', ' ') : ZoneId;
            return $"{city} time (UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm})";
        }
    }
}

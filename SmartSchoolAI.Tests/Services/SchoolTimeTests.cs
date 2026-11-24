using SmartSchoolAI.Services;

namespace SmartSchoolAI.Tests.Services;

/// <summary>
/// 17:00 in Tashkent must mean the same moment everywhere, whatever zone the server itself runs in
/// (a Docker container is UTC, a Windows server usually local time). <see cref="SchoolTime"/> never reads
/// the machine's zone, so these results hold on any machine.
/// </summary>
public class SchoolTimeTests
{
    [Fact]
    public void Default_IsTashkent()
    {
        Assert.Equal("Asia/Tashkent", SchoolTime.ZoneId);
        Assert.Equal("Tashkent time (UTC+05:00)", SchoolTime.Label);
    }

    [Fact]
    public void FivePmInTashkent_IsStoredAsNoonUtc()
    {
        var typed = new DateTime(2099, 11, 27, 17, 0, 0);          // from a datetime-local field: no zone
        var stored = SchoolTime.ToUtc(typed)!.Value;

        Assert.Equal(new DateTime(2099, 11, 27, 12, 0, 0), stored);
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
    }

    [Fact]
    public void StoredValue_ComesBackAsTheSameTime_SoSavingAgainChangesNothing()
    {
        // SQLite hands dates back without a kind; they are UTC.
        var fromDb = DateTime.SpecifyKind(new DateTime(2099, 11, 27, 12, 0, 0), DateTimeKind.Unspecified);

        Assert.Equal("2099-11-27T17:00", SchoolTime.Input(fromDb));
        Assert.Equal("27 Nov 2099 17:00", SchoolTime.Show(fromDb));

        // Save the form again, many times: the stored value never moves.
        var value = fromDb;
        for (var i = 0; i < 5; i++)
            value = SchoolTime.ToUtc(DateTime.Parse(SchoolTime.Input(value)))!.Value;
        Assert.Equal(new DateTime(2099, 11, 27, 12, 0, 0), value);
    }

    [Fact]
    public void Empty_StaysEmpty()
    {
        Assert.Null(SchoolTime.ToUtc(null));
        Assert.Equal("", SchoolTime.Input(null));
        Assert.Equal("—", SchoolTime.Show(null));
        Assert.Equal("No due date", SchoolTime.Show(null, empty: "No due date"));
    }

    [Fact]
    public void ResultIsTheSame_WhatEverTheMachinesZone()
    {
        // The conversion takes the school zone explicitly; the machine's zone plays no part.
        var expected = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2099, 11, 27, 17, 0, 0),
                                                     TimeZoneInfo.FindSystemTimeZoneById("Asia/Tashkent"));
        Assert.Equal(expected, SchoolTime.ToUtc(new DateTime(2099, 11, 27, 17, 0, 0)));
        Assert.Equal(new DateTime(2099, 11, 27, 12, 0, 0), expected);
    }
}

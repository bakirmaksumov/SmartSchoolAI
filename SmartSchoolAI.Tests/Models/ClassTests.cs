using SmartSchoolAI.Models;

namespace SmartSchoolAI.Tests.Models;

public class ClassTests
{
    [Fact]
    public void Archive_SetsIsArchivedTrue()
    {
        var cls = new Class { Name = "5A", Subject = "Math", AcademicYear = "2026" };

        cls.Archive();

        Assert.True(cls.IsArchived);
    }

    [Fact]
    public void Restore_SetsIsArchivedFalse()
    {
        var cls = new Class { Name = "5A", Subject = "Math", AcademicYear = "2026" };
        cls.Archive();

        cls.Restore();

        Assert.False(cls.IsArchived);
    }
}

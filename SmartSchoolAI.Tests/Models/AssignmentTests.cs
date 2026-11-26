using SmartSchoolAI.Models;

namespace SmartSchoolAI.Tests.Models;

public class AssignmentTests
{
    private static Assignment NewDraft(AssignmentType type = AssignmentType.ClassroomPractice) =>
        new() { Title = "Test assignment", Type = type };

    [Fact]
    public void Publish_FromDraft_TransitionsToActive()
    {
        var assignment = NewDraft();

        assignment.Publish();

        Assert.Equal(AssignmentStatus.Active, assignment.Status);
    }

    [Fact]
    public void Publish_WhenNotDraft_Throws()
    {
        var assignment = NewDraft();
        assignment.Publish();

        Assert.Throws<InvalidOperationException>(assignment.Publish);
    }

    [Fact]
    public void Publish_HomeworkWithoutDeadline_Throws()
    {
        var assignment = NewDraft(AssignmentType.Homework);

        Assert.Throws<InvalidOperationException>(assignment.Publish);
    }

    [Fact]
    public void Publish_HomeworkWithDeadlineTooSoon_Throws()
    {
        var assignment = NewDraft(AssignmentType.Homework);
        assignment.Deadline = DateTime.UtcNow.AddMinutes(30);

        Assert.Throws<InvalidOperationException>(assignment.Publish);
    }

    [Fact]
    public void Publish_HomeworkWithValidDeadline_Succeeds()
    {
        var assignment = NewDraft(AssignmentType.Homework);
        assignment.Deadline = DateTime.UtcNow.AddDays(1);

        assignment.Publish();

        Assert.Equal(AssignmentStatus.Active, assignment.Status);
    }

    [Fact]
    public void Close_FromActive_TransitionsToClosed()
    {
        var assignment = NewDraft();
        assignment.Publish();

        assignment.Close();

        Assert.Equal(AssignmentStatus.Closed, assignment.Status);
    }

    [Fact]
    public void Close_WhenNotActive_Throws()
    {
        var assignment = NewDraft();

        Assert.Throws<InvalidOperationException>(assignment.Close);
    }

    [Fact]
    public void Reopen_FromClosed_TransitionsToActive()
    {
        var assignment = NewDraft();
        assignment.Publish();
        assignment.Close();

        assignment.Reopen();

        Assert.Equal(AssignmentStatus.Active, assignment.Status);
    }

    [Fact]
    public void Reopen_WhenNotClosed_Throws()
    {
        var assignment = NewDraft();

        Assert.Throws<InvalidOperationException>(assignment.Reopen);
    }

    [Theory]
    [InlineData(AssignmentStatus.Draft)]
    [InlineData(AssignmentStatus.Active)]
    [InlineData(AssignmentStatus.Closed)]
    public void Archive_FromAnyStatus_TransitionsToArchived(AssignmentStatus status)
    {
        var assignment = NewDraft();
        assignment.Status = status;

        assignment.Archive();

        Assert.Equal(AssignmentStatus.Archived, assignment.Status);
    }

    [Fact]
    public void ExtendDeadline_WhenActive_UpdatesDeadline()
    {
        var assignment = NewDraft(AssignmentType.Homework);
        assignment.Deadline = DateTime.UtcNow.AddDays(1);
        assignment.Publish();
        var newDeadline = DateTime.UtcNow.AddDays(5);

        assignment.ExtendDeadline(newDeadline);

        Assert.Equal(newDeadline, assignment.Deadline);
    }

    [Fact]
    public void ExtendDeadline_WhenNotActive_Throws()
    {
        var assignment = NewDraft();

        Assert.Throws<InvalidOperationException>(() => assignment.ExtendDeadline(DateTime.UtcNow.AddDays(1)));
    }

    [Fact]
    public void ExtendDeadline_InThePast_Throws()
    {
        var assignment = NewDraft(AssignmentType.Homework);
        assignment.Deadline = DateTime.UtcNow.AddDays(1);
        assignment.Publish();

        Assert.Throws<InvalidOperationException>(() => assignment.ExtendDeadline(DateTime.UtcNow.AddDays(-1)));
    }

    [Fact]
    public void SoftDelete_WithSubmissions_Throws()
    {
        var assignment = NewDraft();

        Assert.Throws<InvalidOperationException>(() => assignment.SoftDelete(hasSubmissions: true));
    }

    [Fact]
    public void SoftDelete_WhileActive_Throws()
    {
        var assignment = NewDraft();
        assignment.Publish();

        Assert.Throws<InvalidOperationException>(() => assignment.SoftDelete(hasSubmissions: false));
    }

    [Fact]
    public void SoftDelete_DraftWithoutSubmissions_SetsDeletedAt()
    {
        var assignment = NewDraft();

        assignment.SoftDelete(hasSubmissions: false);

        Assert.NotNull(assignment.DeletedAt);
    }

    [Theory]
    [InlineData(70, 70, true)]
    [InlineData(69, 70, false)]
    [InlineData(100, 70, true)]
    public void IsPass_ComparesScoreAgainstThreshold(int score, int threshold, bool expected)
    {
        var assignment = NewDraft();
        assignment.PassThreshold = threshold;

        Assert.Equal(expected, assignment.IsPass(score));
    }

    [Theory]
    [InlineData(GradingType.Percentage, 85, "85%")]
    [InlineData(GradingType.Letter, 95, "A")]
    [InlineData(GradingType.Letter, 55, "F")]
    [InlineData(GradingType.Gpa, 85, "3.0")]
    [InlineData(GradingType.PassFail, 80, "Pass")]
    public void FormatGrade_RendersAccordingToScale(GradingType type, int score, string expected)
    {
        var assignment = NewDraft();
        assignment.GradingType   = type;
        assignment.PassThreshold = 70;

        Assert.Equal(expected, assignment.FormatGrade(score));
    }

    [Fact]
    public void AllowsUnlimitedAttempts_WhenMaxAttemptsIsZeroOrLess_ReturnsTrue()
    {
        var assignment = NewDraft();
        assignment.MaxAttempts = 0;

        Assert.True(assignment.AllowsUnlimitedAttempts);
    }

    [Fact]
    public void ShowsInstantFeedback_ClassroomPractice_AlwaysTrue()
    {
        var assignment = NewDraft(AssignmentType.ClassroomPractice);
        assignment.AllowInstantFeedback = false;

        Assert.True(assignment.ShowsInstantFeedback);
    }

    [Fact]
    public void ShowsInstantFeedback_Homework_FollowsTeacherSetting()
    {
        var assignment = NewDraft(AssignmentType.Homework);
        assignment.AllowInstantFeedback = false;

        Assert.False(assignment.ShowsInstantFeedback);
    }

    [Fact]
    public void AllowsQuestionEditing_DraftWithoutSubmissions_ReturnsTrue()
    {
        var assignment = NewDraft();

        Assert.True(assignment.AllowsQuestionEditing(hasSubmissions: false));
    }

    [Fact]
    public void AllowsQuestionEditing_ActiveWithSubmissions_ReturnsFalse()
    {
        var assignment = NewDraft();
        assignment.Publish();

        Assert.False(assignment.AllowsQuestionEditing(hasSubmissions: true));
    }

    [Fact]
    public void AllowsQuestionEditing_Archived_ReturnsFalse()
    {
        var assignment = NewDraft();
        assignment.Archive();

        Assert.False(assignment.AllowsQuestionEditing(hasSubmissions: false));
    }
}

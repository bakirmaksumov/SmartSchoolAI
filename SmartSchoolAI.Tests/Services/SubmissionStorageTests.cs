using SmartSchoolAI.Services;
using SmartSchoolAI.Tests.TestHelpers;

namespace SmartSchoolAI.Tests.Services;

public class SubmissionStorageTests : IDisposable
{
    private readonly FakeWebHostEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task SaveAsync_ThenFindFile_RoundTripsTheSameBytes()
    {
        var studentId = Guid.NewGuid();
        var bytes     = new byte[] { 1, 2, 3, 4 };

        var webPath = await SubmissionStorage.SaveAsync(_env, bytes, ".jpg", studentId);
        var fileName = webPath.Split('/').Last();

        var resolved = SubmissionStorage.FindFile(_env, studentId, fileName);

        Assert.NotNull(resolved);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(resolved!));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\Windows\\win.ini")]
    [InlineData("../other-student/photo.jpg")]
    public void FindFile_PathTraversalAttempt_ReturnsNull(string maliciousFileName)
    {
        var studentId = Guid.NewGuid();

        var resolved = SubmissionStorage.FindFile(_env, studentId, maliciousFileName);

        Assert.Null(resolved);
    }

    [Fact]
    public void FindFile_UnknownFileName_ReturnsNull()
    {
        var studentId = Guid.NewGuid();

        var resolved = SubmissionStorage.FindFile(_env, studentId, "does-not-exist.jpg");

        Assert.Null(resolved);
    }

    [Fact]
    public async Task FindFile_AnotherStudentsFile_ReturnsNull()
    {
        var owner   = Guid.NewGuid();
        var intruder = Guid.NewGuid();
        var webPath = await SubmissionStorage.SaveAsync(_env, new byte[] { 9 }, ".png", owner);
        var fileName = webPath.Split('/').Last();

        // Same file name, but requested under a different student's folder —
        // must not resolve to the owner's file.
        var resolved = SubmissionStorage.FindFile(_env, intruder, fileName);

        Assert.Null(resolved);
    }

    [Fact]
    public void FindFile_EmptyFileName_ReturnsNull()
    {
        Assert.Null(SubmissionStorage.FindFile(_env, Guid.NewGuid(), ""));
    }

    [Theory]
    [InlineData("photo.png",  "image/png")]
    [InlineData("photo.webp", "image/webp")]
    [InlineData("photo.heic", "image/heic")]
    [InlineData("photo.bmp",  "image/bmp")]
    [InlineData("photo.gif",  "image/gif")]
    [InlineData("photo.jpg",  "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("photo.unknown", "image/jpeg")]
    public void ContentTypeFor_MapsExtensionToMimeType(string path, string expected)
    {
        Assert.Equal(expected, SubmissionStorage.ContentTypeFor(path));
    }
}

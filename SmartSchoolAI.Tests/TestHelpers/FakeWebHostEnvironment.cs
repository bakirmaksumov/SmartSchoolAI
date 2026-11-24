using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace SmartSchoolAI.Tests.TestHelpers;

/// <summary>Minimal IWebHostEnvironment backed by a real temp directory, for tests that touch disk I/O.</summary>
public sealed class FakeWebHostEnvironment : IWebHostEnvironment, IDisposable
{
    public string ContentRootPath { get; set; }
    public string WebRootPath     { get; set; }
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "SmartSchoolAI.Tests";

    public IFileProvider ContentRootFileProvider { get; set; } = null!;
    public IFileProvider WebRootFileProvider     { get; set; } = null!;

    public FakeWebHostEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), "SmartSchoolAI.Tests", Guid.NewGuid().ToString());
        ContentRootPath = root;
        WebRootPath     = Path.Combine(root, "wwwroot");
        Directory.CreateDirectory(WebRootPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(ContentRootPath))
            Directory.Delete(ContentRootPath, recursive: true);
    }
}

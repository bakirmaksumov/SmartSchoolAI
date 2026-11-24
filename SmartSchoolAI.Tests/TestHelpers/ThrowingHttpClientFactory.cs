namespace SmartSchoolAI.Tests.TestHelpers;

/// <summary>An IHttpClientFactory that fails any request — no test should reach the network (the AI is "down").</summary>
public sealed class ThrowingHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        throw new InvalidOperationException("No test should make an HTTP call to the AI.");
}

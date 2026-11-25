using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSchoolAI.Data;
using SmartSchoolAI.Services;

namespace SmartSchoolAI.Tests.TestHelpers;

/// <summary>The grading services wired as in Program.cs, over a test database and a test HTTP factory.</summary>
public static class AiTestServices
{
    public static GradingService Grading(AppDbContext db, Func<AppDbContext> newContext, IConfiguration config,
                                         IHttpClientFactory http, Microsoft.AspNetCore.Hosting.IWebHostEnvironment env)
    {
        var models  = new GradingModels(config);
        var client  = new OpenRouterClient(config, http, new AiUsageRecorder(newContext), NullLogger<OpenRouterClient>.Instance);
        var marking = new AiMarkingService(new AiUsageRecorder(newContext), client, models, NullLogger<AiMarkingService>.Instance);
        return new GradingService(db,
            new AnswerGradingService(client, models, NullLogger<AnswerGradingService>.Instance),
            new HandwritingAnalysisService(client, models, config, NullLogger<HandwritingAnalysisService>.Instance),
            marking, env, NullLogger<GradingService>.Instance);
    }

    public static AiMarkingService Marking(Func<AppDbContext> newContext, IConfiguration config, IHttpClientFactory http)
    {
        var client = new OpenRouterClient(config, http, new AiUsageRecorder(newContext), NullLogger<OpenRouterClient>.Instance);
        return new AiMarkingService(new AiUsageRecorder(newContext), client, new GradingModels(config), NullLogger<AiMarkingService>.Instance);
    }
}

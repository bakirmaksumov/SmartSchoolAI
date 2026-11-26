using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>What checking one answer cost: every call that went into it, its reading, its grading and the second check,
/// failed tries included.</summary>
public sealed class AiCostSummary
{
    public sealed record Line(string Stage, string Model, int? InputTokens, int? OutputTokens, decimal? Cost, bool Succeeded, string? Failure, bool Shared);

    public List<Line> Lines { get; } = new();
    public int Calls => Lines.Count;
    public int InputTokens => Lines.Sum(l => l.InputTokens ?? 0);
    public int OutputTokens => Lines.Sum(l => l.OutputTokens ?? 0);
    /// <summary>Sum of the reported costs; null when OpenRouter reported none.</summary>
    public decimal? Cost => Lines.Any(l => l.Cost is not null) ? Lines.Sum(l => l.Cost ?? 0) : null;
    /// <summary>True when some call did not report its cost (the sum is then a lower bound).</summary>
    public bool CostIncomplete => Lines.Any(l => l.Cost is null) && Lines.Any(l => l.Cost is not null);
    /// <summary>A batch call shared with other answers of the same hand-in.</summary>
    public bool Shared => Lines.Any(l => l.Shared);

    public string CostText
    {
        get
        {
            var missing = Lines.Count(l => l.Cost is null);
            return Cost is not { } c ? "no data"
                 : missing == 0 ? $"${c:0.#####}"
                 : $"${c:0.#####} (+ {missing} call{(missing == 1 ? "" : "s")} with no cost data)";
        }
    }
}

public static class AiCostReport
{
    /// <summary>Per question index: the calls behind each answer of this attempt (one query).</summary>
    public static async Task<Dictionary<int, AiCostSummary>> ForResultAsync(AppDbContext db, StudentResult result)
    {
        var recognitionKeys = result.StudentAnswers.Where(a => a.RecognitionKey != null).Select(a => a.RecognitionKey!).Distinct().ToList();
        var logs = await db.AiCallLogs.AsNoTracking()
            .Where(l => l.ResultId == result.Id || (l.CacheKey != null && recognitionKeys.Contains(l.CacheKey)))
            .OrderBy(l => l.CreatedAt)
            .ToListAsync();

        var byIndex = new Dictionary<int, AiCostSummary>();
        foreach (var answer in result.StudentAnswers)
        {
            var idx = answer.QuestionIndex.ToString();
            var mine = logs.Where(l =>
                (answer.RecognitionKey != null && l.CacheKey == answer.RecognitionKey) ||
                (l.ResultId == result.Id && l.Questions != null && l.Questions.Split(',').Contains(idx)));
            var summary = new AiCostSummary();
            foreach (var l in mine.DistinctBy(l => l.Id))
                summary.Lines.Add(new(l.Stage, l.Model, l.InputTokens, l.OutputTokens, l.Cost, l.Succeeded, l.Failure,
                                      l.Questions != null && l.Questions.Contains(',')));
            if (summary.Calls > 0) byIndex[answer.QuestionIndex] = summary;
        }
        return byIndex;
    }

    /// <summary>The stored marking of an answer, or null for older work and unfinished marking.</summary>
    public static AiMarkingService.Verdict? Verdict(StudentAnswer? answer)
    {
        if (string.IsNullOrEmpty(answer?.GradingJson) || GradingService.IsUnfinished(answer)) return null;
        try { return JsonSerializer.Deserialize<AiMarkingService.Verdict>(answer.GradingJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return null; }
    }
}

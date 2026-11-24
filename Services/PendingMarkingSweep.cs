using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;

namespace SmartSchoolAI.Services;

/// <summary>
/// The AI marks a submission in the background, after it is saved. If the app stops in between, the attempt stays
/// submitted with <c>GradingPending</c> set. Shortly after start this finishes those, one at a time.
/// </summary>
public sealed class PendingMarkingSweep : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PendingMarkingSweep> _logger;

    public PendingMarkingSweep(IServiceScopeFactory scopes, ILogger<PendingMarkingSweep> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        List<Guid> pending;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            pending = await db.StudentResults
                .Where(r => r.IsSubmitted && r.GradingPending && r.SubmittedAt < DateTime.UtcNow.AddMinutes(-2))
                .Select(r => r.Id)
                .ToListAsync(stoppingToken);
        }

        foreach (var id in pending)
        {
            if (stoppingToken.IsCancellationRequested) return;
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SubmissionService>().GradeAfterSubmitAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not finish marking result {ResultId}", id);
            }
        }
    }
}

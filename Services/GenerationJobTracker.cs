using System.Collections.Concurrent;

namespace SmartSchoolAI.Services;

/// <summary>
/// The AI question-generation/import calls are each a single non-streaming HTTP
/// request to OpenRouter — there is no real per-question progress to report
/// mid-call. Rather than fake a client-side timer that's disconnected from the
/// actual work, jobs run server-side in the background and report a percentage
/// derived from elapsed time vs. an expected duration; it only ever reaches 100%
/// when the real call actually finishes (or errors), so "done" is never a lie.
/// </summary>
public class GenerationJob
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// The user a job's result belongs to, or null for jobs whose endpoint already
    /// authorizes the caller some other way (question import is teacher-only). Set
    /// for student grading jobs so one student can never poll another's result.
    /// </summary>
    public Guid? OwnerId { get; set; }

    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public TimeSpan EstimatedDuration { get; init; } = TimeSpan.FromSeconds(15);
    public string Status { get; set; } = "running"; // running | done | error
    public object? Result { get; set; }
    public string? Error { get; set; }

    public int PercentComplete()
    {
        if (Status != "running") return 100;

        var elapsedMs = (DateTime.UtcNow - StartedAt).TotalMilliseconds;
        var ratio = elapsedMs / EstimatedDuration.TotalMilliseconds;

        // Asymptotic ramp — quick at first, slows down, caps below 100 so the bar
        // never claims completion until the real call actually returns.
        var percent = (int)(92 * (1 - Math.Exp(-3 * ratio)));
        return Math.Clamp(percent, 1, 92);
    }
}

public class GenerationJobTracker
{
    // Generous headroom over the longest real job (handwriting grading, ~25s
    // estimated) — long enough that a slow connection never loses a legitimate
    // in-flight job, short enough that abandoned ones (closed tab, crashed
    // browser, network drop before the caller ever polls) don't sit forever.
    private static readonly TimeSpan MaxJobAge = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, GenerationJob> _jobs = new();

    public GenerationJob Create(TimeSpan estimatedDuration)
    {
        SweepExpired();
        var job = new GenerationJob { EstimatedDuration = estimatedDuration };
        _jobs[job.Id] = job;
        return job;
    }

    public GenerationJob? Get(Guid id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public void Remove(Guid id) => _jobs.TryRemove(id, out _);

    /// <summary>
    /// Drops jobs nobody ever polled to completion. Without this the table only
    /// ever grows — every abandoned "Check answer" or PDF import leaves its job
    /// behind forever, since <see cref="Remove"/> is only ever called from the
    /// progress-polling endpoint after a caller actually reads the final status.
    /// Swept opportunistically on every <see cref="Create"/> rather than on a
    /// timer — no background thread to manage, and jobs are created constantly
    /// during real usage, so the table never grows unbounded between sweeps.
    /// </summary>
    private void SweepExpired()
    {
        var cutoff = DateTime.UtcNow - MaxJobAge;
        foreach (var (id, job) in _jobs)
            if (job.StartedAt < cutoff)
                _jobs.TryRemove(id, out _);
    }
}

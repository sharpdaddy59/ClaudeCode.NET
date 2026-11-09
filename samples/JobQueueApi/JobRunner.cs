using ClaudeCode;
using Microsoft.Extensions.Options;

namespace JobQueueApi;

/// <summary>
/// Hosted service that drains the queue. Runs <see cref="JobQueueOptions.MaxConcurrency"/>
/// consumer loops; each takes one job at a time and runs it to completion.
/// </summary>
public sealed class JobRunner(
    JobQueue queue,
    ClaudeSession claude,
    IOptions<JobQueueOptions> options,
    TimeProvider time,
    ILogger<JobRunner> logger) : BackgroundService
{
    private readonly JobQueueOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Math.Max(1, _options.MaxConcurrency);
        return Task.WhenAll(Enumerable.Range(0, workers).Select(_ => ConsumeAsync(stoppingToken)));
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.ReadAllAsync(stoppingToken))
            {
                await RunJobAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Jobs still queued are dropped with the process (in-memory sample).
        }
    }

    private async Task RunJobAsync(Job job, CancellationToken stoppingToken)
    {
        if (job.TryStart(stoppingToken, time.GetUtcNow()) is not { } token)
        {
            return; // canceled while queued
        }

        var sessionOptions = new ClaudeSessionOptions
        {
            WorkingDirectory = _options.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            Model = _options.Model,
            SessionName = $"job-{job.Id:N}",
            // A queued job has nobody waiting on it, so a usage limit is not an error:
            // wait for the reset and resume the same session.
            RateLimitWait = RateLimitWaitPolicy.Default
        };

        logger.LogInformation("Job {JobId} started", job.Id);
        try
        {
            var result = await claude.RunAsync(job.Prompt, sessionOptions, evt =>
            {
                job.OnEvent(evt);
                if (evt.Kind == ClaudeStreamEventKind.RateLimitWait)
                {
                    logger.LogWarning("Job {JobId}: {Message}", job.Id, evt.Text);
                }
            }, cancellationToken: token);

            job.Completed(result, time.GetUtcNow());
            logger.LogInformation("Job {JobId} finished: {Status}, {Turns} turns, ${Cost:F4}, {Waits} rate-limit waits",
                job.Id, job.Snapshot().Status, result.NumTurns, result.CostUsd, result.RateLimitWaits);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            job.Canceled(time.GetUtcNow());
            logger.LogInformation("Job {JobId} canceled", job.Id);
        }
        catch (ClaudeCliNotFoundException ex)
        {
            job.Failed(ex.Message, time.GetUtcNow());
            logger.LogError("Job {JobId} failed: {Message}", job.Id, ex.Message);
        }
        catch (Exception ex)
        {
            job.Failed($"Unexpected error: {ex.Message}", time.GetUtcNow());
            logger.LogError(ex, "Job {JobId} failed", job.Id);
        }
    }
}

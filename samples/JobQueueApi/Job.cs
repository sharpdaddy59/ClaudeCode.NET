using ClaudeCode;

namespace JobQueueApi;

public enum JobStatus
{
    Queued,
    Running,
    /// <summary>Hit a usage limit; ClaudeCode.Net is waiting for the reset and will resume the same session.</summary>
    WaitingForRateLimit,
    Succeeded,
    Failed,
    Canceled
}

/// <summary>What the API returns: an immutable snapshot of a job.</summary>
public sealed record JobView(
    Guid Id,
    JobStatus Status,
    string Prompt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? SessionId,
    DateTimeOffset? RateLimitResetsAt,
    int RateLimitWaits,
    string? Result,
    string? Error,
    int NumTurns,
    double CostUsd);

/// <summary>
/// One queued prompt. The runner writes to it and HTTP requests read it from other
/// threads, so every change happens under <see cref="_gate"/> and readers get a
/// <see cref="JobView"/> snapshot.
/// </summary>
public sealed class Job(string prompt, DateTimeOffset createdAt)
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private bool _cancelRequested;

    private JobStatus _status = JobStatus.Queued;
    private DateTimeOffset? _startedAt, _finishedAt, _resetsAt;
    private string? _sessionId, _result, _error;
    private int _waits, _turns;
    private double _cost;

    public Guid Id { get; } = Guid.NewGuid();
    public string Prompt { get; } = prompt;
    public DateTimeOffset CreatedAt { get; } = createdAt;

    public bool IsFinished
    {
        get { lock (_gate) return _status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled; }
    }

    /// <summary>
    /// Moves Queued to Running and returns the token the run must observe, or null
    /// when the job was canceled while it sat in the queue.
    /// </summary>
    public CancellationToken? TryStart(CancellationToken stopping, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_status != JobStatus.Queued) return null;
            _status = JobStatus.Running;
            _startedAt = now;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            return _cts.Token;
        }
    }

    /// <summary>Cancels a queued or running job. False when it had already finished.</summary>
    public bool RequestCancel(DateTimeOffset now)
    {
        lock (_gate)
        {
            switch (_status)
            {
                case JobStatus.Queued:
                    _status = JobStatus.Canceled;
                    _finishedAt = now;
                    _error = "Canceled before it started.";
                    return true;
                case JobStatus.Running or JobStatus.WaitingForRateLimit:
                    _cancelRequested = true;
                    _cts?.Cancel(); // the runner observes this and calls Canceled()
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Tracks the live stream: session id, and the rate-limit wait coming and going.</summary>
    public void OnEvent(ClaudeStreamEvent evt)
    {
        lock (_gate)
        {
            if (evt.SessionId is { } id) _sessionId = id;

            if (evt.Kind == ClaudeStreamEventKind.RateLimitWait)
            {
                _status = JobStatus.WaitingForRateLimit;
                _resetsAt = evt.ResetsAt;
                _waits++;
            }
            else if (_status == JobStatus.WaitingForRateLimit)
            {
                _status = JobStatus.Running; // the resumed session is producing events again
            }
        }
    }

    public void Completed(ClaudeSessionResult run, DateTimeOffset now)
    {
        lock (_gate)
        {
            _finishedAt = now;
            _sessionId = run.SessionId ?? _sessionId;
            _waits = run.RateLimitWaits;
            _turns = run.NumTurns;
            _cost = run.CostUsd;
            _resetsAt = run.RateLimitResetsAt ?? _resetsAt;

            if (run.ExitCode == 0 && !run.IsError)
            {
                _status = JobStatus.Succeeded;
                _result = run.ResultText;
            }
            else
            {
                _status = JobStatus.Failed;
                _error = run.RateLimitDetected
                    ? $"Rate limited{(run.RateLimitResetsAt is { } at ? $" until {at:u}" : "")}; the wait policy gave up. {run.ResultText}".TrimEnd()
                    : run.ResultText ?? $"Claude Code exited with code {run.ExitCode}.";
            }
            Release();
        }
    }

    /// <summary>Canceled through DELETE, or because the server is shutting down.</summary>
    public void Canceled(DateTimeOffset now)
    {
        lock (_gate)
        {
            _status = JobStatus.Canceled;
            _finishedAt = now;
            _error = _cancelRequested ? "Canceled by request." : "Canceled: the server shut down.";
            Release();
        }
    }

    public void Failed(string error, DateTimeOffset now)
    {
        lock (_gate)
        {
            _status = JobStatus.Failed;
            _finishedAt = now;
            _error = error;
            Release();
        }
    }

    public JobView Snapshot()
    {
        lock (_gate)
        {
            return new JobView(Id, _status, Prompt, CreatedAt, _startedAt, _finishedAt, _sessionId,
                _resetsAt, _waits, _result, _error, _turns, _cost);
        }
    }

    private void Release()
    {
        _cts?.Dispose();
        _cts = null;
    }
}

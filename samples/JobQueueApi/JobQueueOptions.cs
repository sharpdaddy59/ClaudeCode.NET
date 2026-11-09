namespace JobQueueApi;

/// <summary>Bound from the <c>JobQueue</c> section of appsettings.json.</summary>
public sealed class JobQueueOptions
{
    /// <summary>Where Claude Code runs. Its tools read and write here. Null = current directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Model id (e.g. <c>sonnet</c>). Null = the CLI's default.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// How many jobs run at once. Every session draws on the same subscription
    /// usage window and works in the same directory, so 1 is the safe default.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>Jobs that may wait in the queue. When it is full, POST /jobs returns 503.</summary>
    public int QueueCapacity { get; set; } = 100;

    /// <summary>Finished jobs kept for GET /jobs/{id}; the oldest are dropped beyond this.</summary>
    public int MaxRetainedJobs { get; set; } = 100;
}

using JobQueueApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<JobQueueOptions>()
    .Bind(builder.Configuration.GetSection("JobQueue"))
    .Validate(o => o.WorkingDirectory is null || Directory.Exists(o.WorkingDirectory),
        "JobQueue:WorkingDirectory does not exist.")
    .Validate(o => o.MaxConcurrency >= 1 && o.QueueCapacity >= 1 && o.MaxRetainedJobs >= 0,
        "JobQueue: MaxConcurrency and QueueCapacity must be at least 1, MaxRetainedJobs at least 0.")
    .ValidateOnStart();

// "Running", not 2, in JSON responses
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddClaudeCode();          // ClaudeSession singleton, logs through ILogger
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService<JobRunner>();

var app = builder.Build();

var jobs = app.MapGroup("/jobs");

// Queue a prompt. Returns 202 at once; poll the Location URL for the outcome.
jobs.MapPost("/", (CreateJobRequest request, JobQueue queue, TimeProvider time) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["prompt"] = ["A prompt is required."]
        });
    }

    var job = new Job(request.Prompt, time.GetUtcNow());
    if (!queue.TryEnqueue(job))
    {
        return Results.Problem("The job queue is full; try again later.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Accepted($"/jobs/{job.Id}", job.Snapshot());
});

jobs.MapGet("/", (JobQueue queue) => queue.All().Select(j => j.Snapshot()));

jobs.MapGet("/{id:guid}", (Guid id, JobQueue queue) =>
    queue.Find(id) is { } job ? Results.Ok(job.Snapshot()) : Results.NotFound());

// Cancel a queued or running job. A running job's CLI process tree is killed.
jobs.MapDelete("/{id:guid}", (Guid id, JobQueue queue, TimeProvider time) =>
{
    if (queue.Find(id) is not { } job) return Results.NotFound();
    return job.RequestCancel(time.GetUtcNow())
        ? Results.Accepted($"/jobs/{id}", job.Snapshot())
        : Results.Conflict(job.Snapshot()); // already finished
});

app.Run();

public sealed record CreateJobRequest(string? Prompt);

/// <summary>Entry point, public so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;

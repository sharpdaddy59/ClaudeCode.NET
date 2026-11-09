using System.Diagnostics;
using System.Text;
using ClaudeCode;
using NightlyBatch;

// Unattended batch: runs every task in batch.json one after another, writes each
// result to a timestamped run folder, and exits non-zero if anything failed so the
// scheduler (Task Scheduler, cron, a CI job) can flag the night.
//
//   NightlyBatch [path/to/batch.json]
//
// Exit codes: 0 all succeeded, 1 some tasks failed, 2 bad batch file or another run
// already in progress, 3 Claude Code CLI not found, 130 canceled (Ctrl+C / SIGTERM).

const int ExitOk = 0, ExitTaskFailed = 1, ExitConfig = 2, ExitNoCli = 3, ExitCanceled = 130;

var batchPath = args.Length > 0 ? args[0] : "batch.json"; // dotnet run: the project folder

string outputRoot;
IReadOnlyList<ResolvedTask> tasks;
try
{
    (outputRoot, tasks) = BatchDefinition.Load(batchPath);
}
catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Cannot load batch: {ex.Message}");
    return ExitConfig;
}

Directory.CreateDirectory(outputRoot);

// A slow night must not overlap the next one: hold a lock file for the whole run.
FileStream runLock;
try
{
    runLock = new FileStream(Path.Combine(outputRoot, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, 1, FileOptions.DeleteOnClose);
}
catch (IOException)
{
    Console.Error.WriteLine($"Another batch run is still using {outputRoot}; exiting.");
    return ExitConfig;
}

using (runLock)
{
    var runDir = Path.Combine(outputRoot, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
    Directory.CreateDirectory(runDir);
    using var log = new RunLog(Path.Combine(runDir, "run.log"));

    // Ctrl+C or the scheduler stopping the job: cancel the task in progress (the CLI
    // process tree is killed), skip the rest, still write the summary.
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
        System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Cancel(); });

    var claude = new ClaudeSession();
    var outcomes = new List<TaskOutcome>();
    log.Write($"Batch {Path.GetFullPath(batchPath)}: {tasks.Count} tasks, results in {runDir}");

    foreach (var task in tasks)
    {
        if (stop.IsCancellationRequested)
        {
            outcomes.Add(TaskOutcome.Skipped(task.Name));
            continue;
        }

        var outcome = await RunTaskAsync(task, claude, runDir, log, stop.Token);
        outcomes.Add(outcome);
        if (outcome.Status == "cli-not-found") break; // every other task would fail the same way
    }

    File.WriteAllText(Path.Combine(runDir, "summary.md"), Summary(outcomes, runDir));
    var failed = outcomes.Count(o => o.Status != "succeeded");
    log.Write($"Done: {outcomes.Count(o => o.Status == "succeeded")} succeeded, {failed} not, " +
              $"${outcomes.Sum(o => o.CostUsd):F4}, {outcomes.Sum(o => o.RateLimitWaits)} rate-limit waits");

    if (stop.IsCancellationRequested) return ExitCanceled;
    if (outcomes.Any(o => o.Status == "cli-not-found")) return ExitNoCli;
    return failed == 0 ? ExitOk : ExitTaskFailed;
}

static async Task<TaskOutcome> RunTaskAsync(
    ResolvedTask task, ClaudeSession claude, string runDir, RunLog log, CancellationToken stop)
{
    log.Write($"[{task.Name}] started in {task.WorkingDirectory}");
    var clock = Stopwatch.StartNew();

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
    if (task.Timeout is { } limit) timeout.CancelAfter(limit);

    var options = new ClaudeSessionOptions
    {
        WorkingDirectory = task.WorkingDirectory,
        Model = task.Model,
        SessionName = $"nightly-{task.Name}",
        SkipPermissions = task.SkipPermissions,
        // Nobody is watching at 2 a.m.: wait out a usage limit and resume the same session.
        RateLimitWait = RateLimitWaitPolicy.Default
    };

    try
    {
        var result = await claude.RunAsync(task.Prompt, options, evt =>
        {
            if (evt.Kind == ClaudeStreamEventKind.RateLimitWait) log.Write($"[{task.Name}] {evt.Text}");
        }, cancellationToken: timeout.Token);

        var ok = result.ExitCode == 0 && !result.IsError;
        var body = result.ResultText ?? "";
        File.WriteAllText(Path.Combine(runDir, $"{task.Name}.md"), body);

        var outcome = new TaskOutcome(task.Name, ok ? "succeeded" : "failed", clock.Elapsed, result.NumTurns,
            result.CostUsd, result.RateLimitWaits, result.SessionId,
            ok ? null : result.RateLimitDetected ? $"rate limited until {result.RateLimitResetsAt:u}" : FirstLine(body));
        log.Write($"[{task.Name}] {outcome.Status} in {clock.Elapsed:hh\\:mm\\:ss}, {result.NumTurns} turns, ${result.CostUsd:F4}" +
                  (outcome.Error is null ? "" : $": {outcome.Error}"));
        return outcome;
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        var why = stop.IsCancellationRequested ? "canceled" : "timed-out";
        log.Write($"[{task.Name}] {why} after {clock.Elapsed:hh\\:mm\\:ss}");
        return new TaskOutcome(task.Name, why, clock.Elapsed, 0, 0, 0, null, null);
    }
    catch (ClaudeCliNotFoundException ex)
    {
        log.Write($"[{task.Name}] {ex.Message}");
        return new TaskOutcome(task.Name, "cli-not-found", clock.Elapsed, 0, 0, 0, null, ex.Message);
    }
    catch (Exception ex)
    {
        // Anything else fails this task only: the batch goes on and the summary is still written.
        log.Write($"[{task.Name}] failed: {ex.GetType().Name}: {ex.Message}");
        return new TaskOutcome(task.Name, "failed", clock.Elapsed, 0, 0, 0, null, ex.Message);
    }
}

static string FirstLine(string text) =>
    text.Split('\n', 2)[0].Trim() is { Length: > 0 } line ? line : "the session ended in an error";

static string Summary(IReadOnlyList<TaskOutcome> outcomes, string runDir)
{
    var md = new StringBuilder();
    md.AppendLine($"# Nightly batch {DateTime.Now:yyyy-MM-dd HH:mm}").AppendLine();
    md.AppendLine("| Task | Status | Time | Turns | Cost | Rate-limit waits | Note |");
    md.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
    foreach (var o in outcomes)
    {
        var name = File.Exists(Path.Combine(runDir, $"{o.Name}.md")) ? $"[{o.Name}]({o.Name}.md)" : o.Name;
        md.AppendLine($"| {name} | {o.Status} | {o.Elapsed:hh\\:mm\\:ss} | {o.NumTurns} | ${o.CostUsd:F4} | {o.RateLimitWaits} | {o.Error?.Replace("|", "\\|")} |");
    }
    md.AppendLine().AppendLine($"Total cost ${outcomes.Sum(o => o.CostUsd):F4}.");
    return md.ToString();
}

internal sealed record TaskOutcome(
    string Name, string Status, TimeSpan Elapsed, int NumTurns, double CostUsd, int RateLimitWaits,
    string? SessionId, string? Error)
{
    public static TaskOutcome Skipped(string name) => new(name, "skipped", TimeSpan.Zero, 0, 0, 0, null, "batch canceled");
}

/// <summary>Timestamped lines to the console (for the scheduler's own log) and to run.log in the run folder.</summary>
internal sealed class RunLog(string path) : IDisposable
{
    private readonly StreamWriter _file = new(path, append: false) { AutoFlush = true };

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        Console.WriteLine(line);
        _file.WriteLine(line);
    }

    public void Dispose() => _file.Dispose();
}

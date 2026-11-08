using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCode;

/// <summary>
/// Runs Claude Code CLI sessions and exposes their stream-json output as typed events.
///
/// <code>
/// var session = new ClaudeSession();
/// await foreach (var evt in session.StreamAsync("Fix the failing test", options, ct))
/// {
///     if (evt.Kind == ClaudeStreamEventKind.AssistantText) Console.Write(evt.Text);
/// }
/// </code>
/// </summary>
public sealed class ClaudeSession
{
    /// <summary>One CLI launch. Internal seam so the rate-limit loop can be tested without the CLI.</summary>
    internal delegate Task<ClaudeSessionResult> RunOnceDelegate(
        string prompt,
        ClaudeSessionOptions options,
        Action<ClaudeStreamEvent>? onEvent,
        Action<string>? onStdErrLine,
        CancellationToken cancellationToken);

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly RunOnceDelegate _runOnce;

    public ClaudeSession(ILogger<ClaudeSession>? logger = null)
        : this(logger, TimeProvider.System, runOnce: null)
    {
    }

    internal ClaudeSession(ILogger? logger, TimeProvider timeProvider, RunOnceDelegate? runOnce)
    {
        _logger = logger ?? NullLogger<ClaudeSession>.Instance;
        _timeProvider = timeProvider;
        _runOnce = runOnce ?? RunOnceAsync;
    }

    /// <summary>
    /// Runs the CLI to completion, streaming parsed events as they arrive.
    /// Stderr lines surface as Error events. Cancelling the token kills the
    /// entire CLI process tree. The final event is always Kind == Result unless
    /// the stream ended abnormally.
    /// Throws <see cref="ClaudeCliNotFoundException"/> if the CLI is not installed.
    /// </summary>
    public async IAsyncEnumerable<ClaudeStreamEvent> StreamAsync(
        string prompt,
        ClaudeSessionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ClaudeStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true
        });

        var runTask = RunAsync(
            prompt,
            options,
            evt => channel.Writer.TryWrite(evt),
            line => channel.Writer.TryWrite(new ClaudeStreamEvent
            {
                Kind = ClaudeStreamEventKind.Error,
                Text = line,
                IsError = true,
                IsRateLimited = ClaudeStreamParser.IsRateLimitError(line)
            }),
            cancellationToken);

        _ = runTask.ContinueWith(
            t => channel.Writer.TryComplete(t.Exception?.GetBaseException()),
            TaskScheduler.Default);

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }

        // Surface CLI launch failures (e.g. CLI not installed) to the caller
        await runTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the CLI to completion and returns the aggregated result. Assistant text
    /// is concatenated into <see cref="ClaudeRunResult.AssistantText"/>. For streaming
    /// output, use <see cref="StreamAsync"/> instead.
    /// </summary>
    public async Task<ClaudeRunResult> RunAsync(
        string prompt,
        ClaudeSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        ClaudeSessionResult? result = null;

        result = await RunAsync(
            prompt,
            options,
            evt =>
            {
                if (evt.Kind == ClaudeStreamEventKind.AssistantText && !string.IsNullOrEmpty(evt.Text))
                {
                    text.AppendLine(evt.Text);
                }
            },
            onStdErrLine: null,
            cancellationToken).ConfigureAwait(false);

        return new ClaudeRunResult
        {
            ExitCode = result.ExitCode,
            SessionId = result.SessionId,
            RateLimitDetected = result.RateLimitDetected,
            ResultText = result.ResultText,
            IsError = result.IsError,
            NumTurns = result.NumTurns,
            CostUsd = result.CostUsd,
            Duration = result.Duration,
            RateLimitResetsAt = result.RateLimitResetsAt,
            RateLimitWaits = result.RateLimitWaits,
            AssistantText = text.ToString().Trim()
        };
    }

    /// <summary>
    /// Runs the CLI to completion with per-event callbacks and returns the final
    /// result (exit code, session id, rate-limit signal). The lowest-level overload -
    /// orchestrators that relay events to a UI while also needing the outcome use
    /// this; simpler callers want <see cref="StreamAsync"/> or the aggregating
    /// <see cref="RunAsync(string, ClaudeSessionOptions, CancellationToken)"/>.
    /// With <see cref="ClaudeSessionOptions.RateLimitWait"/> set, a run that ends on
    /// a rate limit waits and resumes the same session; see <see cref="RateLimitWaitPolicy"/>.
    /// </summary>
    public async Task<ClaudeSessionResult> RunAsync(
        string prompt,
        ClaudeSessionOptions options,
        Action<ClaudeStreamEvent>? onEvent,
        Action<string>? onStdErrLine = null,
        CancellationToken cancellationToken = default)
    {
        var policy = options.RateLimitWait;
        var attemptPrompt = prompt;
        var attemptOptions = options;
        var waits = 0;
        var totalTurns = 0;
        var totalCost = 0d;
        var totalDuration = TimeSpan.Zero;

        while (true)
        {
            var result = await _runOnce(attemptPrompt, attemptOptions, onEvent, onStdErrLine, cancellationToken)
                .ConfigureAwait(false);

            totalTurns += result.NumTurns;
            totalCost += result.CostUsd;
            totalDuration += result.Duration;
            var aggregated = result with
            {
                NumTurns = totalTurns,
                CostUsd = totalCost,
                Duration = totalDuration,
                RateLimitWaits = waits
            };

            // Only a run the limit actually ended. A 'rejected' heartbeat the CLI
            // recovered from on its own still completes normally - leave it alone.
            var endedByRateLimit = result.RateLimitDetected && (result.IsError || result.ExitCode != 0);
            if (!endedByRateLimit || policy is null)
            {
                return aggregated;
            }

            if (waits >= policy.MaxWaits)
            {
                _logger.LogWarning("Rate limited again after {Waits} wait(s); giving up (MaxWaits)", waits);
                return aggregated;
            }

            var wait = policy.ComputeWait(result.RateLimitResetsAt, _timeProvider.GetUtcNow());
            if (wait is null)
            {
                _logger.LogWarning("Rate limited until {ResetsAt}, beyond MaxWait {MaxWait}; returning",
                    result.RateLimitResetsAt, policy.MaxWait);
                return aggregated;
            }

            waits++;
            var sessionId = result.SessionId ?? attemptOptions.ResumeSessionId;
            _logger.LogWarning("Rate limited; waiting {Wait} (reset {ResetsAt}) then resuming session {SessionId} ({Attempt}/{Max})",
                wait, result.RateLimitResetsAt, sessionId ?? "(none yet)", waits, policy.MaxWaits);
            onEvent?.Invoke(new ClaudeStreamEvent
            {
                Kind = ClaudeStreamEventKind.RateLimitWait,
                Text = result.RateLimitResetsAt is { } at
                    ? $"Rate limited; waiting {wait.Value:g} until {at:u} then resuming"
                    : $"Rate limited; waiting {wait.Value:g} then resuming",
                ResetsAt = result.RateLimitResetsAt,
                SessionId = sessionId,
                IsRateLimited = true
            });

            if (wait.Value > TimeSpan.Zero)
            {
                await Task.Delay(wait.Value, _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            if (sessionId is not null)
            {
                // Resume: the session keeps its context, so a nudge is enough
                attemptOptions = options with { ResumeSessionId = sessionId };
                attemptPrompt = policy.ContinuationPrompt;
            }
            // else: limited before the session initialized - rerun the original prompt as-is
        }
    }

    private async Task<ClaudeSessionResult> RunOnceAsync(
        string prompt,
        ClaudeSessionOptions options,
        Action<ClaudeStreamEvent>? onEvent,
        Action<string>? onStdErrLine,
        CancellationToken cancellationToken)
    {
        var claudePath = ClaudeCliLocator.FindExecutable()
            ?? throw new ClaudeCliNotFoundException();

        // Long prompts go via stdin: Windows caps the process command line at
        // ~32k chars, so a large prompt passed as an argument fails to launch.
        var promptViaStdin = options.ShouldUseStdin(prompt);

        var startInfo = new ProcessStartInfo
        {
            FileName = claudePath,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Redirect stdin ONLY when we will write the prompt to it and close it.
            // A redirected-but-unwritten stdin makes the CLI hang waiting for input.
            RedirectStandardInput = promptViaStdin,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (promptViaStdin)
        {
            // No BOM - a BOM would prepend garbage to the prompt
            startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        // ArgumentList handles platform quoting; no manual escaping of prompts
        foreach (var arg in options.BuildArguments(prompt, promptViaStdin))
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (options.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in options.EnvironmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        string? sessionId = null;
        string? resultText = null;
        var rateLimitDetected = false;
        var isError = false;
        var numTurns = 0;
        var costUsd = 0d;
        long durationMs = 0;
        DateTimeOffset? resetsAt = null;

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        _logger.LogInformation("Claude CLI started (PID {Pid}) in {WorkingDir}",
            process.Id, options.WorkingDirectory);

        // Cancellation kills the whole tree; the closed pipes then end both pump loops
        using var killRegistration = cancellationToken.Register(() => KillProcessTree(process));

        var stdoutTask = PumpAsync(process.StandardOutput, line =>
        {
            foreach (var evt in ClaudeStreamParser.ParseLine(line))
            {
                // The stderr pump writes these flags concurrently: only ever SET them.
                // (`x |= false` still writes back and can clobber the other pump's true.)
                sessionId = evt.SessionId ?? sessionId;
                if (evt.IsRateLimited) rateLimitDetected = true;
                if (evt.IsError) isError = true;
                // Every rate_limit_event carries the window's reset time, limited or
                // not; the latest one is the right wait target if we end up limited
                if (evt.ResetsAt is not null) resetsAt = evt.ResetsAt;
                if (evt.Kind == ClaudeStreamEventKind.Result)
                {
                    resultText = evt.Text;
                    numTurns = evt.NumTurns;
                    costUsd = evt.CostUsd;
                    durationMs = evt.DurationMs;
                }
                onEvent?.Invoke(evt);
            }
        }, cancellationToken);

        var stderrTask = PumpAsync(process.StandardError, line =>
        {
            _logger.LogWarning("Claude stderr: {Line}", line);
            if (ClaudeStreamParser.IsRateLimitError(line))
            {
                rateLimitDetected = true;
            }
            onStdErrLine?.Invoke(line);
        }, cancellationToken);

        if (promptViaStdin)
        {
            // Pumps are already draining stdout/stderr, so a large write cannot
            // deadlock on a full pipe. Closing stdin signals end-of-prompt.
            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                // CLI exited before reading the whole prompt (bad flags, crash) -
                // the result/stderr pumps will surface the real error
                _logger.LogWarning(ex, "Claude CLI closed stdin early");
            }
            process.StandardInput.Close();
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                KillProcessTree(process);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Claude CLI exited with code {ExitCode}", process.ExitCode);

        return new ClaudeSessionResult
        {
            ExitCode = process.ExitCode,
            SessionId = sessionId,
            RateLimitDetected = rateLimitDetected,
            ResultText = resultText,
            IsError = isError,
            NumTurns = numTurns,
            CostUsd = costUsd,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            RateLimitResetsAt = resetsAt
        };
    }

    private async Task PumpAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        try
        {
            // ReadLineAsync returns null at end-of-stream (including after a kill closes the pipe)
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                onLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled; WaitForExitAsync surfaces the cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading Claude CLI stream");
        }
    }

    private void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error killing Claude process");
        }
    }
}

/// <summary>
/// Result of <see cref="ClaudeSession.RunAsync(string, ClaudeSessionOptions, CancellationToken)"/>: session stats plus aggregated assistant text.
/// </summary>
public sealed record ClaudeRunResult
{
    public required int ExitCode { get; init; }
    public string? SessionId { get; init; }
    public bool RateLimitDetected { get; init; }
    public string? ResultText { get; init; }
    public bool IsError { get; init; }
    public int NumTurns { get; init; }
    public double CostUsd { get; init; }
    public TimeSpan Duration { get; init; }

    /// <inheritdoc cref="ClaudeSessionResult.RateLimitResetsAt"/>
    public DateTimeOffset? RateLimitResetsAt { get; init; }

    /// <inheritdoc cref="ClaudeSessionResult.RateLimitWaits"/>
    public int RateLimitWaits { get; init; }

    /// <summary>All assistant text blocks, concatenated in stream order (across all attempts when rate-limit waits occurred).</summary>
    public required string AssistantText { get; init; }
}

/// <summary>
/// Thrown when the Claude Code CLI cannot be located on this machine.
/// </summary>
public sealed class ClaudeCliNotFoundException() : Exception(
    "Claude CLI not found. Install it with the native installer - Windows PowerShell: irm https://claude.ai/install.ps1 | iex - macOS/Linux: curl -fsSL https://claude.ai/install.sh | bash - then run claude to log in. Docs: https://code.claude.com/docs/en/quickstart");

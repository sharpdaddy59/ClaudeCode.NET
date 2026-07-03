using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeAgent;

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
    private readonly ILogger _logger;

    public ClaudeSession(ILogger<ClaudeSession>? logger = null)
    {
        _logger = logger ?? NullLogger<ClaudeSession>.Instance;
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

        var runTask = RunCoreAsync(
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

        result = await RunCoreAsync(
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
            AssistantText = text.ToString().Trim()
        };
    }

    private async Task<ClaudeSessionResult> RunCoreAsync(
        string prompt,
        ClaudeSessionOptions options,
        Action<ClaudeStreamEvent>? onEvent,
        Action<string>? onStdErrLine,
        CancellationToken cancellationToken)
    {
        var claudePath = ClaudeCliLocator.FindExecutable()
            ?? throw new ClaudeCliNotFoundException();

        var startInfo = new ProcessStartInfo
        {
            FileName = claudePath,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Do NOT redirect stdin - it causes the CLI to hang waiting for input
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // ArgumentList handles platform quoting; no manual escaping of prompts
        foreach (var arg in options.BuildArguments(prompt))
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
            Duration = TimeSpan.FromMilliseconds(durationMs)
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
/// Result of <see cref="ClaudeSession.RunAsync"/>: session stats plus aggregated assistant text.
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

    /// <summary>All assistant text blocks, concatenated in stream order.</summary>
    public required string AssistantText { get; init; }
}

/// <summary>
/// Thrown when the Claude Code CLI cannot be located on this machine.
/// </summary>
public sealed class ClaudeCliNotFoundException() : Exception(
    "Claude CLI not found. Install with: npm install -g @anthropic-ai/claude-code, then authenticate with: claude login");

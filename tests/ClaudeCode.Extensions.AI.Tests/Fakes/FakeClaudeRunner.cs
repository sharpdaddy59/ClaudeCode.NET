using System.Runtime.CompilerServices;

namespace ClaudeCode.Extensions.AI.Tests.Fakes;

/// <summary>How a recorded call reached the fake.</summary>
public enum FakeRunMode
{
    Run,
    Stream,
}

/// <summary>One invocation recorded by <see cref="FakeClaudeRunner"/>.</summary>
public sealed record FakeRunnerCall(
    FakeRunMode Mode,
    string Prompt,
    ClaudeSessionOptions Options,
    bool HadEventCallback,
    CancellationToken CancellationToken);

/// <summary>A scripted response: events replayed in order, then the final result (or an exception).</summary>
public sealed class FakeRun
{
    public List<ClaudeStreamEvent> Events { get; } = [];

    /// <summary>Explicit result. When null, one is derived from the last Result event (or empty).</summary>
    public ClaudeSessionResult? Result { get; set; }

    /// <summary>Thrown after the events have been replayed (e.g. ClaudeCliNotFoundException).</summary>
    public Exception? Exception { get; set; }

    /// <summary>
    /// The real CLI ends every attempt with a Result event, so in Run mode the fake emits one
    /// (built from the resolved result) when the script has none after the last rate-limit
    /// wait. Set this to replay a CLI that exits without one.
    /// </summary>
    public bool OmitResultEvent { get; set; }

    /// <summary>Delay before each event, to exercise cancellation.</summary>
    public TimeSpan DelayPerEvent { get; set; }

    public FakeRun Event(ClaudeStreamEvent evt)
    {
        Events.Add(evt);
        return this;
    }

    public FakeRun Text(string text)
        => Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.AssistantText, Text = text });

    public FakeRun Delta(string text)
        => Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.AssistantTextDelta, Text = text });

    public FakeRun Init(string sessionId)
        => Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.SystemInit, SessionId = sessionId });

    public FakeRun Final(string? text = null, string? sessionId = null, bool isError = false, int numTurns = 1, double costUsd = 0, long durationMs = 0)
        => Event(new ClaudeStreamEvent
        {
            Kind = ClaudeStreamEventKind.Result,
            Text = text!,
            SessionId = sessionId!,
            IsError = isError,
            NumTurns = numTurns,
            CostUsd = costUsd,
            DurationMs = durationMs,
        });

    public FakeRun WithoutResultEvent()
    {
        OmitResultEvent = true;
        return this;
    }

    internal bool NeedsSyntheticResultEvent()
    {
        if (OmitResultEvent) return false;
        var lastWait = Events.FindLastIndex(e => e.Kind == ClaudeStreamEventKind.RateLimitWait);
        return !Events.Skip(lastWait + 1).Any(e => e.Kind == ClaudeStreamEventKind.Result);
    }

    public FakeRun Returns(ClaudeSessionResult result)
    {
        Result = result;
        return this;
    }

    public FakeRun Throws(Exception exception)
    {
        Exception = exception;
        return this;
    }

    public FakeRun WithDelay(TimeSpan delayPerEvent)
    {
        DelayPerEvent = delayPerEvent;
        return this;
    }

    internal ClaudeSessionResult ResolveResult()
    {
        if (Result is not null)
        {
            return Result;
        }

        var final = Events.LastOrDefault(e => e.Kind == ClaudeStreamEventKind.Result);
        var sessionId = final?.SessionId
            ?? Events.LastOrDefault(e => e.Kind == ClaudeStreamEventKind.SystemInit)?.SessionId;
        return new ClaudeSessionResult
        {
            ExitCode = final?.IsError == true ? 1 : 0,
            SessionId = sessionId!,
            ResultText = final?.Text!,
            IsError = final?.IsError ?? false,
            NumTurns = final?.NumTurns ?? 0,
            CostUsd = final?.CostUsd ?? 0,
            Duration = TimeSpan.FromMilliseconds(final?.DurationMs ?? 0),
            RateLimitDetected = Events.Any(e => e.IsRateLimited),
        };
    }
}

/// <summary>
/// Test double for <see cref="IClaudeRunner"/>. Never launches a process: it records every call
/// and replays scripted runs in FIFO order. When the script queue is empty it replays
/// <see cref="DefaultRun"/> (an empty run unless set).
/// </summary>
public sealed class FakeClaudeRunner : IClaudeRunner
{
    private readonly Queue<FakeRun> _script = new();
    private readonly List<FakeRunnerCall> _calls = [];
    private readonly Lock _gate = new();

    public FakeRun DefaultRun { get; set; } = new();

    public IReadOnlyList<FakeRunnerCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public FakeRunnerCall LastCall => Calls[^1];

    /// <summary>Queues a new run and returns it for fluent scripting.</summary>
    public FakeRun Enqueue()
    {
        var run = new FakeRun();
        Enqueue(run);
        return run;
    }

    public FakeClaudeRunner Enqueue(FakeRun run)
    {
        lock (_gate)
        {
            _script.Enqueue(run);
        }

        return this;
    }

    public async Task<ClaudeSessionResult> RunAsync(
        string prompt,
        ClaudeSessionOptions options,
        Action<ClaudeStreamEvent>? onEvent,
        CancellationToken cancellationToken)
    {
        var run = Record(FakeRunMode.Run, prompt, options, onEvent is not null, cancellationToken);
        foreach (var evt in run.Events)
        {
            await DelayAsync(run, cancellationToken).ConfigureAwait(false);
            onEvent?.Invoke(evt);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (run.Exception is not null)
        {
            throw run.Exception;
        }

        var result = run.ResolveResult();
        if (run.NeedsSyntheticResultEvent())
        {
            onEvent?.Invoke(new ClaudeStreamEvent
            {
                Kind = ClaudeStreamEventKind.Result,
                Text = result.ResultText!,
                SessionId = result.SessionId!,
                IsError = result.IsError,
                NumTurns = result.NumTurns,
                CostUsd = result.CostUsd,
                DurationMs = (long)result.Duration.TotalMilliseconds,
            });
        }

        return result;
    }

    public async IAsyncEnumerable<ClaudeStreamEvent> StreamAsync(
        string prompt,
        ClaudeSessionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var run = Record(FakeRunMode.Stream, prompt, options, hadCallback: false, cancellationToken);
        foreach (var evt in run.Events)
        {
            await DelayAsync(run, cancellationToken).ConfigureAwait(false);
            yield return evt;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (run.Exception is not null)
        {
            throw run.Exception;
        }
    }

    private FakeRun Record(FakeRunMode mode, string prompt, ClaudeSessionOptions options, bool hadCallback, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add(new FakeRunnerCall(mode, prompt, options, hadCallback, ct));
            return _script.Count > 0 ? _script.Dequeue() : DefaultRun;
        }
    }

    private static Task DelayAsync(FakeRun run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return run.DelayPerEvent > TimeSpan.Zero ? Task.Delay(run.DelayPerEvent, ct) : Task.CompletedTask;
    }
}

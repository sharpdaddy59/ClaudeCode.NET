using FluentAssertions;
using ClaudeCode;
using Xunit;

namespace ClaudeCode.Tests;

/// <summary>
/// Exercises the wait-and-resume loop in ClaudeSession.RunAsync through the internal
/// RunOnce seam, so no CLI is launched and no real time passes.
/// </summary>
public class RateLimitWaitTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaudeSessionOptions Options(RateLimitWaitPolicy? policy = null) => new()
    {
        WorkingDirectory = "/tmp/project",
        RateLimitWait = policy
    };

    private static ClaudeSessionResult Limited(string? sessionId, DateTimeOffset? resetsAt, int turns = 2) => new()
    {
        ExitCode = 1,
        SessionId = sessionId,
        RateLimitDetected = true,
        IsError = true,
        ResultText = "You've hit your usage limit",
        NumTurns = turns,
        CostUsd = 0.5,
        Duration = TimeSpan.FromMinutes(1),
        RateLimitResetsAt = resetsAt
    };

    private static ClaudeSessionResult Success(string sessionId, int turns = 3) => new()
    {
        ExitCode = 0,
        SessionId = sessionId,
        ResultText = "done",
        NumTurns = turns,
        CostUsd = 0.25,
        Duration = TimeSpan.FromMinutes(2)
    };

    /// <summary>Records each RunOnce call and replays scripted results in order.</summary>
    private sealed class ScriptedRunner
    {
        private readonly Queue<ClaudeSessionResult> _results;
        public List<(string Prompt, ClaudeSessionOptions Options)> Calls { get; } = [];

        public ScriptedRunner(params ClaudeSessionResult[] results) => _results = new(results);

        public Task<ClaudeSessionResult> RunOnce(
            string prompt, ClaudeSessionOptions options, Action<ClaudeStreamEvent>? onEvent,
            Action<string>? onStdErr, CancellationToken ct)
        {
            Calls.Add((prompt, options));
            return Task.FromResult(_results.Dequeue());
        }
    }

    /// <summary>Fixed clock whose timers fire immediately, recording the requested delays.</summary>
    private sealed class InstantTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            callback(state);
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static (ClaudeSession Session, ScriptedRunner Runner, InstantTimeProvider Clock) Build(params ClaudeSessionResult[] script)
    {
        var runner = new ScriptedRunner(script);
        var clock = new InstantTimeProvider();
        var session = new ClaudeSession(logger: null, clock, runner.RunOnce);
        return (session, runner, clock);
    }

    [Fact]
    public async Task NoPolicy_RateLimitedRun_ReturnsImmediately()
    {
        var (session, runner, clock) = Build(Limited("s1", Now.AddMinutes(10)));

        var result = await session.RunAsync("go", Options(), onEvent: null, cancellationToken: Ct);

        runner.Calls.Should().HaveCount(1);
        clock.Delays.Should().BeEmpty();
        result.RateLimitDetected.Should().BeTrue();
        result.RateLimitWaits.Should().Be(0);
        result.RateLimitResetsAt.Should().Be(Now.AddMinutes(10));
    }

    [Fact]
    public async Task Policy_WaitsUntilResetPlusPadding_ThenResumesSameSession()
    {
        var resetsAt = Now.AddMinutes(42);
        var policy = new RateLimitWaitPolicy { Padding = TimeSpan.FromSeconds(30) };
        var (session, runner, clock) = Build(Limited("s1", resetsAt), Success("s1"));
        var events = new List<ClaudeStreamEvent>();

        var result = await session.RunAsync("do the thing", Options(policy), events.Add, cancellationToken: Ct);

        runner.Calls.Should().HaveCount(2);
        runner.Calls[0].Prompt.Should().Be("do the thing");
        runner.Calls[0].Options.ResumeSessionId.Should().BeNull();
        runner.Calls[1].Prompt.Should().Be(policy.ContinuationPrompt);
        runner.Calls[1].Options.ResumeSessionId.Should().Be("s1");
        // Everything else about the options carries over unchanged
        runner.Calls[1].Options.WorkingDirectory.Should().Be("/tmp/project");

        clock.Delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMinutes(42) + TimeSpan.FromSeconds(30));

        var wait = events.Should().ContainSingle(e => e.Kind == ClaudeStreamEventKind.RateLimitWait).Subject;
        wait.ResetsAt.Should().Be(resetsAt);
        wait.SessionId.Should().Be("s1");
        wait.IsRateLimited.Should().BeTrue();
        wait.Text.Should().Contain("waiting");

        result.ExitCode.Should().Be(0);
        result.IsError.Should().BeFalse();
        result.RateLimitWaits.Should().Be(1);
    }

    [Fact]
    public async Task Policy_AggregatesTurnsCostAndDurationAcrossAttempts()
    {
        var (session, _, _) = Build(Limited("s1", Now, turns: 2), Success("s1", turns: 3));

        var result = await session.RunAsync("go", Options(RateLimitWaitPolicy.Default), onEvent: null, cancellationToken: Ct);

        result.NumTurns.Should().Be(5);
        result.CostUsd.Should().BeApproximately(0.75, 1e-9);
        result.Duration.Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task Policy_ResetAlreadyPassed_ResumesWithoutDelay()
    {
        var (session, runner, clock) = Build(Limited("s1", Now.AddMinutes(-5)), Success("s1"));

        await session.RunAsync("go", Options(RateLimitWaitPolicy.Default), onEvent: null, cancellationToken: Ct);

        runner.Calls.Should().HaveCount(2);
        clock.Delays.Should().BeEmpty("a zero wait must not create a timer");
    }

    [Fact]
    public async Task Policy_NoResetTime_UsesFallbackWait()
    {
        var policy = new RateLimitWaitPolicy { FallbackWait = TimeSpan.FromMinutes(7) };
        var (session, runner, clock) = Build(Limited("s1", resetsAt: null), Success("s1"));

        await session.RunAsync("go", Options(policy), onEvent: null, cancellationToken: Ct);

        runner.Calls.Should().HaveCount(2);
        clock.Delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task Policy_ResetBeyondMaxWait_ReturnsLimitedWithoutWaiting()
    {
        // Seven-day window: don't park an unattended job for days
        var policy = new RateLimitWaitPolicy { MaxWait = TimeSpan.FromHours(6) };
        var (session, runner, clock) = Build(Limited("s1", Now.AddDays(3)));

        var result = await session.RunAsync("go", Options(policy), onEvent: null, cancellationToken: Ct);

        runner.Calls.Should().HaveCount(1);
        clock.Delays.Should().BeEmpty();
        result.RateLimitDetected.Should().BeTrue();
        result.RateLimitWaits.Should().Be(0);
    }

    [Fact]
    public async Task Policy_MaxWaitsExhausted_ReturnsLastLimitedResult()
    {
        var policy = new RateLimitWaitPolicy { MaxWaits = 2 };
        var (session, runner, _) = Build(
            Limited("s1", Now), Limited("s1", Now), Limited("s1", Now));

        var result = await session.RunAsync("go", Options(policy), onEvent: null, cancellationToken: Ct);

        runner.Calls.Should().HaveCount(3, "initial run plus two resumes");
        result.RateLimitDetected.Should().BeTrue();
        result.IsError.Should().BeTrue();
        result.RateLimitWaits.Should().Be(2);
        result.NumTurns.Should().Be(6);
    }

    [Fact]
    public async Task Policy_RateLimitSeenButRunSucceeded_DoesNotWait()
    {
        // A 'rejected' heartbeat the CLI recovered from: the run completed normally
        var recovered = Success("s1") with { RateLimitDetected = true, RateLimitResetsAt = Now.AddHours(1) };
        var (session, runner, clock) = Build(recovered);

        var result = await session.RunAsync("go", Options(RateLimitWaitPolicy.Default), onEvent: null, cancellationToken: Ct);

        runner.Calls.Should().HaveCount(1);
        clock.Delays.Should().BeEmpty();
        result.RateLimitWaits.Should().Be(0);
    }

    [Fact]
    public async Task Policy_LimitedBeforeSessionInit_RerunsOriginalPrompt()
    {
        var (session, runner, _) = Build(Limited(sessionId: null, Now), Success("s2"));

        var result = await session.RunAsync("original prompt", Options(RateLimitWaitPolicy.Default), onEvent: null, cancellationToken: Ct);

        runner.Calls.Should().HaveCount(2);
        runner.Calls[1].Prompt.Should().Be("original prompt");
        runner.Calls[1].Options.ResumeSessionId.Should().BeNull();
        result.SessionId.Should().Be("s2");
    }

    [Fact]
    public async Task Policy_ResumingAnExistingSession_KeepsResumingIt()
    {
        // Caller was already chaining onto session "prev" and the CLI never got to
        // report an id before the limit: resume "prev", not nothing
        var options = Options(RateLimitWaitPolicy.Default) with { ResumeSessionId = "prev" };
        var (session, runner, _) = Build(Limited(sessionId: null, Now), Success("prev"));

        await session.RunAsync("next step", options, onEvent: null, cancellationToken: Ct);

        runner.Calls[1].Options.ResumeSessionId.Should().Be("prev");
        runner.Calls[1].Prompt.Should().Be(RateLimitWaitPolicy.Default.ContinuationPrompt);
    }

    [Fact]
    public async Task AggregatingRunAsync_CarriesWaitsAndAssistantTextAcrossAttempts()
    {
        var runner = new ScriptedRunner(Limited("s1", Now), Success("s1"));
        var clock = new InstantTimeProvider();
        // Emit assistant text from inside the seam, as the real pump would
        Task<ClaudeSessionResult> RunOnce(string p, ClaudeSessionOptions o, Action<ClaudeStreamEvent>? onEvent, Action<string>? e, CancellationToken ct)
        {
            onEvent?.Invoke(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.AssistantText, Text = $"attempt {runner.Calls.Count + 1}" });
            return runner.RunOnce(p, o, onEvent, e, ct);
        }
        var session = new ClaudeSession(logger: null, clock, RunOnce);

        var result = await session.RunAsync("go", Options(RateLimitWaitPolicy.Default), Ct);

        result.RateLimitWaits.Should().Be(1);
        result.AssistantText.Should().Be("attempt 1\nattempt 2".Replace("\n", Environment.NewLine));
    }

    [Theory]
    [InlineData(10, 30, 360, 10 * 60 + 30)]   // 10 min out + 30 s pad, under the 6 h cap
    [InlineData(-5, 30, 360, 0)]              // already reset: no wait
    [InlineData(400, 0, 360, -1)]             // beyond cap: null
    public void ComputeWait_FromResetTime(int resetInMinutes, int paddingSeconds, int maxWaitMinutes, int expectedSeconds)
    {
        var policy = new RateLimitWaitPolicy
        {
            Padding = TimeSpan.FromSeconds(paddingSeconds),
            MaxWait = TimeSpan.FromMinutes(maxWaitMinutes)
        };

        var wait = policy.ComputeWait(Now.AddMinutes(resetInMinutes), Now);

        if (expectedSeconds < 0) wait.Should().BeNull();
        else wait.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void ComputeWait_NoResetTime_FallbackMustFitUnderMaxWait()
    {
        var policy = new RateLimitWaitPolicy { FallbackWait = TimeSpan.FromHours(2), MaxWait = TimeSpan.FromHours(1) };

        policy.ComputeWait(resetsAt: null, Now).Should().BeNull();
    }

    [Fact]
    public void Defaults_CoverFiveHourWindowButNotSevenDay()
    {
        var policy = RateLimitWaitPolicy.Default;

        policy.ComputeWait(Now.AddHours(5), Now).Should().NotBeNull();
        policy.ComputeWait(Now.AddDays(2), Now).Should().BeNull();
        policy.MaxWaits.Should().Be(3);
    }
}

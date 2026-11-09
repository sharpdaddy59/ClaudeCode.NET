using ClaudeCode.Extensions.AI.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace ClaudeCode.Extensions.AI.Tests;

public class ClaudeCodeChatClientErrorTests
{
    private readonly FakeClaudeRunner _runner = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClaudeCodeChatClient CreateClient()
        => new(_runner, new ClaudeCodeChatClientOptions { WorkingDirectory = "/repo" });

    [Fact]
    public async Task Error_result_throws_with_all_properties_set()
    {
        var resetsAt = new DateTimeOffset(2026, 11, 8, 20, 0, 0, TimeSpan.Zero);
        _runner.Enqueue().Text("partial").Returns(new ClaudeSessionResult
        {
            ExitCode = 2,
            IsError = true,
            ResultText = "Something broke",
            SessionId = "sess-err",
            RateLimitDetected = true,
            RateLimitResetsAt = resetsAt,
        });
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        var ex = (await act.Should().ThrowAsync<ClaudeCodeChatException>()).Which;
        ex.Message.Should().Be("Something broke");
        ex.ExitCode.Should().Be(2);
        ex.SessionId.Should().Be("sess-err");
        ex.RateLimitDetected.Should().BeTrue();
        ex.RateLimitResetsAt.Should().Be(resetsAt);
        ex.InnerException.Should().BeNull();
    }

    [Fact]
    public async Task IsError_with_zero_exit_code_throws()
    {
        _runner.Enqueue().Returns(new ClaudeSessionResult { ExitCode = 0, IsError = true, ResultText = "max turns reached" });
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        (await act.Should().ThrowAsync<ClaudeCodeChatException>()).Which.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task Non_zero_exit_code_without_IsError_throws_generic_message()
    {
        _runner.Enqueue().Returns(new ClaudeSessionResult { ExitCode = 137 });
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        var ex = (await act.Should().ThrowAsync<ClaudeCodeChatException>()).Which;
        ex.Message.Should().Contain("exit code 137").And.NotContain("rate limit");
        ex.SessionId.Should().BeNull();
        ex.RateLimitDetected.Should().BeFalse();
        ex.RateLimitResetsAt.Should().BeNull();
    }

    [Fact]
    public async Task Negative_exit_code_throws()
    {
        _runner.Enqueue().Returns(new ClaudeSessionResult { ExitCode = -1, ResultText = "  " });
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        (await act.Should().ThrowAsync<ClaudeCodeChatException>()).WithMessage("*exit code -1*");
    }

    [Fact]
    public async Task Rate_limited_failure_exposes_resets_at_and_mentions_it()
    {
        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
        _runner.Enqueue().Returns(new ClaudeSessionResult
        {
            ExitCode = 1,
            IsError = true,
            RateLimitDetected = true,
            RateLimitResetsAt = resetsAt,
            SessionId = "sess-rl",
        });
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        var ex = (await act.Should().ThrowAsync<ClaudeCodeChatException>()).Which;
        ex.RateLimitDetected.Should().BeTrue();
        ex.RateLimitResetsAt.Should().Be(resetsAt);
        ex.SessionId.Should().Be("sess-rl");
        ex.Message.Should().Contain("rate limit").And.Contain(resetsAt.ToString("O"));
    }

    [Fact]
    public async Task Rate_limited_failure_without_reset_time()
    {
        _runner.Enqueue().Returns(new ClaudeSessionResult { ExitCode = 1, RateLimitDetected = true });
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        var ex = (await act.Should().ThrowAsync<ClaudeCodeChatException>()).Which;
        ex.RateLimitResetsAt.Should().BeNull();
        ex.Message.Should().EndWith("A rate limit was hit.");
    }

    [Fact]
    public async Task Error_result_from_stream_Result_event_throws()
    {
        _runner.Enqueue().Init("s").Final("API error", "s", isError: true);
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        (await act.Should().ThrowAsync<ClaudeCodeChatException>()).WithMessage("API error");
    }

    [Fact]
    public async Task Success_with_rate_limit_waits_surfaces_count()
    {
        _runner.Enqueue().Returns(new ClaudeSessionResult
        {
            ExitCode = 0,
            ResultText = "done eventually",
            SessionId = "s",
            RateLimitDetected = true,
            RateLimitWaits = 2,
        });
        using var client = CreateClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        response.Text.Should().Be("done eventually");
        response.AdditionalProperties.Should().NotBeNull();
        response.AdditionalProperties![ClaudeCodeChatClient.RateLimitWaitsKey].Should().Be(2);
        ClaudeCodeChatClient.RateLimitWaitsKey.Should().Be("claude.rate_limit_waits");
    }

    [Fact]
    public async Task Success_without_waits_has_no_rate_limit_entry()
    {
        _runner.Enqueue().Returns(new ClaudeSessionResult { ExitCode = 0, ResultText = "ok" });
        using var client = CreateClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        (response.AdditionalProperties?.ContainsKey(ClaudeCodeChatClient.RateLimitWaitsKey) ?? false).Should().BeFalse();
    }

    [Fact]
    public async Task Cli_not_found_propagates_unchanged()
    {
        var original = new ClaudeCliNotFoundException();
        _runner.Enqueue().Throws(original);
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        (await act.Should().ThrowAsync<ClaudeCliNotFoundException>()).Which.Should().BeSameAs(original);
    }

    [Fact]
    public async Task Cancellation_propagates_unchanged()
    {
        var original = new OperationCanceledException("cancelled by runner");
        _runner.Enqueue().Throws(original);
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(original);
    }

    [Fact]
    public async Task Token_cancellation_is_not_wrapped()
    {
        _runner.Enqueue().Text("x").WithDelay(TimeSpan.FromSeconds(30));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(30));
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: cts.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().NotBeOfType<ClaudeCodeChatException>();
    }

    [Fact]
    public async Task Other_runner_exceptions_are_not_wrapped()
    {
        _runner.Enqueue().Throws(new IOException("pipe closed"));
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public void Exception_is_public_sealed_and_derives_from_Exception()
    {
        typeof(ClaudeCodeChatException).IsPublic.Should().BeTrue();
        typeof(ClaudeCodeChatException).IsSealed.Should().BeTrue();

        var inner = new InvalidOperationException();
        var ex = new ClaudeCodeChatException("m", 3, "s", true, null, inner);
        ex.InnerException.Should().BeSameAs(inner);
        ex.ExitCode.Should().Be(3);
    }

    private static ClaudeStreamEvent RateLimitWait()
        => new() { Kind = ClaudeStreamEventKind.RateLimitWait, Text = "Rate limited; waiting 1m then resuming", IsRateLimited = true };

    [Fact]
    public async Task Clean_exit_without_a_result_event_throws_like_streaming_does()
    {
        // Output cut off: some text, exit code 0, but the CLI never wrote its result line
        _runner.Enqueue().Init("sess-cut").Text("partial answer").WithoutResultEvent()
            .Returns(new ClaudeSessionResult { ExitCode = 0, SessionId = "sess-cut" });
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        var ex = (await act.Should().ThrowAsync<ClaudeCodeChatException>()).Which;
        ex.ExitCode.Should().Be(ClaudeCodeChatException.UnknownExitCode);
        ex.Message.Should().Be("The Claude Code session failed without reporting a result.");
        ex.SessionId.Should().Be("sess-cut");
    }

    [Fact]
    public async Task Resumed_attempt_without_its_own_result_event_throws()
    {
        // The failed attempt reported a result; the resumed one never did
        _runner.Enqueue().Text("before").Final(text: "usage limit", isError: true).Event(RateLimitWait())
            .Text("after").WithoutResultEvent()
            .Returns(new ClaudeSessionResult { ExitCode = 0, SessionId = "s", RateLimitWaits = 1 });
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        (await act.Should().ThrowAsync<ClaudeCodeChatException>()).Which
            .ExitCode.Should().Be(ClaudeCodeChatException.UnknownExitCode);
    }

    [Fact]
    public async Task Text_from_before_a_rate_limit_wait_is_kept_in_the_fallback_reply()
    {
        // No final result text, so the reply falls back to the assistant text: the resumed
        // session continues the same answer, as streaming already showed it
        _runner.Enqueue().Text("first half").Final(text: "usage limit", isError: true).Event(RateLimitWait())
            .Text("second half").Final(text: "", sessionId: "s");
        using var client = CreateClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        response.Text.Should().Be("first half\n\nsecond half");
    }
}

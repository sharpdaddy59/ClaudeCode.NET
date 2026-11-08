using ClaudeCode.Extensions.AI.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace ClaudeCode.Extensions.AI.Tests;

public class ClaudeCodeChatClientUsageTests
{
    private readonly FakeClaudeRunner _runner = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClaudeCodeChatClient CreateClient()
        => new(_runner, new ClaudeCodeChatClientOptions { WorkingDirectory = "/repo" });

    private static ClaudeStreamEvent ResultEvent(string rawJson)
        => new() { Kind = ClaudeStreamEventKind.Result, RawJson = rawJson };

    [Fact]
    public async Task Cli_stats_go_into_additional_properties_with_correct_types()
    {
        _runner.Enqueue().Returns(new ClaudeSessionResult
        {
            ExitCode = 0,
            ResultText = "ok",
            CostUsd = 0.0123,
            NumTurns = 5,
            Duration = TimeSpan.FromMilliseconds(4321),
        });
        using var client = CreateClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        var props = response.AdditionalProperties!;
        props[ClaudeCodeChatClient.CostUsdKey].Should().BeOfType<double>().Which.Should().Be(0.0123);
        props[ClaudeCodeChatClient.NumTurnsKey].Should().BeOfType<int>().Which.Should().Be(5);
        props[ClaudeCodeChatClient.DurationMsKey].Should().BeOfType<long>().Which.Should().Be(4321L);
        ClaudeCodeChatClient.CostUsdKey.Should().Be("claude.cost_usd");
        ClaudeCodeChatClient.NumTurnsKey.Should().Be("claude.num_turns");
        ClaudeCodeChatClient.DurationMsKey.Should().Be("claude.duration_ms");
    }

    [Fact]
    public async Task Zero_cost_is_kept_when_the_result_carries_stats()
    {
        _runner.Enqueue().Returns(new ClaudeSessionResult { ExitCode = 0, ResultText = "ok", NumTurns = 1, CostUsd = 0 });
        using var client = CreateClient();

        var props = (await client.GetResponseAsync("Hi", cancellationToken: Ct)).AdditionalProperties!;

        props[ClaudeCodeChatClient.CostUsdKey].Should().Be(0.0);
        props.ContainsKey(ClaudeCodeChatClient.DurationMsKey).Should().BeFalse("duration was not reported");
    }

    [Fact]
    public async Task Absent_data_stays_null_not_zero()
    {
        _runner.Enqueue().Returns(new ClaudeSessionResult { ExitCode = 0, ResultText = "ok" });
        using var client = CreateClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        response.Usage.Should().BeNull("the CLI reported no token usage");
        response.AdditionalProperties.Should().BeNull("no stats were reported");
    }

    [Fact]
    public async Task Usage_is_parsed_from_result_event_json()
    {
        _runner.Enqueue()
            .Text("ok")
            .Event(ResultEvent("""
                {"type":"result","subtype":"success","is_error":false,"num_turns":1,
                 "usage":{"input_tokens":10,"cache_creation_input_tokens":100,"cache_read_input_tokens":1000,"output_tokens":50}}
                """))
            .Returns(new ClaudeSessionResult { ExitCode = 0, ResultText = "ok", NumTurns = 1 });
        using var client = CreateClient();

        var usage = (await client.GetResponseAsync("Hi", cancellationToken: Ct)).Usage!;

        usage.InputTokenCount.Should().Be(1110, "cached tokens count as input in MEAI");
        usage.CachedInputTokenCount.Should().Be(1000);
        usage.OutputTokenCount.Should().Be(50);
        usage.TotalTokenCount.Should().Be(1160);
        usage.AdditionalCounts![UsageMapper.CacheCreationInputTokensKey].Should().Be(100);
        usage.ReasoningTokenCount.Should().BeNull();
    }

    [Fact]
    public async Task Usage_is_summed_across_result_events_from_rate_limit_resumes()
    {
        _runner.Enqueue()
            .Event(ResultEvent("""{"type":"result","usage":{"input_tokens":5,"output_tokens":7}}"""))
            .Event(ResultEvent("""{"type":"result","usage":{"input_tokens":3,"output_tokens":2}}"""))
            .Returns(new ClaudeSessionResult { ExitCode = 0, ResultText = "ok", RateLimitWaits = 1 });
        using var client = CreateClient();

        var usage = (await client.GetResponseAsync("Hi", cancellationToken: Ct)).Usage!;

        usage.InputTokenCount.Should().Be(8);
        usage.OutputTokenCount.Should().Be(9);
        usage.TotalTokenCount.Should().Be(17);
        usage.CachedInputTokenCount.Should().BeNull();
        usage.AdditionalCounts.Should().BeNull();
    }

    [Fact]
    public async Task Partial_usage_leaves_unreported_counts_null()
    {
        _runner.Enqueue()
            .Event(ResultEvent("""{"type":"result","usage":{"output_tokens":42}}"""))
            .Returns(new ClaudeSessionResult { ExitCode = 0, ResultText = "ok" });
        using var client = CreateClient();

        var usage = (await client.GetResponseAsync("Hi", cancellationToken: Ct)).Usage!;

        usage.OutputTokenCount.Should().Be(42);
        usage.InputTokenCount.Should().BeNull();
        usage.TotalTokenCount.Should().BeNull("total cannot be computed without input");
        usage.CachedInputTokenCount.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"type":"result"}""")]
    [InlineData("""{"type":"result","usage":null}""")]
    [InlineData("""{"type":"result","usage":{}}""")]
    [InlineData("""{"type":"result","usage":{"input_tokens":"ten"}}""")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public async Task Missing_or_malformed_usage_yields_null_usage(string rawJson)
    {
        _runner.Enqueue()
            .Event(ResultEvent(rawJson))
            .Returns(new ClaudeSessionResult { ExitCode = 0, ResultText = "ok" });
        using var client = CreateClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        response.Usage.Should().BeNull();
    }

    [Fact]
    public async Task Result_event_without_raw_json_is_ignored()
    {
        _runner.Enqueue().Final("ok", numTurns: 2);
        using var client = CreateClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        response.Usage.Should().BeNull();
        response.AdditionalProperties![ClaudeCodeChatClient.NumTurnsKey].Should().Be(2);
    }

    [Fact]
    public async Task Usage_from_non_result_events_is_not_counted()
    {
        _runner.Enqueue()
            .Event(new ClaudeStreamEvent
            {
                Kind = ClaudeStreamEventKind.AssistantText,
                Text = "hi",
                RawJson = """{"type":"assistant","usage":{"input_tokens":999,"output_tokens":999}}""",
            })
            .Returns(new ClaudeSessionResult { ExitCode = 0, ResultText = "ok" });
        using var client = CreateClient();

        (await client.GetResponseAsync("Hi", cancellationToken: Ct)).Usage.Should().BeNull();
    }

    [Fact]
    public async Task Stats_and_rate_limit_waits_coexist()
    {
        _runner.Enqueue().Returns(new ClaudeSessionResult
        {
            ExitCode = 0,
            ResultText = "ok",
            NumTurns = 3,
            CostUsd = 1.5,
            Duration = TimeSpan.FromSeconds(2),
            RateLimitWaits = 1,
        });
        using var client = CreateClient();

        var props = (await client.GetResponseAsync("Hi", cancellationToken: Ct)).AdditionalProperties!;

        props.Should().HaveCount(4);
        props[ClaudeCodeChatClient.RateLimitWaitsKey].Should().Be(1);
        props[ClaudeCodeChatClient.DurationMsKey].Should().Be(2000L);
    }
}

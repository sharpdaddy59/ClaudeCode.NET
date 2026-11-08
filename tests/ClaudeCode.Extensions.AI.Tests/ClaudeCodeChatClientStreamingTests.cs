using ClaudeCode.Extensions.AI.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace ClaudeCode.Extensions.AI.Tests;

public class ClaudeCodeChatClientStreamingTests
{
    private readonly FakeClaudeRunner _runner = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClaudeCodeChatClient CreateClient(Action<ClaudeCodeChatClientOptions>? configure = null)
    {
        var options = new ClaudeCodeChatClientOptions { WorkingDirectory = "/repo" };
        configure?.Invoke(options);
        return new ClaudeCodeChatClient(_runner, options);
    }

    private static async Task<List<ChatResponseUpdate>> Collect(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        var list = new List<ChatResponseUpdate>();
        await foreach (var u in updates)
        {
            list.Add(u);
        }

        return list;
    }

    private static List<string> Texts(IEnumerable<ChatResponseUpdate> updates)
        => updates.Where(u => !string.IsNullOrEmpty(u.Text)).Select(u => u.Text).ToList();

    [Fact]
    public async Task Uses_StreamAsync_with_partial_messages()
    {
        _runner.Enqueue().Final();
        using var client = CreateClient(o => o.SystemPrompt = "sys");

        await Collect(client.GetStreamingResponseAsync("Hello", cancellationToken: Ct));

        var call = _runner.Calls.Should().ContainSingle().Subject;
        call.Mode.Should().Be(FakeRunMode.Stream);
        call.Prompt.Should().Be("Hello");
        call.Options.IncludePartialMessages.Should().BeTrue();
        call.Options.SystemPrompt.Should().Be("sys");
    }

    [Fact]
    public async Task Deltas_stream_in_order_as_assistant_text_updates()
    {
        _runner.Enqueue().Init("s1").Delta("Hel").Delta("lo, ").Delta("world").Text("Hello, world").Final("Hello, world", "s1");
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        Texts(updates).Should().Equal("Hel", "lo, ", "world");
        updates.Where(u => !string.IsNullOrEmpty(u.Text)).Should().OnlyContain(u =>
            u.Role == ChatRole.Assistant && u.Contents.Count == 1 && u.Contents[0] is TextContent);
    }

    [Fact]
    public async Task Full_text_after_deltas_is_not_duplicated()
    {
        _runner.Enqueue().Delta("A").Delta("B").Text("AB").Final("AB");
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        string.Concat(updates.Select(u => u.Text)).Should().Be("AB");
    }

    [Fact]
    public async Task Full_text_is_emitted_when_no_deltas_were_seen()
    {
        _runner.Enqueue().Text("Whole block").Final("Whole block");
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        Texts(updates).Should().Equal("Whole block");
    }

    [Fact]
    public async Task Mixed_blocks_with_and_without_deltas_are_separated()
    {
        _runner.Enqueue()
            .Delta("One").Text("One")
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.ToolUse, ToolName = "Bash" })
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.ToolResult, Text = "output" })
            .Text("Two")
            .Delta("Thr").Delta("ee").Text("Three")
            .Final();
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        Texts(updates).Should().Equal("One", "\n\nTwo", "\n\nThr", "ee");
        updates.ToChatResponse().Text.Should().Be("One\n\nTwo\n\nThree");
    }

    [Fact]
    public async Task Final_update_carries_metadata()
    {
        _runner.Enqueue()
            .Init("sess-7")
            .Delta("x")
            .Text("x")
            .Event(new ClaudeStreamEvent
            {
                Kind = ClaudeStreamEventKind.Result,
                SessionId = "sess-7",
                Text = "x",
                NumTurns = 2,
                CostUsd = 0.25,
                DurationMs = 900,
                RawJson = """{"type":"result","usage":{"input_tokens":4,"output_tokens":6}}""",
            });
        using var client = CreateClient(o => o.DefaultModel = "sonnet");

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        var final = updates[^1];
        final.Text.Should().BeEmpty();
        final.ConversationId.Should().Be("sess-7");
        final.ModelId.Should().Be("sonnet");
        final.FinishReason.Should().Be(ChatFinishReason.Stop);
        final.Role.Should().Be(ChatRole.Assistant);
        final.AdditionalProperties![ClaudeCodeChatClient.CostUsdKey].Should().Be(0.25);
        final.AdditionalProperties[ClaudeCodeChatClient.NumTurnsKey].Should().Be(2);
        final.AdditionalProperties[ClaudeCodeChatClient.DurationMsKey].Should().Be(900L);
        final.Contents.OfType<UsageContent>().Should().ContainSingle()
            .Which.Details.TotalTokenCount.Should().Be(10);

        updates.Take(updates.Count - 1).Should().OnlyContain(u => u.FinishReason == null);
    }

    [Fact]
    public async Task Final_update_without_stats_has_null_additional_properties_and_no_usage()
    {
        _runner.Enqueue().Text("x").Final(numTurns: 0);
        using var client = CreateClient();

        var final = (await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct)))[^1];

        final.AdditionalProperties.Should().BeNull();
        final.Contents.Should().BeEmpty();
        final.ConversationId.Should().BeNull();
        final.ModelId.Should().BeNull();
    }

    [Fact]
    public async Task Updates_after_init_carry_conversation_id_and_share_ids()
    {
        _runner.Enqueue().Init("s9").Delta("a").Delta("b").Final();
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        updates.Should().OnlyContain(u => u.ConversationId == "s9");
        updates.Select(u => u.ResponseId).Distinct().Should().ContainSingle().Which.Should().NotBeNullOrEmpty();
        updates.Select(u => u.MessageId).Distinct().Should().ContainSingle();
        updates.Should().OnlyContain(u => u.CreatedAt != null);
    }

    [Fact]
    public async Task Rate_limit_waits_are_counted_and_resumed_success_does_not_throw()
    {
        _runner.Enqueue()
            .Init("s")
            .Delta("part 1")
            .Final("rate limited", "s", isError: true, numTurns: 1, costUsd: 0.1)
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.RateLimitWait, Text = "waiting 5m" })
            .Delta("part 2")
            .Final("done", "s", numTurns: 2, costUsd: 0.2);
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        var props = updates[^1].AdditionalProperties!;
        props[ClaudeCodeChatClient.RateLimitWaitsKey].Should().Be(1);
        props[ClaudeCodeChatClient.NumTurnsKey].Should().Be(3);
        ((double)props[ClaudeCodeChatClient.CostUsdKey]!).Should().BeApproximately(0.3, 1e-9);
        Texts(updates).Should().Equal("part 1", "\n\npart 2");
    }

    [Fact]
    public async Task Error_result_mid_stream_throws_after_earlier_updates()
    {
        var resetsAt = DateTimeOffset.UtcNow.AddHours(1);
        _runner.Enqueue()
            .Init("s-err")
            .Delta("partial ")
            .Delta("answer")
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.RateLimit, IsRateLimited = true, ResetsAt = resetsAt })
            .Final("Usage limit reached", "s-err", isError: true);
        using var client = CreateClient();
        var received = new List<ChatResponseUpdate>();

        var act = async () =>
        {
            await foreach (var u in client.GetStreamingResponseAsync("Hi", cancellationToken: Ct))
            {
                received.Add(u);
            }
        };

        var ex = (await act.Should().ThrowAsync<ClaudeCodeChatException>()).Which;
        Texts(received).Should().Equal("partial ", "answer");
        received.Should().OnlyContain(u => u.FinishReason == null, "no final update is yielded on failure");
        ex.Message.Should().Be("Usage limit reached");
        ex.SessionId.Should().Be("s-err");
        ex.RateLimitDetected.Should().BeTrue();
        ex.RateLimitResetsAt.Should().Be(resetsAt);
    }

    [Fact]
    public async Task Error_event_without_result_throws_after_earlier_updates()
    {
        _runner.Enqueue()
            .Delta("before")
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.Error, Text = "connection reset" });
        using var client = CreateClient();
        var received = new List<string>();

        var act = async () =>
        {
            await foreach (var u in client.GetStreamingResponseAsync("Hi", cancellationToken: Ct))
            {
                received.Add(u.Text);
            }
        };

        (await act.Should().ThrowAsync<ClaudeCodeChatException>()).WithMessage("connection reset");
        received.Should().Equal("before");
    }

    [Fact]
    public async Task Error_event_followed_by_successful_result_does_not_throw()
    {
        // StreamAsync surfaces stderr lines as Error events; a later success result wins.
        _runner.Enqueue()
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.Error, Text = "warning: something on stderr" })
            .Text("fine")
            .Final("fine");
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        updates[^1].FinishReason.Should().Be(ChatFinishReason.Stop);
    }

    [Fact]
    public async Task Error_result_without_text_uses_generic_message()
    {
        _runner.Enqueue().Final(isError: true);
        using var client = CreateClient();

        var act = () => Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        (await act.Should().ThrowAsync<ClaudeCodeChatException>()).WithMessage("*failed*");
    }

    [Fact]
    public async Task Stream_ending_without_result_throws_after_yielding_text()
    {
        // StreamAsync can't report the exit code; a missing Result means the CLI died
        _runner.Enqueue().Init("s1").Delta("cut off");
        using var client = CreateClient();
        var updates = new List<ChatResponseUpdate>();

        var act = async () =>
        {
            await foreach (var u in client.GetStreamingResponseAsync("Hi", cancellationToken: Ct))
            {
                updates.Add(u);
            }
        };

        var ex = (await act.Should().ThrowAsync<ClaudeCodeChatException>()).Which;
        ex.ExitCode.Should().Be(ClaudeCodeChatException.UnknownExitCode);
        ex.SessionId.Should().Be("s1");
        Texts(updates).Should().Equal("cut off");
    }

    [Fact]
    public async Task ToChatResponseAsync_of_updates_equals_non_streaming_text()
    {
        FakeRun Script() => new FakeRun()
            .Init("s1")
            .Delta("First ").Delta("block").Text("First block")
            .Delta("Second").Text("Second")
            .Final(sessionId: "s1", numTurns: 2);

        _runner.Enqueue(Script()).Enqueue(Script());
        using var client = CreateClient(o => o.DefaultModel = "opus");

        var streamed = await client.GetStreamingResponseAsync("Hi", cancellationToken: Ct).ToChatResponseAsync(Ct);
        var direct = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        streamed.Text.Should().Be(direct.Text);
        streamed.Text.Should().Be("First block\n\nSecond");
        streamed.Messages.Should().ContainSingle();
        streamed.ConversationId.Should().Be(direct.ConversationId);
        streamed.ModelId.Should().Be(direct.ModelId);
        streamed.FinishReason.Should().Be(ChatFinishReason.Stop);
        // MEAI folds update AdditionalProperties into the reconstructed message.
        streamed.Messages[0].AdditionalProperties![ClaudeCodeChatClient.NumTurnsKey].Should().Be(2);
    }

    [Fact]
    public async Task ToChatResponseAsync_picks_up_usage()
    {
        _runner.Enqueue().Text("x").Event(new ClaudeStreamEvent
        {
            Kind = ClaudeStreamEventKind.Result,
            RawJson = """{"type":"result","usage":{"input_tokens":1,"output_tokens":2}}""",
        });
        using var client = CreateClient();

        var response = await client.GetStreamingResponseAsync("Hi", cancellationToken: Ct).ToChatResponseAsync(Ct);

        response.Usage!.OutputTokenCount.Should().Be(2);
        response.Usage.InputTokenCount.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_token_reaches_runner_and_stops_stream()
    {
        _runner.Enqueue().Delta("a").Delta("b").Delta("c").WithDelay(TimeSpan.FromMilliseconds(5));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var client = CreateClient();
        var received = new List<string>();

        var act = async () =>
        {
            await foreach (var u in client.GetStreamingResponseAsync("Hi", cancellationToken: cts.Token))
            {
                received.Add(u.Text);
                await cts.CancelAsync();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        received.Should().Equal("a");
        _runner.LastCall.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task WithCancellation_token_is_honored()
    {
        _runner.Enqueue().Delta("a").Delta("b");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var client = CreateClient();

        var act = async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync("Hi", cancellationToken: Ct).WithCancellation(cts.Token))
            {
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Runner_exceptions_propagate_unchanged()
    {
        var original = new ClaudeCliNotFoundException();
        _runner.Enqueue().Throws(original);
        using var client = CreateClient();

        var act = () => Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        (await act.Should().ThrowAsync<ClaudeCliNotFoundException>()).Which.Should().BeSameAs(original);
    }

    [Fact]
    public void Invalid_messages_throw_before_enumeration()
    {
        using var client = CreateClient();

        var act = () => client.GetStreamingResponseAsync(Array.Empty<ChatMessage>(), cancellationToken: Ct);

        act.Should().Throw<ArgumentException>();
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Each_enumeration_runs_a_new_session()
    {
        _runner.Enqueue().Final();
        _runner.Enqueue().Final();
        using var client = CreateClient();
        var stream = client.GetStreamingResponseAsync("Hi", cancellationToken: Ct);

        await Collect(stream);
        await Collect(stream);

        _runner.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Empty_deltas_are_skipped()
    {
        _runner.Enqueue().Delta("").Delta("x").Text("x").Final();
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        updates.Should().HaveCount(2);
        updates[0].Text.Should().Be("x");
    }

    [Fact]
    public async Task Rate_limit_resume_after_a_failed_attempt_succeeds()
    {
        // Attempt 1 dies mid-block on a rate limit; the policy waits and resumes the session
        _runner.Enqueue()
            .Init("s1").Delta("par")
            .Final("Usage limit reached", "s1", isError: true)
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.RateLimitWait, Text = "Rate limited; waiting" })
            .Delta("rest").Text("rest")
            .Final("rest", "s1");
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        Texts(updates).Should().Equal("par", "\n\nrest");
        updates[^1].FinishReason.Should().Be(ChatFinishReason.Stop);
        updates[^1].AdditionalProperties![ClaudeCodeChatClient.RateLimitWaitsKey].Should().Be(1);
    }

    [Fact]
    public async Task Failed_attempt_without_result_before_resume_does_not_fail_the_stream()
    {
        _runner.Enqueue()
            .Init("s1").Delta("par")
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.RateLimitWait, Text = "waiting" })
            .Text("whole block").Final("whole block", "s1");
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        // The resumed block arrived without deltas and must not be dropped
        Texts(updates).Should().Equal("par", "\n\nwhole block");
    }

    [Fact]
    public async Task Full_text_longer_than_its_deltas_emits_the_missing_tail()
    {
        _runner.Enqueue().Delta("Hello").Text("Hello, world").Final();
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        Texts(updates).Should().Equal("Hello", ", world");
    }

    [Fact]
    public async Task Instructions_are_appended_to_the_system_prompt()
    {
        _runner.Enqueue().Final();
        using var client = CreateClient(o => o.SystemPrompt = "base");

        await Collect(client.GetStreamingResponseAsync("Hi", new ChatOptions { Instructions = "be brief" }, Ct));

        _runner.Calls.Single().Options.SystemPrompt.Should().Be("base\n\nbe brief");
    }
}

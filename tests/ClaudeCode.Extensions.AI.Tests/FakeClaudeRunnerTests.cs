using ClaudeCode.Extensions.AI.Tests.Fakes;

namespace ClaudeCode.Extensions.AI.Tests;

public class FakeClaudeRunnerTests
{
    private static readonly ClaudeSessionOptions Options = new() { WorkingDirectory = "/work", Model = "sonnet" };

    [Fact]
    public async Task RunAsync_records_prompt_and_options()
    {
        var fake = new FakeClaudeRunner();
        using var cts = new CancellationTokenSource();

        await fake.RunAsync("hello", Options, null, cts.Token);

        fake.Calls.Should().ContainSingle();
        var call = fake.LastCall;
        call.Mode.Should().Be(FakeRunMode.Run);
        call.Prompt.Should().Be("hello");
        call.Options.Should().BeSameAs(Options);
        call.HadEventCallback.Should().BeFalse();
        call.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task RunAsync_replays_events_to_callback_in_order_and_returns_explicit_result()
    {
        var fake = new FakeClaudeRunner();
        var result = new ClaudeSessionResult { ExitCode = 0, SessionId = "s-1", ResultText = "done", NumTurns = 3 };
        fake.Enqueue().Init("s-1").Text("a").Text("b").Final("done", "s-1").Returns(result);
        var seen = new List<ClaudeStreamEvent>();

        var actual = await fake.RunAsync("p", Options, seen.Add, CancellationToken.None);

        actual.Should().BeSameAs(result);
        seen.Select(e => e.Kind).Should().Equal(
            ClaudeStreamEventKind.SystemInit,
            ClaudeStreamEventKind.AssistantText,
            ClaudeStreamEventKind.AssistantText,
            ClaudeStreamEventKind.Result);
        seen.Where(e => e.Kind == ClaudeStreamEventKind.AssistantText).Select(e => e.Text).Should().Equal("a", "b");
        fake.LastCall.HadEventCallback.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_derives_result_from_Result_event_when_not_scripted()
    {
        var fake = new FakeClaudeRunner();
        fake.Enqueue().Init("s-9").Text("x").Final("final", "s-9", isError: true, numTurns: 4, costUsd: 0.5, durationMs: 1500);

        var result = await fake.RunAsync("p", Options, null, CancellationToken.None);

        result.SessionId.Should().Be("s-9");
        result.ResultText.Should().Be("final");
        result.IsError.Should().BeTrue();
        result.ExitCode.Should().Be(1);
        result.NumTurns.Should().Be(4);
        result.CostUsd.Should().Be(0.5);
        result.Duration.Should().Be(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task Derived_result_falls_back_to_init_session_id_and_detects_rate_limit()
    {
        var fake = new FakeClaudeRunner();
        fake.Enqueue().Init("s-init").Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.RateLimit, IsRateLimited = true });

        var result = await fake.RunAsync("p", Options, null, CancellationToken.None);

        result.SessionId.Should().Be("s-init");
        result.RateLimitDetected.Should().BeTrue();
        result.ResultText.Should().BeNull();
    }

    [Fact]
    public async Task Empty_run_returns_empty_successful_result()
    {
        var fake = new FakeClaudeRunner();

        var result = await fake.RunAsync("p", Options, null, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.ExitCode.Should().Be(0);
        result.SessionId.Should().BeNull();
    }

    [Fact]
    public async Task StreamAsync_yields_scripted_events_and_records_call()
    {
        var fake = new FakeClaudeRunner();
        fake.Enqueue().Delta("He").Delta("llo").Text("Hello").Final("Hello");

        var events = await Collect(fake.StreamAsync("stream me", Options, CancellationToken.None));

        events.Select(e => e.Text).Should().Equal("He", "llo", "Hello", "Hello");
        fake.LastCall.Mode.Should().Be(FakeRunMode.Stream);
        fake.LastCall.Prompt.Should().Be("stream me");
        fake.LastCall.Options.Should().BeSameAs(Options);
    }

    [Fact]
    public async Task StreamAsync_records_call_only_when_enumerated()
    {
        var fake = new FakeClaudeRunner();

        var stream = fake.StreamAsync("p", Options, CancellationToken.None);
        fake.Calls.Should().BeEmpty();

        await Collect(stream);
        fake.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Scripted_runs_are_consumed_fifo_then_default_run_is_used()
    {
        var fake = new FakeClaudeRunner { DefaultRun = new FakeRun().Text("default") };
        fake.Enqueue().Text("first");
        fake.Enqueue(new FakeRun().Text("second"));

        var texts = new List<string?>();
        for (var i = 0; i < 4; i++)
        {
            await foreach (var e in fake.StreamAsync($"p{i}", Options, CancellationToken.None))
            {
                texts.Add(e.Text);
            }
        }

        texts.Should().Equal("first", "second", "default", "default");
        fake.Calls.Select(c => c.Prompt).Should().Equal("p0", "p1", "p2", "p3");
    }

    [Fact]
    public async Task RunAsync_throws_scripted_exception_after_events()
    {
        var fake = new FakeClaudeRunner();
        fake.Enqueue().Text("partial").Throws(new ClaudeCliNotFoundException());
        var seen = new List<ClaudeStreamEvent>();

        var act = () => fake.RunAsync("p", Options, seen.Add, CancellationToken.None);

        await act.Should().ThrowAsync<ClaudeCliNotFoundException>();
        seen.Should().ContainSingle();
    }

    [Fact]
    public async Task StreamAsync_throws_scripted_exception_after_events()
    {
        var fake = new FakeClaudeRunner();
        fake.Enqueue().Text("partial").Throws(new InvalidOperationException("boom"));
        var seen = new List<ClaudeStreamEvent>();

        var act = async () =>
        {
            await foreach (var e in fake.StreamAsync("p", Options, CancellationToken.None))
            {
                seen.Add(e);
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        seen.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_honors_pre_cancelled_token()
    {
        var fake = new FakeClaudeRunner();
        fake.Enqueue().Text("never");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var seen = new List<ClaudeStreamEvent>();

        var act = () => fake.RunAsync("p", Options, seen.Add, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().BeEmpty();
        fake.Calls.Should().ContainSingle("the call is still recorded");
    }

    [Fact]
    public async Task StreamAsync_cancellation_mid_stream_stops_enumeration()
    {
        var fake = new FakeClaudeRunner();
        fake.Enqueue().Text("one").Text("two").Text("three").WithDelay(TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();
        var seen = new List<string?>();

        var act = async () =>
        {
            await foreach (var e in fake.StreamAsync("p", Options, cts.Token))
            {
                seen.Add(e.Text);
                await cts.CancelAsync();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().Equal("one");
    }

    [Fact]
    public async Task WithCancellation_on_enumerator_flows_token()
    {
        var fake = new FakeClaudeRunner();
        fake.Enqueue().Text("one").Text("two");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () =>
        {
            await foreach (var _ in fake.StreamAsync("p", Options, CancellationToken.None).WithCancellation(cts.Token))
            {
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Concurrent_calls_are_all_recorded()
    {
        var fake = new FakeClaudeRunner();

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => fake.RunAsync($"p{i}", Options, null, CancellationToken.None)));

        fake.Calls.Should().HaveCount(50);
        fake.Calls.Select(c => c.Prompt).Should().OnlyHaveUniqueItems();
    }

    private static async Task<List<ClaudeStreamEvent>> Collect(IAsyncEnumerable<ClaudeStreamEvent> stream)
    {
        var list = new List<ClaudeStreamEvent>();
        await foreach (var e in stream)
        {
            list.Add(e);
        }

        return list;
    }
}

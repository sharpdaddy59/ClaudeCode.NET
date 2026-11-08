using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ClaudeCode.Extensions.AI.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ClaudeCode.Extensions.AI.Tests;

public class MiddlewareCompatibilityTests
{
    private readonly FakeClaudeRunner _runner = new();
    private readonly CapturingLoggerFactory _loggers = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClaudeCodeChatClient CreateInner()
        => new(_runner, new ClaudeCodeChatClientOptions { WorkingDirectory = "/repo", DefaultModel = "sonnet" });

    private IChatClient BuildPipeline(ClaudeCodeChatClient inner, CountingChatClient? counter = null)
        => new ChatClientBuilder(inner)
            .UseLogging(_loggers)
            .Use(next => counter is null ? next : counter.Wrap(next))
            .Build();

    [Fact]
    public async Task GetResponseAsync_flows_through_logging_and_custom_middleware()
    {
        _runner.Enqueue().Init("s1").Text("Hello back").Final("Hello back", "s1");
        var counter = new CountingChatClient();
        using var pipeline = BuildPipeline(CreateInner(), counter);

        var response = await pipeline.GetResponseAsync("Hello", cancellationToken: Ct);

        response.Text.Should().Be("Hello back");
        response.ConversationId.Should().Be("s1");
        response.ModelId.Should().Be("sonnet");
        counter.ResponseCalls.Should().Be(1);
        _loggers.Messages.Should().Contain(m => m.Contains("GetResponseAsync"));
        _runner.LastCall.Prompt.Should().Be("Hello");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_flows_through_logging_and_custom_middleware()
    {
        _runner.Enqueue().Init("s2").Delta("Str").Delta("eam").Text("Stream").Final("Stream", "s2");
        var counter = new CountingChatClient();
        using var pipeline = BuildPipeline(CreateInner(), counter);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in pipeline.GetStreamingResponseAsync("Hello", cancellationToken: Ct))
        {
            updates.Add(u);
        }

        updates.Where(u => u.Text.Length > 0).Select(u => u.Text).Should().Equal("Str", "eam");
        updates.ToChatResponse().Text.Should().Be("Stream");
        updates[^1].ConversationId.Should().Be("s2");
        counter.StreamingCalls.Should().Be(1);
        counter.StreamedUpdates.Should().Be(updates.Count);
        _loggers.Messages.Should().Contain(m => m.Contains("GetStreamingResponseAsync"));
        _runner.LastCall.Mode.Should().Be(FakeRunMode.Stream);
    }

    [Fact]
    public async Task Errors_propagate_through_the_pipeline()
    {
        _runner.Enqueue().Final("boom", isError: true);
        using var pipeline = BuildPipeline(CreateInner());

        var act = () => pipeline.GetResponseAsync("Hello", cancellationToken: Ct);

        await act.Should().ThrowAsync<ClaudeCodeChatException>().WithMessage("boom");
        _loggers.Messages.Should().Contain(m => m.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Pipeline_exposes_inner_services()
    {
        using var inner = CreateInner();
        using var pipeline = BuildPipeline(inner);

        pipeline.GetService<ClaudeCodeChatClient>().Should().BeSameAs(inner);
        pipeline.GetService<ChatClientMetadata>()!.ProviderName.Should().Be("claude-code");
        pipeline.GetService<IClaudeRunner>().Should().BeSameAs(_runner);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Multi_turn_conversation_resumes_and_sends_only_the_new_user_message()
    {
        _runner.Enqueue().Init("sess-1").Text("Paris").Final("Paris", "sess-1");
        _runner.Enqueue().Init("sess-1").Text("About 2.1 million").Final("About 2.1 million", "sess-1");
        using var pipeline = BuildPipeline(CreateInner());

        List<ChatMessage> history =
        [
            new(ChatRole.System, "Answer briefly."),
            new(ChatRole.User, "What is the capital of France?"),
        ];
        var first = await pipeline.GetResponseAsync(history, cancellationToken: Ct);
        history.AddMessages(first);
        history.Add(new ChatMessage(ChatRole.User, "And its population?"));

        var second = await pipeline.GetResponseAsync(
            history, new ChatOptions { ConversationId = first.ConversationId }, Ct);

        second.Text.Should().Be("About 2.1 million");
        _runner.Calls.Should().HaveCount(2);

        var firstCall = _runner.Calls[0];
        firstCall.Options.ResumeSessionId.Should().BeNull();
        firstCall.Prompt.Should().Be("What is the capital of France?");

        var resumeCall = _runner.Calls[1];
        resumeCall.Options.ResumeSessionId.Should().Be("sess-1");
        resumeCall.Prompt.Should().Be("And its population?", "the CLI session already holds earlier turns");
        resumeCall.Prompt.Should().NotContain("capital").And.NotContain(PromptBuilder.TranscriptPreamble);
        resumeCall.Options.SystemPrompt.Should().Be("Answer briefly.", "system messages still apply on resume");
    }

    [Fact]
    public async Task Streaming_multi_turn_conversation_resumes_with_only_the_new_message()
    {
        _runner.Enqueue().Init("sess-9").Delta("One").Text("One").Final(sessionId: "sess-9");
        _runner.Enqueue().Init("sess-9").Delta("Two").Text("Two").Final(sessionId: "sess-9");
        using var pipeline = BuildPipeline(CreateInner());

        List<ChatMessage> history = [new(ChatRole.User, "first")];
        var first = await pipeline.GetStreamingResponseAsync(history, cancellationToken: Ct).ToChatResponseAsync(Ct);
        history.AddMessages(first);
        history.Add(new ChatMessage(ChatRole.User, "second"));

        await pipeline.GetStreamingResponseAsync(history, new ChatOptions { ConversationId = first.ConversationId }, Ct)
            .ToChatResponseAsync(Ct);

        first.ConversationId.Should().Be("sess-9");
        _runner.Calls[1].Options.ResumeSessionId.Should().Be("sess-9");
        _runner.Calls[1].Prompt.Should().Be("second");
    }

    [Fact]
    public async Task Without_conversation_id_the_full_transcript_is_replayed()
    {
        using var pipeline = BuildPipeline(CreateInner());

        await pipeline.GetResponseAsync(
            [new(ChatRole.User, "q1"), new(ChatRole.Assistant, "a1"), new(ChatRole.User, "q2")],
            cancellationToken: Ct);

        _runner.LastCall.Prompt.Should().StartWith(PromptBuilder.TranscriptPreamble).And.Contain("User: q1");
        _runner.LastCall.Options.ResumeSessionId.Should().BeNull();
    }

    [Fact]
    public async Task Resuming_with_several_new_user_messages_sends_all_of_them()
    {
        using var client = CreateInner();

        await client.GetResponseAsync(
            [new(ChatRole.User, "q1"), new(ChatRole.Assistant, "a1"), new(ChatRole.User, "q2"), new(ChatRole.User, "q3")],
            new ChatOptions { ConversationId = "s" },
            Ct);

        _runner.LastCall.Prompt.Should().Be("q2\n\nq3");
    }

    [Fact]
    public async Task Resuming_with_only_the_new_message_works()
    {
        using var client = CreateInner();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "just this")], new ChatOptions { ConversationId = "s" }, Ct);

        _runner.LastCall.Prompt.Should().Be("just this");
        _runner.LastCall.Options.ResumeSessionId.Should().Be("s");
    }

    [Fact]
    public async Task ConfigureSession_setting_ResumeSessionId_also_suppresses_transcript()
    {
        var options = new ClaudeCodeChatClientOptions
        {
            WorkingDirectory = "/repo",
            ConfigureSession = s => s with { ResumeSessionId = "forced" },
        };
        using var client = new ClaudeCodeChatClient(_runner, options);

        await client.GetResponseAsync(
            [new(ChatRole.User, "q1"), new(ChatRole.Assistant, "a1"), new(ChatRole.User, "q2")], cancellationToken: Ct);

        _runner.LastCall.Options.ResumeSessionId.Should().Be("forced");
        _runner.LastCall.Prompt.Should().Be("q2");
    }

    [Fact]
    public async Task ConfigureSession_clearing_ResumeSessionId_restores_transcript()
    {
        var options = new ClaudeCodeChatClientOptions
        {
            WorkingDirectory = "/repo",
            ConfigureSession = s => s with { ResumeSessionId = null! },
        };
        using var client = new ClaudeCodeChatClient(_runner, options);

        await client.GetResponseAsync(
            [new(ChatRole.User, "q1"), new(ChatRole.Assistant, "a1"), new(ChatRole.User, "q2")],
            new ChatOptions { ConversationId = "ignored" },
            Ct);

        _runner.LastCall.Options.ResumeSessionId.Should().BeNull();
        _runner.LastCall.Prompt.Should().StartWith(PromptBuilder.TranscriptPreamble);
    }

    [Fact]
    public async Task Lazy_message_sequence_is_enumerated_once()
    {
        var enumerations = 0;
        IEnumerable<ChatMessage> Messages()
        {
            enumerations++;
            yield return new ChatMessage(ChatRole.User, "q1");
            yield return new ChatMessage(ChatRole.Assistant, "a1");
            yield return new ChatMessage(ChatRole.User, "q2");
        }

        var options = new ClaudeCodeChatClientOptions
        {
            WorkingDirectory = "/repo",
            ConfigureSession = s => s with { ResumeSessionId = "forced" }, // forces the rebuild path
        };
        using var client = new ClaudeCodeChatClient(_runner, options);

        await client.GetResponseAsync(Messages(), cancellationToken: Ct);

        enumerations.Should().Be(1);
        _runner.LastCall.Prompt.Should().Be("q2");
    }

    /// <summary>Custom middleware that counts calls and streamed updates.</summary>
    private sealed class CountingChatClient
    {
        public int ResponseCalls;
        public int StreamingCalls;
        public int StreamedUpdates;

        public IChatClient Wrap(IChatClient next) => new Impl(next, this);

        private sealed class Impl(IChatClient inner, CountingChatClient counts) : DelegatingChatClient(inner)
        {
            public override Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref counts.ResponseCalls);
                return base.GetResponseAsync(messages, options, cancellationToken);
            }

            public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref counts.StreamingCalls);
                await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
                {
                    Interlocked.Increment(ref counts.StreamedUpdates);
                    yield return update;
                }
            }
        }
    }

    /// <summary>Minimal ILoggerFactory capturing formatted messages at every level.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => sink.Enqueue(formatter(state, exception));
        }
    }
}

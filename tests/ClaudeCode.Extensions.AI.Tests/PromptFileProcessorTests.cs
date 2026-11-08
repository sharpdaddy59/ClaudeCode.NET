using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ClaudeCode.Extensions.AI.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PromptWorker;

namespace ClaudeCode.Extensions.AI.Tests;

public sealed class PromptFileProcessorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "promptworker-tests", Guid.NewGuid().ToString("N"));
    private readonly PromptWorkerOptions _options;
    private readonly FakeClaudeRunner _runner = new();
    private readonly CapturingLogger<PromptFileProcessor> _logger = new();

    public PromptFileProcessorTests()
    {
        _options = new PromptWorkerOptions
        {
            InboxPath = Path.Combine(_root, "inbox"),
            OutboxPath = Path.Combine(_root, "outbox"),
            DonePath = Path.Combine(_root, "done"),
            FailedPath = Path.Combine(_root, "failed"),
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private PromptFileProcessor CreateProcessor(IChatClient? chat = null)
    {
        chat ??= new ClaudeCodeChatClient(_runner, new ClaudeCodeChatClientOptions
        {
            WorkingDirectory = _root,
            RateLimitWait = RateLimitWaitPolicy.Default,
        });
        var processor = new PromptFileProcessor(chat, Options.Create(_options), _logger);
        processor.EnsureFolders();
        return processor;
    }

    private string WritePrompt(string name, string text)
    {
        Directory.CreateDirectory(_options.InboxPath);
        var path = Path.Combine(_options.InboxPath, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Success_writes_reply_to_outbox_and_moves_prompt_to_done()
    {
        _runner.Enqueue().Init("s").Delta("The ").Delta("answer").Text("The answer").Final("The answer", "s");
        var processor = CreateProcessor();
        var prompt = WritePrompt("001-question.md", "What is the answer?");

        var outcome = await processor.ProcessAsync(prompt, Ct);

        outcome.Should().Be(PromptOutcome.Succeeded);
        File.ReadAllText(Path.Combine(_options.OutboxPath, "001-question.md")).Should().Be("The answer");
        File.Exists(prompt).Should().BeFalse();
        File.ReadAllText(Path.Combine(_options.DonePath, "001-question.md")).Should().Be("What is the answer?");
        Directory.GetFiles(_options.FailedPath).Should().BeEmpty();
        _runner.LastCall.Prompt.Should().Be("What is the answer?");
        _runner.LastCall.Options.RateLimitWait.Should().BeSameAs(RateLimitWaitPolicy.Default);
        _runner.LastCall.Options.ResumeSessionId.Should().BeNull("each prompt is a fresh conversation");
    }

    [Fact]
    public async Task Chat_exception_moves_prompt_to_failed_with_error_written()
    {
        var resetsAt = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        _runner.Enqueue()
            .Init("sess-x")
            .Delta("partial")
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.RateLimit, IsRateLimited = true, ResetsAt = resetsAt })
            .Final("Weekly limit reached", "sess-x", isError: true);
        var processor = CreateProcessor();
        var prompt = WritePrompt("002-big-job.md", "Refactor everything");

        var outcome = await processor.ProcessAsync(prompt, Ct);

        outcome.Should().Be(PromptOutcome.Failed);
        File.Exists(prompt).Should().BeFalse();
        File.Exists(Path.Combine(_options.FailedPath, "002-big-job.md")).Should().BeTrue();
        var error = File.ReadAllText(Path.Combine(_options.FailedPath, "002-big-job.error.txt"));
        error.Should().Contain("ClaudeCodeChatException")
            .And.Contain("Weekly limit reached")
            .And.Contain("sess-x")
            .And.Contain("RateLimitDetected: True")
            .And.Contain(resetsAt.ToString("O"));
        Directory.GetFiles(_options.OutboxPath).Should().BeEmpty("no reply is written on failure");
        Directory.GetFiles(_options.DonePath).Should().BeEmpty();
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("002-big-job.md"));
    }

    [Fact]
    public async Task Fake_client_throwing_ClaudeCodeChatException_moves_prompt_to_failed()
    {
        var processor = CreateProcessor(new ScriptedChatClient(
            _ => throw new ClaudeCodeChatException("exit 2", 2, null, false, null)));
        var prompt = WritePrompt("p.md", "hello");

        var outcome = await processor.ProcessAsync(prompt, Ct);

        outcome.Should().Be(PromptOutcome.Failed);
        File.ReadAllText(Path.Combine(_options.FailedPath, "p.error.txt")).Should().Contain("exit 2").And.Contain("ExitCode: 2");
    }

    [Fact]
    public async Task Cancellation_leaves_the_file_in_the_inbox()
    {
        _runner.Enqueue().Delta("slow").Delta("reply").WithDelay(TimeSpan.FromSeconds(30));
        var processor = CreateProcessor();
        var prompt = WritePrompt("003-slow.md", "Take your time");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = () => processor.ProcessAsync(prompt, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(prompt).Should().BeTrue();
        File.ReadAllText(prompt).Should().Be("Take your time");
        Directory.GetFiles(_options.OutboxPath).Should().BeEmpty();
        Directory.GetFiles(_options.DonePath).Should().BeEmpty();
        Directory.GetFiles(_options.FailedPath).Should().BeEmpty();
    }

    [Fact]
    public async Task Already_cancelled_token_leaves_file_and_does_not_call_the_client()
    {
        var calls = 0;
        var processor = CreateProcessor(new ScriptedChatClient(_ => { calls++; return [new ChatResponseUpdate(ChatRole.Assistant, "x")]; }));
        var prompt = WritePrompt("p.md", "hello");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => processor.ProcessAsync(prompt, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(prompt).Should().BeTrue();
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Rate_limit_wait_is_logged_and_the_prompt_still_succeeds()
    {
        var resetsAt = DateTimeOffset.UtcNow.AddHours(2);
        _runner.Enqueue()
            .Init("s")
            .Delta("first half")
            .Final("limit", "s", isError: true)
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.RateLimitWait, Text = "Waiting 2h for reset", ResetsAt = resetsAt })
            .Delta(" second half")
            .Final("done", "s");
        var processor = CreateProcessor();
        var prompt = WritePrompt("004-limited.md", "Long task");

        var outcome = await processor.ProcessAsync(prompt, Ct);

        outcome.Should().Be(PromptOutcome.Succeeded);
        File.Exists(Path.Combine(_options.DonePath, "004-limited.md")).Should().BeTrue();
        _logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Waiting 2h for reset") && e.Message.Contains("004-limited.md"));
        _logger.Entries.Should().Contain(e => e.Message.Contains("1 rate-limit wait"));
    }

    [Fact]
    public async Task Empty_prompt_fails_without_calling_the_runner()
    {
        var processor = CreateProcessor();
        var prompt = WritePrompt("empty.md", "   \n");

        var outcome = await processor.ProcessAsync(prompt, Ct);

        outcome.Should().Be(PromptOutcome.Failed);
        File.Exists(Path.Combine(_options.FailedPath, "empty.md")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_options.FailedPath, "empty.error.txt")).Should().Contain("ArgumentException");
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cli_not_found_propagates_and_leaves_the_file()
    {
        _runner.Enqueue().Throws(new ClaudeCliNotFoundException());
        var processor = CreateProcessor();
        var prompt = WritePrompt("p.md", "hello");

        var act = () => processor.ProcessAsync(prompt, Ct);

        await act.Should().ThrowAsync<ClaudeCliNotFoundException>();
        File.Exists(prompt).Should().BeTrue();
    }

    [Fact]
    public async Task Reprocessing_same_name_overwrites_previous_outputs()
    {
        _runner.Enqueue().Text("v1").Final("v1");
        _runner.Enqueue().Text("v2").Final("v2");
        var processor = CreateProcessor();

        await processor.ProcessAsync(WritePrompt("same.md", "one"), Ct);
        await processor.ProcessAsync(WritePrompt("same.md", "two"), Ct);

        File.ReadAllText(Path.Combine(_options.OutboxPath, "same.md")).Should().Be("v2");
        File.ReadAllText(Path.Combine(_options.DonePath, "same.md")).Should().Be("two");
    }

    [Fact]
    public void PendingFiles_lists_only_markdown_in_name_order()
    {
        var processor = CreateProcessor();
        WritePrompt("b.md", "b");
        WritePrompt("a.md", "a");
        WritePrompt("notes.txt", "ignored");

        processor.PendingFiles().Select(Path.GetFileName).Should().Equal("a.md", "b.md");
    }

    [Fact]
    public void PendingFiles_is_empty_when_inbox_is_missing()
    {
        var processor = new PromptFileProcessor(
            new ScriptedChatClient(_ => []), Options.Create(new PromptWorkerOptions { InboxPath = Path.Combine(_root, "nope") }), _logger);

        processor.PendingFiles().Should().BeEmpty();
    }

    [Fact]
    public void Default_options_match_appsettings()
    {
        var defaults = new PromptWorkerOptions();

        defaults.InboxPath.Should().Be("queue/inbox");
        defaults.OutboxPath.Should().Be("queue/outbox");
        defaults.DonePath.Should().Be("queue/done");
        defaults.FailedPath.Should().Be("queue/failed");
        defaults.PollInterval.Should().Be(TimeSpan.FromSeconds(10));
    }

    /// <summary>Minimal fake IChatClient whose streaming output (or failure) is scripted per call.</summary>
    private sealed class ScriptedChatClient(Func<IEnumerable<ChatMessage>, IEnumerable<ChatResponseUpdate>> script) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            foreach (var update in script(messages))
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyCollection<(LogLevel Level, string Message)> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, formatter(state, exception)));
    }
}

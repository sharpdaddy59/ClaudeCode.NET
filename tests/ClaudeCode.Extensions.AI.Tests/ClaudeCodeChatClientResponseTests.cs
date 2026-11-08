using ClaudeCode.Extensions.AI.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace ClaudeCode.Extensions.AI.Tests;

public class ClaudeCodeChatClientResponseTests
{
    private readonly FakeClaudeRunner _runner = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClaudeCodeChatClient CreateClient(Action<ClaudeCodeChatClientOptions>? configure = null)
    {
        var options = new ClaudeCodeChatClientOptions { WorkingDirectory = "/repo" };
        configure?.Invoke(options);
        return new ClaudeCodeChatClient(_runner, options);
    }

    private static ClaudeSessionResult Result(string? text, string? sessionId = "sess-1")
        => new() { ExitCode = 0, ResultText = text!, SessionId = sessionId! };

    // ---- construction ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_working_directory_throws_on_construction(string dir)
    {
        var act = () => new ClaudeCodeChatClient(_runner, new ClaudeCodeChatClientOptions { WorkingDirectory = dir });

        act.Should().Throw<ArgumentException>().WithParameterName("WorkingDirectory");
    }

    [Fact]
    public void Empty_working_directory_throws_on_real_session_constructor_too()
    {
        var act = () => new ClaudeCodeChatClient(new ClaudeCodeChatClientOptions { WorkingDirectory = "" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var options = new ClaudeCodeChatClientOptions { WorkingDirectory = "/repo" };

        ((Action)(() => new ClaudeCodeChatClient(null!, options))).Should().Throw<ArgumentNullException>().WithParameterName("runner");
        ((Action)(() => new ClaudeCodeChatClient(_runner, null!))).Should().Throw<ArgumentNullException>().WithParameterName("options");
        ((Action)(() => new ClaudeCodeChatClient((ClaudeCodeChatClientOptions)null!))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Options_constructor_uses_a_ClaudeSessionRunner()
    {
        using var client = new ClaudeCodeChatClient(new ClaudeCodeChatClientOptions { WorkingDirectory = "/repo" });

        client.Runner.Should().BeOfType<ClaudeSessionRunner>();
    }

    // ---- response ----

    [Fact]
    public async Task Returns_result_text_conversation_id_and_model()
    {
        _runner.Enqueue().Init("sess-42").Text("draft").Returns(Result("Final answer", "sess-42"));
        using var client = CreateClient(o => o.DefaultModel = "sonnet");

        var response = await client.GetResponseAsync("Hello", cancellationToken: Ct);

        response.Text.Should().Be("Final answer");
        response.Messages.Should().ContainSingle().Which.Role.Should().Be(ChatRole.Assistant);
        response.ConversationId.Should().Be("sess-42");
        response.ModelId.Should().Be("sonnet");
        response.FinishReason.Should().Be(ChatFinishReason.Stop);
    }

    [Fact]
    public async Task ModelId_reflects_per_call_override()
    {
        _runner.Enqueue().Returns(Result("ok"));
        using var client = CreateClient(o => o.DefaultModel = "sonnet");

        var response = await client.GetResponseAsync("Hi", new ChatOptions { ModelId = "opus" }, Ct);

        response.ModelId.Should().Be("opus");
        _runner.LastCall.Options.Model.Should().Be("opus");
    }

    [Fact]
    public async Task ModelId_reflects_ConfigureSession_override()
    {
        _runner.Enqueue().Returns(Result("ok"));
        using var client = CreateClient(o => o.ConfigureSession = s => s with { Model = "haiku" });

        (await client.GetResponseAsync("Hi", cancellationToken: Ct)).ModelId.Should().Be("haiku");
    }

    [Fact]
    public async Task ModelId_is_null_when_cli_default_model_is_used()
    {
        _runner.Enqueue().Returns(Result("ok"));
        using var client = CreateClient();

        (await client.GetResponseAsync("Hi", cancellationToken: Ct)).ModelId.Should().BeNull();
    }

    [Fact]
    public async Task Falls_back_to_concatenated_assistant_text_when_result_text_missing()
    {
        _runner.Enqueue().Text("Part one.").Text("Part two.").Returns(Result(null));
        using var client = CreateClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        response.Text.Should().Be("Part one.\n\nPart two.");
    }

    [Fact]
    public async Task Falls_back_when_result_text_is_empty_and_ignores_non_text_events()
    {
        _runner.Enqueue()
            .Delta("ignored delta")
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.ToolUse, ToolName = "Bash", Text = "ls" })
            .Text("Only this")
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.AssistantText, Text = "" })
            .Returns(Result(""));
        using var client = CreateClient();

        (await client.GetResponseAsync("Hi", cancellationToken: Ct)).Text.Should().Be("Only this");
    }

    [Fact]
    public async Task Empty_run_yields_empty_assistant_message_and_null_conversation()
    {
        using var client = CreateClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        response.Text.Should().BeEmpty();
        response.Messages.Should().ContainSingle();
        response.ConversationId.Should().BeNull();
    }

    [Fact]
    public async Task CreatedAt_is_set_to_now()
    {
        _runner.Enqueue().Returns(Result("ok"));
        using var client = CreateClient();
        var before = DateTimeOffset.UtcNow;

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        response.CreatedAt.Should().NotBeNull();
        response.CreatedAt!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Raw_representation_is_the_session_result()
    {
        var result = Result("ok");
        _runner.Enqueue().Returns(result);
        using var client = CreateClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        response.RawRepresentation.Should().BeSameAs(result);
    }

    // ---- what the runner receives ----

    [Fact]
    public async Task Runner_receives_built_prompt_and_mapped_options()
    {
        var policy = new RateLimitWaitPolicy { MaxWaits = 1 };
        using var client = CreateClient(o =>
        {
            o.SystemPrompt = "From options";
            o.MaxTurns = 4;
            o.RateLimitWait = policy;
        });
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "From message"),
            new(ChatRole.User, "Q1"),
            new(ChatRole.Assistant, "A1"),
            new(ChatRole.User, "Q2"),
        ];

        await client.GetResponseAsync(messages, new ChatOptions { ConversationId = "resume-me" }, Ct);

        var call = _runner.Calls.Should().ContainSingle().Subject;
        call.Mode.Should().Be(FakeRunMode.Run);
        call.HadEventCallback.Should().BeTrue();
        call.Prompt.Should().Be("Q2", "resuming a session sends only the new user message");
        call.Prompt.Should().Be(PromptBuilder.Build(messages, "From options", resuming: true).Prompt);
        call.Options.WorkingDirectory.Should().Be("/repo");
        call.Options.SystemPrompt.Should().Be("From options\n\nFrom message");
        call.Options.ResumeSessionId.Should().Be("resume-me");
        call.Options.MaxTurns.Should().Be(4);
        call.Options.RateLimitWait.Should().BeSameAs(policy);
        call.Options.IncludePartialMessages.Should().BeFalse();
    }

    [Fact]
    public async Task Single_user_message_is_sent_verbatim()
    {
        using var client = CreateClient();

        await client.GetResponseAsync("Fix the build", cancellationToken: Ct);

        _runner.LastCall.Prompt.Should().Be("Fix the build");
        _runner.LastCall.Options.SystemPrompt.Should().BeNull();
    }

    [Fact]
    public async Task ConfigureSession_result_is_what_the_runner_receives()
    {
        using var client = CreateClient(o => o.ConfigureSession = s => s with { SkipPermissions = false });

        await client.GetResponseAsync("Hi", cancellationToken: Ct);

        _runner.LastCall.Options.SkipPermissions.Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_token_reaches_the_runner()
    {
        using var cts = new CancellationTokenSource();
        using var client = CreateClient();

        await client.GetResponseAsync("Hi", cancellationToken: cts.Token);

        _runner.LastCall.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task Cancelled_token_cancels_the_call()
    {
        _runner.Enqueue().Text("slow").WithDelay(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Runner_exceptions_propagate()
    {
        _runner.Enqueue().Throws(new ClaudeCliNotFoundException());
        using var client = CreateClient();

        var act = () => client.GetResponseAsync("Hi", cancellationToken: Ct);

        await act.Should().ThrowAsync<ClaudeCliNotFoundException>();
    }

    [Fact]
    public async Task Invalid_messages_fail_before_the_runner_is_called()
    {
        using var client = CreateClient();

        var empty = () => client.GetResponseAsync(Array.Empty<ChatMessage>(), cancellationToken: Ct);
        var unsupported = () => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/png")])], cancellationToken: Ct);
        var nullMessages = () => client.GetResponseAsync((IEnumerable<ChatMessage>)null!, cancellationToken: Ct);

        await empty.Should().ThrowAsync<ArgumentException>();
        await unsupported.Should().ThrowAsync<NotSupportedException>();
        await nullMessages.Should().ThrowAsync<ArgumentNullException>();
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Client_options_changes_after_construction_apply_to_later_calls()
    {
        var options = new ClaudeCodeChatClientOptions { WorkingDirectory = "/repo", DefaultModel = "sonnet" };
        using var client = new ClaudeCodeChatClient(_runner, options);

        options.DefaultModel = "opus";
        await client.GetResponseAsync("Hi", cancellationToken: Ct);

        _runner.LastCall.Options.Model.Should().Be("opus");
    }

    [Fact]
    public async Task Sequential_calls_chain_via_conversation_id()
    {
        _runner.Enqueue().Returns(Result("first", "sess-A"));
        _runner.Enqueue().Returns(Result("second", "sess-A"));
        using var client = CreateClient();

        var first = await client.GetResponseAsync("one", cancellationToken: Ct);
        await client.GetResponseAsync("two", new ChatOptions { ConversationId = first.ConversationId }, Ct);

        _runner.Calls[0].Options.ResumeSessionId.Should().BeNull();
        _runner.Calls[1].Options.ResumeSessionId.Should().Be("sess-A");
    }
}

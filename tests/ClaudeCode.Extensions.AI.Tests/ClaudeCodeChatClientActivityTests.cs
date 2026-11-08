using ClaudeCode.Extensions.AI.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace ClaudeCode.Extensions.AI.Tests;

public class ClaudeCodeChatClientActivityTests
{
    private readonly FakeClaudeRunner _runner = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClaudeCodeChatClient CreateClient()
        => new(_runner, new ClaudeCodeChatClientOptions { WorkingDirectory = "/repo" });

    private static ClaudeStreamEvent ToolUse(string name, string? input)
        => new() { Kind = ClaudeStreamEventKind.ToolUse, ToolName = name, ToolInput = input! };

    private static ClaudeStreamEvent ToolResult(string text)
        => new() { Kind = ClaudeStreamEventKind.ToolResult, Text = text };

    private static ClaudeStreamEvent Wait(string text, DateTimeOffset? resetsAt)
        => new() { Kind = ClaudeStreamEventKind.RateLimitWait, Text = text, ResetsAt = resetsAt };

    private static async Task<List<ChatResponseUpdate>> Collect(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        var list = new List<ChatResponseUpdate>();
        await foreach (var u in updates)
        {
            list.Add(u);
        }

        return list;
    }

    [Fact]
    public async Task ToolUse_emits_update_with_properties_and_no_text()
    {
        var evt = ToolUse("Bash", """{"command":"ls"}""");
        _runner.Enqueue().Init("s").Event(evt).Final();
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        var tool = updates.Should().ContainSingle(u => u.AdditionalProperties != null && u.AdditionalProperties.ContainsKey(ClaudeCodeChatClient.ToolNameKey)).Subject;
        tool.AdditionalProperties![ClaudeCodeChatClient.ToolNameKey].Should().Be("Bash");
        tool.AdditionalProperties[ClaudeCodeChatClient.ToolInputKey].Should().Be("""{"command":"ls"}""");
        tool.Text.Should().BeEmpty();
        tool.Contents.Should().BeEmpty();
        tool.Role.Should().Be(ChatRole.Assistant);
        tool.FinishReason.Should().BeNull();
        tool.ConversationId.Should().Be("s");
        tool.RawRepresentation.Should().BeSameAs(evt);
        ClaudeCodeChatClient.ToolNameKey.Should().Be("claude.tool_name");
        ClaudeCodeChatClient.ToolInputKey.Should().Be("claude.tool_input");
    }

    [Fact]
    public async Task ToolUse_with_null_input_still_emits_update()
    {
        _runner.Enqueue().Event(ToolUse("TodoWrite", null)).Final();
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        var props = updates[0].AdditionalProperties!;
        props[ClaudeCodeChatClient.ToolNameKey].Should().Be("TodoWrite");
        props[ClaudeCodeChatClient.ToolInputKey].Should().BeNull();
    }

    [Fact]
    public async Task ToolResult_events_are_not_emitted()
    {
        _runner.Enqueue().Event(ToolUse("Read", "{}")).Event(ToolResult("file contents")).Final();
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        updates.Should().HaveCount(2, "one tool-use update plus the final update");
        updates.Should().NotContain(u => u.Text.Contains("file contents"));
        updates.Select(u => u.RawRepresentation).OfType<ClaudeStreamEvent>()
            .Should().NotContain(e => e.Kind == ClaudeStreamEventKind.ToolResult);
    }

    [Fact]
    public async Task Other_noise_events_are_not_emitted()
    {
        _runner.Enqueue()
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.System, Text = "sys" })
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.Progress })
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.PlainText, Text = "plain" })
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.Unknown, RawJson = "{}" })
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.RateLimit, IsRateLimited = false })
            .Final();
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        updates.Should().ContainSingle().Which.FinishReason.Should().Be(ChatFinishReason.Stop);
    }

    [Fact]
    public async Task RateLimitWait_emits_update_with_text_and_resets_at()
    {
        var resetsAt = new DateTimeOffset(2026, 11, 8, 23, 0, 0, TimeSpan.Zero);
        _runner.Enqueue().Event(Wait("Waiting 2h 10m for the rate limit to reset", resetsAt)).Final();
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        var wait = updates[0];
        wait.AdditionalProperties![ClaudeCodeChatClient.RateLimitWaitKey].Should().Be("Waiting 2h 10m for the rate limit to reset");
        wait.AdditionalProperties[ClaudeCodeChatClient.ResetsAtKey].Should().Be(resetsAt);
        wait.Text.Should().BeEmpty();
        wait.Contents.Should().BeEmpty();
        ClaudeCodeChatClient.RateLimitWaitKey.Should().Be("claude.rate_limit_wait");
        ClaudeCodeChatClient.ResetsAtKey.Should().Be("claude.resets_at");
    }

    [Fact]
    public async Task RateLimitWait_without_reset_time_has_null_resets_at()
    {
        _runner.Enqueue().Event(Wait("Waiting 15m", null)).Final();
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        updates[0].AdditionalProperties!.Should().ContainKey(ClaudeCodeChatClient.ResetsAtKey)
            .WhoseValue.Should().BeNull();
    }

    [Fact]
    public async Task Activity_updates_are_interleaved_in_stream_order()
    {
        _runner.Enqueue()
            .Delta("Let me look.").Text("Let me look.")
            .Event(ToolUse("Grep", "{}"))
            .Event(ToolResult("hits"))
            .Event(Wait("Waiting", null))
            .Delta("Found it.").Text("Found it.")
            .Final();
        using var client = CreateClient();

        var updates = await Collect(client.GetStreamingResponseAsync("Hi", cancellationToken: Ct));

        updates.Select(Describe).Should().Equal(
            "text:Let me look.",
            "tool:Grep",
            "wait",
            "text:\n\nFound it.",
            "final");
    }

    [Fact]
    public async Task Aggregated_text_is_unchanged_by_activity_updates()
    {
        FakeRun Script(bool withActivity)
        {
            var run = new FakeRun().Init("s").Delta("Checking").Text("Checking");
            if (withActivity)
            {
                run.Event(ToolUse("Bash", """{"command":"dotnet test"}"""))
                   .Event(ToolResult("Passed!"))
                   .Event(ToolUse("Read", """{"file":"a.cs"}"""))
                   .Event(Wait("Waiting 1m", DateTimeOffset.UtcNow));
            }

            return run.Delta("All ").Delta("good").Text("All good").Final(sessionId: "s");
        }

        _runner.Enqueue(Script(withActivity: true)).Enqueue(Script(withActivity: false)).Enqueue(Script(withActivity: true));
        using var client = CreateClient();

        var withActivity = await client.GetStreamingResponseAsync("Hi", cancellationToken: Ct).ToChatResponseAsync(Ct);
        var withoutActivity = await client.GetStreamingResponseAsync("Hi", cancellationToken: Ct).ToChatResponseAsync(Ct);
        var nonStreaming = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        withActivity.Text.Should().Be("Checking\n\nAll good");
        withActivity.Text.Should().Be(withoutActivity.Text);
        withActivity.Text.Should().Be(nonStreaming.Text);
        withActivity.Messages.Should().ContainSingle();
        withActivity.Messages[0].Contents.OfType<TextContent>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Non_streaming_text_ignores_tool_events()
    {
        _runner.Enqueue().Text("Before").Event(ToolUse("Bash", "{}")).Event(ToolResult("noise")).Text("After").Final(numTurns: 0);
        using var client = CreateClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        response.Text.Should().Be("Before\n\nAfter");
    }

    private static string Describe(ChatResponseUpdate u)
    {
        if (u.FinishReason is not null)
        {
            return "final";
        }

        if (u.AdditionalProperties?.TryGetValue(ClaudeCodeChatClient.ToolNameKey, out var name) == true)
        {
            return $"tool:{name}";
        }

        if (u.AdditionalProperties?.ContainsKey(ClaudeCodeChatClient.RateLimitWaitKey) == true)
        {
            return "wait";
        }

        return $"text:{u.Text}";
    }
}

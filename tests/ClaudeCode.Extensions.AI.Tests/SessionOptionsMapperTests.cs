using Microsoft.Extensions.AI;

namespace ClaudeCode.Extensions.AI.Tests;

public class SessionOptionsMapperTests
{
    private static ClaudeCodeChatClientOptions Client() => new() { WorkingDirectory = "/repo" };

    [Fact]
    public void Minimal_mapping_uses_working_directory_and_library_defaults()
    {
        var defaults = new ClaudeSessionOptions { WorkingDirectory = "x" };

        var mapped = SessionOptionsMapper.Map(Client(), null, null, streaming: false);

        mapped.WorkingDirectory.Should().Be("/repo");
        mapped.Model.Should().BeNull();
        mapped.SystemPrompt.Should().BeNull();
        mapped.ResumeSessionId.Should().BeNull();
        mapped.RateLimitWait.Should().BeNull();
        mapped.IncludePartialMessages.Should().BeFalse();
        mapped.MaxTurns.Should().Be(defaults.MaxTurns, "unset MaxTurns keeps ClaudeCode.Net's default");
        mapped.SkipPermissions.Should().Be(defaults.SkipPermissions);
    }

    [Fact]
    public void DefaultModel_is_used_when_no_ModelId()
    {
        var client = Client();
        client.DefaultModel = "sonnet";

        SessionOptionsMapper.Map(client, new ChatOptions(), null, false).Model.Should().Be("sonnet");
    }

    [Fact]
    public void ModelId_overrides_DefaultModel()
    {
        var client = Client();
        client.DefaultModel = "sonnet";

        SessionOptionsMapper.Map(client, new ChatOptions { ModelId = "opus" }, null, false).Model.Should().Be("opus");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_ModelId_falls_back_to_DefaultModel(string modelId)
    {
        var client = Client();
        client.DefaultModel = "sonnet";

        SessionOptionsMapper.Map(client, new ChatOptions { ModelId = modelId }, null, false).Model.Should().Be("sonnet");
    }

    [Fact]
    public void ConversationId_becomes_ResumeSessionId()
    {
        var mapped = SessionOptionsMapper.Map(Client(), new ChatOptions { ConversationId = "sess-123" }, null, false);

        mapped.ResumeSessionId.Should().Be("sess-123");
    }

    [Fact]
    public void Blank_ConversationId_does_not_resume()
    {
        SessionOptionsMapper.Map(Client(), new ChatOptions { ConversationId = " " }, null, false)
            .ResumeSessionId.Should().BeNull();
    }

    [Fact]
    public void System_prompt_is_passed_through_and_blank_is_null()
    {
        SessionOptionsMapper.Map(Client(), null, "be terse", false).SystemPrompt.Should().Be("be terse");
        SessionOptionsMapper.Map(Client(), null, "  ", false).SystemPrompt.Should().BeNull();
    }

    [Fact]
    public void RateLimitWait_and_MaxTurns_are_copied()
    {
        var policy = new RateLimitWaitPolicy { MaxWaits = 2 };
        var client = Client();
        client.RateLimitWait = policy;
        client.MaxTurns = 7;

        var mapped = SessionOptionsMapper.Map(client, null, null, false);

        mapped.RateLimitWait.Should().BeSameAs(policy);
        mapped.MaxTurns.Should().Be(7);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IncludePartialMessages_follows_streaming_flag(bool streaming)
    {
        SessionOptionsMapper.Map(Client(), null, null, streaming).IncludePartialMessages.Should().Be(streaming);
    }

    [Fact]
    public void ConfigureSession_is_applied_last_and_sees_all_mapped_values()
    {
        ClaudeSessionOptions? seen = null;
        var client = Client();
        client.DefaultModel = "sonnet";
        client.MaxTurns = 3;
        client.RateLimitWait = RateLimitWaitPolicy.Default;
        client.ConfigureSession = o =>
        {
            seen = o;
            return o with { Model = "haiku", SkipPermissions = false, SessionName = "custom", IncludePartialMessages = false };
        };

        var mapped = SessionOptionsMapper.Map(
            client, new ChatOptions { ModelId = "opus", ConversationId = "c1" }, "sys", streaming: true);

        seen.Should().NotBeNull();
        seen!.Model.Should().Be("opus");
        seen.ResumeSessionId.Should().Be("c1");
        seen.SystemPrompt.Should().Be("sys");
        seen.MaxTurns.Should().Be(3);
        seen.IncludePartialMessages.Should().BeTrue();
        seen.RateLimitWait.Should().BeSameAs(RateLimitWaitPolicy.Default);

        mapped.Model.Should().Be("haiku", "the hook wins over every mapped value");
        mapped.SkipPermissions.Should().BeFalse();
        mapped.SessionName.Should().Be("custom");
        mapped.IncludePartialMessages.Should().BeFalse();
        mapped.ResumeSessionId.Should().Be("c1");
    }

    [Fact]
    public void ConfigureSession_returning_null_throws()
    {
        var client = Client();
        client.ConfigureSession = _ => null!;

        var act = () => SessionOptionsMapper.Map(client, null, null, false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConfigureSession*");
    }

    [Fact]
    public void ConfigureSession_is_invoked_once_per_map()
    {
        var calls = 0;
        var client = Client();
        client.ConfigureSession = o => { calls++; return o; };

        SessionOptionsMapper.Map(client, null, null, false);
        SessionOptionsMapper.Map(client, null, null, true);

        calls.Should().Be(2);
    }

    [Fact]
    public void Unsupported_ChatOptions_are_ignored_without_throwing()
    {
        var chatOptions = new ChatOptions
        {
            Temperature = 0.2f,
            TopP = 0.9f,
            TopK = 40,
            MaxOutputTokens = 100,
            StopSequences = ["STOP"],
            FrequencyPenalty = 0.1f,
            PresencePenalty = 0.1f,
            Seed = 42,
            ResponseFormat = ChatResponseFormat.Json,
            Tools = [AIFunctionFactory.Create(() => "x", "f")],
            ToolMode = ChatToolMode.RequireAny,
            AdditionalProperties = new AdditionalPropertiesDictionary { ["k"] = "v" },
        };

        var act = () => SessionOptionsMapper.Map(Client(), chatOptions, null, false);

        act.Should().NotThrow();
        act().Should().BeEquivalentTo(SessionOptionsMapper.Map(Client(), null, null, false));
    }

    [Fact]
    public void Mapping_does_not_mutate_inputs()
    {
        var client = Client();
        client.DefaultModel = "sonnet";
        var chatOptions = new ChatOptions { ModelId = "opus", ConversationId = "c" };

        SessionOptionsMapper.Map(client, chatOptions, "s", true);

        client.DefaultModel.Should().Be("sonnet");
        chatOptions.ModelId.Should().Be("opus");
        chatOptions.ConversationId.Should().Be("c");
    }
}

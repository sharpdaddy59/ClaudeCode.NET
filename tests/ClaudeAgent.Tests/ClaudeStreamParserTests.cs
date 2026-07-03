using FluentAssertions;
using ClaudeAgent;
using Xunit;

namespace ClaudeAgent.Tests;

public class ClaudeStreamParserTests
{
    [Fact]
    public void ParseLine_SystemInit_CapturesSessionId()
    {
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"system","subtype":"init","cwd":"/tmp","session_id":"abc-123","model":"claude-sonnet-5"}""");

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(ClaudeStreamEventKind.SystemInit);
        events[0].SessionId.Should().Be("abc-123");
    }

    [Fact]
    public void ParseLine_ThinkingTokens_IsProgressNoise()
    {
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"system","subtype":"thinking_tokens","estimated_tokens":87}""");

        events.Should().ContainSingle().Which.Kind.Should().Be(ClaudeStreamEventKind.Progress);
    }

    [Fact]
    public void ParseLine_AssistantMessage_ExtractsNestedText()
    {
        // Real CLI shape: content nested under "message"
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hello"}]}}""");

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(ClaudeStreamEventKind.AssistantText);
        events[0].Text.Should().Be("hello");
    }

    [Fact]
    public void ParseLine_AssistantMessage_YieldsTextAndToolUseEvents()
    {
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"..."},{"type":"text","text":"Let me check."},{"type":"tool_use","name":"Bash","input":{"command":"ls"}}]}}""");

        events.Should().HaveCount(3);
        events[0].Kind.Should().Be(ClaudeStreamEventKind.Progress);
        events[1].Kind.Should().Be(ClaudeStreamEventKind.AssistantText);
        events[1].Text.Should().Be("Let me check.");
        events[2].Kind.Should().Be(ClaudeStreamEventKind.ToolUse);
        events[2].ToolName.Should().Be("Bash");
        events[2].ToolInput.Should().Contain("\"command\"");
    }

    [Fact]
    public void ParseLine_LegacyRootContent_StillParses()
    {
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"assistant","content":[{"type":"text","text":"legacy"}]}""");

        events.Should().ContainSingle().Which.Text.Should().Be("legacy");
    }

    [Fact]
    public void ParseLine_UserToolResult_ExtractsText()
    {
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"user","message":{"content":[{"type":"tool_result","content":[{"type":"text","text":"file.txt\nother.txt"}]}]}}""");

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(ClaudeStreamEventKind.ToolResult);
        events[0].Text.Should().Be("file.txt\nother.txt");
    }

    [Fact]
    public void ParseLine_Result_CapturesStatsAndSessionId()
    {
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":2190,"num_turns":3,"result":"done","session_id":"s-1","total_cost_usd":0.018}""");

        events.Should().ContainSingle();
        var evt = events[0];
        evt.Kind.Should().Be(ClaudeStreamEventKind.Result);
        evt.SessionId.Should().Be("s-1");
        evt.DurationMs.Should().Be(2190);
        evt.NumTurns.Should().Be(3);
        evt.CostUsd.Should().BeApproximately(0.018, 0.0001);
        evt.Text.Should().Be("done");
        evt.IsError.Should().BeFalse();
        evt.IsRateLimited.Should().BeFalse();
    }

    [Fact]
    public void ParseLine_ErrorResult_MentioningRateLimit_FlagsRateLimited()
    {
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"result","subtype":"error","is_error":true,"result":"Usage limit reached, try again later","session_id":"s-2"}""");

        events[0].IsError.Should().BeTrue();
        events[0].IsRateLimited.Should().BeTrue();
    }

    [Theory]
    [InlineData("allowed", false)]
    [InlineData("rejected", true)]
    // Unknown/benign statuses must not trigger the retry loop - only a hard
    // rejection counts; error-text heuristics cover everything else
    [InlineData("queued", false)]
    [InlineData("allowed_warning", false)]
    public void ParseLine_RateLimitEvent_FlagsByStatus(string status, bool expected)
    {
        var events = ClaudeStreamParser.ParseLine(
            $$$"""{"type":"rate_limit_event","rate_limit_info":{"status":"{{{status}}}","rateLimitType":"five_hour"}}""");

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(ClaudeStreamEventKind.RateLimit);
        events[0].IsRateLimited.Should().Be(expected);
    }

    [Fact]
    public void ParseLine_ErrorMessage_IsErrorEvent()
    {
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"error","message":"something broke"}""");

        events[0].Kind.Should().Be(ClaudeStreamEventKind.Error);
        events[0].Text.Should().Be("something broke");
        events[0].IsError.Should().BeTrue();
    }

    [Fact]
    public void ParseLine_NonJson_IsPlainText()
    {
        var events = ClaudeStreamParser.ParseLine("not json at all");

        events[0].Kind.Should().Be(ClaudeStreamEventKind.PlainText);
        events[0].Text.Should().Be("not json at all");
    }

    [Fact]
    public void ParseLine_UnknownType_PreservesRawJson()
    {
        var events = ClaudeStreamParser.ParseLine("""{"type":"mystery","data":1}""");

        events[0].Kind.Should().Be(ClaudeStreamEventKind.Unknown);
        events[0].RawJson.Should().Contain("mystery");
    }

    [Theory]
    [InlineData("Rate limit exceeded", true)]
    [InlineData("rate_limit_error from API", true)]
    [InlineData("HTTP 429 Too Many Requests", true)]
    [InlineData("quota exceeded for this billing period", true)]
    [InlineData("Usage limit reached", true)]
    [InlineData("The API is overloaded", true)]
    [InlineData("Servers are at capacity right now", true)]
    // 'capacity' alone used to false-positive on unrelated output like disk errors
    [InlineData("Disk capacity is 80%", false)]
    [InlineData("Everything is fine", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsRateLimitError_MatchesExpectedPatterns(string? message, bool expected)
    {
        ClaudeStreamParser.IsRateLimitError(message).Should().Be(expected);
    }
}

using FluentAssertions;
using ClaudeCode;
using Xunit;

namespace ClaudeCode.Tests;

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
    public void ParseLine_RateLimitEvent_CapturesResetTime()
    {
        // Captured from CLI 2.1.319: an 'allowed' heartbeat still reports when the window resets
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1794166200,"rateLimitType":"five_hour","overageStatus":"rejected","isUsingOverage":false,"unifiedWindows":{"five_hour":{"utilization":0.04,"resetsAt":1794166200,"status":"allowed"},"seven_day":{"utilization":0.18,"resetsAt":1794362400,"status":"allowed"}}},"uuid":"u","session_id":"s"}""");

        var evt = events.Should().ContainSingle().Subject;
        evt.Kind.Should().Be(ClaudeStreamEventKind.RateLimit);
        evt.IsRateLimited.Should().BeFalse();
        evt.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1794166200));
    }

    [Fact]
    public void ParseLine_RateLimitEvent_NonNumericResetTime_IsIgnored()
    {
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":"2026-11-08T12:00:00Z"}}""");

        events[0].IsRateLimited.Should().BeTrue();
        events[0].ResetsAt.Should().BeNull();
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
    public void ParseLine_StreamEventTextDelta_IsAssistantTextDelta()
    {
        // Captured from `claude --include-partial-messages` (CLI 2.1.319)
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello from partial stre"}},"session_id":"7fd11789-566a-4956-a609-22e732e56721","parent_tool_use_id":null,"uuid":"75d54ce2-097a-4ad4-82a5-03f56b332841","api_message_id":"msg_011CgqfePwcgYBbhaCd3htCz"}""");

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(ClaudeStreamEventKind.AssistantTextDelta);
        events[0].Text.Should().Be("Hello from partial stre");
    }

    [Theory]
    [InlineData("""{"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-fable-5-1","id":"msg_1","type":"message","role":"assistant","content":[],"stop_reason":null,"usage":{"input_tokens":2}}},"session_id":"s"}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"session_id":"s"}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_stop","index":0},"session_id":"s"}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":11}},"session_id":"s"}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"message_stop"},"session_id":"s"}""")]
    // Tool-call argument streaming and thinking deltas carry nothing to render
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"command\":"}},"session_id":"s"}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me"}},"session_id":"s"}""")]
    public void ParseLine_OtherStreamEvents_AreProgressNoise(string line)
    {
        var events = ClaudeStreamParser.ParseLine(line);

        events.Should().ContainSingle().Which.Kind.Should().Be(ClaudeStreamEventKind.Progress);
    }

    [Fact]
    public void ParseLine_TextDelta_DoesNotCarrySessionIdOrFlags()
    {
        // Deltas must not perturb the session's aggregate state (session id, error,
        // rate-limit flags) that ClaudeSession folds from every event
        var events = ClaudeStreamParser.ParseLine(
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"x"}},"session_id":"s"}""");

        events[0].SessionId.Should().BeNull();
        events[0].IsError.Should().BeFalse();
        events[0].IsRateLimited.Should().BeFalse();
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

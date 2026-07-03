using System.Text.Json;

namespace ClaudeAgent;

public enum ClaudeStreamEventKind
{
    /// <summary>First event of a session; carries the session ID.</summary>
    SystemInit,

    /// <summary>Informational system message.</summary>
    System,

    /// <summary>Assistant text output.</summary>
    AssistantText,

    /// <summary>Assistant invoked a tool.</summary>
    ToolUse,

    /// <summary>Result of a tool invocation.</summary>
    ToolResult,

    /// <summary>Final result event with session stats.</summary>
    Result,

    /// <summary>Error reported in the stream.</summary>
    Error,

    /// <summary>Rate-limit status event.</summary>
    RateLimit,

    /// <summary>High-frequency progress noise (thinking tokens, etc.) — safe to ignore.</summary>
    Progress,

    /// <summary>A line that wasn't JSON.</summary>
    PlainText,

    /// <summary>JSON with an unrecognized type.</summary>
    Unknown
}

/// <summary>
/// A single parsed event from the Claude CLI's stream-json output.
/// </summary>
public sealed record ClaudeStreamEvent
{
    public required ClaudeStreamEventKind Kind { get; init; }
    public string? Text { get; init; }
    public string? ToolName { get; init; }
    public string? ToolInput { get; init; }
    public string? SessionId { get; init; }
    public long DurationMs { get; init; }
    public int NumTurns { get; init; }
    public double CostUsd { get; init; }
    public bool IsRateLimited { get; init; }
    public bool IsError { get; init; }
    public string? RawJson { get; init; }
}

/// <summary>
/// Parses Claude CLI stream-json lines into typed events. Stateless and side-effect free.
///
/// Actual stream shape (verified against Claude CLI output):
///   {"type":"system","subtype":"init","session_id":...}
///   {"type":"rate_limit_event","rate_limit_info":{"status":"allowed",...}}
///   {"type":"system","subtype":"thinking_tokens",...}
///   {"type":"assistant","message":{"content":[{"type":"text"|"thinking"|"tool_use",...}]}}
///   {"type":"user","message":{"content":[{"type":"tool_result",...}]}}
///   {"type":"result","subtype":"success","is_error":false,"session_id":...,"num_turns":...,"total_cost_usd":...}
/// </summary>
public static class ClaudeStreamParser
{
    /// <summary>
    /// Parses one stdout line. A single line can yield multiple events
    /// (an assistant message may contain both text and tool_use blocks).
    /// Never throws; malformed lines come back as PlainText.
    /// </summary>
    public static IReadOnlyList<ClaudeStreamEvent> ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeElement))
            {
                return [new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.Unknown, RawJson = line }];
            }

            return typeElement.GetString() switch
            {
                "system" => ParseSystem(root),
                "assistant" => ParseAssistant(root),
                "user" => ParseUser(root),
                "result" => ParseResult(root),
                "error" => ParseError(root),
                "rate_limit_event" => ParseRateLimit(root),
                // Legacy root-level shapes, kept for compatibility with older CLI output
                "tool_use" => ParseLegacyToolUse(root),
                "tool_result" => ParseLegacyToolResult(root),
                var other => [new ClaudeStreamEvent
                {
                    Kind = ClaudeStreamEventKind.Unknown,
                    Text = other,
                    RawJson = line
                }]
            };
        }
        catch (JsonException)
        {
            return [new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.PlainText, Text = line }];
        }
    }

    /// <summary>
    /// True if the message looks like a rate-limit / capacity error. Substring heuristic —
    /// apply only to error messages and stderr, not arbitrary tool output.
    /// </summary>
    public static bool IsRateLimitError(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;

        var lower = message.ToLowerInvariant();
        return lower.Contains("rate limit") ||
               lower.Contains("rate_limit") ||
               lower.Contains("ratelimit") ||
               lower.Contains("too many requests") ||
               lower.Contains("429") ||
               lower.Contains("quota exceeded") ||
               lower.Contains("usage limit") ||
               lower.Contains("at capacity") ||
               lower.Contains("overloaded");
    }

    private static IReadOnlyList<ClaudeStreamEvent> ParseSystem(JsonElement root)
    {
        var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;

        if (subtype == "init")
        {
            var sessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            return [new ClaudeStreamEvent
            {
                Kind = ClaudeStreamEventKind.SystemInit,
                SessionId = sessionId,
                Text = model is not null ? $"Session initialized (model: {model})" : "Session initialized"
            }];
        }

        // thinking_tokens etc. fire once per streamed chunk — pure noise for consumers
        if (subtype == "thinking_tokens")
        {
            return [new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.Progress }];
        }

        var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : root.GetRawText();
        return [new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.System, Text = message ?? "" }];
    }

    private static IReadOnlyList<ClaudeStreamEvent> ParseAssistant(JsonElement root)
    {
        // Real CLI shape nests content under "message"; fall back to root "content" (legacy)
        JsonElement content;
        if (root.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var nested))
        {
            content = nested;
        }
        else if (!root.TryGetProperty("content", out content))
        {
            return [];
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return [new ClaudeStreamEvent
            {
                Kind = ClaudeStreamEventKind.AssistantText,
                Text = content.GetString() ?? ""
            }];
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var events = new List<ClaudeStreamEvent>();
        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var blockType)) continue;

            switch (blockType.GetString())
            {
                case "text" when block.TryGetProperty("text", out var text):
                    events.Add(new ClaudeStreamEvent
                    {
                        Kind = ClaudeStreamEventKind.AssistantText,
                        Text = text.GetString() ?? ""
                    });
                    break;

                case "tool_use":
                    events.Add(new ClaudeStreamEvent
                    {
                        Kind = ClaudeStreamEventKind.ToolUse,
                        ToolName = block.TryGetProperty("name", out var name) ? name.GetString() : "unknown",
                        ToolInput = block.TryGetProperty("input", out var input) ? input.GetRawText() : ""
                    });
                    break;

                case "thinking":
                    events.Add(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.Progress });
                    break;
            }
        }

        return events;
    }

    private static IReadOnlyList<ClaudeStreamEvent> ParseUser(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var events = new List<ClaudeStreamEvent>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var blockType) &&
                blockType.GetString() == "tool_result")
            {
                events.Add(new ClaudeStreamEvent
                {
                    Kind = ClaudeStreamEventKind.ToolResult,
                    Text = ExtractToolResultText(block)
                });
            }
        }

        return events;
    }

    private static string ExtractToolResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return "";

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    item.TryGetProperty("text", out var text))
                {
                    parts.Add(text.GetString() ?? "");
                }
            }
            return string.Join("\n", parts);
        }

        return "";
    }

    private static IReadOnlyList<ClaudeStreamEvent> ParseResult(JsonElement root)
    {
        var resultText = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;
        var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;

        return [new ClaudeStreamEvent
        {
            Kind = ClaudeStreamEventKind.Result,
            SessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null,
            DurationMs = root.TryGetProperty("duration_ms", out var d) ? d.GetInt64() : 0,
            NumTurns = root.TryGetProperty("num_turns", out var n) ? n.GetInt32() : 0,
            CostUsd = root.TryGetProperty("total_cost_usd", out var c) ? c.GetDouble() : 0,
            Text = resultText,
            IsError = isError,
            IsRateLimited = isError && IsRateLimitError(resultText)
        }];
    }

    private static IReadOnlyList<ClaudeStreamEvent> ParseError(JsonElement root)
    {
        var message = root.TryGetProperty("message", out var m) ? m.GetString() : root.GetRawText();
        return [new ClaudeStreamEvent
        {
            Kind = ClaudeStreamEventKind.Error,
            Text = message ?? "Unknown error",
            IsError = true,
            IsRateLimited = IsRateLimitError(message)
        }];
    }

    private static IReadOnlyList<ClaudeStreamEvent> ParseRateLimit(JsonElement root)
    {
        // {"type":"rate_limit_event","rate_limit_info":{"status":"allowed"|"rejected"|...}}
        var status = root.TryGetProperty("rate_limit_info", out var info) &&
                     info.TryGetProperty("status", out var s)
            ? s.GetString()
            : null;

        return [new ClaudeStreamEvent
        {
            Kind = ClaudeStreamEventKind.RateLimit,
            Text = status,
            // Only 'rejected' clearly means throttled. Treating every unknown status as
            // a rate limit would send benign heartbeats (or a future 'allowed_warning')
            // into the retry loop; the substring heuristic still covers error text.
            IsRateLimited = status == "rejected"
        }];
    }

    private static IReadOnlyList<ClaudeStreamEvent> ParseLegacyToolUse(JsonElement root)
    {
        return [new ClaudeStreamEvent
        {
            Kind = ClaudeStreamEventKind.ToolUse,
            ToolName = root.TryGetProperty("name", out var name) ? name.GetString() : "unknown",
            ToolInput = root.TryGetProperty("input", out var input) ? input.GetRawText() : ""
        }];
    }

    private static IReadOnlyList<ClaudeStreamEvent> ParseLegacyToolResult(JsonElement root)
    {
        var content = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : "";
        return [new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.ToolResult, Text = content ?? "" }];
    }
}

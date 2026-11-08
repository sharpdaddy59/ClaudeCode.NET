namespace ClaudeCode.Extensions.AI;

/// <summary>
/// Configuration for the Claude Code <c>IChatClient</c> adapter. Values here are the defaults for
/// every request; per-request <c>ChatOptions</c> can override some of them.
/// </summary>
public sealed class ClaudeCodeChatClientOptions
{
    /// <summary>
    /// Directory the Claude CLI runs in. Claude reads <c>.mcp.json</c>, <c>CLAUDE.md</c>, etc. from here.
    /// Must be non-empty.
    /// </summary>
    public required string WorkingDirectory { get; set; }

    /// <summary>Model used when a request does not specify <c>ChatOptions.ModelId</c>. Null uses the CLI's default.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>Text appended to Claude's default system prompt for every request.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Maximum agentic turns per request. Null keeps ClaudeCode.Net's default.</summary>
    public int? MaxTurns { get; set; }

    /// <summary>
    /// Rate-limit wait-and-resume policy passed to every session. Null (default) returns
    /// rate-limited runs to the caller instead of waiting.
    /// </summary>
    public RateLimitWaitPolicy? RateLimitWait { get; set; }

    /// <summary>
    /// Optional hook applied last, after the adapter has mapped everything it knows about, so callers
    /// can set any <see cref="ClaudeSessionOptions"/> the adapter does not map (for example
    /// <see cref="ClaudeSessionOptions.SkipPermissions"/> or environment variables). Return the
    /// options to use - typically a <c>with</c> copy of the input.
    /// </summary>
    public Func<ClaudeSessionOptions, ClaudeSessionOptions>? ConfigureSession { get; set; }

    /// <summary>Throws if the options cannot be used to construct a chat client.</summary>
    /// <exception cref="ArgumentException"><see cref="WorkingDirectory"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="MaxTurns"/> is zero or negative.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            throw new ArgumentException(
                "WorkingDirectory must be a non-empty path.",
                nameof(WorkingDirectory));
        }

        if (MaxTurns is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxTurns),
                MaxTurns,
                "MaxTurns must be positive when set.");
        }
    }
}

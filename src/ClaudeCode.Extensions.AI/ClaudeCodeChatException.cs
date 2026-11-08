namespace ClaudeCode.Extensions.AI;

/// <summary>
/// Thrown by <see cref="ClaudeCodeChatClient"/> when a Claude Code CLI session ends in an error
/// (the CLI reported an error result or exited with a non-zero code).
/// </summary>
public sealed class ClaudeCodeChatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Error description.</param>
    /// <param name="exitCode">CLI process exit code.</param>
    /// <param name="sessionId">CLI session id, if one was reported.</param>
    /// <param name="rateLimitDetected">True if the failure was caused by a rate limit.</param>
    /// <param name="rateLimitResetsAt">When the rate-limit window resets, if known.</param>
    /// <param name="innerException">Optional inner exception.</param>
    public ClaudeCodeChatException(
        string message,
        int exitCode,
        string? sessionId,
        bool rateLimitDetected,
        DateTimeOffset? rateLimitResetsAt,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
        SessionId = sessionId;
        RateLimitDetected = rateLimitDetected;
        RateLimitResetsAt = rateLimitResetsAt;
    }

    /// <summary>CLI process exit code.</summary>
    public int ExitCode { get; }

    /// <summary>
    /// <see cref="ExitCode"/> value for failures detected while streaming, where the CLI's exit
    /// code is not observable.
    /// </summary>
    public const int UnknownExitCode = int.MinValue;

    /// <summary>CLI session id, if one was reported; can be resumed via <c>ChatOptions.ConversationId</c>.</summary>
    public string? SessionId { get; }

    /// <summary>True if a rate limit was detected during the session.</summary>
    public bool RateLimitDetected { get; }

    /// <summary>When the rate-limit window resets, if the CLI reported it.</summary>
    public DateTimeOffset? RateLimitResetsAt { get; }

    /// <summary>True if <paramref name="result"/> represents a failed session.</summary>
    internal static bool IsFailure(ClaudeSessionResult result) => result.IsError || result.ExitCode != 0;

    /// <summary>Builds the exception for a failed session result.</summary>
    internal static ClaudeCodeChatException FromResult(ClaudeSessionResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.ResultText)
            ? GenericMessage(result)
            : result.ResultText;

        return new ClaudeCodeChatException(
            message,
            result.ExitCode,
            string.IsNullOrEmpty(result.SessionId) ? null : result.SessionId,
            result.RateLimitDetected,
            result.RateLimitResetsAt);
    }

    private static string GenericMessage(ClaudeSessionResult result)
    {
        var message = result.ExitCode == UnknownExitCode
            ? "The Claude Code session failed without reporting a result."
            : $"The Claude Code session failed (exit code {result.ExitCode}).";
        if (result.RateLimitDetected)
        {
            message += result.RateLimitResetsAt is { } resetsAt
                ? $" A rate limit was hit; it resets at {resetsAt:O}."
                : " A rate limit was hit.";
        }

        return message;
    }
}

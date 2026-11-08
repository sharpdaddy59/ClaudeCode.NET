namespace ClaudeCode.Extensions.AI;

/// <summary>
/// Abstraction over <see cref="ClaudeSession"/> (which is sealed) so the chat client can be
/// driven by a test double. Members mirror the session's own signatures.
/// </summary>
public interface IClaudeRunner
{
    /// <summary>
    /// Runs the CLI to completion, invoking <paramref name="onEvent"/> for every parsed event,
    /// and returns the final result. Mirrors
    /// <see cref="ClaudeSession.RunAsync(string, ClaudeSessionOptions, Action{ClaudeStreamEvent}, Action{string}, CancellationToken)"/>.
    /// </summary>
    /// <param name="prompt">Prompt sent to the CLI.</param>
    /// <param name="options">Options for this invocation.</param>
    /// <param name="onEvent">Optional per-event callback.</param>
    /// <param name="cancellationToken">Cancels the run (kills the CLI process tree).</param>
    /// <returns>The final session result.</returns>
    Task<ClaudeSessionResult> RunAsync(
        string prompt,
        ClaudeSessionOptions options,
        Action<ClaudeStreamEvent>? onEvent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs the CLI to completion, yielding parsed events as they arrive. Mirrors
    /// <see cref="ClaudeSession.StreamAsync(string, ClaudeSessionOptions, CancellationToken)"/>.
    /// </summary>
    /// <param name="prompt">Prompt sent to the CLI.</param>
    /// <param name="options">Options for this invocation.</param>
    /// <param name="cancellationToken">Cancels the run (kills the CLI process tree).</param>
    /// <returns>The parsed event stream; the final event is normally <see cref="ClaudeStreamEventKind.Result"/>.</returns>
    IAsyncEnumerable<ClaudeStreamEvent> StreamAsync(
        string prompt,
        ClaudeSessionOptions options,
        CancellationToken cancellationToken);
}

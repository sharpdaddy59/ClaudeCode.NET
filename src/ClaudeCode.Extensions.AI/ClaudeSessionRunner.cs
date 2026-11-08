namespace ClaudeCode.Extensions.AI;

/// <summary>
/// Production <see cref="IClaudeRunner"/> that delegates to a real <see cref="ClaudeSession"/>,
/// forwarding every argument unchanged.
/// </summary>
public sealed class ClaudeSessionRunner : IClaudeRunner
{
    private static readonly Action<ClaudeStreamEvent> NoOpEvent = static _ => { };
    private static readonly Action<string> NoOpStdErr = static _ => { };

    /// <summary>Creates a runner over a new <see cref="ClaudeSession"/> with no logging.</summary>
    public ClaudeSessionRunner()
        : this(new ClaudeSession())
    {
    }

    /// <summary>Creates a runner over an existing (for example DI-registered) session.</summary>
    /// <param name="session">The session to delegate to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public ClaudeSessionRunner(ClaudeSession session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>The underlying session every call is forwarded to.</summary>
    public ClaudeSession Session { get; }

    /// <inheritdoc />
    public Task<ClaudeSessionResult> RunAsync(
        string prompt,
        ClaudeSessionOptions options,
        Action<ClaudeStreamEvent>? onEvent,
        CancellationToken cancellationToken)
        => Session.RunAsync(prompt, options, onEvent ?? NoOpEvent, NoOpStdErr, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ClaudeStreamEvent> StreamAsync(
        string prompt,
        ClaudeSessionOptions options,
        CancellationToken cancellationToken)
        => Session.StreamAsync(prompt, options, cancellationToken);
}

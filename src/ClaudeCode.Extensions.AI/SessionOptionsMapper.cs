using Microsoft.Extensions.AI;

namespace ClaudeCode.Extensions.AI;

/// <summary>
/// Builds the <see cref="ClaudeSessionOptions"/> for one CLI invocation from the client options
/// and the per-call <see cref="ChatOptions"/>.
/// </summary>
/// <remarks>
/// Mapped: <see cref="ChatOptions.ModelId"/> (overrides <see cref="ClaudeCodeChatClientOptions.DefaultModel"/>)
/// and <see cref="ChatOptions.ConversationId"/> (becomes <see cref="ClaudeSessionOptions.ResumeSessionId"/>,
/// resuming that CLI session). Blank values count as unset.
/// <para>
/// Ignored without an error because the Claude CLI has no equivalent: <see cref="ChatOptions.Temperature"/>,
/// <see cref="ChatOptions.TopP"/>, <see cref="ChatOptions.TopK"/>, <see cref="ChatOptions.MaxOutputTokens"/>,
/// <see cref="ChatOptions.StopSequences"/>, <see cref="ChatOptions.FrequencyPenalty"/>,
/// <see cref="ChatOptions.PresencePenalty"/>, <see cref="ChatOptions.Seed"/>,
/// <see cref="ChatOptions.ResponseFormat"/>, <see cref="ChatOptions.Tools"/>,
/// <see cref="ChatOptions.ToolMode"/> and <see cref="ChatOptions.AdditionalProperties"/>.
/// Claude Code uses its own tools, configured through the working directory.
/// </para>
/// </remarks>
internal static class SessionOptionsMapper
{
    /// <summary>Maps options for one call.</summary>
    /// <param name="clientOptions">The client's configuration.</param>
    /// <param name="chatOptions">Per-call options; may be null.</param>
    /// <param name="systemPrompt">The merged system prompt from <see cref="PromptBuilder"/>.</param>
    /// <param name="streaming">True for streaming calls, which turns on partial messages.</param>
    /// <exception cref="InvalidOperationException"><see cref="ClaudeCodeChatClientOptions.ConfigureSession"/> returned null.</exception>
    public static ClaudeSessionOptions Map(
        ClaudeCodeChatClientOptions clientOptions,
        ChatOptions? chatOptions,
        string? systemPrompt,
        bool streaming)
    {
        ArgumentNullException.ThrowIfNull(clientOptions);

        var model = NullIfBlank(chatOptions?.ModelId) ?? NullIfBlank(clientOptions.DefaultModel);

        var options = new ClaudeSessionOptions
        {
            WorkingDirectory = clientOptions.WorkingDirectory,
            Model = model,
            SystemPrompt = NullIfBlank(systemPrompt),
            ResumeSessionId = NullIfBlank(chatOptions?.ConversationId),
            RateLimitWait = clientOptions.RateLimitWait,
            IncludePartialMessages = streaming,
        };

        if (clientOptions.MaxTurns is { } maxTurns)
        {
            options = options with { MaxTurns = maxTurns };
        }

        if (clientOptions.ConfigureSession is { } configure)
        {
            options = configure(options)
                ?? throw new InvalidOperationException(
                    $"{nameof(ClaudeCodeChatClientOptions)}.{nameof(ClaudeCodeChatClientOptions.ConfigureSession)} returned null.");
        }

        return options;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

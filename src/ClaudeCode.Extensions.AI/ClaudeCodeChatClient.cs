using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace ClaudeCode.Extensions.AI;

/// <summary>
/// <see cref="IChatClient"/> backed by Claude Code CLI sessions (via ClaudeCode.Net). Each call runs
/// one CLI invocation. The CLI session id comes back as <see cref="ChatResponse.ConversationId"/>;
/// pass it in <see cref="ChatOptions.ConversationId"/> to resume that session. When resuming, the
/// CLI already holds the earlier turns, so only the messages after the last assistant message
/// (normally the new user message) are sent. Without a ConversationId, the whole history is
/// replayed as a transcript.
/// </summary>
/// <remarks>
/// Only text content is supported. <see cref="ChatOptions.ModelId"/> and
/// <see cref="ChatOptions.ConversationId"/> are honored, and <see cref="ChatOptions.Instructions"/>
/// is appended to the system prompt. Temperature, TopP, TopK, MaxOutputTokens,
/// StopSequences, penalties, Seed, ResponseFormat, Tools and ToolMode are ignored, because the CLI
/// has no equivalent. Claude Code uses its own tools, configured through the working directory.
/// Use <see cref="ClaudeCodeChatClientOptions.ConfigureSession"/> for CLI settings the adapter
/// does not map.
/// </remarks>
public sealed class ClaudeCodeChatClient : IChatClient
{
    /// <summary>Provider name reported in <see cref="ChatClientMetadata"/>.</summary>
    public const string ProviderName = "claude-code";

    /// <summary>
    /// <see cref="ChatResponse.AdditionalProperties"/> key holding the number of rate-limit
    /// wait-and-resume cycles (an <see cref="int"/>) a successful run needed. Present only when
    /// the count is greater than zero.
    /// </summary>
    public const string RateLimitWaitsKey = "claude.rate_limit_waits";

    /// <summary>
    /// <see cref="ChatResponse.AdditionalProperties"/> key holding the session cost in USD (a
    /// <see cref="double"/>; 0 on subscription plans). Absent when the CLI reported no stats.
    /// </summary>
    public const string CostUsdKey = "claude.cost_usd";

    /// <summary>
    /// <see cref="ChatResponse.AdditionalProperties"/> key holding the number of agentic turns
    /// (an <see cref="int"/>). Absent when not reported.
    /// </summary>
    public const string NumTurnsKey = "claude.num_turns";

    /// <summary>
    /// <see cref="ChatResponse.AdditionalProperties"/> key holding the CLI-reported session duration in
    /// milliseconds (a <see cref="long"/>). Absent when not reported.
    /// </summary>
    public const string DurationMsKey = "claude.duration_ms";

    /// <summary>
    /// Streaming-update <see cref="ChatResponseUpdate.AdditionalProperties"/> key holding the name of
    /// a tool the CLI ran itself (a <see cref="string"/>). Such updates carry no text. This is
    /// Claude Code's own tool use, not <see cref="IChatClient"/> function calling.
    /// </summary>
    public const string ToolNameKey = "claude.tool_name";

    /// <summary>Streaming-update key holding the tool input JSON (a <see cref="string"/>) next to <see cref="ToolNameKey"/>.</summary>
    public const string ToolInputKey = "claude.tool_input";

    /// <summary>
    /// Streaming-update key present when the session hit a rate limit and is waiting to resume.
    /// The value is the wait description (a <see cref="string"/>). Such updates carry no text.
    /// </summary>
    public const string RateLimitWaitKey = "claude.rate_limit_wait";

    /// <summary>Streaming-update key holding the rate-limit reset time (a nullable <see cref="DateTimeOffset"/>) next to <see cref="RateLimitWaitKey"/>.</summary>
    public const string ResetsAtKey = "claude.resets_at";

    private const string BlockSeparator = "\n\n";

    private readonly ClaudeCodeChatClientOptions _options;
    private ChatClientMetadata _metadata;

    /// <summary>Creates a client that runs the real Claude CLI through a new <see cref="ClaudeSession"/>.</summary>
    /// <param name="options">Client configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid (for example an empty working directory).</exception>
    public ClaudeCodeChatClient(ClaudeCodeChatClientOptions options)
        : this(new ClaudeSessionRunner(), options)
    {
    }

    /// <summary>Creates a client over a custom runner (for tests, or a DI-provided session).</summary>
    /// <param name="runner">Runs CLI invocations.</param>
    /// <param name="options">Client configuration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runner"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid (for example an empty working directory).</exception>
    public ClaudeCodeChatClient(IClaudeRunner runner, ClaudeCodeChatClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        Runner = runner;
        _options = options;
        _metadata = new ChatClientMetadata(ProviderName, providerUri: null, defaultModelId: options.DefaultModel);
    }

    /// <summary>The runner every call goes through.</summary>
    internal IClaudeRunner Runner { get; }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (prompt, sessionOptions) = Prepare(messages, options, streaming: false);

        var assistantText = new StringBuilder();
        var resultEventJson = new List<string?>();
        var result = await Runner.RunAsync(
            prompt,
            sessionOptions,
            evt =>
            {
                if (evt.Kind == ClaudeStreamEventKind.Result)
                {
                    resultEventJson.Add(evt.RawJson);
                }
                else if (evt.Kind == ClaudeStreamEventKind.AssistantText && !string.IsNullOrEmpty(evt.Text))
                {
                    if (assistantText.Length > 0)
                    {
                        assistantText.Append(BlockSeparator);
                    }

                    assistantText.Append(evt.Text);
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (ClaudeCodeChatException.IsFailure(result))
        {
            throw ClaudeCodeChatException.FromResult(result);
        }

        var text = string.IsNullOrEmpty(result.ResultText) ? assistantText.ToString() : result.ResultText;
        var conversationId = string.IsNullOrEmpty(result.SessionId) ? null : result.SessionId;

        var message = new ChatMessage(ChatRole.Assistant, text)
        {
            RawRepresentation = result,
        };

        var response = new ChatResponse(message)
        {
            ConversationId = conversationId,
            ModelId = NullIfBlank(sessionOptions.Model),
            CreatedAt = DateTimeOffset.UtcNow,
            FinishReason = ChatFinishReason.Stop,
            RawRepresentation = result,
            Usage = UsageMapper.ParseUsage(resultEventJson),
        };

        var properties = new AdditionalPropertiesDictionary();
        UsageMapper.AddStats(properties, result);
        if (result.RateLimitWaits > 0)
        {
            properties[RateLimitWaitsKey] = result.RateLimitWaits;
        }

        if (properties.Count > 0)
        {
            response.AdditionalProperties = properties;
        }

        return response;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs the CLI with partial messages on and yields one update per text delta. A complete
    /// text block that was already streamed as deltas is not repeated. A block that arrives
    /// without deltas is yielded whole. Blocks after the first are prefixed with a blank line, so
    /// the concatenated text matches <see cref="GetResponseAsync"/>. The last update has no text.
    /// It carries <see cref="ChatResponseUpdate.ConversationId"/>, <see cref="ChatResponseUpdate.ModelId"/>,
    /// <see cref="ChatFinishReason.Stop"/>, any reported usage (as <see cref="UsageContent"/>) and the
    /// <c>claude.*</c> additional properties. If the session ended in an error, or the CLI exited
    /// without a result, the updates received so far are yielded and then
    /// <see cref="ClaudeCodeChatException"/> is thrown (with
    /// <see cref="ClaudeCodeChatException.UnknownExitCode"/>, since streaming can't observe it).
    /// Message validation happens immediately, before enumeration starts.
    /// <para>
    /// The CLI's own tool use shows up as text-free updates carrying <see cref="ToolNameKey"/> and
    /// <see cref="ToolInputKey"/>. Tool results are not surfaced. Rate-limit waits show up as
    /// text-free updates carrying <see cref="RateLimitWaitKey"/> and <see cref="ResetsAtKey"/>.
    /// Consumers that only read text are unaffected.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (prompt, sessionOptions) = Prepare(messages, options, streaming: true);
        return StreamCoreAsync(prompt, sessionOptions, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamCoreAsync(
        string prompt,
        ClaudeSessionOptions sessionOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var modelId = NullIfBlank(sessionOptions.Model);
        var responseId = Guid.NewGuid().ToString("N");
        var messageId = Guid.NewGuid().ToString("N");

        string? sessionId = null;
        var anyTextEmitted = false;
        // Text streamed as deltas for the current block; null when the block has had none
        StringBuilder? blockDeltas = null;

        ClaudeStreamEvent? lastResult = null;
        ClaudeStreamEvent? lastError = null;
        var resultJson = new List<string?>();
        double costUsd = 0;
        int numTurns = 0;
        long durationMs = 0;
        var rateLimitWaits = 0;
        var rateLimitDetected = false;
        DateTimeOffset? resetsAt = null;

        await foreach (var evt in Runner.StreamAsync(prompt, sessionOptions, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(evt.SessionId))
            {
                sessionId = evt.SessionId;
            }

            rateLimitDetected |= evt.IsRateLimited;
            if (evt.ResetsAt is not null)
            {
                resetsAt = evt.ResetsAt;
            }

            ChatResponseUpdate? update = null;
            switch (evt.Kind)
            {
                case ClaudeStreamEventKind.AssistantTextDelta when !string.IsNullOrEmpty(evt.Text):
                    update = TextUpdate(BlockText(evt.Text!, startsBlock: blockDeltas is null), evt);
                    (blockDeltas ??= new StringBuilder()).Append(evt.Text);
                    break;

                case ClaudeStreamEventKind.AssistantText:
                    if (!string.IsNullOrEmpty(evt.Text))
                    {
                        if (blockDeltas is null)
                        {
                            update = TextUpdate(BlockText(evt.Text!, startsBlock: true), evt);
                        }
                        else
                        {
                            // Deltas normally cover the whole block; emit any tail they missed
                            var streamed = blockDeltas.ToString();
                            if (evt.Text!.Length > streamed.Length && evt.Text.StartsWith(streamed, StringComparison.Ordinal))
                            {
                                update = TextUpdate(evt.Text[streamed.Length..], evt);
                            }
                        }
                    }

                    blockDeltas = null;
                    break;

                case ClaudeStreamEventKind.ToolUse:
                    update = ActivityUpdate(evt, new AdditionalPropertiesDictionary
                    {
                        [ToolNameKey] = evt.ToolName,
                        [ToolInputKey] = evt.ToolInput,
                    });
                    break;

                case ClaudeStreamEventKind.Result:
                    lastResult = evt;
                    resultJson.Add(evt.RawJson);
                    costUsd += evt.CostUsd;
                    numTurns += evt.NumTurns;
                    durationMs += evt.DurationMs;
                    blockDeltas = null;
                    break;

                case ClaudeStreamEventKind.Error:
                    lastError = evt;
                    break;

                case ClaudeStreamEventKind.RateLimitWait:
                    // The failed attempt is over; only the resumed attempt decides the outcome
                    rateLimitWaits++;
                    rateLimitDetected = true;
                    lastResult = null;
                    lastError = null;
                    blockDeltas = null;
                    update = ActivityUpdate(evt, new AdditionalPropertiesDictionary
                    {
                        [RateLimitWaitKey] = evt.Text,
                        [ResetsAtKey] = evt.ResetsAt,
                    });
                    break;

                // ToolResult, SystemInit, System, RateLimit, Progress, PlainText and Unknown
                // are not surfaced as updates.
            }

            if (update is not null)
            {
                yield return update;
            }
        }

        // No Result means the CLI died before finishing; the exit code isn't observable
        // through StreamAsync, so that counts as a failure rather than an empty success
        if (lastResult is null || lastResult.IsError)
        {
            throw ClaudeCodeChatException.FromResult(new ClaudeSessionResult
            {
                ExitCode = ClaudeCodeChatException.UnknownExitCode,
                SessionId = sessionId,
                IsError = true,
                ResultText = NullIfBlank(lastResult?.Text) ?? NullIfBlank(lastError?.Text),
                RateLimitDetected = rateLimitDetected,
                RateLimitResetsAt = resetsAt,
            });
        }

        var final = new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            ResponseId = responseId,
            MessageId = messageId,
            ConversationId = sessionId,
            ModelId = modelId,
            CreatedAt = DateTimeOffset.UtcNow,
            FinishReason = ChatFinishReason.Stop,
            RawRepresentation = lastResult,
        };

        if (UsageMapper.ParseUsage(resultJson) is { } usage)
        {
            final.Contents.Add(new UsageContent(usage));
        }

        var properties = new AdditionalPropertiesDictionary();
        UsageMapper.AddStats(properties, costUsd, numTurns, durationMs);
        if (rateLimitWaits > 0)
        {
            properties[RateLimitWaitsKey] = rateLimitWaits;
        }

        if (properties.Count > 0)
        {
            final.AdditionalProperties = properties;
        }

        yield return final;

        string BlockText(string value, bool startsBlock)
            => startsBlock && anyTextEmitted ? BlockSeparator + value : value;

        ChatResponseUpdate TextUpdate(string text, ClaudeStreamEvent evt)
        {
            anyTextEmitted = true;
            var update = ActivityUpdate(evt, properties: null);
            update.Contents.Add(new TextContent(text));
            return update;
        }

        ChatResponseUpdate ActivityUpdate(ClaudeStreamEvent evt, AdditionalPropertiesDictionary? properties) => new()
        {
            Role = ChatRole.Assistant,
            ResponseId = responseId,
            MessageId = messageId,
            ConversationId = sessionId,
            ModelId = modelId,
            CreatedAt = DateTimeOffset.UtcNow,
            AdditionalProperties = properties,
            RawRepresentation = evt,
        };
    }

    /// <summary>
    /// Returns, when <paramref name="serviceKey"/> is null:
    /// a <see cref="ChatClientMetadata"/> (provider <see cref="ProviderName"/>, the current
    /// <see cref="ClaudeCodeChatClientOptions.DefaultModel"/>) for <see cref="ChatClientMetadata"/>;
    /// this client for any type it is an instance of (<see cref="IChatClient"/>,
    /// <see cref="ClaudeCodeChatClient"/>, ...); the underlying runner for <see cref="IClaudeRunner"/>
    /// or the runner's concrete type. Otherwise returns null.
    /// </summary>
    /// <param name="serviceType">The requested service type.</param>
    /// <param name="serviceKey">Must be null; keyed lookups are not supported.</param>
    /// <returns>The service, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serviceType"/> is null.</exception>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return Metadata();
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return serviceType.IsInstanceOfType(Runner) ? Runner : null;
    }

    /// <summary>
    /// Does nothing: the client holds no process or other resource between calls (each call owns
    /// its CLI process while it runs), so it can be disposed any number of times. It also keeps
    /// working after disposal.
    /// </summary>
    public void Dispose()
    {
    }

    private ChatClientMetadata Metadata()
    {
        // DefaultModel is mutable; refresh the cached metadata if it changed since construction.
        var current = _metadata;
        if (!string.Equals(current.DefaultModelId, _options.DefaultModel, StringComparison.Ordinal))
        {
            current = new ChatClientMetadata(ProviderName, providerUri: null, defaultModelId: _options.DefaultModel);
            _metadata = current;
        }

        return current;
    }

    private (string Prompt, ClaudeSessionOptions Options) Prepare(
        IEnumerable<ChatMessage> messages, ChatOptions? options, bool streaming)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Materialize once: the history may be a lazy sequence and may need a second pass.
        var history = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();

        var resuming = !string.IsNullOrWhiteSpace(options?.ConversationId);
        var baseSystemPrompt = JoinNonBlank(_options.SystemPrompt, options?.Instructions);
        var built = PromptBuilder.Build(history, baseSystemPrompt, resuming);
        var sessionOptions = SessionOptionsMapper.Map(_options, options, built.SystemPrompt, streaming);

        // ConfigureSession has the last word on ResumeSessionId. If it changed whether this call
        // resumes, rebuild the prompt (the system prompt doesn't depend on resuming).
        var actuallyResuming = !string.IsNullOrWhiteSpace(sessionOptions.ResumeSessionId);
        if (actuallyResuming != resuming)
        {
            built = PromptBuilder.Build(history, baseSystemPrompt, actuallyResuming);
        }

        return (built.Prompt, sessionOptions);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? JoinNonBlank(string? first, string? second)
        => NullIfBlank(first) is { } a
            ? NullIfBlank(second) is { } b ? a + BlockSeparator + b : a
            : NullIfBlank(second);
}

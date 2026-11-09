using System.Text;
using Microsoft.Extensions.AI;

namespace ClaudeCode.Extensions.AI;

/// <summary>The CLI prompt and appended system prompt built from a chat history.</summary>
/// <param name="Prompt">The prompt passed to the CLI.</param>
/// <param name="SystemPrompt">Text for <c>--append-system-prompt</c>, or null when there is none.</param>
internal sealed record BuiltPrompt(string Prompt, string? SystemPrompt);

/// <summary>
/// Flattens a <see cref="ChatMessage"/> list into the single prompt (plus optional system
/// prompt) a Claude CLI invocation takes.
/// </summary>
internal static class PromptBuilder
{
    internal const string TranscriptPreamble =
        "The following is the conversation so far between the user and you (the assistant). " +
        "Continue it by replying to the final user message.";

    private const string Separator = "\n\n";

    /// <summary>Builds the prompt.</summary>
    /// <param name="messages">The chat history; the last non-system message must be from the user.</param>
    /// <param name="baseSystemPrompt">Configured system prompt (from the client options), placed first.</param>
    /// <param name="resuming">
    /// True when the call resumes an existing CLI session, which already holds the earlier turns.
    /// No transcript is replayed then: the prompt is just the messages after the last assistant
    /// message (normally the single new user message), joined with blank lines. The whole list is
    /// still validated, and system messages still go into the system prompt.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The list is empty, contains null, has only system messages, does not end with a user
    /// message, or the final user message has no text.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A message that is sent contains content other than <see cref="TextContent"/>. When
    /// resuming, turns the session already holds are not sent, and other content there is skipped.
    /// </exception>
    public static BuiltPrompt Build(IEnumerable<ChatMessage> messages, string? baseSystemPrompt = null, bool resuming = false)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var systemParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseSystemPrompt))
        {
            systemParts.Add(baseSystemPrompt!);
        }

        var list = messages.ToList();
        if (list.Any(m => m is null))
        {
            throw new ArgumentException("The message list must not contain null entries.", nameof(messages));
        }

        // When resuming, the CLI session already holds every turn up to the last assistant
        // message; those are never sent, so content this client can't send (reasoning, tool
        // calls from other providers or middleware) is skipped there rather than rejected.
        var unsentThrough = resuming ? list.FindLastIndex(m => m.Role == ChatRole.Assistant) : -1;

        var turns = new List<(ChatRole Role, string Text)>();
        for (var i = 0; i < list.Count; i++)
        {
            var message = list[i];
            var text = ExtractText(message, skipNonText: i <= unsentThrough && message.Role != ChatRole.System);
            if (message.Role == ChatRole.System)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    systemParts.Add(text);
                }
            }
            else
            {
                turns.Add((message.Role, text));
            }
        }

        if (turns.Count == 0)
        {
            throw new ArgumentException(
                "At least one non-system message is required to build a prompt.", nameof(messages));
        }

        var final = turns[^1];
        if (final.Role != ChatRole.User)
        {
            throw new ArgumentException(
                $"The last non-system message must have the user role, but was '{final.Role}'.", nameof(messages));
        }

        if (string.IsNullOrWhiteSpace(final.Text))
        {
            throw new ArgumentException("The final user message must contain text.", nameof(messages));
        }

        var systemPrompt = systemParts.Count == 0 ? null : string.Join(Separator, systemParts);

        if (turns.Count == 1)
        {
            return new BuiltPrompt(final.Text, systemPrompt);
        }

        if (resuming)
        {
            var lastAssistant = turns.FindLastIndex(t => t.Role == ChatRole.Assistant);
            var newTexts = turns.Skip(lastAssistant + 1)
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t));
            return new BuiltPrompt(string.Join(Separator, newTexts), systemPrompt);
        }

        var sb = new StringBuilder(TranscriptPreamble);
        foreach (var (role, text) in turns)
        {
            sb.Append(Separator).Append(RoleLabel(role)).Append(": ").Append(text);
        }

        return new BuiltPrompt(sb.ToString(), systemPrompt);
    }

    private static string ExtractText(ChatMessage message, bool skipNonText)
    {
        var contents = skipNonText
            ? message.Contents.Where(c => c is TextContent).ToList()
            : message.Contents;
        if (contents.Count == 1)
        {
            return AsText(contents[0]);
        }

        var sb = new StringBuilder();
        foreach (var content in contents)
        {
            sb.Append(AsText(content));
        }

        return sb.ToString();
    }

    private static string AsText(AIContent content) => content switch
    {
        TextContent text => text.Text ?? string.Empty,
        null => throw new NotSupportedException("Null message content is not supported."),
        _ => throw new NotSupportedException(
            $"Content of type '{content.GetType().Name}' is not supported by the Claude Code chat client; only TextContent is."),
    };

    private static string RoleLabel(ChatRole role)
    {
        if (role == ChatRole.User)
        {
            return "User";
        }

        if (role == ChatRole.Assistant)
        {
            return "Assistant";
        }

        var value = role.Value;
        return value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value.Substring(1);
    }
}

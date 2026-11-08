using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ClaudeCode.Extensions.AI;

/// <summary>
/// Turns what the CLI reports (result stats plus the <c>usage</c> object on result events) into
/// <see cref="UsageDetails"/> and CLI-specific additional properties. Never invents values: a
/// figure the CLI did not report stays absent.
/// </summary>
internal static class UsageMapper
{
    /// <summary>AdditionalCounts key for tokens written to the prompt cache.</summary>
    public const string CacheCreationInputTokensKey = "claude.cache_creation_input_tokens";

    /// <summary>
    /// Sums the <c>usage</c> objects of the given result-event JSON payloads (more than one when
    /// rate-limit waits resumed the session). Returns null when none reported usage. Malformed
    /// JSON is skipped.
    /// </summary>
    public static UsageDetails? ParseUsage(IEnumerable<string?> resultEventJson)
    {
        long? input = null, output = null, cacheRead = null, cacheCreation = null;

        foreach (var json in resultEventJson)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(json!);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("usage", out var usage)
                    || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                input = Add(input, usage, "input_tokens");
                output = Add(output, usage, "output_tokens");
                cacheRead = Add(cacheRead, usage, "cache_read_input_tokens");
                cacheCreation = Add(cacheCreation, usage, "cache_creation_input_tokens");
            }
            catch (JsonException)
            {
                // Not ours to fail on: usage is best-effort.
            }
        }

        if (input is null && output is null && cacheRead is null && cacheCreation is null)
        {
            return null;
        }

        // MEAI counts cached tokens as part of InputTokenCount; Anthropic reports them separately.
        long? totalInput = input is null && cacheRead is null && cacheCreation is null
            ? null
            : (input ?? 0) + (cacheRead ?? 0) + (cacheCreation ?? 0);

        var details = new UsageDetails
        {
            InputTokenCount = totalInput,
            OutputTokenCount = output,
            CachedInputTokenCount = cacheRead,
            TotalTokenCount = totalInput is not null && output is not null ? totalInput + output : null,
        };

        if (cacheCreation is not null)
        {
            details.AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                [CacheCreationInputTokensKey] = cacheCreation.Value,
            };
        }

        return details;
    }

    /// <summary>
    /// Adds <c>claude.cost_usd</c>, <c>claude.num_turns</c> and <c>claude.duration_ms</c> for the values
    /// the result actually carries. Turns and duration are added when positive. Cost is added
    /// when positive, or when the result carries other stats (a reported cost of 0 is real on
    /// subscription plans).
    /// </summary>
    public static void AddStats(AdditionalPropertiesDictionary properties, ClaudeSessionResult result)
        => AddStats(properties, result.CostUsd, result.NumTurns, (long)result.Duration.TotalMilliseconds);

    /// <summary>Same as the result overload, for stats accumulated from stream events.</summary>
    public static void AddStats(AdditionalPropertiesDictionary properties, double costUsd, int numTurns, long durationMs)
    {
        var hasStats = numTurns > 0 || durationMs > 0;

        if (costUsd > 0 || hasStats)
        {
            properties[ClaudeCodeChatClient.CostUsdKey] = costUsd;
        }

        if (numTurns > 0)
        {
            properties[ClaudeCodeChatClient.NumTurnsKey] = numTurns;
        }

        if (durationMs > 0)
        {
            properties[ClaudeCodeChatClient.DurationMsKey] = durationMs;
        }
    }

    private static long? Add(long? current, JsonElement usage, string name)
    {
        if (usage.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var count))
        {
            return (current ?? 0) + count;
        }

        return current;
    }
}

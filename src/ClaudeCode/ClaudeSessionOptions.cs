using System.Globalization;

namespace ClaudeCode;

/// <summary>
/// Options for a single Claude CLI invocation.
/// </summary>
public sealed record ClaudeSessionOptions
{
    /// <summary>Directory the CLI runs in. Claude reads .mcp.json, CLAUDE.md, etc. from here.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Model to pass via --model. Null uses the CLI's default.</summary>
    public string? Model { get; init; }

    public int MaxTurns { get; init; } = 1000;

    /// <summary>Appended to Claude's default system prompt via --append-system-prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Display name for the session (-n).</summary>
    public string? SessionName { get; init; }

    /// <summary>Session ID to resume via --resume (session chaining).</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>
    /// Bypass permission prompts (--dangerously-skip-permissions). Required for
    /// unattended tool use under --print; without it, Bash/Write calls fail with
    /// permission errors. Leave true only when you trust the prompt and workspace.
    /// </summary>
    public bool SkipPermissions { get; init; } = true;

    /// <summary>Extra environment variables for the CLI process.</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>
    /// Builds the CLI argument vector for this invocation. Arguments are individual
    /// items intended for ProcessStartInfo.ArgumentList, which performs correct
    /// platform quoting - no manual escaping, and prompts keep their newlines.
    /// </summary>
    public IReadOnlyList<string> BuildArguments(string prompt)
    {
        var args = new List<string>();

        if (!string.IsNullOrEmpty(ResumeSessionId))
        {
            args.Add("--resume");
            args.Add(ResumeSessionId);
        }

        // Non-interactive mode with machine-readable output.
        // Note: stream-json requires --verbose when using --print.
        args.Add("--print");
        args.Add("--verbose");
        args.Add("--output-format");
        args.Add("stream-json");

        if (!string.IsNullOrEmpty(SessionName))
        {
            args.Add("-n");
            args.Add(SessionName);
        }

        if (SkipPermissions)
        {
            args.Add("--dangerously-skip-permissions");
        }

        if (!string.IsNullOrEmpty(Model))
        {
            args.Add("--model");
            args.Add(Model);
        }

        args.Add("--max-turns");
        args.Add(MaxTurns.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(SystemPrompt))
        {
            args.Add("--append-system-prompt");
            args.Add(SystemPrompt);
        }

        args.Add(prompt);

        return args;
    }
}

/// <summary>
/// Outcome of a completed Claude CLI invocation.
/// </summary>
public sealed record ClaudeSessionResult
{
    public required int ExitCode { get; init; }

    /// <summary>Session ID reported by the CLI, for chaining via ResumeSessionId.</summary>
    public string? SessionId { get; init; }

    /// <summary>True if a rate limit was detected in the stream or stderr.</summary>
    public bool RateLimitDetected { get; init; }

    /// <summary>Final result text from the CLI's result event, if any.</summary>
    public string? ResultText { get; init; }

    /// <summary>True if the CLI reported the session ended in an error.</summary>
    public bool IsError { get; init; }

    /// <summary>Number of API turns, from the result event.</summary>
    public int NumTurns { get; init; }

    /// <summary>Total session cost in USD, from the result event (0 on subscription plans).</summary>
    public double CostUsd { get; init; }

    /// <summary>Session duration reported by the CLI.</summary>
    public TimeSpan Duration { get; init; }
}

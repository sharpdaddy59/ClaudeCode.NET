using ClaudeCode;

// Minimal end-to-end example: stream a session's events to the console.
// Requires the Claude Code CLI installed and authenticated (claude login).

var prompt = args.Length > 0
    ? string.Join(' ', args)
    : "In one short sentence, say hello from ClaudeCode.NET.";

var options = new ClaudeSessionOptions
{
    WorkingDirectory = Environment.CurrentDirectory,
    Model = Environment.GetEnvironmentVariable("CLAUDE_AGENT_MODEL"), // null = CLI default
    MaxTurns = 5
};

var session = new ClaudeSession();

await foreach (var evt in session.StreamAsync(prompt, options))
{
    switch (evt.Kind)
    {
        case ClaudeStreamEventKind.SystemInit:
            Console.WriteLine($"[session {evt.SessionId}]");
            break;
        case ClaudeStreamEventKind.AssistantText:
            Console.WriteLine(evt.Text);
            break;
        case ClaudeStreamEventKind.ToolUse:
            Console.WriteLine($"  -> tool: {evt.ToolName}");
            break;
        case ClaudeStreamEventKind.Result:
            Console.WriteLine($"[done: {evt.NumTurns} turns, {evt.DurationMs} ms, ${evt.CostUsd:F4}]");
            break;
        case ClaudeStreamEventKind.Error:
            Console.Error.WriteLine($"[error] {evt.Text}");
            break;
    }
}

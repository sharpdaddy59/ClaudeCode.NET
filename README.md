# ClaudeCode.NET

**Drive Claude Code sessions from .NET.**

The Claude Agent SDK ships for TypeScript and Python. .NET developers who want to orchestrate [Claude Code](https://code.claude.com/docs/en/overview) sessions end up hand-rolling the same plumbing every time: find the CLI, spawn the process, escape the arguments, pump `stream-json` output, parse events, capture the session ID for resume. This library is that plumbing, done once and tested.

- **Typed event stream** — `IAsyncEnumerable<ClaudeStreamEvent>` over the CLI's stream-json output: assistant text, tool calls, tool results, session stats, rate-limit signals
- **Safe argument handling** — prompts pass through `ProcessStartInfo.ArgumentList`, so newlines, quotes, and backslashes arrive verbatim (no shell-escaping bugs)
- **Session chaining** — capture `SessionId` from one run, pass it as `ResumeSessionId` to the next
- **Clean cancellation** — cancelling the token kills the entire CLI process tree; no orphaned `claude` processes
- **Max plan friendly** — uses the CLI's `claude login` auth; no API key required
- **No logging opinion** — takes an optional `Microsoft.Extensions.Logging.ILogger`; silent by default

Parsing is verified against real CLI output, not guessed from docs.

## Requirements

- .NET 8.0 or later
- Install the [Claude Code CLI](https://code.claude.com/docs/en/quickstart) (native installer, auto-updates):

  ```powershell
  # Windows (PowerShell)
  irm https://claude.ai/install.ps1 | iex
  ```

  ```bash
  # macOS / Linux / WSL
  curl -fsSL https://claude.ai/install.sh | bash
  ```

  Then authenticate: run `claude` and follow the login prompts.

## Quick start

```csharp
using ClaudeCode;

var session = new ClaudeSession();
var options = new ClaudeSessionOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "claude-sonnet-5",   // optional; omit for the CLI default
    MaxTurns = 50
};

// Streaming: react to events as they arrive
await foreach (var evt in session.StreamAsync("Fix the failing test in FooTests", options))
{
    switch (evt.Kind)
    {
        case ClaudeStreamEventKind.AssistantText: Console.WriteLine(evt.Text); break;
        case ClaudeStreamEventKind.ToolUse:       Console.WriteLine($"-> {evt.ToolName}"); break;
        case ClaudeStreamEventKind.Result:        Console.WriteLine($"done in {evt.NumTurns} turns"); break;
    }
}
```

```csharp
// Or aggregate: one call, one result
var result = await session.RunAsync("Summarize this repo's architecture", options);
Console.WriteLine(result.AssistantText);
Console.WriteLine($"session {result.SessionId}, {result.NumTurns} turns");
```

### Session chaining

Each `--print` invocation is a fresh session, but you can resume the previous one so Claude keeps its context:

```csharp
var first = await session.RunAsync("Implement feature A", options);

var chained = options with { ResumeSessionId = first.SessionId };
var second = await session.RunAsync("Now implement feature B", chained);
```

### Rate limits

`ClaudeRunResult.RateLimitDetected` is true when the CLI reports a rate limit (via its
`rate_limit_event` stream event or recognizable error text) — poll-and-retry policies
are yours to choose; the library just gives you a reliable signal.

### Unattended tool use

`SkipPermissions` defaults to `true` (`--dangerously-skip-permissions`), because non-interactive
sessions can't answer permission prompts — without it, any Bash/Write tool call fails.
Only run unattended sessions in a workspace you trust Claude to modify.

## Sample

```bash
cd samples/HelloAgent
dotnet run -- "What files are in this directory?"
```

## Status

Early (0.1.0). Extracted from SharpCoder (a private project — an AI application generator), where this code drives multi-hour autonomous build sessions. API may move before 1.0.

## License

MIT

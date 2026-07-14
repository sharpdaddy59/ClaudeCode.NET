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

## Authentication & Anthropic's terms

**ClaudeCode.Net never handles credentials.** The library contains no authentication
code and makes no API calls — you can audit this in about a minute: there is no
`HttpClient`, no token handling, and no reference to Anthropic's API anywhere in the
source. Its entire integration surface is spawning the **official Claude Code CLI**
as a child process and parsing its output stream.

That means authentication belongs to the CLI, exactly as if you ran it yourself in a
terminal, and you have two options:

- **Claude subscription** (Pro/Max): run `claude` once and log in. Sessions started
  by this library then run under your subscription, within its rate limits.
- **API key** (metered billing): set `ANTHROPIC_API_KEY` in the environment of the
  process you spawn. Identical library code; pay-per-token billing.

**Why this architecture matters for compliance:** Anthropic's terms prohibit taking
OAuth tokens from consumer accounts into other products — the prohibited pattern is
*extracting your credential and calling the API from a different client*. This library
does the opposite: every request originates from Anthropic's own client, authenticated
by its own login flow. This is the same architecture as Anthropic's official Claude
Agent SDK, which likewise wraps the CLI.

**A caveat about the future:** Anthropic has signaled (announced May 2026, then
postponed) that programmatic/SDK-style usage on subscriptions may eventually be billed
from a separate credit pool than interactive use. If that lands, subscription users of
this library may need to budget differently or switch to an API key — a billing
change, not a compatibility one; the code path is identical either way. Check
[Anthropic's consumer terms](https://www.anthropic.com/legal/consumer-terms) and the
[Claude Code docs](https://code.claude.com/docs) for current policy; this README
describes the situation as of July 2026 and is not legal advice.

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

## Cookbook

**Commit messages from your staged diff** (pure text generation - one turn, no tools):

```csharp
var diff = /* output of: git diff --cached */;
var result = await session.RunAsync(
    $"Write a conventional commit message for this diff. Reply with ONLY the message.\n\n{diff}",
    new ClaudeSessionOptions { WorkingDirectory = repoDir, MaxTurns = 1, SkipPermissions = false });
Console.WriteLine(result.ResultText);
```

**Live progress display** - render what the agent is doing as it works:

```csharp
await foreach (var evt in session.StreamAsync(prompt, options))
{
    var line = evt.Kind switch
    {
        ClaudeStreamEventKind.ToolUse       => $"  [tool] {evt.ToolName}",
        ClaudeStreamEventKind.AssistantText => $"  {evt.Text}",
        ClaudeStreamEventKind.Result        => $"  [done: {evt.NumTurns} turns]",
        _ => null
    };
    if (line is not null) Console.WriteLine(line);
}
```

**Unattended runs: check the result honestly.** A session can "complete" while having
failed - always look at the whole outcome, not just the text:

```csharp
var result = await session.RunAsync(prompt, options);

if (result.RateLimitDetected && result.IsError)
    /* back off and retry later */ ;
else if (result.IsError || result.ExitCode != 0)
    logger.LogError("Session failed: {Text}", result.ResultText);
else
    /* trust - then verify: run your tests before accepting the changes */ ;
```

**Timeouts** - cancellation kills the entire CLI process tree, no orphans:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
var result = await session.RunAsync(prompt, options, cts.Token);
```

## Samples

Runnable projects in [`samples/`](https://github.com/sharpdaddy59/ClaudeCode.NET/tree/main/samples):

- [`CommitMessageBot`](https://github.com/sharpdaddy59/ClaudeCode.NET/tree/main/samples/CommitMessageBot) - staged diff in, conventional commit message out (~40 lines)
- [`ChainedRefactor`](https://github.com/sharpdaddy59/ClaudeCode.NET/tree/main/samples/ChainedRefactor) - two sessions where the second *resumes* the first and provably builds on its context
- [`HelloAgent`](https://github.com/sharpdaddy59/ClaudeCode.NET/tree/main/samples/HelloAgent) - minimal event-stream walkthrough

## Status

Early (0.1.0). Extracted from SharpCoder (a private project — an AI application generator), where this code drives multi-hour autonomous build sessions. API may move before 1.0.

## License

MIT

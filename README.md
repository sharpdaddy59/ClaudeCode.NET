# ClaudeCode.NET

[![NuGet](https://img.shields.io/nuget/v/ClaudeCode.Net.svg?logo=nuget&label=ClaudeCode.Net)](https://www.nuget.org/packages/ClaudeCode.Net)
[![NuGet](https://img.shields.io/nuget/v/ClaudeCode.Net.Extensions.AI.svg?logo=nuget&label=ClaudeCode.Net.Extensions.AI)](https://www.nuget.org/packages/ClaudeCode.Net.Extensions.AI)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/sharpdaddy59/ClaudeCode.NET/blob/main/LICENSE)

**Drive Claude Code sessions from .NET.**

The Claude Agent SDK ships for TypeScript and Python. .NET developers who want to orchestrate [Claude Code](https://code.claude.com/docs/en/overview) sessions end up hand-rolling the same plumbing every time: find the CLI, spawn the process, escape the arguments, pump `stream-json` output, parse events, capture the session ID for resume. This library is that plumbing, done once and tested.

- **Typed event stream** — `IAsyncEnumerable<ClaudeStreamEvent>` over the CLI's stream-json output: assistant text, tool calls, tool results, session stats, rate-limit signals
- **Safe argument handling** — prompts pass through `ProcessStartInfo.ArgumentList`, so newlines, quotes, and backslashes arrive verbatim (no shell-escaping bugs)
- **Session chaining** — capture `SessionId` from one run, pass it as `ResumeSessionId` to the next
- **Clean cancellation** — cancelling the token kills the entire CLI process tree; no orphaned `claude` processes
- **Rate limits handled** — a reliable signal by default, or opt in to wait for the window and resume the same session
- **Max plan friendly** — uses the CLI's `claude login` auth; no API key required
- **No logging opinion** — takes an optional `Microsoft.Extensions.Logging.ILogger`; silent by default
- **`IChatClient` too** — a separate adapter package plugs Claude Code into `Microsoft.Extensions.AI` ([below](#using-it-through-ichatclient))

## Design goals

ClaudeCode.Net is deliberately small. Three public types, about a thousand lines, and
only the `Microsoft.Extensions.*.Abstractions` packages every ASP.NET app already carries
(logging and dependency injection). You can read the whole thing in a sitting, which is
the point: it is the piece of your system you should never have to debug.

- **Small.** It spawns the official CLI and parses its output. Nothing else. No HTTP
  client, no credential handling, no protocol to keep in sync with the CLI's internals.
- **Subscription-first.** Built for people running Claude Code under a Pro/Max plan with
  `claude login`. API keys work too, but the subscription path is the one that gets
  tested every day.
- **Reliable under long unattended runs.** Extracted from a production system that drives
  multi-hour autonomous build sessions. The Windows command-line cap, process-tree
  cleanup on cancel, and rate-limit detection all came from real failures.

**What it is not.** It does not implement Claude Code's hooks, in-process MCP servers,
subagent definitions, or the bidirectional control protocol. Those are the parts of the
CLI's surface that change most often, and keeping them current is a project in itself.
Several community SDKs port Anthropic's Python Agent SDK to .NET and cover that ground;
[search nuget.org for "claude agent sdk"](https://www.nuget.org/packages?q=claude+agent+sdk)
if you need it. If you need to run sessions, stream what they do, chain them, and get a
trustworthy result, this library is enough.

Parsing is verified against real CLI output, not guessed from docs.

## Install

```bash
dotnet add package ClaudeCode.Net
```

Package: [nuget.org/packages/ClaudeCode.Net](https://www.nuget.org/packages/ClaudeCode.Net) · Changelog: [GitHub Releases](https://github.com/sharpdaddy59/ClaudeCode.NET/releases)

## Using it through `IChatClient`

A second package, **ClaudeCode.Net.Extensions.AI**, wraps the core library as a
[`Microsoft.Extensions.AI`](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai)
`IChatClient`. That is the standard .NET chat abstraction, so code written against it can
talk to Claude Code the same way it talks to any other model provider.

```bash
dotnet add package ClaudeCode.Net.Extensions.AI
```

```csharp
using ClaudeCode.Extensions.AI;
using Microsoft.Extensions.AI;

IChatClient chat = new ClaudeCodeChatClient(new ClaudeCodeChatClientOptions
{
    WorkingDirectory = @"C:\src\my-repo",
});

ChatResponse response = await chat.GetResponseAsync("Why does the build fail?");
Console.WriteLine(response.Text);
```

**Which package do you want?**

- **`ClaudeCode.Net`** when you are orchestrating Claude Code itself: unattended runs,
  chaining sessions, watching every tool call and result, exit codes, rate-limit handling.
  You get the CLI's full event stream with nothing abstracted away.
- **`ClaudeCode.Net.Extensions.AI`** when your app is already built on `IChatClient`, or you
  want to be able to swap providers: chat UIs, `IChatClient` middleware (logging, caching,
  OpenTelemetry), or libraries that accept an `IChatClient`. It supports streaming,
  multi-turn conversations (`ConversationId` resumes the CLI session), usage and cost,
  and dependency injection via `AddClaudeCodeChatClient`.

The adapter depends on the core package, and both always ship with the same version.
Claude Code runs its own tools, so `IChatClient` function calling (`ChatOptions.Tools`)
does not apply. The [adapter README](https://github.com/sharpdaddy59/ClaudeCode.NET/tree/main/src/ClaudeCode.Extensions.AI)
covers that, its options, and errors.

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
`rate_limit_event` stream event or recognizable error text), and `RateLimitResetsAt` is
the window's reset time when the CLI gave one. By default the run returns and the
decision is yours.

For unattended work on a subscription, opt in to waiting it out instead:

```csharp
var options = new ClaudeSessionOptions
{
    WorkingDirectory = repoDir,
    RateLimitWait = RateLimitWaitPolicy.Default   // or new RateLimitWaitPolicy { MaxWaits = 5, ... }
};

var result = await session.RunAsync(prompt, options);
Console.WriteLine($"finished after {result.RateLimitWaits} rate-limit wait(s), {result.NumTurns} turns");
```

When a run ends because of the limit, the session waits until the CLI's reported reset
time (plus a small pad), then **resumes the same session** with a short continuation
prompt, so no context is lost. Stream consumers see a `RateLimitWait` event with the
wait and reset time before the pause. `NumTurns`, `CostUsd` and `Duration` are summed
across attempts.

The defaults are tuned for the five-hour window and refuse the seven-day one: at most
3 waits, never a single wait over 6 hours (the run returns rate-limited instead), and a
15-minute fallback when the CLI reported the limit without a reset time. A rate-limit
heartbeat the CLI recovered from on its own never triggers a wait; only a run the
limit actually ended does.

### Unattended tool use

`SkipPermissions` defaults to `true` (`--dangerously-skip-permissions`), because non-interactive
sessions can't answer permission prompts — without it, any Bash/Write tool call fails.
Only run unattended sessions in a workspace you trust Claude to modify.

### Long prompts (stdin delivery)

Windows caps a process command line at ~32k characters, so a large prompt — a replayed
conversation, an embedded document — fails to launch when passed as an argument
("The filename or extension is too long"). Since 0.2.5, prompts longer than
`ClaudeSessionOptions.StdinAutoThreshold` (8k chars) are piped to the CLI's stdin
automatically. `PromptDelivery` on the options overrides the behavior: `Auto` (default),
`Stdin` (always pipe), or `Argument` (pre-0.2.5 behavior).

### Live text (partial messages)

By default the stream carries each assistant text block once, when it is complete. For a
UI that should show text as it is generated, turn on partial messages:

```csharp
var live = options with { IncludePartialMessages = true };

await foreach (var evt in session.StreamAsync(prompt, live))
{
    if (evt.Kind == ClaudeStreamEventKind.AssistantTextDelta)
        Console.Write(evt.Text);               // fragments, as generated
    else if (evt.Kind == ClaudeStreamEventKind.AssistantText)
        Console.WriteLine();                   // the complete block follows; don't print it twice
}
```

`AssistantTextDelta` events arrive ahead of each complete `AssistantText`, so render one
or the other. `RunAsync` aggregates only the complete blocks, so its `AssistantText` is
unaffected either way. Off by default: batch callers get a quieter stream.

### Dependency injection

```csharp
builder.Services.AddClaudeCode();          // registers ClaudeSession as a singleton
```

`ClaudeSession` holds no per-run state (every call takes its own `ClaudeSessionOptions`),
so one instance serves the whole application. It logs through `ILogger<ClaudeSession>`
when logging is registered and stays silent when it isn't.

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
    /* still limited after any RateLimitWait policy gave up: back off until result.RateLimitResetsAt */ ;
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

Through the `IChatClient` adapter ([`ClaudeCode.Net.Extensions.AI`](https://github.com/sharpdaddy59/ClaudeCode.NET/tree/main/src/ClaudeCode.Extensions.AI)):

- [`ConsoleChat`](https://github.com/sharpdaddy59/ClaudeCode.NET/tree/main/samples/ConsoleChat) - streaming multi-turn chat that resumes one session
- [`PromptWorker`](https://github.com/sharpdaddy59/ClaudeCode.NET/tree/main/samples/PromptWorker) - worker service draining a folder of prompts, waiting out rate limits
- [`StreamingApi`](https://github.com/sharpdaddy59/ClaudeCode.NET/tree/main/samples/StreamingApi) - ASP.NET minimal API streaming replies as server-sent events

## Status

Stable and in production use: this code was extracted from SharpCoder (a private AI
application generator), where it drives multi-hour autonomous build sessions. The public
API has only grown additively since 0.2.0.

1.0.0 added, all additively: dependency-injection registration, partial-message
streaming for live UIs, and the opt-in rate-limit wait policy. It also introduced the
`Microsoft.Extensions.AI` `IChatClient` adapter as a separate package
(`ClaudeCode.Net.Extensions.AI`) so the core stays this small. Both packages share one
version.

## License

MIT

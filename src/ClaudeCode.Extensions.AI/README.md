# ClaudeCode.Net.Extensions.AI

[![NuGet](https://img.shields.io/nuget/v/ClaudeCode.Net.Extensions.AI.svg?logo=nuget&label=ClaudeCode.Net.Extensions.AI)](https://www.nuget.org/packages/ClaudeCode.Net.Extensions.AI)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/sharpdaddy59/ClaudeCode.NET/blob/main/LICENSE)

A [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai) `IChatClient`
for the **Claude Code CLI**, built on [ClaudeCode.Net](https://www.nuget.org/packages/ClaudeCode.Net).
Anything written against `IChatClient` (middleware, logging, `ChatClientBuilder` pipelines, your own
code) can drive Claude Code sessions. That includes Max/Pro plan authentication (`claude login`),
with no API key needed.

Each call runs one Claude Code CLI invocation in a working directory you choose. Claude Code reads
`CLAUDE.md` and `.mcp.json` from there and uses its own tools (Bash, Read, Edit, ...) while it works.

## Install

```sh
dotnet add package ClaudeCode.Net.Extensions.AI --prerelease
```

The Claude Code CLI must be installed and signed in (`claude login`).

## Quick start

```csharp
using ClaudeCode.Extensions.AI;
using Microsoft.Extensions.AI;

IChatClient chat = new ClaudeCodeChatClient(new ClaudeCodeChatClientOptions
{
    WorkingDirectory = @"C:\src\my-repo",
    DefaultModel = "sonnet",
});

ChatResponse response = await chat.GetResponseAsync("Why does the build fail?");
Console.WriteLine(response.Text);
```

## Streaming

```csharp
await foreach (ChatResponseUpdate update in chat.GetStreamingResponseAsync("Summarize README.md"))
{
    Console.Write(update.Text);
}
```

Text arrives as deltas. The last update has no text and carries `ConversationId`, `ModelId`,
`FinishReason = Stop`, usage and stats. Claude Code's own tool calls and rate-limit waits show up as
updates with no text, carrying `AdditionalProperties` (`ClaudeCodeChatClient.ToolNameKey`,
`ToolInputKey`, `RateLimitWaitKey`, `ResetsAtKey`). Code that only reads `update.Text` is unaffected.

## Dependency injection

```csharp
builder.Services
    .AddClaudeCodeChatClient(options =>
    {
        options.WorkingDirectory = "/srv/workspace";
        options.SystemPrompt = "Answer concisely.";
        options.RateLimitWait = RateLimitWaitPolicy.Default; // wait out usage limits and resume
    })
    .UseLogging();   // any ChatClientBuilder middleware

// Several clients side by side:
builder.Services.AddKeyedClaudeCodeChatClient("opus", o => { o.WorkingDirectory = "/srv/a"; o.DefaultModel = "opus"; });
```

`AddClaudeCodeChatClient` registers `ClaudeSession` (via ClaudeCode.Net's `AddClaudeCode()`) and an
`IClaudeRunner`. It only adds the runner if none is registered yet, so tests can substitute a fake
runner and never launch the CLI.

## Conversations

The response's `ConversationId` is the Claude Code session id. Pass it back to resume that session:

```csharp
var history = new List<ChatMessage> { new(ChatRole.User, "What does Program.cs do?") };
var first = await chat.GetResponseAsync(history);
history.AddMessages(first);
history.Add(new ChatMessage(ChatRole.User, "Add a --verbose flag to it."));

var second = await chat.GetResponseAsync(history, new ChatOptions { ConversationId = first.ConversationId });
```

When resuming, only the messages after the last assistant reply are sent, because the CLI session
already holds the earlier turns. Without a `ConversationId`, the whole history is replayed as a
transcript in a new session. System messages (and `ClaudeCodeChatClientOptions.SystemPrompt`) go to
the CLI's `--append-system-prompt`.

## Options

| `ClaudeCodeChatClientOptions` | |
| --- | --- |
| `WorkingDirectory` (required) | Where the CLI runs |
| `DefaultModel` | Used when `ChatOptions.ModelId` is not set |
| `SystemPrompt` | Appended to Claude Code's system prompt |
| `MaxTurns` | Agentic turn limit per call |
| `RateLimitWait` | `RateLimitWaitPolicy` to wait and resume on usage limits (default: off) |
| `ConfigureSession` | `Func<ClaudeSessionOptions, ClaudeSessionOptions>` applied last, for anything else (e.g. `SkipPermissions`, environment variables) |

Per call, `ChatOptions.ModelId` picks the model, `ChatOptions.ConversationId` resumes a session,
and `ChatOptions.Instructions` is appended to the system prompt.

## Results, usage and errors

- `ChatResponse.Usage` is filled with the token counts the CLI reported. Counts it did not report stay null.
- `AdditionalProperties` carries `claude.cost_usd`, `claude.num_turns`, `claude.duration_ms` and,
  after rate-limit waits, `claude.rate_limit_waits` (constants on `ClaudeCodeChatClient`).
- A session that ends in an error throws `ClaudeCodeChatException`, which has `ExitCode`, `SessionId`,
  `RateLimitDetected` and `RateLimitResetsAt`. When streaming, a CLI that exits without a final
  result also throws, after the updates received so far; its `ExitCode` is
  `ClaudeCodeChatException.UnknownExitCode` because streaming cannot observe it.
  `ClaudeCliNotFoundException` and cancellation propagate unchanged.

## Not supported, and why

Claude Code is an agent that **runs its own tools** inside the working directory. It is not a raw
model endpoint, so some `IChatClient` features have no CLI equivalent:

- **Function calling** (`ChatOptions.Tools`, `ToolMode`) is ignored. Claude Code decides on and
  executes its own tools. To give it more, configure MCP servers in the working directory's `.mcp.json`.
- **Sampling settings** (`Temperature`, `TopP`, `TopK`, `MaxOutputTokens`, `StopSequences`,
  penalties, `Seed`, `ResponseFormat`) are silently ignored. The CLI does not expose them.
- **Non-text content** (`DataContent` images/files, `UriContent`, `FunctionCallContent`, ...) throws
  `NotSupportedException`. Only `TextContent` is accepted. Reference files by path instead, since
  Claude Code can read them from the working directory.

> ClaudeCode.Net skips permission prompts by default (`SkipPermissions = true`), so Claude Code can
> edit files and run commands in the working directory. Use a directory you trust it with, or set
> `ConfigureSession = s => s with { SkipPermissions = false }`.

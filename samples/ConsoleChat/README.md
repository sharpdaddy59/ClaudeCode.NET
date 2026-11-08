# ConsoleChat sample

A minimal console chat that talks to Claude Code through `IChatClient`
(`Microsoft.Extensions.AI`). It uses only the abstraction: the client is registered with
`AddClaudeCodeChatClient` and resolved as `IChatClient`.

## Prerequisites

- .NET 10 SDK
- The Claude Code CLI installed and signed in (`claude login`). A Max/Pro plan works and no API key is needed.

## Run

From the directory you want Claude to work in (its working directory is the current directory):

```sh
dotnet run --project path/to/samples/ConsoleChat
# optionally pick a model:
dotnet run --project path/to/samples/ConsoleChat -- sonnet
```

- Type a message and press **Enter**. The reply streams as it is generated.
- Each turn resumes the same Claude Code session (via `ChatOptions.ConversationId`), so the
  conversation keeps its context.
- **Ctrl+C** while a reply is streaming cancels that reply. At the prompt it exits.
- An empty line or `/exit` quits.

> Claude Code runs with its own tools in the working directory, and ClaudeCode.Net skips permission
> prompts by default. Run it only in a directory you trust it to modify.

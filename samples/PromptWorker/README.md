# PromptWorker sample

A .NET Worker Service that drains a folder of prompt files through Claude Code using
`IChatClient` (`ClaudeCodeChatClient`).

1. Every `*.md` file in the **inbox** is sent to Claude as one prompt, one file at a time, oldest name first.
2. On success, the reply is written to the **outbox** under the same file name, and the prompt moves to **done**.
3. On failure (the session ended in an error, or the prompt was empty), the prompt moves to
   **failed**, next to a `<name>.error.txt` describing the error.
4. When the inbox is empty, the worker polls again after `PollInterval`.

Stopping the worker (Ctrl+C) cancels the prompt in progress. That file stays in the inbox and runs
again on the next start.

## Configuration

`appsettings.json`, section `PromptWorker`. Relative paths are relative to the current directory (the project folder when started with `dotnet run`).

| Setting | Default | Meaning |
| --- | --- | --- |
| `InboxPath` | `queue/inbox` | Folder scanned for `*.md` prompts |
| `OutboxPath` | `queue/outbox` | Replies |
| `DonePath` | `queue/done` | Processed prompts |
| `FailedPath` | `queue/failed` | Failed prompts and `.error.txt` files |
| `PollInterval` | `00:00:10` | Delay between scans of an empty inbox |
| `WorkingDirectory` | current directory | Where Claude Code runs (its tools act here) |
| `Model` | CLI default | Model id, e.g. `sonnet` |

## Rate limits

The client is configured with `RateLimitWait = RateLimitWaitPolicy.Default`. When a prompt hits a
Claude usage limit (for example the five-hour window on a subscription plan), the run **does not
fail**:

- ClaudeCode.Net waits until the limit resets, plus a short padding, then **resumes the same session**
  with a continuation prompt. The work done so far is kept.
- The worker logs a warning as soon as the wait starts, with the wait length and reset time, so a
  long pause is not mistaken for a hang. When the prompt finishes, it logs how many waits were needed.
- The default policy waits at most 3 times per prompt, and never longer than 6 hours at a time. A
  limit further away than that (such as a weekly cap) ends the run with a rate-limit error, and the
  prompt moves to `failed/` with the reset time in its `.error.txt`.

## Run

Requires the Claude Code CLI installed and signed in (`claude login`).

```sh
cd samples/PromptWorker
dotnet run
# in another terminal:
echo "Summarize the README in this folder." > queue/inbox/001-summary.md
```

> Claude Code runs with its own tools in `WorkingDirectory`, and ClaudeCode.Net skips permission
> prompts by default. Point it only at a directory you trust it to modify.

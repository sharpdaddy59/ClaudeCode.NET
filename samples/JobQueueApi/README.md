# JobQueueApi sample

An ASP.NET Core minimal API that queues prompts and runs them in the background with
ClaudeCode.Net. A request is answered at once with `202 Accepted` and a job URL. The prompt
runs later, and the caller checks back for the result.

This is the deployment shape for work that takes too long to hold an HTTP request open: Claude
Code sessions that use tools often run for minutes, and a usage limit can pause one for hours.

It uses the core `ClaudeSession` directly (not the `IChatClient` adapter), registered with
`AddClaudeCode()`.

## How it works

- `POST /jobs` puts the job in a bounded in-memory queue ([`JobQueue`](JobQueue.cs)).
- A hosted service ([`JobRunner`](JobRunner.cs)) takes jobs off the queue and runs each one with
  `ClaudeSession.RunAsync`, `MaxConcurrency` at a time.
- Each [`Job`](Job.cs) records its status, session id, result or error, turns and cost as the
  run progresses. `GET /jobs/{id}` returns a snapshot.

| Status | Meaning |
| --- | --- |
| `Queued` | Waiting for a free runner |
| `Running` | Claude Code is working on it |
| `WaitingForRateLimit` | Hit a usage limit; waiting for the reset, then the same session resumes |
| `Succeeded` | Finished; `result` holds Claude's final reply |
| `Failed` | The session ended in an error; see `error` |
| `Canceled` | Canceled through `DELETE`, or the server shut down while it ran |

**Everything is in memory.** Restarting the server loses the queue and the job history. For
durable jobs, store them in a database and have the runner pick them up from there: the
`JobRunner` loop stays the same.

## Endpoints

| Request | Response |
| --- | --- |
| `POST /jobs` with `{ "prompt": "..." }` | `202` with the job and a `Location` header. `400` for an empty prompt, `503` when the queue is full |
| `GET /jobs/{id}` | `200` with the job, `404` if unknown (or pruned) |
| `GET /jobs` | All retained jobs, newest first |
| `DELETE /jobs/{id}` | `202` when canceled. A running job's CLI process tree is killed. `409` if it already finished |

[`JobQueueApi.http`](JobQueueApi.http) has these ready to send from Visual Studio or VS Code.

## Rate limits

Every job runs with `RateLimitWait = RateLimitWaitPolicy.Default`. Nobody is waiting on a
queued job, so a usage limit is not treated as a failure:

- The job's status changes to `WaitingForRateLimit`, with the reset time in `rateLimitResetsAt`,
  and the server logs a warning.
- After the reset, ClaudeCode.Net resumes the **same session**, the status returns to `Running`,
  and the job finishes normally. `rateLimitWaits` counts the waits.
- A limit further away than the policy allows (6 hours, at most 3 waits per job, for example a
  weekly cap) fails the job with the reset time in `error`.

While one job waits, the others behind it wait too: they would hit the same limit.

## Configuration

`appsettings.json`, section `JobQueue`. Environment variables work too, for example
`JobQueue__WorkingDirectory`.

| Setting | Default | Meaning |
| --- | --- | --- |
| `WorkingDirectory` | current directory | Where Claude Code runs (its tools read and write here) |
| `Model` | CLI default | Model id, e.g. `sonnet` |
| `MaxConcurrency` | `1` | Jobs run at once. All of them share one usage window and one directory |
| `QueueCapacity` | `100` | Queued jobs before `POST /jobs` returns `503` |
| `MaxRetainedJobs` | `100` | Finished jobs kept for lookup; older ones are dropped |

The server listens on `http://localhost:5180` (the `Urls` setting).

## Run

Requires the Claude Code CLI installed and signed in (`claude login`).

```sh
cd samples/JobQueueApi
dotnet run

# in another terminal:
curl -i -H "Content-Type: application/json" -d '{"prompt":"Summarize README.md in one sentence."}' http://localhost:5180/jobs
curl http://localhost:5180/jobs/<id from the Location header>
```

## Security

> Anyone who can reach this API can make Claude Code run commands on the server.
> ClaudeCode.Net skips permission prompts by default, so a prompt can read, write and execute
> anything the server's account can in `WorkingDirectory` and beyond.
>
> The sample listens on localhost only and has no authentication. Before exposing it, add
> authentication (`RequireAuthorization()` on the `/jobs` group), run it under a low-privilege
> account, and point `WorkingDirectory` at a directory it may change.

# StreamingApi sample

An ASP.NET Core minimal API that streams Claude Code replies as
[server-sent events](https://html.spec.whatwg.org/multipage/server-sent-events.html). It uses
`IChatClient` from DI, registered with `AddClaudeCodeChatClient`.

## Endpoint

`POST /chat` with a JSON body:

```json
{ "prompt": "Explain Program.cs", "conversationId": null }
```

The response is `text/event-stream`:

```
data: Program.cs sets up

data:  a minimal API...

event: done
data: {"conversationId":"0f3c..."}

```

- Each `data:` event is a text fragment. Concatenate them in order (a fragment containing
  newlines is sent as several `data:` lines, which SSE clients join with `\n`).
- `event: done` carries the `conversationId`. Send it back in the next request to continue the
  same Claude Code session.
- If the session fails, the stream ends with `event: error` and `{"message": "...", "conversationId": ...}`.
- A missing or blank `prompt` returns `400` with a JSON error.
- If the client disconnects, `HttpContext.RequestAborted` cancels the request, and with it the
  Claude Code CLI run (its process tree is killed).

## Run

Requires the Claude Code CLI installed and signed in (`claude login`).

```sh
cd samples/StreamingApi
dotnet run --urls http://localhost:5080
```

```sh
curl -N -X POST http://localhost:5080/chat \
  -H "Content-Type: application/json" \
  -d '{"prompt":"Say hello in three languages"}'

# continue the conversation with the id from the done event:
curl -N -X POST http://localhost:5080/chat \
  -H "Content-Type: application/json" \
  -d '{"prompt":"Now in two more","conversationId":"<id from done>"}'
```

(`-N` turns off curl's buffering so events show up as they arrive.)

Configuration (`appsettings.json`): `Claude:WorkingDirectory` (default: current directory) and
`Claude:Model` (default: CLI default).

> Claude Code runs with its own tools in the working directory, and ClaudeCode.Net skips permission
> prompts by default. Don't expose this endpoint publicly without authentication.

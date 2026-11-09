using System.Text;
using System.Text.Json;
using ClaudeCode.Extensions.AI;
using Microsoft.Extensions.AI;

namespace StreamingApi;

/// <summary>Body of <c>POST /chat</c>.</summary>
/// <param name="Prompt">The user message.</param>
/// <param name="ConversationId">Session id from a previous <c>done</c> event, to continue that conversation.</param>
public sealed record ChatRequest(string? Prompt, string? ConversationId);

/// <summary>
/// Streams an <see cref="IChatClient"/> reply as server-sent events:
/// <c>data: &lt;delta&gt;</c> per text fragment, then <c>event: done</c> with <c>{"conversationId": ...}</c>.
/// A failed session ends with <c>event: error</c> and <c>{"message": ...}</c>. Client disconnect cancels
/// the run through <see cref="HttpContext.RequestAborted"/>.
/// </summary>
public static class ChatSseEndpoint
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Maps <c>POST /chat</c>.</summary>
    public static IEndpointRouteBuilder MapChatSse(this IEndpointRouteBuilder app)
    {
        app.MapPost("/chat", HandleAsync);
        return app;
    }

    private static async Task HandleAsync(ChatRequest request, IChatClient chat, HttpContext http)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "prompt is required" }, Json);
            return;
        }

        var aborted = http.RequestAborted;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(aborted);

        var options = new ChatOptions { ConversationId = string.IsNullOrWhiteSpace(request.ConversationId) ? null : request.ConversationId };
        string? conversationId = options.ConversationId;

        try
        {
            await foreach (var update in chat.GetStreamingResponseAsync(request.Prompt, options, aborted))
            {
                conversationId = update.ConversationId ?? conversationId;
                if (update.Text.Length > 0)
                {
                    await WriteEventAsync(http.Response, eventName: null, update.Text, aborted);
                }
            }

            await WriteEventAsync(http.Response, "done", JsonSerializer.Serialize(new { conversationId }, Json), aborted);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // Client went away; the CLI run was cancelled via the same token. Nothing to write.
        }
        catch (ClaudeCodeChatException ex)
        {
            var payload = JsonSerializer.Serialize(new { message = ex.Message, conversationId = ex.SessionId ?? conversationId }, Json);
            await WriteEventAsync(http.Response, "error", payload, aborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The response has started, so a status code can no longer say it: end the stream
            // with an error event instead of cutting it off. Only the CLI-not-found message is
            // meant for users; anything else stays in the server log.
            http.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("StreamingApi.Chat").LogError(ex, "Chat stream failed");
            var message = ex is ClaudeCode.ClaudeCliNotFoundException ? ex.Message : "The request failed unexpectedly.";
            var payload = JsonSerializer.Serialize(new { message, conversationId }, Json);
            await WriteEventAsync(http.Response, "error", payload, aborted);
        }
    }

    /// <summary>Writes one SSE event. Multi-line data becomes several <c>data:</c> lines, per the SSE spec.</summary>
    internal static async Task WriteEventAsync(HttpResponse response, string? eventName, string data, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        if (eventName is not null)
        {
            sb.Append("event: ").Append(eventName).Append('\n');
        }

        foreach (var line in data.Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append("data: ").Append(line).Append('\n');
        }

        sb.Append('\n');
        await response.WriteAsync(sb.ToString(), cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}

// Console chat over Claude Code, written purely against IChatClient.
// Usage: dotnet run --project samples/ConsoleChat [-- <model>]
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddClaudeCodeChatClient(options =>
{
    options.WorkingDirectory = Directory.GetCurrentDirectory();
    options.DefaultModel = args.Length > 0 ? args[0] : null;
});

await using var provider = services.BuildServiceProvider();
var chat = provider.GetRequiredService<IChatClient>();

// Ctrl+C while a reply is streaming cancels that reply only; at the prompt it exits as usual.
CancellationTokenSource? currentReply = null;
Console.CancelKeyPress += (_, e) =>
{
    var reply = Volatile.Read(ref currentReply);
    if (reply is null)
    {
        return;
    }

    e.Cancel = true;
    try
    {
        reply.Cancel();
    }
    catch (ObjectDisposedException)
    {
        // The reply finished between the key press and the cancel.
    }
};

Console.WriteLine($"Claude Code chat in {Directory.GetCurrentDirectory()}");
Console.WriteLine("Type a message and press Enter. Ctrl+C stops a reply; /new starts a fresh session; an empty line or /exit quits.");

string? conversationId = null;
while (true)
{
    Console.Write("\n> ");
    var line = Console.ReadLine();
    if (line is null || string.IsNullOrWhiteSpace(line) || line.Trim() == "/exit")
    {
        break;
    }

    if (line.Trim() == "/new")
    {
        conversationId = null;
        Console.WriteLine("[new session]");
        continue;
    }

    // The CLI session keeps the history, so each turn sends only the new message and
    // resumes the session via ConversationId.
    var chatOptions = new ChatOptions { ConversationId = conversationId };

    using var cts = new CancellationTokenSource();
    Volatile.Write(ref currentReply, cts);
    try
    {
        await foreach (var update in chat.GetStreamingResponseAsync(line, chatOptions, cts.Token))
        {
            Console.Write(update.Text);
            conversationId = update.ConversationId ?? conversationId;
        }

        Console.WriteLine();
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        Console.WriteLine("\n[reply cancelled]");
    }
    catch (ClaudeCode.Extensions.AI.ClaudeCodeChatException ex)
    {
        // Keep the session so the next message can pick up where the failed turn stopped
        conversationId = ex.SessionId ?? conversationId;
        Console.WriteLine($"\n[error] {ex.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[error] {ex.Message}");
    }
    finally
    {
        Volatile.Write(ref currentReply, null);
    }
}

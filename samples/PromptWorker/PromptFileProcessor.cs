using System.Text;
using ClaudeCode;
using ClaudeCode.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace PromptWorker;

/// <summary>What happened to a prompt file.</summary>
public enum PromptOutcome
{
    /// <summary>Reply written to the outbox; prompt moved to done.</summary>
    Succeeded,

    /// <summary>Error written next to the prompt, which was moved to failed.</summary>
    Failed,
}

/// <summary>
/// Processes one prompt file through any <see cref="IChatClient"/>. With the Claude Code client
/// configured with a rate-limit wait policy, a usage limit pauses the run (logged as it happens)
/// and resumes it, so it does not fail.
/// </summary>
public sealed class PromptFileProcessor(
    IChatClient chat,
    IOptions<PromptWorkerOptions> options,
    ILogger<PromptFileProcessor> logger)
{
    private readonly PromptWorkerOptions _options = options.Value;

    /// <summary>Creates the inbox, outbox, done and failed folders if needed.</summary>
    public void EnsureFolders()
    {
        Directory.CreateDirectory(_options.InboxPath);
        Directory.CreateDirectory(_options.OutboxPath);
        Directory.CreateDirectory(_options.DonePath);
        Directory.CreateDirectory(_options.FailedPath);
    }

    /// <summary>The prompt files currently waiting, oldest name first.</summary>
    public IReadOnlyList<string> PendingFiles()
        => Directory.Exists(_options.InboxPath)
            ? Directory.GetFiles(_options.InboxPath, "*.md").Order(StringComparer.Ordinal).ToArray()
            : [];

    /// <summary>
    /// Runs the prompt in <paramref name="promptPath"/>. On success the reply goes to the outbox and
    /// the prompt to done. On a session error (or invalid prompt) the error goes to
    /// <c>failed/&lt;name&gt;.error.txt</c> and the prompt to failed. On cancellation nothing is written or
    /// moved and <see cref="OperationCanceledException"/> propagates, so the prompt is retried on the
    /// next start. <see cref="ClaudeCliNotFoundException"/> also propagates: retrying cannot help.
    /// </summary>
    public async Task<PromptOutcome> ProcessAsync(string promptPath, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(promptPath);
        logger.LogInformation("Processing {Prompt}", fileName);

        string reply;
        try
        {
            var prompt = await File.ReadAllTextAsync(promptPath, cancellationToken).ConfigureAwait(false);
            reply = await RunAsync(fileName, prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Cancelled {Prompt}; it stays in the inbox", fileName);
            throw;
        }
        catch (ClaudeCliNotFoundException)
        {
            throw;
        }
        // Anything else fails this prompt, not the host. (IO errors still go up to the worker,
        // which retries the file on the next poll: it may be locked or still being written.)
        // Letting an unexpected exception escape would stop the host, and the prompt, still in
        // the inbox, would crash it again on every restart.
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            await FailAsync(promptPath, fileName, ex).ConfigureAwait(false);
            return PromptOutcome.Failed;
        }

        Directory.CreateDirectory(_options.OutboxPath);
        Directory.CreateDirectory(_options.DonePath);
        await File.WriteAllTextAsync(Path.Combine(_options.OutboxPath, fileName), reply, CancellationToken.None).ConfigureAwait(false);
        File.Move(promptPath, Path.Combine(_options.DonePath, fileName), overwrite: true);
        logger.LogInformation("Completed {Prompt}", fileName);
        return PromptOutcome.Succeeded;
    }

    private async Task<string> RunAsync(string fileName, string prompt, CancellationToken cancellationToken)
    {
        // Stream so rate-limit waits can be logged while they happen (they can last hours).
        var text = new StringBuilder();
        await foreach (var update in chat.GetStreamingResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            text.Append(update.Text);

            if (update.AdditionalProperties?.TryGetValue(ClaudeCodeChatClient.RateLimitWaitKey, out var wait) == true)
            {
                update.AdditionalProperties.TryGetValue(ClaudeCodeChatClient.ResetsAtKey, out var resetsAt);
                logger.LogWarning(
                    "Rate limit hit while processing {Prompt}: {Wait} (resets at {ResetsAt}). The run will resume automatically",
                    fileName,
                    wait,
                    resetsAt ?? "unknown");
            }

            if (update.AdditionalProperties?.TryGetValue(ClaudeCodeChatClient.RateLimitWaitsKey, out var waits) == true)
            {
                logger.LogInformation("{Prompt} needed {Waits} rate-limit wait(s)", fileName, waits);
            }
        }

        return text.ToString();
    }

    private async Task FailAsync(string promptPath, string fileName, Exception ex)
    {
        logger.LogError(ex, "Failed {Prompt}", fileName);

        Directory.CreateDirectory(_options.FailedPath);
        var error = new StringBuilder()
            .AppendLine($"{ex.GetType().Name}: {ex.Message}");
        if (ex is ClaudeCodeChatException chatError)
        {
            error.AppendLine($"ExitCode: {chatError.ExitCode}")
                .AppendLine($"SessionId: {chatError.SessionId ?? "(none)"}")
                .AppendLine($"RateLimitDetected: {chatError.RateLimitDetected}")
                .AppendLine($"RateLimitResetsAt: {chatError.RateLimitResetsAt?.ToString("O") ?? "(unknown)"}");
        }

        var errorPath = Path.Combine(_options.FailedPath, Path.GetFileNameWithoutExtension(fileName) + ".error.txt");
        await File.WriteAllTextAsync(errorPath, error.ToString(), CancellationToken.None).ConfigureAwait(false);
        File.Move(promptPath, Path.Combine(_options.FailedPath, fileName), overwrite: true);
    }
}

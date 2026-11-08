using ClaudeCode;
using Microsoft.Extensions.Options;

namespace PromptWorker;

/// <summary>Drains the inbox one prompt at a time, then polls for new files.</summary>
public sealed class Worker(
    PromptFileProcessor processor,
    IOptions<PromptWorkerOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<Worker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        processor.EnsureFolders();
        logger.LogInformation("Watching {Inbox} for *.md prompts", Path.GetFullPath(options.Value.InboxPath));

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var file in processor.PendingFiles())
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    await processor.ProcessAsync(file, stoppingToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked, still being written, or removed since listing: retry next poll
                    // rather than letting one file stop the host
                    logger.LogWarning(ex, "Skipping {Prompt} for now", Path.GetFileName(file));
                }
                catch (ClaudeCliNotFoundException ex)
                {
                    logger.LogCritical(ex, "The Claude Code CLI is not installed or not on PATH; stopping");
                    lifetime.StopApplication();
                    return;
                }
            }

            await Task.Delay(options.Value.PollInterval, stoppingToken);
        }
    }
}

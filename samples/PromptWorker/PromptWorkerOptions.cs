namespace PromptWorker;

/// <summary>Settings bound from the <c>PromptWorker</c> section of appsettings.json.</summary>
public sealed class PromptWorkerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "PromptWorker";

    /// <summary>Folder scanned for <c>*.md</c> prompt files.</summary>
    public string InboxPath { get; set; } = "queue/inbox";

    /// <summary>Folder the replies are written to (same file name as the prompt).</summary>
    public string OutboxPath { get; set; } = "queue/outbox";

    /// <summary>Folder successfully processed prompts are moved to.</summary>
    public string DonePath { get; set; } = "queue/done";

    /// <summary>Folder failed prompts are moved to, next to a <c>.error.txt</c> describing the failure.</summary>
    public string FailedPath { get; set; } = "queue/failed";

    /// <summary>How long to wait between inbox scans when it is empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Directory Claude Code works in. Null uses the current directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Model to use. Null uses the CLI default.</summary>
    public string? Model { get; set; }
}

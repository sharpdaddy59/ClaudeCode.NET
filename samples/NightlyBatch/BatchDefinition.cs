using System.Text.Json;

namespace NightlyBatch;

/// <summary>Settings a task inherits unless it sets its own.</summary>
public sealed record TaskDefaults
{
    public string? Prompt { get; init; }
    public string? Model { get; init; }
    public TimeSpan? Timeout { get; init; }
    public bool SkipPermissions { get; init; }
}

public sealed record BatchTask
{
    public required string Name { get; init; }
    public required string WorkingDirectory { get; init; }
    public string? Prompt { get; init; }
    public string? Model { get; init; }
    public TimeSpan? Timeout { get; init; }
    public bool? SkipPermissions { get; init; }
}

/// <summary>A task with its defaults applied and its directory made absolute.</summary>
public sealed record ResolvedTask(
    string Name, string WorkingDirectory, string Prompt, string? Model, TimeSpan? Timeout, bool SkipPermissions);

/// <summary>The batch file: where results go, shared defaults, and the tasks in run order.</summary>
public sealed record BatchDefinition
{
    public string OutputDirectory { get; init; } = "runs";
    public TaskDefaults Defaults { get; init; } = new();
    public IReadOnlyList<BatchTask> Tasks { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // "name": null for a non-nullable property is a JsonException, not a NullReferenceException later
        RespectNullableAnnotations = true
    };

    /// <summary>
    /// Loads and validates the batch file. Relative paths in it are relative to the
    /// file's own folder, so the schedule works whatever directory it starts in.
    /// Throws <see cref="InvalidDataException"/> listing every problem found.
    /// </summary>
    public static (string OutputDirectory, IReadOnlyList<ResolvedTask> Tasks) Load(string path)
    {
        var batch = JsonSerializer.Deserialize<BatchDefinition>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path} is empty.");
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        var problems = new List<string>();
        var tasks = new List<ResolvedTask>();
        // Nullable annotations don't reach list elements: "tasks": [null] gets this far
        if (batch.Tasks.Any(t => t is null))
        {
            throw new InvalidDataException($"{path}: a task entry is null.");
        }

        foreach (var t in batch.Tasks)
        {
            var dir = Path.GetFullPath(t.WorkingDirectory, baseDir);
            var prompt = t.Prompt ?? batch.Defaults.Prompt;

            if (!Directory.Exists(dir)) problems.Add($"task '{t.Name}': directory not found: {dir}");
            if (string.IsNullOrWhiteSpace(prompt)) problems.Add($"task '{t.Name}': no prompt (and no defaults.prompt)");
            if (t.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) problems.Add($"task '{t.Name}': name must be usable as a file name");

            tasks.Add(new ResolvedTask(t.Name, dir, prompt ?? "", t.Model ?? batch.Defaults.Model,
                t.Timeout ?? batch.Defaults.Timeout, t.SkipPermissions ?? batch.Defaults.SkipPermissions));
        }

        if (tasks.Count == 0) problems.Add("no tasks");
        foreach (var dup in tasks.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"task name '{dup.Key}' is used more than once");
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException($"{path}:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", problems));
        }
        return (Path.GetFullPath(batch.OutputDirectory, baseDir), tasks);
    }
}

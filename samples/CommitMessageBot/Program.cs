using System.Diagnostics;
using ClaudeCode;

// CommitMessageBot: turn your staged diff into a conventional commit message.
//   cd your-repo && git add -p ... && dotnet run --project path/to/CommitMessageBot
// Optionally: CommitMessageBot <repo-path>; CLAUDE_MODEL env var overrides the model.

var repoDir = Path.GetFullPath(args.Length > 0 ? args[0] : Environment.CurrentDirectory);

var psi = new ProcessStartInfo("git", "diff --cached")
{
    WorkingDirectory = repoDir,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
using var git = Process.Start(psi)!;
var diff = await git.StandardOutput.ReadToEndAsync();
await git.WaitForExitAsync();

if (string.IsNullOrWhiteSpace(diff))
{
    Console.Error.WriteLine("Nothing staged. Stage changes with 'git add' first.");
    return 1;
}

// Very large diffs: keep the prompt sane; the head of a diff carries most of the signal
const int MaxDiffChars = 30_000;
if (diff.Length > MaxDiffChars)
{
    diff = diff[..MaxDiffChars] + "\n... [diff truncated]";
}

var session = new ClaudeSession();
var result = await session.RunAsync(
    $"""
    Write a conventional commit message (type(scope): summary, then an optional short
    body) for the following staged diff. Reply with ONLY the commit message - no
    preamble, no code fences.

    {diff}
    """,
    new ClaudeSessionOptions
    {
        WorkingDirectory = repoDir,
        Model = Environment.GetEnvironmentVariable("CLAUDE_MODEL"),
        MaxTurns = 1,          // pure text generation - no tools needed
        SkipPermissions = false // nothing to permit; don't request bypass we don't need
    });

if (result.IsError)
{
    Console.Error.WriteLine($"Session failed (exit {result.ExitCode}): {result.ResultText}");
    return 1;
}

var message = string.IsNullOrWhiteSpace(result.ResultText) ? result.AssistantText : result.ResultText;
Console.WriteLine(message);
Console.Error.WriteLine($"\n[{result.NumTurns} turn(s), {result.Duration.TotalSeconds:F1}s]");
Console.Error.WriteLine("Use it: git commit -F <(dotnet run ...)  - or just copy/paste.");
return 0;

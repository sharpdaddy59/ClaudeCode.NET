using ClaudeCode;

// ChainedRefactor: the feature that's awkward with raw CLI scripting - two sessions
// where the second RESUMES the first and builds on its context. Session 2 is never
// told what session 1 did; it remembers.

var workDir = Path.Combine(Path.GetTempPath(), $"chained-refactor-{Guid.NewGuid():N}");
Directory.CreateDirectory(workDir);
Console.WriteLine($"Workspace: {workDir}\n");

var session = new ClaudeSession();
var options = new ClaudeSessionOptions
{
    WorkingDirectory = workDir,
    Model = Environment.GetEnvironmentVariable("CLAUDE_MODEL"),
    MaxTurns = 15
};

// ---- Step 1: build something ----
Console.WriteLine("Step 1: creating TemperatureConverter.cs ...");
var first = await session.RunAsync(
    "Create a file TemperatureConverter.cs containing a static class with " +
    "CelsiusToFahrenheit and FahrenheitToCelsius methods. Reply with one sentence.",
    options);

Console.WriteLine($"  -> {first.ResultText}");
Console.WriteLine($"  -> session id: {first.SessionId}\n");

if (first.IsError || first.SessionId is null)
{
    Console.Error.WriteLine("Step 1 failed - cannot chain.");
    return 1;
}

// ---- Step 2: resume and extend. Note: we never mention the file name. ----
Console.WriteLine("Step 2: resuming the SAME session - it must remember step 1 ...");
var second = await session.RunAsync(
    "Add XML doc comments to the methods you just created, including one worked " +
    "example each. Reply with one sentence naming the file you modified.",
    options with { ResumeSessionId = first.SessionId });

Console.WriteLine($"  -> {second.ResultText}\n");

// ---- Prove it ----
var file = Path.Combine(workDir, "TemperatureConverter.cs");
var chained = File.Exists(file) && (await File.ReadAllTextAsync(file)).Contains("///");
Console.WriteLine(chained
    ? "CHAINING PROVEN: step 2 found and extended step 1's file without being told about it."
    : "Something's off - inspect the workspace.");
Console.WriteLine($"Inspect or delete: {workDir}");
return chained ? 0 : 1;

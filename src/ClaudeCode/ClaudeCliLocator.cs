namespace ClaudeCode;

/// <summary>
/// Locates the Claude Code CLI executable across platforms.
/// </summary>
public static class ClaudeCliLocator
{
    /// <summary>
    /// Returns the full path to the Claude CLI executable, or null if not found.
    /// Checks common install locations first, then falls back to PATH.
    /// On Windows this can be an npm <c>claude.cmd</c> shim. <see cref="ClaudeSession"/>
    /// never starts a shim directly: it launches the program the shim wraps, because
    /// <c>cmd.exe</c> would interpret quotes, <c>&amp;</c> and <c>%</c> in the arguments.
    /// </summary>
    public static string? FindExecutable()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var possiblePaths = new[]
        {
            // Windows locations
            Path.Combine(homeDir, ".local", "bin", "claude.exe"),
            Path.Combine(homeDir, "AppData", "Local", "Programs", "claude-code", "claude.exe"),
            Path.Combine(homeDir, "AppData", "Roaming", "npm", "claude.cmd"),
            // Unix locations
            Path.Combine(homeDir, ".local", "bin", "claude"),
            "/usr/local/bin/claude",
            "/usr/bin/claude"
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var separator = OperatingSystem.IsWindows() ? ';' : ':';

        var exeNames = OperatingSystem.IsWindows()
            ? new[] { "claude.exe", "claude.cmd", "claude.bat" }
            : new[] { "claude" };

        foreach (var dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var exeName in exeNames)
            {
                var fullPath = Path.Combine(dir, exeName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// How to start the CLI found at <paramref name="path"/> without a shell. A native
    /// executable starts as is. A Windows batch shim (<c>claude.cmd</c> from an npm
    /// global install) is resolved to the program it wraps: Windows would otherwise run
    /// it through <c>cmd.exe</c>, which re-parses the arguments and treats quotes,
    /// <c>&amp;</c>, <c>|</c> and <c>%</c> in a prompt as shell syntax.
    /// </summary>
    /// <exception cref="ClaudeCliNotFoundException">
    /// The path is a batch shim whose target cannot be found, so it cannot be started safely.
    /// </exception>
    internal static CliLaunch ResolveLaunch(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return new CliLaunch(path, []);
        }

        var shimDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        var package = Path.Combine(shimDir, "node_modules", "@anthropic-ai", "claude-code");

        // Current npm releases install a native binary and point the shim at it
        var native = Path.Combine(package, "bin", "claude.exe");
        if (File.Exists(native))
        {
            return new CliLaunch(native, []);
        }

        // Older npm releases run cli.js under node; start node directly, as the shim would
        var cliJs = Path.Combine(package, "cli.js");
        if (File.Exists(cliJs) && FindNode(shimDir) is { } node)
        {
            return new CliLaunch(node, [cliJs]);
        }

        throw new ClaudeCliNotFoundException(
            $"Found the Claude CLI as the batch file {path}, but not the program it starts. " +
            "Running it through cmd.exe would let cmd interpret characters in the prompt, so " +
            "ClaudeCode.Net does not. Install the native CLI instead. " + ClaudeCliNotFoundException.InstallHint);
    }

    private static string? FindNode(string shimDir)
    {
        var besideShim = Path.Combine(shimDir, "node.exe");
        if (File.Exists(besideShim)) return besideShim;

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>The program to start and the arguments that go before the CLI's own.</summary>
internal sealed record CliLaunch(string FileName, IReadOnlyList<string> LeadingArguments);

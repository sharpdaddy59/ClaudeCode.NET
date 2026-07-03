namespace ClaudeAgent;

/// <summary>
/// Locates the Claude Code CLI executable across platforms.
/// </summary>
public static class ClaudeCliLocator
{
    /// <summary>
    /// Returns the full path to the Claude CLI executable, or null if not found.
    /// Checks common install locations first, then falls back to PATH.
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
}

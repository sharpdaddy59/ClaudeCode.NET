using System.Xml.Linq;

namespace ClaudeCode.Extensions.AI.Tests;

/// <summary>
/// The sample is never run by tests (it would launch the real CLI). These tests only check that it
/// is part of the solution, that it builds, and that it is written against IChatClient alone.
/// </summary>
public class SampleProjectTests
{
    private const string SampleProject = "samples/ConsoleChat/ConsoleChat.csproj";

    private static readonly DirectoryInfo RepoRoot = FindRepoRoot();

    [Fact]
    public void Sample_is_part_of_the_solution()
    {
        var solution = XDocument.Load(Path.Combine(RepoRoot.FullName, "ClaudeAgent.slnx"));

        solution.Descendants("Project")
            .Select(p => ((string?)p.Attribute("Path"))?.Replace('\\', '/'))
            .Should().Contain(SampleProject);
    }

    [Fact]
    public void Sample_project_builds()
    {
        // The test project has a build-order-only reference to the sample, so building the tests
        // (or the solution) builds the sample. Its output must exist for the same configuration.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Parent!.Name;
        var output = Path.Combine(RepoRoot.FullName, "samples", "ConsoleChat", "bin", configuration, "net10.0", "ConsoleChat.dll");

        File.Exists(output).Should().BeTrue($"the sample should have been built to {output}");
    }

    [Fact]
    public void Sample_uses_only_IChatClient()
    {
        var sources = Directory.GetFiles(Path.Combine(RepoRoot.FullName, "samples", "ConsoleChat"), "*.cs", SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText)
            .ToList();

        sources.Should().NotBeEmpty();
        var code = string.Join("\n", sources);
        code.Should().Contain("IChatClient")
            .And.Contain("GetStreamingResponseAsync")
            .And.Contain("ConversationId")
            .And.Contain("AddClaudeCodeChatClient");
        code.Should().NotContain("ClaudeSession").And.NotContain("IClaudeRunner").And.NotContain("using ClaudeCode");
    }

    [Fact]
    public void Sample_has_a_readme()
    {
        var readme = Path.Combine(RepoRoot.FullName, "samples", "ConsoleChat", "README.md");

        File.Exists(readme).Should().BeTrue();
        File.ReadAllText(readme).Should().Contain("dotnet run");
    }

    private static DirectoryInfo FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClaudeAgent.slnx")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}

using FluentAssertions;
using ClaudeCode;
using Xunit;

namespace ClaudeCode.Tests;

public class ClaudeSessionOptionsTests
{
    private static ClaudeSessionOptions Options() => new() { WorkingDirectory = "/tmp/project" };

    [Fact]
    public void BuildArguments_Defaults_ContainNonInteractiveFlags()
    {
        var args = Options().BuildArguments("do the thing");

        args.Should().ContainInOrder("--print", "--verbose", "--output-format", "stream-json");
        args.Should().Contain("--dangerously-skip-permissions");
        args.Should().ContainInOrder("--max-turns", "1000");
        args.Last().Should().Be("do the thing");
    }

    [Fact]
    public void BuildArguments_PromptKeepsNewlinesAndQuotes()
    {
        // ArgumentList quoting means no manual escaping: conversation history with
        // markdown, quotes, and Windows paths must arrive verbatim
        var prompt = "**User**: path is C:\\dev\\test and my word is \"syzygy\"\nline two";

        var args = Options().BuildArguments(prompt);

        args.Last().Should().Be(prompt);
    }

    [Fact]
    public void BuildArguments_Resume_ComesFirst()
    {
        var args = new ClaudeSessionOptions
        {
            WorkingDirectory = "/tmp",
            ResumeSessionId = "abc-123"
        }.BuildArguments("p");

        args[0].Should().Be("--resume");
        args[1].Should().Be("abc-123");
    }

    [Fact]
    public void BuildArguments_OptionalFlags_IncludedWhenSet()
    {
        var args = new ClaudeSessionOptions
        {
            WorkingDirectory = "/tmp",
            Model = "claude-sonnet-5",
            MaxTurns = 42,
            SessionName = "sc-my-project",
            SystemPrompt = "Use headless testing."
        }.BuildArguments("p");

        args.Should().ContainInOrder("--model", "claude-sonnet-5");
        args.Should().ContainInOrder("--max-turns", "42");
        args.Should().ContainInOrder("-n", "sc-my-project");
        args.Should().ContainInOrder("--append-system-prompt", "Use headless testing.");
    }

    [Fact]
    public void BuildArguments_OptionalFlags_OmittedWhenUnset()
    {
        var args = Options().BuildArguments("p");

        args.Should().NotContain("--resume");
        args.Should().NotContain("--model");
        args.Should().NotContain("-n");
        args.Should().NotContain("--append-system-prompt");
    }

    [Fact]
    public void BuildArguments_SkipPermissionsFalse_OmitsFlag()
    {
        var args = new ClaudeSessionOptions
        {
            WorkingDirectory = "/tmp",
            SkipPermissions = false
        }.BuildArguments("p");

        args.Should().NotContain("--dangerously-skip-permissions");
    }
}

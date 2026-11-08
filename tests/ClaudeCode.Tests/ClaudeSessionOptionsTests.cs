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
    public void BuildArguments_PartialMessagesOffByDefault()
    {
        Options().BuildArguments("p").Should().NotContain("--include-partial-messages");
    }

    [Fact]
    public void BuildArguments_IncludePartialMessages_AddsFlag()
    {
        var args = (Options() with { IncludePartialMessages = true }).BuildArguments("p");

        args.Should().Contain("--include-partial-messages");
        // Flag belongs with the output-format flags, before the prompt
        args.Last().Should().Be("p");
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
    public void BuildArguments_StdinMode_OmitsThePromptArgument()
    {
        var args = Options().BuildArguments("a very long prompt", promptViaStdin: true);

        args.Should().NotContain("a very long prompt");
        args.Last().Should().Be("1000", "the argument list ends with the --max-turns value, not the prompt");
    }

    [Fact]
    public void ShouldUseStdin_Auto_SwitchesAtTheThreshold()
    {
        var options = Options();  // PromptDelivery defaults to Auto

        options.ShouldUseStdin(new string('x', ClaudeSessionOptions.StdinAutoThreshold)).Should().BeFalse();
        options.ShouldUseStdin(new string('x', ClaudeSessionOptions.StdinAutoThreshold + 1)).Should().BeTrue();
    }

    [Fact]
    public void ShouldUseStdin_ExplicitModes_OverrideLength()
    {
        // Windows caps a process command line at ~32k chars; a 30KB conversation
        // replay passed as an argument fails with "filename or extension is too long"
        (Options() with { PromptDelivery = PromptDelivery.Stdin }).ShouldUseStdin("short").Should().BeTrue();
        (Options() with { PromptDelivery = PromptDelivery.Argument }).ShouldUseStdin(new string('x', 100_000)).Should().BeFalse();
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

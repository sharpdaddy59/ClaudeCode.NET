namespace ClaudeCode.Extensions.AI.Tests;

public class ClaudeCodeChatClientOptionsTests
{
    [Fact]
    public void Defaults_are_null_except_working_directory()
    {
        var options = new ClaudeCodeChatClientOptions { WorkingDirectory = "/repo" };

        options.WorkingDirectory.Should().Be("/repo");
        options.DefaultModel.Should().BeNull();
        options.SystemPrompt.Should().BeNull();
        options.MaxTurns.Should().BeNull();
        options.RateLimitWait.Should().BeNull();
        options.ConfigureSession.Should().BeNull();
    }

    [Fact]
    public void All_properties_round_trip()
    {
        var policy = new RateLimitWaitPolicy { MaxWaits = 1 };
        Func<ClaudeSessionOptions, ClaudeSessionOptions> hook = o => o with { SkipPermissions = false };

        var options = new ClaudeCodeChatClientOptions
        {
            WorkingDirectory = "C:/work",
            DefaultModel = "opus",
            SystemPrompt = "be terse",
            MaxTurns = 5,
            RateLimitWait = policy,
            ConfigureSession = hook,
        };

        options.DefaultModel.Should().Be("opus");
        options.SystemPrompt.Should().Be("be terse");
        options.MaxTurns.Should().Be(5);
        options.RateLimitWait.Should().BeSameAs(policy);
        options.ConfigureSession.Should().BeSameAs(hook);
    }

    [Fact]
    public void Valid_options_pass_validation()
    {
        var options = new ClaudeCodeChatClientOptions { WorkingDirectory = "/repo", MaxTurns = 1 };

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData(null)]
    public void Empty_working_directory_fails_validation(string? workingDirectory)
    {
        var options = new ClaudeCodeChatClientOptions { WorkingDirectory = workingDirectory! };

        options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>()
            .WithParameterName(nameof(ClaudeCodeChatClientOptions.WorkingDirectory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_max_turns_fails_validation(int maxTurns)
    {
        var options = new ClaudeCodeChatClientOptions { WorkingDirectory = "/repo", MaxTurns = maxTurns };

        options.Invoking(o => o.Validate()).Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(ClaudeCodeChatClientOptions.MaxTurns));
    }

    [Fact]
    public void Working_directory_is_required_at_compile_time()
    {
        typeof(ClaudeCodeChatClientOptions).GetProperty(nameof(ClaudeCodeChatClientOptions.WorkingDirectory))!
            .GetCustomAttributes(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false)
            .Should().ContainSingle();
    }
}

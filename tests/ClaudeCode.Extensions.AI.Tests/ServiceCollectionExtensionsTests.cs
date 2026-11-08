using ClaudeCode.Extensions.AI.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeCode.Extensions.AI.Tests;

public class ServiceCollectionExtensionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ServiceCollection Services, FakeClaudeRunner Runner) WithFakeRunner()
    {
        var services = new ServiceCollection();
        var runner = new FakeClaudeRunner();
        services.AddSingleton<IClaudeRunner>(runner);
        return (services, runner);
    }

    [Fact]
    public async Task Resolves_IChatClient_using_pre_registered_fake_runner()
    {
        var (services, runner) = WithFakeRunner();
        runner.Enqueue().Text("hi there").Final("hi there", "s1");
        services.AddClaudeCodeChatClient(o => o.WorkingDirectory = "/repo");
        await using var sp = services.BuildServiceProvider();

        var client = sp.GetRequiredService<IChatClient>();
        var response = await client.GetResponseAsync("Hello", cancellationToken: Ct);

        client.GetService<ClaudeCodeChatClient>().Should().NotBeNull();
        client.GetService<IClaudeRunner>().Should().BeSameAs(runner);
        response.Text.Should().Be("hi there");
        runner.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Configured_options_reach_the_client()
    {
        var (services, runner) = WithFakeRunner();
        services.AddClaudeCodeChatClient(o =>
        {
            o.WorkingDirectory = "/configured";
            o.DefaultModel = "opus";
            o.SystemPrompt = "be brief";
            o.MaxTurns = 9;
            o.ConfigureSession = s => s with { SessionName = "from-di" };
        });
        await using var sp = services.BuildServiceProvider();

        var client = sp.GetRequiredService<IChatClient>();
        await client.GetResponseAsync("Hello", cancellationToken: Ct);

        var options = runner.LastCall.Options;
        options.WorkingDirectory.Should().Be("/configured");
        options.Model.Should().Be("opus");
        options.SystemPrompt.Should().Be("be brief");
        options.MaxTurns.Should().Be(9);
        options.SessionName.Should().Be("from-di");
        client.GetService<ChatClientMetadata>()!.DefaultModelId.Should().Be("opus");
    }

    [Fact]
    public void Builder_chaining_wraps_the_client()
    {
        var (services, _) = WithFakeRunner();
        var builderCalls = 0;
        services.AddClaudeCodeChatClient(o => o.WorkingDirectory = "/repo")
            .Use(inner =>
            {
                builderCalls++;
                return new MarkerChatClient(inner);
            });
        using var sp = services.BuildServiceProvider();

        var client = sp.GetRequiredService<IChatClient>();

        client.Should().BeOfType<MarkerChatClient>();
        client.GetService<ClaudeCodeChatClient>().Should().NotBeNull();
        builderCalls.Should().Be(1);
    }

    [Fact]
    public void Returns_a_ChatClientBuilder()
    {
        var (services, _) = WithFakeRunner();

        services.AddClaudeCodeChatClient(o => o.WorkingDirectory = "/repo").Should().BeOfType<ChatClientBuilder>();
        services.AddKeyedClaudeCodeChatClient("k", o => o.WorkingDirectory = "/repo").Should().BeOfType<ChatClientBuilder>();
    }

    [Fact]
    public void Client_is_singleton_by_default()
    {
        var (services, _) = WithFakeRunner();
        services.AddClaudeCodeChatClient(o => o.WorkingDirectory = "/repo");
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IChatClient>().Should().BeSameAs(sp.GetRequiredService<IChatClient>());
    }

    [Fact]
    public void Lifetime_can_be_transient()
    {
        var (services, _) = WithFakeRunner();
        services.AddClaudeCodeChatClient(o => o.WorkingDirectory = "/repo", ServiceLifetime.Transient);
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IChatClient>().Should().NotBeSameAs(sp.GetRequiredService<IChatClient>());
    }

    [Fact]
    public async Task Keyed_registration_resolves_by_key()
    {
        var (services, runner) = WithFakeRunner();
        services.AddKeyedClaudeCodeChatClient("fast", o => { o.WorkingDirectory = "/a"; o.DefaultModel = "haiku"; });
        services.AddKeyedClaudeCodeChatClient("smart", o => { o.WorkingDirectory = "/b"; o.DefaultModel = "opus"; });
        await using var sp = services.BuildServiceProvider();

        var fast = sp.GetRequiredKeyedService<IChatClient>("fast");
        var smart = sp.GetRequiredKeyedService<IChatClient>("smart");

        fast.Should().NotBeSameAs(smart);
        await fast.GetResponseAsync("q", cancellationToken: Ct);
        runner.LastCall.Options.Model.Should().Be("haiku");
        runner.LastCall.Options.WorkingDirectory.Should().Be("/a");
        await smart.GetResponseAsync("q", cancellationToken: Ct);
        runner.LastCall.Options.Model.Should().Be("opus");

        sp.GetService<IChatClient>().Should().BeNull("only keyed clients were registered");
        sp.GetKeyedService<IChatClient>("missing").Should().BeNull();
    }

    [Fact]
    public void Keyed_and_unkeyed_registrations_coexist()
    {
        var (services, _) = WithFakeRunner();
        services.AddClaudeCodeChatClient(o => o.WorkingDirectory = "/default");
        services.AddKeyedClaudeCodeChatClient(42, o => o.WorkingDirectory = "/keyed");
        using var sp = services.BuildServiceProvider();

        var unkeyed = sp.GetRequiredService<IChatClient>();
        var keyed = sp.GetRequiredKeyedService<IChatClient>(42);

        unkeyed.Should().NotBeSameAs(keyed);
    }

    [Fact]
    public void Keyed_builder_chaining_works()
    {
        var (services, _) = WithFakeRunner();
        services.AddKeyedClaudeCodeChatClient("k", o => o.WorkingDirectory = "/repo").Use(inner => new MarkerChatClient(inner));
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredKeyedService<IChatClient>("k").Should().BeOfType<MarkerChatClient>();
    }

    [Fact]
    public void Without_a_fake_registers_ClaudeSessionRunner_over_the_DI_session()
    {
        var services = new ServiceCollection();
        services.AddClaudeCodeChatClient(o => o.WorkingDirectory = "/repo");
        using var sp = services.BuildServiceProvider();

        // Resolving constructs objects only; nothing launches the CLI.
        var runner = sp.GetRequiredService<IClaudeRunner>().Should().BeOfType<ClaudeSessionRunner>().Subject;
        runner.Session.Should().BeSameAs(sp.GetRequiredService<ClaudeSession>());
        sp.GetRequiredService<IChatClient>().GetService<IClaudeRunner>().Should().BeSameAs(runner);
    }

    [Fact]
    public void Fake_registered_after_still_wins_when_using_replace()
    {
        var services = new ServiceCollection();
        services.AddClaudeCodeChatClient(o => o.WorkingDirectory = "/repo");
        var fake = new FakeClaudeRunner();
        services.AddSingleton<IClaudeRunner>(fake); // last registration wins for single resolution
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IChatClient>().GetService<IClaudeRunner>().Should().BeSameAs(fake);
    }

    [Fact]
    public void Repeated_registration_does_not_duplicate_core_services()
    {
        var services = new ServiceCollection();
        services.AddClaudeCodeChatClient(o => o.WorkingDirectory = "/a");
        services.AddKeyedClaudeCodeChatClient("k", o => o.WorkingDirectory = "/b");
        services.AddKeyedClaudeCodeChatClient("k2", o => o.WorkingDirectory = "/c");

        services.Count(d => d.ServiceType == typeof(ClaudeSession)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IClaudeRunner)).Should().Be(1);
    }

    [Fact]
    public void Existing_ClaudeSession_registration_is_reused()
    {
        var services = new ServiceCollection();
        var session = new ClaudeSession();
        services.AddSingleton(session);
        services.AddClaudeCodeChatClient(o => o.WorkingDirectory = "/a");
        using var sp = services.BuildServiceProvider();

        services.Count(d => d.ServiceType == typeof(ClaudeSession)).Should().Be(1);
        ((ClaudeSessionRunner)sp.GetRequiredService<IClaudeRunner>()).Session.Should().BeSameAs(session);
    }

    [Fact]
    public void Configure_runs_once_at_registration()
    {
        var (services, _) = WithFakeRunner();
        var calls = 0;
        services.AddClaudeCodeChatClient(o => { calls++; o.WorkingDirectory = "/repo"; }, ServiceLifetime.Transient);
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IChatClient>();
        sp.GetRequiredService<IChatClient>();

        calls.Should().Be(1);
    }

    [Fact]
    public void Invalid_options_fail_at_registration()
    {
        var services = new ServiceCollection();

        var missingDir = () => services.AddClaudeCodeChatClient(_ => { });
        var keyedMissingDir = () => services.AddKeyedClaudeCodeChatClient("k", o => o.WorkingDirectory = " ");
        var badTurns = () => services.AddClaudeCodeChatClient(o => { o.WorkingDirectory = "/r"; o.MaxTurns = 0; });

        missingDir.Should().Throw<ArgumentException>().WithParameterName("WorkingDirectory");
        keyedMissingDir.Should().Throw<ArgumentException>();
        badTurns.Should().Throw<ArgumentOutOfRangeException>();
        services.Should().BeEmpty("nothing is registered when validation fails");
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var services = new ServiceCollection();

        ((Action)(() => ((IServiceCollection)null!).AddClaudeCodeChatClient(o => o.WorkingDirectory = "/r"))).Should().Throw<ArgumentNullException>();
        ((Action)(() => services.AddClaudeCodeChatClient(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => services.AddKeyedClaudeCodeChatClient(null!, o => o.WorkingDirectory = "/r"))).Should().Throw<ArgumentNullException>();
        ((Action)(() => services.AddKeyedClaudeCodeChatClient("k", null!))).Should().Throw<ArgumentNullException>();
    }

    private sealed class MarkerChatClient(IChatClient inner) : DelegatingChatClient(inner);
}

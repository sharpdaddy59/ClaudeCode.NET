using ClaudeCode.Extensions.AI.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace ClaudeCode.Extensions.AI.Tests;

public class ClaudeCodeChatClientServiceTests
{
    private readonly FakeClaudeRunner _runner = new();
    private readonly ClaudeCodeChatClientOptions _options = new() { WorkingDirectory = "/repo", DefaultModel = "sonnet" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClaudeCodeChatClient CreateClient() => new(_runner, _options);

    [Fact]
    public void Metadata_has_provider_name_and_default_model()
    {
        using var client = CreateClient();

        var metadata = client.GetService<ChatClientMetadata>();

        metadata.Should().NotBeNull();
        metadata!.ProviderName.Should().Be("claude-code");
        ClaudeCodeChatClient.ProviderName.Should().Be("claude-code");
        metadata.DefaultModelId.Should().Be("sonnet");
        metadata.ProviderUri.Should().BeNull();
    }

    [Fact]
    public void Metadata_default_model_is_null_when_unset()
    {
        using var client = new ClaudeCodeChatClient(_runner, new ClaudeCodeChatClientOptions { WorkingDirectory = "/r" });

        client.GetService<ChatClientMetadata>()!.DefaultModelId.Should().BeNull();
    }

    [Fact]
    public void Metadata_is_cached_and_refreshed_when_default_model_changes()
    {
        using var client = CreateClient();

        var first = client.GetService<ChatClientMetadata>();
        client.GetService<ChatClientMetadata>().Should().BeSameAs(first);

        _options.DefaultModel = "opus";
        var second = client.GetService<ChatClientMetadata>();

        second.Should().NotBeSameAs(first);
        second!.DefaultModelId.Should().Be("opus");
    }

    [Fact]
    public void Returns_itself_for_its_own_types()
    {
        using var client = CreateClient();

        client.GetService<ClaudeCodeChatClient>().Should().BeSameAs(client);
        client.GetService<IChatClient>().Should().BeSameAs(client);
        client.GetService<IDisposable>().Should().BeSameAs(client);
        client.GetService<object>().Should().BeSameAs(client);
    }

    [Fact]
    public void Returns_the_runner_for_IClaudeRunner()
    {
        using var client = CreateClient();

        client.GetService<IClaudeRunner>().Should().BeSameAs(_runner);
        client.GetService<FakeClaudeRunner>().Should().BeSameAs(_runner);
    }

    [Fact]
    public void Returns_the_default_session_runner_for_the_options_constructor()
    {
        using var client = new ClaudeCodeChatClient(_options);

        client.GetService<IClaudeRunner>().Should().BeOfType<ClaudeSessionRunner>();
        client.GetService<ClaudeSessionRunner>().Should().BeSameAs(client.GetService<IClaudeRunner>());
    }

    [Fact]
    public void Returns_null_for_unknown_types()
    {
        using var client = CreateClient();

        client.GetService<string>().Should().BeNull();
        client.GetService<ClaudeCodeChatClientOptions>().Should().BeNull();
        client.GetService<ClaudeSession>().Should().BeNull();
        client.GetService<IEmbeddingGenerator<string, Embedding<float>>>().Should().BeNull();
    }

    [Theory]
    [InlineData("key")]
    [InlineData(42)]
    public void Returns_null_for_any_non_null_key(object key)
    {
        using var client = CreateClient();

        client.GetService(typeof(ChatClientMetadata), key).Should().BeNull();
        client.GetService(typeof(IChatClient), key).Should().BeNull();
        client.GetService(typeof(IClaudeRunner), key).Should().BeNull();
    }

    [Fact]
    public void Null_service_type_throws()
    {
        using var client = CreateClient();

        client.Invoking(c => c.GetService(null!)).Should().Throw<ArgumentNullException>().WithParameterName("serviceType");
    }

    [Fact]
    public void Works_through_a_delegating_pipeline()
    {
        using var inner = CreateClient();
        using IChatClient pipeline = new PassThroughChatClient(inner);

        pipeline.GetService<ChatClientMetadata>()!.ProviderName.Should().Be("claude-code");
        pipeline.GetService<ClaudeCodeChatClient>().Should().BeSameAs(inner);
        pipeline.GetService<IClaudeRunner>().Should().BeSameAs(_runner);
    }

    [Fact]
    public void Dispose_can_be_called_twice()
    {
        var client = CreateClient();

        client.Dispose();
        client.Invoking(c => c.Dispose()).Should().NotThrow();
    }

    [Fact]
    public async Task Client_still_works_after_dispose()
    {
        _runner.Enqueue().Text("ok").Final("ok");
        var client = CreateClient();
        client.Dispose();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        response.Text.Should().Be("ok");
    }

    private sealed class PassThroughChatClient(IChatClient inner) : DelegatingChatClient(inner);
}

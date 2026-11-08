using FluentAssertions;
using ClaudeCode;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeCode.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddClaudeCode_WithoutLogging_ResolvesSession()
    {
        // ILogger<ClaudeSession> is an optional ctor parameter: DI must fall back to
        // the default (null) when no logging is registered, not throw
        using var provider = new ServiceCollection()
            .AddClaudeCode()
            .BuildServiceProvider();

        provider.GetRequiredService<ClaudeSession>().Should().NotBeNull();
    }

    [Fact]
    public void AddClaudeCode_WithLogging_ResolvesSession()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddClaudeCode()
            .BuildServiceProvider();

        provider.GetRequiredService<ClaudeSession>().Should().NotBeNull();
    }

    [Fact]
    public void AddClaudeCode_IsSingleton()
    {
        using var provider = new ServiceCollection()
            .AddClaudeCode()
            .BuildServiceProvider();

        provider.GetRequiredService<ClaudeSession>()
            .Should().BeSameAs(provider.GetRequiredService<ClaudeSession>());
    }

    [Fact]
    public void AddClaudeCode_CalledTwice_RegistersOnce()
    {
        var services = new ServiceCollection().AddClaudeCode().AddClaudeCode();

        services.Count(d => d.ServiceType == typeof(ClaudeSession)).Should().Be(1);
    }

    [Fact]
    public void AddClaudeCode_ReturnsSameCollection_ForChaining()
    {
        var services = new ServiceCollection();

        services.AddClaudeCode().Should().BeSameAs(services);
    }
}

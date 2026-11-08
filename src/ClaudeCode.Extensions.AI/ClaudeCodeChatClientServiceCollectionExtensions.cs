using ClaudeCode.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Dependency-injection registration for <see cref="ClaudeCodeChatClient"/>.
/// </summary>
public static class ClaudeCodeChatClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="ClaudeCodeChatClient"/> as the <see cref="IChatClient"/>. Also registers
    /// <see cref="ClaudeSession"/> (via <c>AddClaudeCode()</c>) and, unless one is already registered,
    /// <see cref="IClaudeRunner"/> as a <see cref="ClaudeSessionRunner"/> over that session.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Configures the client options. It runs once, here, and the options are validated
    /// immediately, so a missing working directory fails at startup rather than at first use.
    /// </param>
    /// <param name="lifetime">Lifetime of the chat client registration. Default singleton.</param>
    /// <returns>A <see cref="ChatClientBuilder"/> for adding middleware (logging, caching, ...).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException">The configured options are invalid.</exception>
    public static ChatClientBuilder AddClaudeCodeChatClient(
        this IServiceCollection services,
        Action<ClaudeCodeChatClientOptions> configure,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = BuildOptions(configure);

        AddCoreServices(services);
        return services.AddChatClient(sp => CreateClient(sp, options), lifetime);
    }

    /// <summary>
    /// Registers a <see cref="ClaudeCodeChatClient"/> as a keyed <see cref="IChatClient"/>. Shares the
    /// <see cref="ClaudeSession"/> and <see cref="IClaudeRunner"/> registrations of
    /// <see cref="AddClaudeCodeChatClient"/>, so several keyed clients (for example one per model or
    /// working directory) can coexist.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="serviceKey">Key the client is registered under.</param>
    /// <param name="configure">Configures the client options. Runs once, here, and is validated immediately.</param>
    /// <param name="lifetime">Lifetime of the chat client registration. Default singleton.</param>
    /// <returns>A <see cref="ChatClientBuilder"/> for adding middleware.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="serviceKey"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException">The configured options are invalid.</exception>
    public static ChatClientBuilder AddKeyedClaudeCodeChatClient(
        this IServiceCollection services,
        object serviceKey,
        Action<ClaudeCodeChatClientOptions> configure,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        var options = BuildOptions(configure);

        AddCoreServices(services);
        return services.AddKeyedChatClient(serviceKey, sp => CreateClient(sp, options), lifetime);
    }

    private static ClaudeCodeChatClientOptions BuildOptions(Action<ClaudeCodeChatClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ClaudeCodeChatClientOptions { WorkingDirectory = string.Empty };
        configure(options);
        options.Validate();
        return options;
    }

    private static void AddCoreServices(IServiceCollection services)
    {
        // TryAdd: keeps a ClaudeSession or IClaudeRunner the caller registered first
        services.AddClaudeCode();
        services.TryAddSingleton<IClaudeRunner>(
            sp => new ClaudeSessionRunner(sp.GetRequiredService<ClaudeSession>()));
    }

    private static ClaudeCodeChatClient CreateClient(IServiceProvider services, ClaudeCodeChatClientOptions options)
        => new(services.GetRequiredService<IClaudeRunner>(), options);
}

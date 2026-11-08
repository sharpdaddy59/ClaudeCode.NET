using ClaudeCode;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Conventional namespace for AddXxx registration methods: visible wherever the
// caller already has `using Microsoft.Extensions.DependencyInjection;`.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Dependency-injection registration for ClaudeCode.Net.
/// </summary>
public static class ClaudeCodeServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ClaudeSession"/> as a singleton. The session holds no
    /// per-run state (every call takes its own <see cref="ClaudeSessionOptions"/>),
    /// so one instance serves the whole application. If logging is registered,
    /// the session logs through <c>ILogger&lt;ClaudeSession&gt;</c>; if not, it is silent.
    /// </summary>
    public static IServiceCollection AddClaudeCode(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ClaudeSession>();
        return services;
    }
}

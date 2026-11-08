using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeCode.Extensions.AI.Tests;

/// <summary>Keeps the package README honest: its snippets compile and the names it cites exist.</summary>
public partial class ReadmeTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string LibraryDir = Path.Combine(RepoRoot, "src", "ClaudeCode.Extensions.AI");
    private static readonly string Readme = File.ReadAllText(Path.Combine(LibraryDir, "README.md"));

    [Fact]
    public void Readme_is_configured_as_the_package_readme()
    {
        var project = XDocument.Load(Path.Combine(LibraryDir, "ClaudeCode.Extensions.AI.csproj"));

        project.Descendants("PackageReadmeFile").Single().Value.Should().Be("README.md");
        project.Descendants("None")
            .Should().Contain(e => (string?)e.Attribute("Include") == "README.md" && (string?)e.Attribute("Pack") == "true");
        project.Descendants("PackageId").Single().Value.Should().Be("ClaudeCode.Net.Extensions.AI");
    }

    [Fact]
    public void Readme_covers_the_required_topics()
    {
        Readme.Should().Contain("dotnet add package ClaudeCode.Net.Extensions.AI")
            .And.Contain("GetResponseAsync")
            .And.Contain("GetStreamingResponseAsync")
            .And.Contain("AddClaudeCodeChatClient")
            .And.Contain("ConversationId")
            .And.Contain("Not supported")
            .And.Contain("Tools")
            .And.Contain("Temperature")
            .And.Contain("NotSupportedException");
    }

    [Fact]
    public void ClaudeCodeChatClient_constants_cited_in_readme_exist()
    {
        var cited = ClientMemberRegex().Matches(Readme).Select(m => m.Groups[1].Value).Distinct().ToList();
        var bareKeys = BareKeyRegex().Matches(Readme).Select(m => m.Groups[1].Value);
        var all = cited.Concat(bareKeys).Distinct().ToList();

        all.Should().NotBeEmpty();
        foreach (var name in all)
        {
            typeof(ClaudeCodeChatClient).GetField(name, BindingFlags.Public | BindingFlags.Static)
                .Should().NotBeNull($"README cites ClaudeCodeChatClient.{name}");
        }
    }

    [Fact]
    public void Claude_additional_property_keys_in_readme_are_real()
    {
        var keys = typeof(ClaudeCodeChatClient).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        foreach (Match match in ClaudeKeyRegex().Matches(Readme))
        {
            keys.Should().Contain(match.Groups[1].Value);
        }
    }

    [Fact]
    public void Options_table_lists_real_properties()
    {
        var rows = OptionsRowRegex().Matches(Readme)
            .Select(m => m.Groups[1].Value)
            .Where(name => name != nameof(ClaudeCodeChatClientOptions)) // the table's header cell
            .ToList();

        rows.Should().BeEquivalentTo(
            typeof(ClaudeCodeChatClientOptions).GetProperties().Select(p => p.Name),
            "the options table should document exactly the public options");
    }

    [Fact]
    public void Exception_properties_cited_in_readme_exist()
    {
        foreach (var name in new[] { "ExitCode", "SessionId", "RateLimitDetected", "RateLimitResetsAt" })
        {
            Readme.Should().Contain(name);
            typeof(ClaudeCodeChatException).GetProperty(name).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Readme_snippets_work_against_a_fake_runner()
    {
        var runner = new Fakes.FakeClaudeRunner();
        runner.Enqueue().Init("s1").Text("first").Final("first", "s1");
        runner.Enqueue().Init("s1").Text("second").Final("second", "s1");
        IChatClient chat = new ClaudeCodeChatClient(runner, new ClaudeCodeChatClientOptions
        {
            WorkingDirectory = Path.GetTempPath(),
            DefaultModel = "sonnet",
        });

        var (first, second) = await ReadmeSnippets.ConversationAsync(chat);

        first.ConversationId.Should().Be("s1");
        second.Text.Should().Be("second");
        runner.LastCall.Options.ResumeSessionId.Should().Be("s1");
        runner.LastCall.Prompt.Should().Be("Add a --verbose flag to it.");
    }

    [Fact]
    public void Readme_di_snippet_registers_resolvable_clients()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClaudeRunner>(new Fakes.FakeClaudeRunner());
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        ReadmeSnippets.RegisterServices(services);
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IChatClient>().GetService<ClaudeCodeChatClient>().Should().NotBeNull();
        sp.GetRequiredKeyedService<IChatClient>("opus").GetService<ChatClientMetadata>()!.DefaultModelId.Should().Be("opus");
    }

    [GeneratedRegex(@"ClaudeCodeChatClient\.(\w+Key)\b")]
    private static partial Regex ClientMemberRegex();

    [GeneratedRegex(@"`(\w+Key)`")]
    private static partial Regex BareKeyRegex();

    [GeneratedRegex(@"`(claude\.[a-z_]+)`")]
    private static partial Regex ClaudeKeyRegex();

    [GeneratedRegex(@"^\| `(\w+)`", RegexOptions.Multiline)]
    private static partial Regex OptionsRowRegex();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClaudeAgent.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>
    /// Compile-checked copies of the README snippets. If an API name in the README changes, this
    /// stops compiling. The methods that would launch the real CLI are never called.
    /// </summary>
    private static class ReadmeSnippets
    {
        public static async Task QuickStartAsync()
        {
            IChatClient chat = new ClaudeCodeChatClient(new ClaudeCodeChatClientOptions
            {
                WorkingDirectory = @"C:\src\my-repo",
                DefaultModel = "sonnet",
            });

            ChatResponse response = await chat.GetResponseAsync("Why does the build fail?");
            Console.WriteLine(response.Text);

            await foreach (ChatResponseUpdate update in chat.GetStreamingResponseAsync("Summarize README.md"))
            {
                Console.Write(update.Text);
            }
        }

        public static void RegisterServices(IServiceCollection services)
        {
            services
                .AddClaudeCodeChatClient(options =>
                {
                    options.WorkingDirectory = "/srv/workspace";
                    options.SystemPrompt = "Answer concisely.";
                    options.RateLimitWait = RateLimitWaitPolicy.Default;
                })
                .UseLogging();

            services.AddKeyedClaudeCodeChatClient("opus", o => { o.WorkingDirectory = "/srv/a"; o.DefaultModel = "opus"; });
        }

        public static async Task<(ChatResponse First, ChatResponse Second)> ConversationAsync(IChatClient chat)
        {
            var history = new List<ChatMessage> { new(ChatRole.User, "What does Program.cs do?") };
            var first = await chat.GetResponseAsync(history);
            history.AddMessages(first);
            history.Add(new ChatMessage(ChatRole.User, "Add a --verbose flag to it."));

            var second = await chat.GetResponseAsync(history, new ChatOptions { ConversationId = first.ConversationId });
            return (first, second);
        }

        public static ClaudeCodeChatClientOptions PermissionsSnippet() => new()
        {
            WorkingDirectory = ".",
            ConfigureSession = s => s with { SkipPermissions = false },
        };
    }
}

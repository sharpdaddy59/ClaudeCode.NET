using System.Reflection;

namespace ClaudeCode.Extensions.AI.Tests;

public class ClaudeSessionRunnerTests
{
    [Fact]
    public void Implements_IClaudeRunner()
    {
        IClaudeRunner runner = new ClaudeSessionRunner(new ClaudeSession());
        runner.Should().BeOfType<ClaudeSessionRunner>();
    }

    [Fact]
    public void Wraps_the_supplied_session_instance()
    {
        // Constructing a ClaudeSession does not launch the CLI; only Run/Stream do.
        var session = new ClaudeSession();

        var runner = new ClaudeSessionRunner(session);

        runner.Session.Should().BeSameAs(session);
    }

    [Fact]
    public void Default_constructor_creates_its_own_session()
    {
        var a = new ClaudeSessionRunner();
        var b = new ClaudeSessionRunner();

        a.Session.Should().NotBeNull();
        a.Session.Should().NotBeSameAs(b.Session);
    }

    [Fact]
    public void Null_session_is_rejected()
    {
        var act = () => new ClaudeSessionRunner(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("session");
    }

    [Fact]
    public void Runner_is_sealed()
    {
        typeof(ClaudeSessionRunner).IsSealed.Should().BeTrue();
    }

    [Fact]
    public void StreamAsync_signature_mirrors_ClaudeSession()
    {
        var sessionMethod = typeof(ClaudeSession).GetMethod(nameof(ClaudeSession.StreamAsync))!;
        var runnerMethod = typeof(IClaudeRunner).GetMethod(nameof(IClaudeRunner.StreamAsync))!;

        runnerMethod.ReturnType.Should().Be(sessionMethod.ReturnType);
        ParameterTypes(runnerMethod).Should().Equal(ParameterTypes(sessionMethod));
    }

    [Fact]
    public void RunAsync_signature_mirrors_ClaudeSession_callback_overload_minus_stderr()
    {
        var sessionMethod = typeof(ClaudeSession).GetMethods()
            .Single(m => m.Name == nameof(ClaudeSession.RunAsync) && m.GetParameters().Length == 5);
        var runnerMethod = typeof(IClaudeRunner).GetMethod(nameof(IClaudeRunner.RunAsync))!;

        runnerMethod.ReturnType.Should().Be(sessionMethod.ReturnType);
        runnerMethod.ReturnType.Should().Be<Task<ClaudeSessionResult>>();

        var expected = ParameterTypes(sessionMethod).Where(t => t != typeof(Action<string>));
        ParameterTypes(runnerMethod).Should().Equal(expected);
    }

    [Fact]
    public void Interface_has_exactly_two_members()
    {
        typeof(IClaudeRunner).GetMembers().Select(m => m.Name)
            .Should().BeEquivalentTo(nameof(IClaudeRunner.RunAsync), nameof(IClaudeRunner.StreamAsync));
    }

    [Fact]
    public void StreamAsync_returns_the_sessions_enumerable_without_starting_it()
    {
        // StreamAsync on ClaudeSession is an async iterator: obtaining the enumerable is lazy and
        // launches nothing until enumerated, so forwarding can be checked without a process.
        var runner = new ClaudeSessionRunner();
        var options = new ClaudeSessionOptions { WorkingDirectory = Path.GetTempPath() };

        var stream = runner.StreamAsync("prompt", options, CancellationToken.None);

        stream.Should().NotBeNull();
        stream.Should().BeAssignableTo<IAsyncEnumerable<ClaudeStreamEvent>>();
    }

    private static Type[] ParameterTypes(MethodInfo method)
        => method.GetParameters().Select(p => p.ParameterType).ToArray();
}

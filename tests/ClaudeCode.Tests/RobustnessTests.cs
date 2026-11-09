using System.Text;
using FluentAssertions;
using ClaudeCode;
using Xunit;

namespace ClaudeCode.Tests;

/// <summary>
/// Malformed CLI output, callbacks that throw, callers that stop reading early, and
/// Windows batch shims: none of these may hang a run, leak a process, or reach cmd.exe.
/// </summary>
public class RobustnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- parser ----

    [Theory]
    [InlineData("""{"type":"result","duration_ms":null,"num_turns":"3","total_cost_usd":{},"session_id":42,"result":"ok"}""")]
    [InlineData("""{"type":"result","num_turns":99999999999,"result":"ok"}""")]
    public void ParseLine_Result_WithOddValues_KeepsTheEvent(string line)
    {
        var events = ClaudeStreamParser.ParseLine(line);

        events.Should().ContainSingle();
        events[0].Kind.Should().Be(ClaudeStreamEventKind.Result);
        events[0].Text.Should().Be("ok");
        events[0].NumTurns.Should().Be(0);
        events[0].SessionId.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":5,"session_id":true}""")]
    [InlineData("""{"type":"assistant","message":"not an object"}""")]
    [InlineData("""{"type":"error","message":123}""")]
    [InlineData("""{"type":7}""")]
    public void ParseLine_UnexpectedValueTypes_DoNotThrow(string line)
    {
        var parse = () => ClaudeStreamParser.ParseLine(line);

        parse.Should().NotThrow().Which.Should().NotBeEmpty();
    }

    // ---- output reader ----

    [Fact]
    public async Task PumpAsync_KeepsReadingAfterAHandlerThrows()
    {
        var session = new ClaudeSession(logger: null, TimeProvider.System, runOnce: null);
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("one\ntwo\nthree\n")));
        var seen = new List<string>();

        await session.PumpAsync(reader, line =>
        {
            seen.Add(line);
            if (line == "one") throw new InvalidOperationException("callback bug");
        }, Ct);

        seen.Should().Equal("one", "two", "three");
    }

    // ---- StreamAsync abandoned early ----

    [Fact]
    public async Task StreamAsync_BreakingEarly_CancelsTheRun()
    {
        var runCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ClaudeSession(logger: null, TimeProvider.System, async (_, _, onEvent, _, ct) =>
        {
            onEvent?.Invoke(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.SystemInit, SessionId = "s1" });
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                runCanceled.TrySetResult();
                throw;
            }
            return new ClaudeSessionResult { ExitCode = 0 };
        });

        await foreach (var evt in session.StreamAsync("hi", new ClaudeSessionOptions { WorkingDirectory = "." }, Ct))
        {
            evt.Kind.Should().Be(ClaudeStreamEventKind.SystemInit);
            break;
        }

        // The run was canceled (its process tree killed) before the enumeration returned
        runCanceled.Task.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_ReadToEnd_StillSurfacesLaunchFailures()
    {
        var session = new ClaudeSession(logger: null, TimeProvider.System,
            (_, _, _, _, _) => throw new ClaudeCliNotFoundException());

        var read = async () =>
        {
            await foreach (var _ in session.StreamAsync("hi", new ClaudeSessionOptions { WorkingDirectory = "." }, Ct)) { }
        };

        await read.Should().ThrowAsync<ClaudeCliNotFoundException>();
    }

    // ---- launching without cmd.exe ----

    [Fact]
    public void ResolveLaunch_NativeExecutable_StartsAsIs()
    {
        var launch = ClaudeCliLocator.ResolveLaunch("/usr/local/bin/claude");

        launch.FileName.Should().Be("/usr/local/bin/claude");
        launch.LeadingArguments.Should().BeEmpty();
    }

    [Fact]
    public void ResolveLaunch_NpmShim_StartsTheNativeBinaryItWraps()
    {
        using var npm = new TempDir();
        var shim = npm.File("claude.cmd");
        var native = npm.File("node_modules/@anthropic-ai/claude-code/bin/claude.exe");

        var launch = ClaudeCliLocator.ResolveLaunch(shim);

        launch.FileName.Should().Be(native);
        launch.LeadingArguments.Should().BeEmpty();
    }

    [Fact]
    public void ResolveLaunch_OlderNpmShim_StartsNodeWithCliJs()
    {
        using var npm = new TempDir();
        var shim = npm.File("claude.CMD");
        var cliJs = npm.File("node_modules/@anthropic-ai/claude-code/cli.js");
        var node = npm.File("node.exe");

        var launch = ClaudeCliLocator.ResolveLaunch(shim);

        launch.FileName.Should().Be(node);
        launch.LeadingArguments.Should().Equal(cliJs);
    }

    [Theory]
    [InlineData("claude.cmd")]
    [InlineData("claude.bat")]
    public void ResolveLaunch_ShimWithoutTarget_RefusesInsteadOfUsingCmd(string shimName)
    {
        using var other = new TempDir();
        var shim = other.File(shimName);

        var resolve = () => ClaudeCliLocator.ResolveLaunch(shim);

        resolve.Should().Throw<ClaudeCliNotFoundException>()
            .WithMessage("*cmd.exe*")
            .WithMessage("*install.ps1*");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("claudecode-tests-").FullName;

        public string File(string relative)
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relative));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, "");
            return full;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

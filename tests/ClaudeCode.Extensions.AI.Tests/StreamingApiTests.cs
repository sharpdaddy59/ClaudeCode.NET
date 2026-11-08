using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeCode.Extensions.AI.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeCode.Extensions.AI.Tests;

public sealed class StreamingApiTests : IDisposable
{
    private readonly FakeClaudeRunner _runner = new();
    private readonly WebApplicationFactory<Program> _factory;

    public StreamingApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IClaudeRunner>(_runner)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    private sealed record SseEvent(string? Event, string Data);

    private static List<SseEvent> ParseSse(string body)
    {
        var events = new List<SseEvent>();
        foreach (var block in body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null;
            var data = new List<string>();
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    name = line["event: ".Length..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data.Add(line["data: ".Length..]);
                }
            }

            events.Add(new SseEvent(name, string.Join("\n", data)));
        }

        return events;
    }

    private async Task<(HttpResponseMessage Response, List<SseEvent> Events)> PostChatAsync(object body)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/chat", body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, ParseSse(text));
    }

    [Fact]
    public async Task Streams_ordered_data_events_then_done_with_conversation_id()
    {
        _runner.Enqueue().Init("sess-42").Delta("Hello").Delta(", ").Delta("world").Text("Hello, world").Final("Hello, world", "sess-42");

        var (response, events) = await PostChatAsync(new { prompt = "Say hello" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.CacheControl!.NoCache.Should().BeTrue();

        events.Should().HaveCount(4);
        events.Take(3).Should().OnlyContain(e => e.Event == null);
        events.Take(3).Select(e => e.Data).Should().Equal("Hello", ", ", "world");

        var done = events[^1];
        done.Event.Should().Be("done");
        JsonDocument.Parse(done.Data).RootElement.GetProperty("conversationId").GetString().Should().Be("sess-42");

        _runner.LastCall.Prompt.Should().Be("Say hello");
        _runner.LastCall.Options.IncludePartialMessages.Should().BeTrue();
        _runner.LastCall.Options.ResumeSessionId.Should().BeNull();
    }

    [Fact]
    public async Task ConversationId_in_request_resumes_the_session()
    {
        _runner.Enqueue().Init("sess-7").Text("again").Final("again", "sess-7");

        var (_, events) = await PostChatAsync(new { prompt = "Continue", conversationId = "sess-7" });

        _runner.LastCall.Options.ResumeSessionId.Should().Be("sess-7");
        _runner.LastCall.Prompt.Should().Be("Continue");
        events.Select(e => e.Data).First().Should().Be("again");
        JsonDocument.Parse(events[^1].Data).RootElement.GetProperty("conversationId").GetString().Should().Be("sess-7");
    }

    [Fact]
    public async Task Multi_line_delta_is_split_into_data_lines_and_rejoined()
    {
        _runner.Enqueue().Delta("line one\nline two\r\nline three").Final();

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/chat", new { prompt = "lines" }, Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);

        raw.Should().StartWith("data: line one\ndata: line two\ndata: line three\n\n");
        ParseSse(raw)[0].Data.Should().Be("line one\nline two\nline three");
    }

    [Fact]
    public async Task Tool_and_empty_updates_produce_no_data_events()
    {
        _runner.Enqueue()
            .Delta("Looking")
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.ToolUse, ToolName = "Grep", ToolInput = "{}" })
            .Event(new ClaudeStreamEvent { Kind = ClaudeStreamEventKind.ToolResult, Text = "secret tool output" })
            .Delta(" done")
            .Final(sessionId: "s");

        var (_, events) = await PostChatAsync(new { prompt = "go" });

        events.Where(e => e.Event == null).Select(e => e.Data).Should().Equal("Looking", " done");
        events.Should().NotContain(e => e.Data.Contains("secret"));
    }

    [Fact]
    public async Task Session_error_ends_with_error_event()
    {
        _runner.Enqueue().Init("sess-e").Delta("partial").Final("Usage limit reached", "sess-e", isError: true);

        var (response, events) = await PostChatAsync(new { prompt = "go" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "headers were already sent when the error occurred");
        events[0].Data.Should().Be("partial");
        events[^1].Event.Should().Be("error");
        var payload = JsonDocument.Parse(events[^1].Data).RootElement;
        payload.GetProperty("message").GetString().Should().Be("Usage limit reached");
        payload.GetProperty("conversationId").GetString().Should().Be("sess-e");
        events.Should().NotContain(e => e.Event == "done");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Missing_prompt_returns_400_without_running(string? prompt)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/chat", new { prompt }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("prompt is required");
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Client_disconnect_cancels_the_cli_run()
    {
        var run = _runner.Enqueue().Init("s").WithDelay(TimeSpan.FromMilliseconds(100));
        for (var i = 0; i < 100; i++)
        {
            run.Delta($"chunk{i} ");
        }

        var client = _factory.CreateClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/chat") { Content = JsonContent.Create(new { prompt = "long" }) };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var reader = new StreamReader(stream);

        var first = await reader.ReadLineAsync(cts.Token);
        first.Should().Be("data: chunk0 ");

        // Disconnect mid-stream: abandon the request and close the connection (TestServer raises
        // RequestAborted when the client disposes the response, as Kestrel does on a socket close).
        await cts.CancelAsync();
        reader.Dispose();
        response.Dispose();

        var runnerToken = _runner.LastCall.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!runnerToken.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        runnerToken.IsCancellationRequested.Should().BeTrue("RequestAborted must reach the runner and kill the CLI run");
        _runner.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Uses_IChatClient_from_DI_backed_by_the_replaced_runner()
    {
        using var scope = _factory.Services.CreateScope();

        var chat = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.AI.IChatClient>();

        chat.GetService(typeof(IClaudeRunner)).Should().BeSameAs(_runner);
        await Task.CompletedTask;
    }
}

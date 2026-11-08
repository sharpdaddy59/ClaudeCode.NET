using StreamingApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddClaudeCodeChatClient(options =>
{
    options.WorkingDirectory = builder.Configuration["Claude:WorkingDirectory"] ?? Directory.GetCurrentDirectory();
    options.DefaultModel = builder.Configuration["Claude:Model"];
});

var app = builder.Build();

app.MapChatSse();

app.Run();

/// <summary>Entry point, public so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;

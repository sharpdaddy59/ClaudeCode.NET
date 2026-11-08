using ClaudeCode;
using PromptWorker;

var builder = Host.CreateApplicationBuilder(args);

var section = builder.Configuration.GetSection(PromptWorkerOptions.SectionName);
builder.Services.Configure<PromptWorkerOptions>(section);
var settings = section.Get<PromptWorkerOptions>() ?? new PromptWorkerOptions();

builder.Services.AddClaudeCodeChatClient(options =>
{
    options.WorkingDirectory = settings.WorkingDirectory ?? Directory.GetCurrentDirectory();
    options.DefaultModel = settings.Model;

    // Wait out a usage limit and resume the same session instead of failing the prompt.
    options.RateLimitWait = RateLimitWaitPolicy.Default;
});

builder.Services.AddSingleton<PromptFileProcessor>();
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();

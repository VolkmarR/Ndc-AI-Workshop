using FinanceAssistant.McpServer;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

builder.Services.AddChatClient(builder.Configuration);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<McpTools>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders("Mcp-Session-Id"));
});

var services = new ServiceCollection();
services.AddChatClient(builder.Configuration);

var app = builder.Build();

McpTools._chatClient = app.Services.GetRequiredService<IChatClient>();



app.UseCors();
app.MapMcp();
app.Run("http://localhost:5050");



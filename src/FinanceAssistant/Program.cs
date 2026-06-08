using FinanceAssistant;
using Pgvector;
using FinanceAssistant.Data;
using Microsoft.Extensions.Configuration;
using FinanceAssistant.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

await using (var db = new FinanceDbContext())
{
    await db.Database.EnsureCreatedAsync();
}

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddChatClient(config);
services.AddEmbeddingGenerator(config);

var provider = services.BuildServiceProvider();

var embedder = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

await using (var db = new FinanceDbContext())
{
    var unembedded = await db.Transactions
        .Where(t => t.Embedding == null)
        .ToListAsync();

    if (unembedded.Count > 0)
    {
        Console.WriteLine($"Embedding {unembedded.Count} transactions...");
        var texts = unembedded.Select(t => $"{t.Merchant} {t.Description}").ToList();
        var embeddings = await embedder.GenerateAsync(texts);
        for (int i = 0; i < unembedded.Count; i++)
        {
            unembedded[i].Embedding = new Vector(embeddings[i].Vector.ToArray());
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"Embedded {unembedded.Count} transactions.");
    }
}

var chatClient = provider.GetRequiredService<IChatClient>();

var convertCurrency = new ConvertCurrencyTool();
var getCurrentTime = new CurrentTimeTool();
var getTransactions = new GetTransactionsTool();
var searchTransactions = new SearchTransactionsTool(embedder);
var importStatementTool = new ImportStatementTool(chatClient);

var chatOptions = new ChatOptions
{
    Tools =
    [
        AIFunctionFactory.Create(convertCurrency.Convert),
        AIFunctionFactory.Create(getCurrentTime.GetCurrentTime),
        AIFunctionFactory.Create(getTransactions.GetTransactions),
        AIFunctionFactory.Create(searchTransactions.SearchTransactions),
        AIFunctionFactory.Create(importStatementTool.ImportStatement)
    ]
};

Console.WriteLine("Finance assistant. Type a message, or 'exit' to quit.");

var systemPrompt = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "SystemPrompt.md"));

var chatAgent = new ChatAgent(chatClient, chatOptions);

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    var messages = new List<ChatMessage>
    {
        new(ChatRole.System, systemPrompt),
        new(ChatRole.User, input)
    };

    // var response = await chatClient.GetResponseAsync(messages, chatOptions);
    // Console.WriteLine(response.Text);

    var reply = await chatAgent.RunTurnAsync(messages);
    Console.WriteLine(reply);
}

return 0;

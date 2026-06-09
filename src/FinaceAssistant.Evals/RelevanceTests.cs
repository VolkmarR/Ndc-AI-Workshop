using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.Configuration;
using OpenAI;
using Xunit;

namespace FinanceAssistant.Evals;

public class RelevanceTests
{
    [Fact]
    public async Task Answer_is_relevant_to_the_request()
    {
        // 1. The judge is just another IChatClient — the same abstraction you
        //    have wired all workshop. Quality evaluators send the response here.
        ChatConfiguration chatConfig = new(CreateJudgeClient());

        // 2. The thing under test: a user request and the agent's answer.
        //    In real life you capture this pair from a live run. Here we hard-code
        //    one so the eval is fast, repeatable, and offline from the agent.
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "List my transactions for May 2025.")
        ];

        ChatResponse response = new(
            new ChatMessage(
                ChatRole.Assistant,
                "Here are your May 2025 transactions: a $1,500 rent payment on the 1st, " +
                "$82.40 of groceries on the 4th, and a $14.99 streaming subscription on the 9th."));

        // 3. Run one evaluator. Relevance asks a single question: did the answer
        //    actually address what the user requested?
        IEvaluator evaluator = new RelevanceEvaluator();
        EvaluationResult result = await evaluator.EvaluateAsync(messages, response, chatConfig);

        // 4. Read the metric and assert on it. Quality metrics score 1–5;
        //    Interpretation gives you pass/fail without inventing a threshold.
        //    Feeding Reason into the assertion message means a red test prints
        //    the judge's reasoning — your error-analysis loop, in the failure.
        NumericMetric relevance = result.Get<NumericMetric>(RelevanceEvaluator.RelevanceMetricName);

        Assert.False(
            relevance.Interpretation?.Failed ?? false,
            $"Relevance failed (scored {relevance.Value}/5): {relevance.Reason}");
    }

    // --- Judge client, built from the SAME user-secrets the agent reads ---
    // Mirrors the agent's construction in ServiceCollectionExtensions.cs: the
    // OpenAI SDK pointed at Azure's OpenAI-compatible endpoint. Because this
    // project shares the agent's UserSecretsId (see the csproj in Step 1), the
    // keys are already populated.
    private static IChatClient CreateJudgeClient()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddUserSecrets<RelevanceTests>()   // shared store via the matching UserSecretsId
            .Build();

        string endpoint = config["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException(
                "AzureOpenAI:Endpoint is missing. Confirm this project's <UserSecretsId> "
                + "matches the agent's, or run 'dotnet user-secrets list' against the agent.");
        string apiKey = config["AzureOpenAI:ApiKey"]!;
        string deployment = config["AzureOpenAI:Deployment"]!;

        // The stored endpoint is the bare resource root. The agent builds the v1
        // path itself, and so do we — keep this identical to the agent's code.
        Uri apiBase = new UriBuilder(endpoint) { Path = "openai/v1/" }.Uri;

        OpenAIClient client = new(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = apiBase });

        // The deployment name is passed as the model id to GetChatClient.
        return client.GetChatClient(deployment).AsIChatClient();
    }
}

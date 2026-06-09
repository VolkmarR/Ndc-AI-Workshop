# Bonus 03 - Evaluate Agent Responses with Microsoft.Extensions.AI

> Bonus material. Optional. Pick up after Pillar 2.

## Mission

Add a small evaluation **test** that scores one of your agent's answers with a real evaluator. The student runs `dotnet test`, the test sends a captured conversation to an LLM judge, the judge returns a relevance score from 1 to 5 with its reasoning, and the test passes or fails on that score.

By the end, "that answer looks fine to me" has become a number a test can fail on, in the same `dotnet test` run as the rest of your suite. The judge is just another `IChatClient`, the same abstraction you have used all workshop.

**Learning Objectives**:

- The evaluator/judge split: the thing under test, and the model that grades it
- Why "looks good" is not a regression test, and what an assertion buys you
- The minimal `IEvaluator.EvaluateAsync` shape: messages in, a graded metric out
- Turning a graded metric into a pass/fail assertion that lives in CI

---

## Prerequisites

- P2.02 finished. The agent has `GetTransactions` and `SearchTransactions` working, and you can run it against a populated database.
- The same model the agent uses, with no new keys to issue. The catch: your credentials live in **.NET user-secrets** under `AzureOpenAI:Endpoint/ApiKey/Deployment`, and user-secrets are scoped to a project's `UserSecretsId`. A brand-new project gets its own empty store and will not see the agent's secrets. So "reuse your credentials" means one line of csproj wiring to point the test project at the *same* store. Step 1 covers it.
- One new test project and four packages on top of the xUnit template. No changes to the agent itself.

> If you did the Bonus Lecture on Evaluations in .NET, this is the hands-on version of slide B5 — "an eval is just a unit test." Same API, fewer slides.

---

## What we're solving

You change the system prompt. Did answer quality go up or down? You swap to a cheaper model. What did you just break? A tool's `[Description]` gets reworded. Does the agent still answer correctly? Today the only way to know is to read responses by hand and trust your gut. That does not scale past the demo, and it does not run on every commit.

An eval turns that judgement call into a score you can assert on. `Microsoft.Extensions.AI.Evaluation` gives you a set of evaluators that take a conversation and return a graded metric. The quality evaluators work by **LLM-as-judge**: they send the response to a model and ask it to grade against a rubric. The judge is an `IChatClient`, the exact abstraction the whole workshop is built on.

The whole point is that the result is an *assertion*, not a printout. A console app that prints "relevance: 5" still needs a human to read it — that is the eval-by-eyeball problem we are trying to kill. A `[Fact]` that fails the build when relevance drops is the thing that actually runs on every commit. So we build this as a test from the start, the smallest possible one: one evaluator, one captured conversation, one assertion.

The judge reuses the agent's model and credentials. This repo configures the model with the **OpenAI SDK** (`OpenAIClient` + `ApiKeyCredential`) pointed at Azure's OpenAI-compatible endpoint. It stores the bare resource root in user-secrets and appends the `openai/v1/` path in code. The test mirrors that exact construction, so there is one client-building pattern to learn, not two.

Two patterns to notice:

1. **The thing under test is data, not a live call.** An eval scores a `(messages, response)` pair. You can hard-code that pair, load it from a file, or capture it from a real run. Decoupling evaluation from generation is what makes evals fast and repeatable. We hard-code one pair here.

2. **The assertion carries the judge's reasoning.** A quality metric is not just a number. It comes with the judge's explanation of why it scored that way, and we feed that explanation into the assertion message — so when the test goes red, the failure tells you *why*. That is your error-analysis loop, built into the test output.

---

## Which evaluator

`Microsoft.Extensions.AI.Evaluation.Quality` ships a family of LLM-as-judge evaluators. The simplest to start with is `RelevanceEvaluator`. It needs only the user's request and the agent's answer. No reference answer, no tool definitions, no experimental opt-in.

> **Why Relevance and not ToolCallAccuracy.** For a tool-calling agent, `ToolCallAccuracyEvaluator` is the evaluator that matters most. It checks whether the agent picked the right tool with the right arguments. We are not starting there because it needs the tool definitions passed as context and is still marked `[Experimental]`, so it wants an extra pragma. Relevance has the smallest possible call shape, which is the right place to learn the API. We wire ToolCallAccuracy in the "What's next" section once the loop is familiar.

Relevance scores 1 to 5. By default the evaluator's interpretation marks anything below a threshold as a failure, so you get the pass/fail your `Assert` needs for free, without inventing your own cutoff.

---

## If you're comfortable, do this

Four steps. Skip the rest if it works on the first try.

1. Create a new xUnit project `tests/FinanceAssistant.Evals`, point its `UserSecretsId` at the agent's store, and add `Microsoft.Extensions.AI.Evaluation`, `Microsoft.Extensions.AI.Evaluation.Quality`, `Microsoft.Extensions.AI.OpenAI`, and `Microsoft.Extensions.Configuration.UserSecrets`.
2. In a helper, read `AzureOpenAI:Endpoint/ApiKey/Deployment` from user-secrets and build the judge `IChatClient` with the same `OpenAIClient` construction the agent uses. Wrap it in a `ChatConfiguration`.
3. Write one `[Fact]`: hard-code a `(messages, response)` pair, run `new RelevanceEvaluator()` over it with `EvaluateAsync`, and `Assert.False(metric.Interpretation!.Failed)`.
4. Run `dotnet test`. Then weaken the answer on purpose and watch the test go red.

---

## Step 1: Create the eval test project

From the repo root:

```bash
dotnet new xunit -o tests/FinanceAssistant.Evals
dotnet sln add tests/FinanceAssistant.Evals
dotnet add tests/FinanceAssistant.Evals package Microsoft.Extensions.AI.Evaluation
dotnet add tests/FinanceAssistant.Evals package Microsoft.Extensions.AI.Evaluation.Quality
dotnet add tests/FinanceAssistant.Evals package Microsoft.Extensions.AI.OpenAI
dotnet add tests/FinanceAssistant.Evals package Microsoft.Extensions.Configuration.UserSecrets
```

The `xunit` template already brings the test SDK and runner, so `dotnet test` works out of the box. The four packages above are the only additions.

Now point the test project at the **same** user-secrets store as the agent. Open the agent's csproj (`src/FinanceAssistant/FinanceAssistant.csproj`) and copy its `<UserSecretsId>` value verbatim. In this repo that value is the string `finance-assistant-workshop` (a user-secrets id is any stable string, not necessarily a GUID). Set the same id in the test csproj so both projects read one shared secret store:

```xml
<!-- tests/FinanceAssistant.Evals/FinanceAssistant.Evals.csproj -->
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <IsPackable>false</IsPackable>
  <!-- The SAME id that appears in FinanceAssistant.csproj -->
  <UserSecretsId>finance-assistant-workshop</UserSecretsId>
</PropertyGroup>
```

> **Why share the `UserSecretsId` instead of re-entering keys.** User-secrets are stored per-id on your machine, not per-project by name. Two projects that declare the same `UserSecretsId` read the same physical file, so `AzureOpenAI:Endpoint/ApiKey/Deployment` set for the agent are visible to the test project for free. Without this line the new project gets a fresh, empty store and the judge throws on a missing key — which is the trap to avoid. (If you would rather not hard-code the id, add a project reference to the agent and load secrets from its assembly with `AddUserSecrets(typeof(FinanceAssistant.Startup).Assembly)`; same effect, no copied id.)

> **Pin package versions, but let the eval family lead.** This repo pins package versions inline in each csproj, so do the same here — an unrelated bump should never move your eval scores. The catch: do *not* blindly match the agent's versions. The `Microsoft.Extensions.AI.Evaluation.*` packages lead (currently `10.6.0`), and `Microsoft.Extensions.AI.Evaluation` depends on the matching `Microsoft.Extensions.AI` / `Microsoft.Extensions.AI.OpenAI`. So the OpenAI adapter must be pulled *up* to the eval family's version (`10.6.0`), not pinned to the agent's older `10.5.2`, or you get a dependency conflict. Keep the eval and OpenAI packages together at the same version, and pin `Microsoft.Extensions.Configuration.UserSecrets` to whatever the agent uses (`10.0.7`).

> **Why a separate project and not a folder in the agent.** Evals are a test harness that points at the app, not part of the shipping app. Keeping them in their own test project under `tests/` means they pull their own packages, run under `dotnet test`, and never get deployed by accident. It also drops straight into whatever CI already runs your tests.

> **A note on `tests/` vs `src/` in this repo.** Every existing project here lives under `src/` (`src/FinanceAssistant`, `src/FinanceAssistant.McpServer`). Put the evals under `tests/` anyway — a test harness is conventionally separated from shipping source, and the location does not affect the build. If you see a stray `src/FinanceAssistant.Evals/` with only `bin/` and `obj/` (no csproj, not in the solution) left over from an earlier attempt, delete it so it does not confuse you about which path is live. The one in `tests/` is the real one.

---

## Step 2: Write the eval test

Delete the template's placeholder test file (`UnitTest1.cs`) and add `tests/FinanceAssistant.Evals/RelevanceTests.cs`:

```csharp
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
```

> **What `?? false` is doing.** `relevance.Interpretation?.Failed ?? false` treats a *missing* interpretation as a pass. `RelevanceEvaluator` always supplies one, so this is safe here. But if you later swap in an evaluator that does not set an interpretation, the test would pass silently — there would be no pass/fail verdict to assert on. In that case, assert on `relevance.Value` against your own threshold instead.

> **Seeing the score on a passing test.** The assertion above prints the judge's reasoning whenever the test *fails*, which is when you most want it. To also log the score on a *passing* run, inject xUnit's `ITestOutputHelper` through the constructor and write to it instead of using `Console.WriteLine` (xUnit does not capture console output). Mind the namespace: it is `Xunit.Abstractions` on xUnit v2 (which is what the current `dotnet new xunit` template scaffolds) and `Xunit` on xUnit v3.

> **About `AsIChatClient`.** The provider SDK gives you a chat client in its own shape. `AsIChatClient()` adapts it to the `Microsoft.Extensions.AI` interface the evaluator expects. This is the same adapter the agent uses to get its `IChatClient`. The punchline of the whole evaluation story is right here: the judge and the agent are the same kind of object, built from the same secrets.

> **Build the v1 path, do not store it.** This repo stores `AzureOpenAI:Endpoint` as the bare resource root (`https://<resource>.openai.azure.com/`) and appends `openai/v1/` in code with `UriBuilder`, exactly as `ServiceCollectionExtensions.cs` does. That is why the plain `OpenAIClient` (not `AzureOpenAIClient`) works against it. If you pass the stored root straight to `OpenAIClientOptions.Endpoint` without appending the path, the call will 404. Mirror the agent line for line.

> **Pick a capable judge.** The quality evaluators are tuned for GPT-4o / GPT-4.1 class models. A small or local model grades unreliably and you will chase scores that move for the wrong reasons. Use the same model family you use for the agent, or a stronger one.

---

## Step 3: Run

```bash
dotnet test --filter "FullyQualifiedName~Answer_is_relevant_to_the_request"
```

The test should pass (green). Now break it on purpose. Change the assistant message to something that ignores the question:

```csharp
ChatResponse response = new(
    new ChatMessage(ChatRole.Assistant,
        "Budgeting is a great habit. Have you considered the 50/30/20 rule?"));
```

Run `dotnet test` again. The test should now **fail**, and the failure message carries the judge's reasoning — something like `Relevance failed (scored 1/5): The response never addresses the request to list May 2025 transactions.` That red test, with the explanation attached, is the entire value of an eval: a quality regression that fails the build exactly like a broken unit test.

---

## Troubleshooting

### `InvalidOperationException: AzureOpenAI:Endpoint is missing`

The test project is reading an empty secret store. Almost always this means its `<UserSecretsId>` does not match the agent's. Open both csproj files and confirm the ids are identical, character for character — in this repo it is the string `finance-assistant-workshop`. Verify the secrets actually exist by running `dotnet user-secrets list --project src/FinanceAssistant` — you should see `AzureOpenAI:Endpoint`, `AzureOpenAI:ApiKey`, and `AzureOpenAI:Deployment`. If they are there but the test still cannot see them, the id is the culprit.

### `404 Not Found` or `Resource not found` when the judge runs

You passed the bare resource root to `OpenAIClientOptions.Endpoint` without appending the `openai/v1/` path. The stored `AzureOpenAI:Endpoint` is the root by design; the v1 path is built in code. Confirm your `UriBuilder(endpoint) { Path = "openai/v1/" }` line is present and matches the agent's construction.

### The test passes but I never see the score

A passing xUnit test shows no output by default, and xUnit does not capture `Console.WriteLine` at all. Either rely on the assertion message — a failing test always prints the judge's score and reasoning, which is when you most want it — or inject `ITestOutputHelper` (see the note under Step 2) to log the score on green runs too.

### `Reason` is empty in the output

Some models return a grade without an explanation if the evaluator's prompt is truncated. Confirm your judge deployment is a full chat model, not a tiny completion model, and that you did not cap `MaxOutputTokens` somewhere low. The reasoning is the most useful part of the result. Do not lose it.

### The test always passes, even on bad answers

Either your test answer really is relevant, or your judge is too weak to discriminate. Run the "break it on purpose" step. If a clearly off-topic answer still passes, swap the judge for a stronger model. A judge that cannot fail a bad answer is not measuring anything.

### `Get<NumericMetric>` throws `KeyNotFoundException`

The metric name does not match the evaluator. Each evaluator exposes its own metric name constant. For `RelevanceEvaluator` it is `RelevanceEvaluator.RelevanceMetricName`. If you swap evaluators, swap the constant too, for example `CoherenceEvaluator.CoherenceMetricName`.

### Build error: `AsIChatClient` does not exist

You are on an older `Microsoft.Extensions.AI` where the adapter was named `AsChatClient`. Update the package, or use the older name. The rename happened as the abstractions stabilised.

---

## You can now

Assert on answer quality instead of eyeballing it:

- Run `dotnet test` and have a quality regression fail the build like any other test.
- Read the judge's reasoning straight from the failure message when a test goes red.
- Swap `RelevanceEvaluator` for `CoherenceEvaluator` with a two-line change and assert on a different quality dimension.

You also have the smallest possible eval loop, already in test shape: a judge that is an `IChatClient`, a conversation that is plain data, an evaluator that returns a graded metric, and an assertion that lives in `dotnet test`. Everything past here is scaling this shape up.

---

## Summary

You've added:

- **`tests/FinanceAssistant.Evals`**: an xUnit project that scores one agent response and asserts on it.
- **A judge `IChatClient`**: the same abstraction as the agent, reused as the grader, built from the same shared user-secrets.
- **One quality metric turned into an assertion**: `Interpretation.Failed` drives the pass/fail, and `Reason` rides along in the failure message.
- **A deliberate-failure check**: weaken the answer, watch the test go red with the judge's explanation attached.

---

## What's next

You already have the test in CI shape. Three natural steps build on it.

1. **Score the tool call, not just the prose.** Add a second test using `ToolCallAccuracyEvaluator` to check the Pillar 2 behaviour directly: given "list May 2025 transactions", did the agent call `GetTransactions` with the right ISO date range? This evaluator takes your tool definitions as context and is still `[Experimental]`, so add `#pragma warning disable AIEVAL001` around the construction. This is the evaluator that matters most for the agent you built.

2. **Persist results and see them move.** Add `Microsoft.Extensions.AI.Evaluation.Reporting`, swap the bare `EvaluateAsync` for a `ReportingConfiguration` + `CreateScenarioRunAsync` (response caching makes re-runs cheap), then render an HTML report:

   ```bash
   dotnet tool install Microsoft.Extensions.AI.Evaluation.Console --create-manifest-if-needed
   dotnet aieval report -p ./eval-results -o ./report.html --open
   ```

   The report carries each score's reasoning and trends across runs, so you can prove a prompt change moved the numbers instead of hoping it did.

3. **Grow the eval set.** Capture a handful of real conversations from the finance assistant, drop each into a `[Theory]` with one `[InlineData]` per case, and assert on all of them. A dozen cases across your tools is enough to catch most prompt and model regressions before they ship.

Each step reinforces the same lesson the bonus opened with: an eval turns a judgement call into a score you can re-run.

---

## Additional Resources

- [The Microsoft.Extensions.AI.Evaluation libraries](https://learn.microsoft.com/en-us/dotnet/ai/evaluation/libraries)
- [Tutorial: evaluate response quality with caching and reporting](https://learn.microsoft.com/en-us/dotnet/ai/evaluation/evaluate-with-reporting)
- [Exploring the agent quality and NLP evaluators (.NET Blog)](https://devblogs.microsoft.com/dotnet/exploring-agent-quality-and-nlp-evaluators/)
- The methodology — Hamel Husain & Shreya Shankar, *AI Evals* (the take-home from the morning activity)
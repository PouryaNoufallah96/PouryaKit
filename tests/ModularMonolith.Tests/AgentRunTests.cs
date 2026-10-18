using System.Text.Json;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.AskAboutProducts;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.GetProduct;
using Catalog.Products.Features.RenameProduct;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.AI;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// Ticket #136: agents of the Agent Framework over the Host's one chat client, in each run shape and each workflow shape,
// with sessions and memory over the application's stores, a chat reducer, the guardrail hook and scheduled work. Each run
// that calls a tool executes inside an authorized Operation, as a handler runs it, so the #135 guarantee is asserted
// whole: each call an Operation through Catalog's Gateway continuing the run, trusted, with the run's Actor, tenant and
// correlation. Against the in-process ChatStub, deterministic (spec #115's A2).
public sealed class AgentRunTests(AppFixture app, AiFixture ai) : IClassFixture<AiFixture>
{
    private const string Instructions = "You answer questions about the shop.";
    private const string Refusal = "The guardrail refused.";

    private static readonly Actor Ada = Actor.Person("ada");

    private IChatClient Chat => ai.Host.Services.GetRequiredService<IChatClient>();

    private IGatewayProcessor<CatalogModule> Catalog => ai.Host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();

    [Fact]
    public async Task A_plain_run_completes_and_each_tool_call_is_an_operation_that_continues_the_run()
    {
        var (product, name) = await CreateProductAsync();
        ai.Stub.Script(ChatReply.Calls(GetProduct(product)), ChatReply.Text($"It is the {name}."));

        var (run, answer) = await RunInsideAsync(async (context, cancellationToken) =>
            (await Agent(context).RunAsync($"What is {product} called?", cancellationToken: cancellationToken)).Text
        );

        Assert.Equal($"It is the {name}.", answer);
        AssertEachCallContinues(run, calls: 1);
        // The allow-list holds for an agent's run: RenameProduct, which Tools does not name, is never offered.
        Assert.Equal(["GetProduct"], ai.Stub.ToolsOf(0));
    }

    [Fact]
    public async Task A_streaming_run_streams_its_answer_and_stops_calling_tools_at_the_iteration_cap()
    {
        var (product, _) = await CreateProductAsync();
        ai.Stub.Script([.. Enumerable.Repeat(ChatReply.Calls(GetProduct(product)), AiFixture.MaximumIterations), ChatReply.Text("Looked it up.")]);
        List<string> parts = [];

        var (run, answer) = await RunInsideAsync(async (context, cancellationToken) =>
        {
            await foreach (var update in Agent(context).RunStreamingAsync("Look it up.", cancellationToken: cancellationToken))
            {
                if (update.Text is { Length: > 0 } text)
                {
                    parts.Add(text);
                }
            }

            return string.Concat(parts);
        });

        Assert.Equal("Looked it up.", answer);
        Assert.True(parts.Count > 1, "The answer arrived whole, not streamed.");
        Assert.All(ai.Stub.Requests, request => Assert.True(request.GetProperty("stream").GetBoolean()));
        AssertEachCallContinues(run, calls: AiFixture.MaximumIterations);
        Assert.Equal(
            [.. Enumerable.Repeat<IReadOnlyList<string>>(["GetProduct"], AiFixture.MaximumIterations), []],
            Enumerable.Range(0, ai.Stub.Requests.Count).Select(ai.Stub.ToolsOf)
        );
    }

    [Fact]
    public async Task A_structured_run_asks_for_its_schema_and_returns_the_typed_answer()
    {
        var (product, name) = await CreateProductAsync();
        ai.Stub.Script(ChatReply.Calls(GetProduct(product)), ChatReply.Text(JsonSerializer.Serialize(new { name })));

        var (run, answer) = await RunInsideAsync(async (context, cancellationToken) =>
            (await Agent(context).RunAsync<ProductName>($"Name {product}.", cancellationToken: cancellationToken)).Result.Name
        );

        Assert.Equal(name, answer);
        Assert.Equal("json_schema", ai.Stub.Requests[^1].GetProperty("response_format").GetProperty("type").GetString());
        AssertEachCallContinues(run, calls: 1);
    }

    [Fact]
    public async Task A_sequential_workflow_gives_each_agent_what_the_one_before_answered()
    {
        var (product, name) = await CreateProductAsync();
        ai.Stub.Script(ChatReply.Calls(GetProduct(product)), ChatReply.Text($"Found the {name}."), ChatReply.Text("Checked."));

        var (run, answer) = await RunInsideAsync(async (context, cancellationToken) =>
        {
            var workflow = AgentWorkflowBuilder.BuildSequential(Agent(context, "finder"), Agent(context, "checker"));
            return (await workflow.AsAIAgent().RunAsync($"Find {product}.", cancellationToken: cancellationToken)).Text;
        });

        AssertEachCallContinues(run, calls: 1);
        Assert.Contains($"Found the {name}.", TextsOf(2));
        Assert.EndsWith("Checked.", answer);
    }

    [Fact]
    public async Task A_concurrent_workflow_runs_each_agent_on_the_question_and_gathers_their_answers()
    {
        var (product, _) = await CreateProductAsync();
        // Each agent looks the product up, then answers in its own name, in whatever order their requests arrive.
        ai.Stub.Script(body =>
            Messages(body).Last().GetProperty("role").GetString() == "tool"
                ? ChatReply.Text(body.GetRawText().Contains("You price", StringComparison.Ordinal) ? "Priced." : "Described.")
                : ChatReply.Calls(GetProduct(product))
        );

        var (run, answer) = await RunInsideAsync(async (context, cancellationToken) =>
        {
            var workflow = AgentWorkflowBuilder.BuildConcurrent(
                [Agent(context, "pricer", "You price products."), Agent(context, "describer", "You describe products.")]
            );
            return (await workflow.AsAIAgent().RunAsync($"Tell me about {product}.", cancellationToken: cancellationToken)).Text;
        });

        AssertEachCallContinues(run, calls: 2);
        Assert.Contains("Priced.", answer, StringComparison.Ordinal);
        Assert.Contains("Described.", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_handoff_workflow_hands_the_conversation_to_the_agent_the_model_chooses()
    {
        var (product, name) = await CreateProductAsync();
        ai.Stub.Script(
            ChatReply.Calls(new ToolCall("handoff_to_1", new { reasonForHandoff = "A question about a product." })),
            ChatReply.Calls(GetProduct(product)),
            ChatReply.Text($"It is the {name}.")
        );

        var (run, answer) = await RunInsideAsync(async (context, cancellationToken) =>
        {
            var triage = Chat.AsAIAgent("You hand each question to the agent that answers it.", "triage");
            var workflow = AgentWorkflowBuilder
                .CreateHandoffBuilderWith(triage)
                .WithHandoff(triage, Agent(context, "expert"), "Answers questions about one product.")
                .Build();
            return (await workflow.AsAIAgent().RunAsync($"What is {product} called?", cancellationToken: cancellationToken)).Text;
        });

        // The handoff is a declaration the client never invokes, so the allow-list offers it unnamed.
        Assert.Equal(["handoff_to_1"], ai.Stub.ToolsOf(0));
        AssertEachCallContinues(run, calls: 1);
        Assert.EndsWith($"It is the {name}.", answer);
    }

    [Fact]
    public async Task A_session_saved_in_the_applications_store_carries_its_history_into_a_run_in_a_new_scope()
    {
        var sessions = new SessionStore();
        var key = $"conversation-{Guid.CreateVersion7():N}";
        ai.Stub.Script(ChatReply.Text("Hello, Ada."), ChatReply.Text("You are Ada."));

        await RunInsideAsync(async (_, cancellationToken) =>
        {
            var agent = Chat.AsAIAgent(Instructions);
            var session = await agent.LoadSessionAsync(sessions, key, cancellationToken);
            var answer = await agent.RunAsync("My name is Ada.", session, cancellationToken: cancellationToken);
            await agent.SaveSessionAsync(sessions, key, session, cancellationToken);
            return answer.Text;
        });
        // Another Operation, in a scope of its own, with a new agent: only the application's store links it to the first.
        await RunInsideAsync(async (_, cancellationToken) =>
        {
            var agent = Chat.AsAIAgent(Instructions);
            return (await agent.RunAsync("What is my name?", await agent.LoadSessionAsync(sessions, key, cancellationToken), cancellationToken: cancellationToken)).Text;
        });

        Assert.Equal(["My name is Ada.", "Hello, Ada.", "What is my name?"], TextsOf(1));
        Assert.Equal(1, sessions.Saves);
    }

    [Fact]
    public async Task A_counting_reducer_keeps_the_history_a_run_sends_to_its_last_messages()
    {
        ai.Stub.Script(ChatReply.Text("1."), ChatReply.Text("2."), ChatReply.Text("3."));
#pragma warning disable MEAI001 // Microsoft.Extensions.AI 10.11's reducers are experimental.
        var agent = Chat.AsAIAgent(
            new ChatClientAgentOptions { ChatHistoryProvider = new InMemoryChatHistoryProvider(new() { ChatReducer = new MessageCountingChatReducer(2) }) }
        );
#pragma warning restore MEAI001
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        foreach (var turn in new[] { "One.", "Two.", "Three." })
        {
            await agent.RunAsync(turn, session, cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(["Two.", "2.", "Three."], TextsOf(2));
    }

    [Fact]
    public async Task A_summarizing_reducer_replaces_the_older_history_with_the_models_summary()
    {
        ai.Stub.Script(ChatReply.Text("1."), ChatReply.Text("The user counted to one."), ChatReply.Text("2."));
#pragma warning disable MEAI001 // Microsoft.Extensions.AI 10.11's reducers are experimental.
        var agent = Chat.AsAIAgent(
            new ChatClientAgentOptions
            {
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new() { ChatReducer = new SummarizingChatReducer(Chat, targetCount: 1, threshold: 0) }),
            }
        );
#pragma warning restore MEAI001
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await agent.RunAsync("One.", session, cancellationToken: TestContext.Current.CancellationToken);
        await agent.RunAsync("Two.", session, cancellationToken: TestContext.Current.CancellationToken);

        // The second run first asked the Host's client for the summary, then sent it in place of what it summarized.
        Assert.Contains("One.", TextsOf(1));
        Assert.Equal(["The user counted to one.", "1.", "Two."], TextsOf(2));
    }

    [Fact]
    public async Task Memory_as_a_context_provider_reads_and_writes_through_the_applications_store()
    {
        var memories = new MemoryStore();
        ai.Stub.Script(ChatReply.Text("Noted."), ChatReply.Text("Green."));

        // Two runs, each with an agent and a provider of its own: what the second knows came through the store.
        foreach (var turn in new[] { "I like green.", "Which colour do I like?" })
        {
            var agent = Chat.AsAIAgent(
                new ChatClientAgentOptions { ChatOptions = new() { Instructions = Instructions }, AIContextProviders = [new Remembered(memories, Ada)] }
            );
            await agent.RunAsync(turn, cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(["I like green.", "Which colour do I like?"], memories.Of(Ada));
        Assert.DoesNotContain("I like green.", SystemOf(0));
        Assert.Contains("The user told you: I like green.", SystemOf(1));
    }

    [Fact]
    public async Task The_guardrail_is_called_before_and_after_each_model_call_and_a_refused_request_never_reaches_the_model()
    {
        var guardrail = new Guardrail { Refuses = "request" };
        await using var host = GuardedHost(guardrail);
        ai.Stub.Script(ChatReply.Text("Hello."));
        var chat = host.Services.GetRequiredService<IChatClient>();

        var refused = await chat.GetResponseAsync("Hello.", cancellationToken: TestContext.Current.CancellationToken);
        var answered = await chat.GetResponseAsync("Hello again.", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((Refusal, ChatFinishReason.ContentFilter), (refused.Text, refused.FinishReason));
        Assert.Equal("Hello.", answered.Text);
        // The refused call reached no model; the one the hook let go was checked on its way out and on its way back.
        Assert.Single(ai.Stub.Requests);
        Assert.Equal(["request", "request", "response"], guardrail.Checks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_response_the_guardrail_refuses_is_replaced_and_no_tool_call_it_asked_for_runs(bool streaming)
    {
        var guardrail = new Guardrail { Refuses = "response" };
        await using var host = GuardedHost(guardrail);
        ai.Stub.Script(ChatReply.Calls(GetProduct(Guid.CreateVersion7())), ChatReply.Text("Looked it up."));
        var run = new OperationContext(Ada, AppFixture.TestTenant);
        var catalog = host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
        var agent = host.Services.GetRequiredService<IChatClient>().AsAIAgent(Instructions, tools: [GetProductTool(catalog, run)]);

        var answer = streaming
            ? string.Concat(await agent.RunStreamingAsync("Look it up.", cancellationToken: TestContext.Current.CancellationToken).Select(update => update.Text).ToListAsync(TestContext.Current.CancellationToken))
            : (await agent.RunAsync("Look it up.", cancellationToken: TestContext.Current.CancellationToken)).Text;

        Assert.Equal(Refusal, answer);
        Assert.Single(ai.Stub.Requests);
        Assert.Empty(SeenBy(host, run));
    }

    [Fact]
    public async Task A_scheduled_run_executes_as_its_named_system_actor_and_its_tool_calls_carry_that_actor()
    {
        const string Digest = "stub.agent-digest";
        const string DigestAgent = "stub.digest-agent";
        // A Host of its own, on a database and a model of its own, so its occurrences touch no other test's.
        await using var stub = await ChatStub.StartAsync(TestContext.Current.CancellationToken);
        stub.Script(ChatReply.Calls(new ToolCall("ReadOperationContext", new { })), ChatReply.Text("The digest is written."));
        var database = $"agent_{Guid.CreateVersion7():N}";
        await app.RowsAsync($"create database {database}");
        await using var host = AppFixture.ComposeHost(
            "Development",
            new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = database }.ConnectionString,
            new RecordedLogs(),
            builder =>
                builder
                    .Services.AddPouryaKitRecurringCommand(Digest, DigestAgent, occurrence => new RunScheduledAgentCommand(occurrence), typeof(StubModule))
                    .Configure(options => options.Every = TimeSpan.FromSeconds(1)),
            [.. AiFixture.StubArguments(stub), "--PouryaKit:AI:Tools:3=ReadOperationContext"]
        );
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        try
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            waiting.CancelAfter(TimeSpan.FromSeconds(30));
            while (stub.Requests.Count < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), waiting.Token);
            }
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // What the tool's Operation ran with, as it answered the model: the occurrence's system Actor, continued.
        var call = JsonDocument.Parse(stub.ToolResultsOf(1)[0]).RootElement;
        var occurrence = Assert.Single(DeliveryProbes.ContextsOf(Digest), context => context.OperationId == call.GetProperty("causationId").GetGuid());
        Assert.Equal(Actor.System(DigestAgent), occurrence.Actor);
        Assert.Equal(
            (DigestAgent, (int)ActorKind.System, JsonValueKind.Null, occurrence.CorrelationId, true),
            (
                call.GetProperty("actor").GetProperty("id").GetString(),
                call.GetProperty("actor").GetProperty("kind").GetInt32(),
                call.GetProperty("tenant").ValueKind,
                call.GetProperty("correlationId").GetGuid(),
                call.GetProperty("isTrustedContinuation").GetBoolean()
            )
        );
    }

    // Runs the agents inside an authorized Operation of Ada's, as a handler does, and returns that Operation's context.
    private async Task<(OperationContext Run, string Answer)> RunInsideAsync(Func<OperationContext, CancellationToken, Task<string>> agents)
    {
        OperationContext? run = null;
        var answered = await ai.Host.Services.GetRequiredService<IGatewayProcessor<StubModule>>()
            .QueryAsync(
                new RunAgentQuery((context, cancellationToken) =>
                {
                    run = context;
                    return agents(context, cancellationToken);
                }),
                new OperationContext(Ada, AppFixture.TestTenant),
                TestContext.Current.CancellationToken
            );
        Assert.True(answered.IsSuccess, string.Join(", ", answered.Errors.Select(error => error.Code)));
        return (run!, answered.Value);
    }

    // Each GetProduct call of the run was an Operation of its own through Catalog's Gateway, continuing the run, trusted.
    private void AssertEachCallContinues(OperationContext run, int calls)
    {
        var seen = SeenBy(ai.Host, run);
        Assert.Equal(calls, seen.Count);
        Assert.All(
            seen,
            call => Assert.Equal(
                (run.Actor, run.Tenant, run.CorrelationId, (Guid?)run.OperationId, (string?)null, true),
                (call.Actor, call.Tenant, call.CorrelationId, call.CausationId, call.RequestId, call.IsTrustedContinuation)
            )
        );
        Assert.Equal(calls + 1, seen.Select(call => call.OperationId).Append(run.OperationId).Distinct().Count());
    }

    private static List<OperationContext> SeenBy(IHost host, OperationContext run) =>
        [.. host.Services.GetRequiredService<CatalogQuestionContexts>().Seen.Where(seen => seen.CorrelationId == run.CorrelationId)];

    private AIAgent Agent(OperationContext run, string? name = null, string instructions = Instructions) =>
        Chat.AsAIAgent(
            instructions,
            name,
            tools:
            [
                GetProductTool(Catalog, run),
                Catalog.Tool("RenameProduct", "Renames a product.", run, (ProductToRename product) => new RenameProductCommand(product.ProductId, product.Name)),
            ]
        );

    private static AIFunction GetProductTool(IGatewayProcessor<CatalogModule> catalog, OperationContext run) =>
        catalog.Tool("GetProduct", "Finds one product by its id.", run, (ProductToFind product) => new GetProductQuery(product.ProductId));

    private WebApplication GuardedHost(Guardrail guardrail) =>
        AppFixture.ComposeHost(
            "Development",
            app.ConnectionString,
            new RecordedLogs(),
            builder => builder.Services.AddSingleton<IChatGuardrail>(guardrail),
            AiFixture.StubArguments(ai.Stub)
        );

    private static IEnumerable<JsonElement> Messages(JsonElement request) => request.GetProperty("messages").EnumerateArray();

    // The text of each message the request at index sent the model but its instructions, in order.
    private IReadOnlyList<string> TextsOf(int index) =>
        [.. Messages(ai.Stub.Requests[index]).Where(message => message.GetProperty("role").GetString() is not ("system" or "developer")).Select(TextOf)];

    private string SystemOf(int index) =>
        string.Concat(Messages(ai.Stub.Requests[index]).Where(message => message.GetProperty("role").GetString() is "system" or "developer").Select(TextOf));

    // A message that only calls tools has no content.
    private static string TextOf(JsonElement message) =>
        message.TryGetProperty("content", out var content) && content.ValueKind is JsonValueKind.Array
            ? string.Concat(content.EnumerateArray().Select(part => part.GetProperty("text").GetString()))
            : content.ValueKind is JsonValueKind.String ? content.GetString()! : "";

    private async Task<(Guid Product, string Name)> CreateProductAsync()
    {
        var (category, product, name) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Unique("Lamp"));
        Assert.True((await Catalog.SendAsync(new CreateCategoryCommand(category, Unique("Lamps")), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await Catalog.SendAsync(new CreateProductCommand(product, category, name), TestContext.Current.CancellationToken)).IsSuccess);
        return (product, name);
    }

    private static ToolCall GetProduct(Guid product) => new("GetProduct", new { productId = product });

    private static string Unique(string name) => $"{name} {Guid.CreateVersion7():N}";

    private sealed record ProductName(string Name);

    private sealed record ProductToRename(Guid ProductId, string Name);

    // The application's store of sessions, standing in for a table: the Kit gives it each session's JSON to keep.
    private sealed class SessionStore : IAgentSessionStore
    {
        private readonly Dictionary<string, JsonElement> _saved = [];

        public int Saves { get; private set; }

        public Task<JsonElement?> FindAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<JsonElement?>(_saved.TryGetValue(key, out var session) ? session : null);

        public Task SaveAsync(string key, JsonElement session, CancellationToken cancellationToken)
        {
            _saved[key] = session.Clone();
            Saves++;
            return Task.CompletedTask;
        }
    }

    // The application's store of what each Actor told its agents.
    private sealed class MemoryStore
    {
        private readonly Dictionary<Actor, List<string>> _told = [];

        public IReadOnlyList<string> Of(Actor actor) => _told.GetValueOrDefault(actor) ?? [];

        public void Add(Actor actor, IEnumerable<string> told)
        {
            if (!_told.TryGetValue(actor, out var list))
            {
                _told[actor] = list = [];
            }

            list.AddRange(told);
        }
    }

    // Memory as a context provider: before a run it gives the agent what the Actor told it before, after the run it keeps
    // what the Actor said.
    private sealed class Remembered(MemoryStore memories, Actor actor) : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(memories.Of(actor) is [] ? new AIContext() : new AIContext { Instructions = $"The user told you: {string.Join(" ", memories.Of(actor))}" });

        protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
        {
            memories.Add(actor, context.RequestMessages.Where(message => message.Role == ChatRole.User).Select(message => message.Text));
            return ValueTask.CompletedTask;
        }
    }

    // A guardrail with no policy but the test's: it records each check and refuses the first one of the named kind, once.
    private sealed class Guardrail : IChatGuardrail
    {
        public string? Refuses { get; set; }

        public List<string> Checks { get; } = [];

        public Task<string?> CheckRequestAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) => Check("request");

        public Task<string?> CheckResponseAsync(ChatResponse response, CancellationToken cancellationToken) => Check("response");

        private Task<string?> Check(string kind)
        {
            Checks.Add(kind);
            if (Refuses != kind)
            {
                return Task.FromResult<string?>(null);
            }

            Refuses = null;
            return Task.FromResult<string?>(Refusal);
        }
    }
}

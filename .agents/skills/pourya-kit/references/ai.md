# AI

`PouryaKit.AI`: the Host's one chat client, `IChatClient` of Microsoft.Extensions.AI, built from validated options for OpenAI, Azure OpenAI or Ollama through the OpenAI client alone; the Gateway tool, which offers a Module's command or query to a model and runs each call as an Operation through the Module's Gateway; and agents of the Microsoft Agent Framework over that client, in plain, streaming and structured runs and in sequential, concurrent and handoff workflows, with sessions and memory over the application's stores, a chat reducer and a guardrail hook; and the Host's clients of the MCP servers its options name, over stdio or HTTP, whose tools a run is given beside its Gateway tools. It references `PouryaKit.Cqrs` and `PouryaKit.Modularity`. Namespace `PouryaKit.AI`. Embeddings are not built yet.

- [Registering](#registering)
- [A Gateway tool](#a-gateway-tool)
- [MCP tools](#mcp-tools)
- [Agent runs](#agent-runs)
- [Sessions](#sessions)
- [A chat reducer](#a-chat-reducer)
- [Memory](#memory)
- [Workflows](#workflows)
- [Guardrails](#guardrails)
- [Scheduled agent work](#scheduled-agent-work)
- [What the Kit guarantees](#what-the-kit-guarantees)
- [Limits](#limits)
- [Testing](#testing)

## Registering

On the Host:

```csharp
using PouryaKit.AI;

builder.Services.AddPouryaKitAI();
builder.Services.Configure<PouryaKitAIOptions>(builder.Configuration.GetSection("PouryaKit:AI"));
```

```json
"PouryaKit": {
  "AI": {
    "Provider": "Ollama",
    "Endpoint": "http://localhost:11434/v1",
    "Model": "qwen2.5:0.5b",
    "MaximumIterationsPerRun": 10,
    "Tools": ["GetProduct"]
  }
}
```

- `AddPouryaKitAI()` registers the Host's one `IChatClient`, a singleton. A Host that registers another unkeyed `IChatClient`, before or after the call, fails when its container is built, naming each registration, and so does one that removes it after the call, naming the contract: the registration adds a composition check, since a second client would serve runs without the allow-list and the cap.
- Locally, an Aspire AppHost runs the model beside the Host and sets `PouryaKit__AI__Endpoint` and `PouryaKit__AI__Model` on it ([aspire.md](aspire.md#a-local-agent)).
- `PouryaKitAIOptions.Provider` (`AIProvider`): `OpenAI` (the default; `Endpoint` may stay null for OpenAI's own API), `AzureOpenAI` (`Endpoint` is the resource's v1 endpoint, such as `https://my-resource.openai.azure.com/openai/v1/`, and `Model` the deployment's name) or `Ollama` (`Endpoint` is its OpenAI-compatible API, `http://<host>:11434/v1`, and no key). Each goes through the OpenAI client's chat completions API: no OllamaSharp and no `Azure.AI.OpenAI` (spec #115's A1).
- `ApiKey` is required for OpenAI and Azure OpenAI. Set it from user secrets or the environment, as `PouryaKit__AI__ApiKey`, never in a committed file; the Kit never logs it.
- `MaximumIterationsPerRun`, 10 by default, caps how many times a run calls the model and invokes the tools it asks for; `Tools` is the allow-list, the names of the tools a run may be given, empty by default. A declaration that is no function, which the client never invokes and hands back to its caller, such as a handoff workflow's `handoff_to_1`, passes unnamed.
- The Host fails when it starts, naming the option, when the provider is not an `AIProvider` value (a number such as `99` binds), `Model` is blank, `Endpoint` is not an absolute HTTP or HTTPS address or is missing for Azure OpenAI or Ollama, `ApiKey` is missing for OpenAI or Azure OpenAI, the cap is below 1, or a tool name is blank.
- The client sends through the HTTP client named `AIServiceCollectionExtensions.HttpClientName` (`PouryaKit.AI`). The package adds no resilience handler and turns the OpenAI client's own retries off, so the client keeps whatever the Host's defaults give every client: with the service defaults ([host-recipes.md](host-recipes.md)), their one pipeline, which retries safe methods only, so a chat completion, a POST, is sent once. That pipeline's attempt timeout is 10 seconds; give the client its own limits by replacing it, as for any client: `builder.Services.AddHttpClient(AIServiceCollectionExtensions.HttpClientName).RemoveAllResilienceHandlers()`, then `AddPouryaKitResilience().Configure(...)` ([web.md](web.md)). Adding a second pipeline instead is refused when the client is created.
- The client's pipeline, outermost first: the allow-list, function invocation with the cap, the application's guardrails when it registers any ([Guardrails](#guardrails)), OpenTelemetry with sensitive data off, and logging. The telemetry step writes a span and duration and token metrics for each call to the model, on the activity source and meter Experimental.Microsoft.Extensions.AI, which the service defaults' tracing and metrics export ([host-recipes.md](host-recipes.md#what-each-recipe-does)); a Host with an exporter of its own adds that name.

## A Gateway tool

A run is one `GetResponseAsync` or `GetStreamingResponseAsync` call of the Host's `IChatClient`, given its tools in `ChatOptions.Tools`, as each agent's run makes. `Tool` on a Module's `IGatewayProcessor<TModule>` makes a command or query a Gateway tool for one run's Operation Context. The model's arguments are read with the Kit's JSON into an arguments record, whose schema the model is given, and mapped explicitly to the operation:

```csharp
using Microsoft.Extensions.AI;
using PouryaKit.AI;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;

internal sealed class AskAboutProductsQueryHandler(IChatClient chat, IGatewayProcessor<CatalogModule> catalog, OperationContext context)
    : QueryHandler<AskAboutProductsQuery, string>
{
    public override async Task<Result<string>> Handle(AskAboutProductsQuery query, CancellationToken cancellationToken)
    {
        var getProduct = catalog.Tool(
            "GetProduct",
            "Finds one product of the catalog by its id: its id, its category's id and its name.",
            context,
            (ProductToFind product) => new GetProductQuery(product.ProductId)
        );
        var answer = await chat.GetResponseAsync(
            [new(ChatRole.System, "You answer questions about a shop's catalog."), new(ChatRole.User, query.Question)],
            new ChatOptions { Tools = [getProduct] },
            cancellationToken
        );
        return answer.Text;
    }
}

internal sealed record ProductToFind(Guid ProductId);
```

- Three overloads: a `Query<TResponse>`, a `Command` and a `Command<TResponse>`. Name the tool as `Tools` names it, and describe it for the model; a `[Description]` on the arguments record's properties reaches the schema.
- Each call runs through the Gateway in a scope of its own, as an Operation with the run's context continued (`Continue()`): its own Operation id, the run's as its causation, the run's Actor, tenant and correlation, and no request id. Validation, the authorizers, the tenant requirement of a tenant-scoped Module and, for a command, its own Unit of Work apply, which commits that call alone.
- **The run's authority.** A run that executes inside an authorized Operation, as in a command's or query's handler that injects its `OperationContext`, gives each call a trusted continuation: like any work that Operation causes (G.6), a declared permission is not checked again for it. So give such a run only tools its Operation may run. A run outside one, with a context the code builds, as in a background service, an endpoint that runs the model itself or a test, gives untrusted continuations: each call's permissions are checked for the Actor, and a denied one is a failed tool result.
- A success returns to the model as the response in the Kit's JSON, a `Command` with no value as a completed call; a failed `Result` returns its Errors, `{"errors":[{"code":…,"message":…,"target":…}]}`, never thrown, so the model can recover. An exception, a bug or arguments that do not read as the record, is function invocation's to handle: the model is told the call failed, and when the calls of three iterations in a row have failed, the next failure is thrown to the run's caller.
- In a tenant-scoped Module, the Dapper tenant rule reads every string literal that names a tenant-scoped table beside a SQL word as SQL ([multi-tenancy.md](multi-tenancy.md#reads)), a model's instructions included: `"...the products ... with the GetProduct tool"` names `catalog.products` beside `with`, so word a prompt around the table names.
- A tool call carries no idempotency of its own: two calls of one command type in a run each run, even when the run's context has a request id.

## MCP tools

`McpServers` in the same section names the MCP servers whose tools a run may be given, each by a name of the Host's: one with a `Command` is a program the Host starts and speaks to over its standard input and output (stdio), one with an `Endpoint` is reached over HTTP.

```json
"PouryaKit": {
  "AI": {
    "Tools": ["GetProduct", "read_file", "search_docs"],
    "McpServers": {
      "files": { "Command": "npx", "Arguments": ["-y", "@modelcontextprotocol/server-filesystem", "/srv/shared"] },
      "docs": { "Endpoint": "https://docs.example.com/mcp" }
    }
  }
}
```

```csharp
internal sealed class AskAboutFilesQueryHandler(IChatClient chat, IMcpServers mcp, IGatewayProcessor<CatalogModule> catalog, OperationContext context)
    : QueryHandler<AskAboutFilesQuery, string>
{
    public override async Task<Result<string>> Handle(AskAboutFilesQuery query, CancellationToken cancellationToken)
    {
        var getProduct = catalog.Tool("GetProduct", "Finds one product of the catalog by its id.", context, (ProductToFind product) => new GetProductQuery(product.ProductId));
        var files = await mcp.ListToolsAsync("files", cancellationToken);
        var agent = chat.AsAIAgent("You answer questions about the shop's files and products.", tools: [getProduct, .. files]);
        return (await agent.RunAsync(query.Question, cancellationToken: cancellationToken)).Text;
    }
}
```

- `IMcpServers.ListToolsAsync(server)` gives the tools of the server the configuration names, as `McpClientTool`s of the official MCP C# SDK (`ModelContextProtocol.Core`, which `PouryaKit.AI` brings): functions a run is given in `ChatOptions.Tools`, or an agent in its tools, beside its Gateway tools. A name the configuration does not have throws, naming the servers it has.
- **One allow-list and one cap.** An MCP tool is a function like a Gateway tool, so the allow-list and the iteration cap of the Host's client hold for it: `Tools` names each MCP tool a run may be given as its server names it, and one the list does not name is withheld from the model and its call answered as a function that does not exist, never reaching the server; each iteration that calls MCP tools counts toward `MaximumIterationsPerRun`. A tool of a server and a Gateway tool with one name are one name to the list: rename one, as with the SDK's `WithName`.
- **The Host owns each client.** `IMcpServers` is a singleton of the Host's services: a server is connected at its first listing, which concurrent listings share, and stays connected for every run after it; a listing that cancels stops waiting and leaves the connection to the others, and one that failed is tried again by the next listing. No run owns a client: disposing the Host's services ends them all, a stdio server's process told to exit then killed, and an HTTP server's session, where the server keeps one, ended with its DELETE.
- **A stdio server gets no secret of the Host's.** Its process inherits only the variables a process needs to start, such as `PATH` and `HOME`, and those `Environment` names (`"Environment": { "API_TOKEN": "..." }`, set from a secret store like `ApiKey`); `WorkingDirectory` is where it starts, by default the Host's.
- An HTTP server is reached through the HTTP client named `AIServiceCollectionExtensions.McpHttpClientName` (`PouryaKit.AI.Mcp`), apart from the model's, so the Host's defaults apply to it as to every client and the Host adds a server's authentication there, by that name, without sending it to the model's provider. The client opens no standing stream for messages the server sends unasked, so a resilience pipeline's attempt timeout never cuts one; it does apply to each call, as to the model's.
- The Host fails when it starts, naming the option, when a server has neither or both of `Command` and `Endpoint`, or an `Endpoint` that is not an absolute HTTP or HTTPS address.

## Agent runs

An agent is the Agent Framework's `AIAgent` (package `Microsoft.Agents.AI`, which `PouryaKit.AI` brings) that `AsAIAgent` makes over the Host's `IChatClient`. Build it in the handler, for each run, since its Gateway tools are bound to the run's Operation Context:

```csharp
using Microsoft.Agents.AI;

var agent = chat.AsAIAgent("You answer questions about a shop's catalog.", "catalog", tools: [getProduct]);

var answer = await agent.RunAsync(query.Question, cancellationToken: cancellationToken);
await foreach (var update in agent.RunStreamingAsync(query.Question, cancellationToken: cancellationToken))
{
    // update.Text, as the model writes it
}
var summary = await agent.RunAsync<ProductSummary>(query.Question, cancellationToken: cancellationToken);
```

- A plain run returns the agent's response, whose text is its answer; a streaming run yields the answer in parts; a structured run gives the model the JSON schema of the type, as the request's `response_format`, and reads the answer into the typed response's result, which throws when the answer does not read as the type.
- The agent runs through the Host's client, whose function invocation it finds and keeps, adding none, so the allow-list, the cap, the guardrails and the telemetry apply to every run of every agent.

## Sessions

A conversation over several runs is a session, which the agent restores from the application's store and saves back to it:

```csharp
var session = await agent.LoadSessionAsync(sessions, conversationId, cancellationToken);
var answer = await agent.RunAsync(question, session, cancellationToken: cancellationToken);
await agent.SaveSessionAsync(sessions, conversationId, session, cancellationToken);
```

- `IAgentSessionStore` is the application's port, with `FindAsync` and `SaveAsync` of a session's state, JSON, under a key such as a conversation's id. Implement it over the application's own storage, keyed so that one Actor never reads another's session; the Kit ships no store and keeps no copy, so a session that is not saved ends with its scope.
- `LoadSessionAsync` restores the saved session, the history with it, or starts a new one when the store holds none; `SaveSessionAsync` serializes the session as the agent does, the history its chat history provider kept included.

## A chat reducer

An agent's history is its `InMemoryChatHistoryProvider`, kept in the session; a reducer bounds what each run sends the model and what the session saves:

```csharp
#pragma warning disable MEAI001 // The reducers are experimental in Microsoft.Extensions.AI 10.11.
var agent = chat.AsAIAgent(
    new ChatClientAgentOptions
    {
        ChatOptions = new() { Instructions = "You answer questions about a shop's catalog.", Tools = [getProduct] },
        ChatHistoryProvider = new InMemoryChatHistoryProvider(new() { ChatReducer = new MessageCountingChatReducer(20) }),
    }
);
#pragma warning restore MEAI001
```

- `MessageCountingChatReducer(targetCount)` keeps the first system message and the last messages up to the count, leaving out tool calls and their results; `SummarizingChatReducer(chat, targetCount, threshold)` asks the model for a summary of the older messages and sends it in their place. Give it the Host's `IChatClient`, never a second client.
- The reducer runs before each run reads the history and keeps it reduced: the run sends it, and the session saves it with the run's new messages.

## Memory

Memory is an `AIContextProvider` of the Agent Framework over the application's store: before each run it gives the agent instructions, messages or tools, and after the run it receives the run's messages to keep. The Kit adds no type of its own:

```csharp
internal sealed class RememberedPreferences(IPreferences preferences, OperationContext context) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext invoking, CancellationToken cancellationToken = default) =>
        new() { Instructions = $"The user prefers: {await preferences.ReadAsync(context.Actor, cancellationToken)}" };

    protected override async ValueTask StoreAIContextAsync(InvokedContext invoked, CancellationToken cancellationToken = default) =>
        await preferences.NoteAsync(context.Actor, invoked.RequestMessages, cancellationToken);
}

var agent = chat.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [new RememberedPreferences(preferences, context)] });
```

- Key what it keeps by the run's Actor and tenant, from the Operation Context the provider is given, so no Actor reads another's memory.

## Workflows

The Agent Framework's workflows (package `Microsoft.Agents.AI.Workflows`, which `PouryaKit.AI` brings) compose agents, each built over the Host's client with its tools bound to the run's context:

```csharp
using Microsoft.Agents.AI.Workflows;

var sequential = AgentWorkflowBuilder.BuildSequential(finder, checker);
var concurrent = AgentWorkflowBuilder.BuildConcurrent([pricer, describer]);
var handoff = AgentWorkflowBuilder.CreateHandoffBuilderWith(triage).WithHandoff(triage, expert, "Answers questions about one product.").Build();

var answer = await sequential.AsAIAgent().RunAsync(question, cancellationToken: cancellationToken);
```

- Sequential: each agent is given the conversation so far, with what the agent before it answered. Concurrent: each agent is given the question at once, and their answers are gathered. Handoff: an agent hands the conversation to another by calling a `handoff_to_` declaration, which the allow-list passes unnamed since it runs nothing.
- A workflow runs its agents off the calling thread, and the run's authority goes with them: inside an authorized Operation, each tool call of each agent is a trusted continuation of the run, as in a single agent's run.

## Guardrails

A guardrail is the application's `IChatGuardrail`, registered as a singleton on the Host, which the client resolves when it is built; a Host that registers one with another lifetime fails when its container is built, naming it. The Kit has no policy and registers none:

```csharp
builder.Services.AddSingleton<IChatGuardrail, CardNumberGuardrail>();

internal sealed class CardNumberGuardrail : IChatGuardrail
{
    public Task<string?> CheckRequestAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) =>
        Task.FromResult(messages.Any(message => CardNumbers.IsMatch(message.Text)) ? "I can't take card numbers here." : null);

    public Task<string?> CheckResponseAsync(ChatResponse response, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
```

- Each guardrail is called before every call to the model, with the messages it sends, the call's instructions, an agent's and its context providers', first as the system message the model is given, and after it, with the response, before any tool call the response asks for runs: every run of the Host's client, every agent of a workflow and a summarizing reducer's summary included. Null lets the call through. A refusal ends that call as the provider's content filter would, with the refusal as its text and `ChatFinishReason.ContentFilter`: refused before, the model is not called; refused after, the response is replaced and no tool it asked for runs. The first refusal, in registration order, wins.
- With a guardrail registered, a streamed call is checked whole: the client takes the model's complete response, checks it, then gives it to the caller as updates.

## Scheduled agent work

Agent work on a cadence is a recurring command whose handler runs the agent ([messaging.md](messaging.md#recurring-commands)): each occurrence is an Operation of its own for the named system Actor, and the agent's tool calls continue it.

```csharp
// In the Composition Root:
//     module
//         .AddPouryaKitRecurringCommand("orders.morning-digest", systemActor: "orders.digest-agent", occurrence => new WriteDigestCommand(occurrence))
//         .BindConfiguration("Orders:MorningDigest");

internal sealed class WriteDigestCommandHandler(IChatClient chat, IGatewayProcessor<OrdersModule> orders, OperationContext context)
    : CommandHandler<WriteDigestCommand>
{
    public override async Task<Result> Handle(WriteDigestCommand command, CancellationToken cancellationToken)
    {
        var findOrders = orders.Tool("FindOrders", "Finds the orders placed on a day.", context, (Day day) => new FindOrdersQuery(day.Date));
        await chat.AsAIAgent("You write the morning digest of yesterday's orders.", tools: [findOrders])
            .RunAsync($"Write the digest of {command.Occurrence:yyyy-MM-dd}.", cancellationToken: cancellationToken);
        return Result.Success();
    }
}
```

- Each tool call carries the system Actor, as a trusted continuation of the occurrence, and no tenant: a recurring command's Module is not tenant-scoped, and a tenant-scoped Module's tool refuses a call with no tenant.

## What the Kit guarantees

- **A tool call is an Operation through the Module's Gateway under the run's continued context**, in every run shape and in every agent of a workflow, trusted only when the run executes inside the authorized Operation the context belongs to.
- **A failed Result is a failed tool result**, never an exception to the model.
- **The allow-list and the cap hold for every run** of the Host's client, each agent's included, and for every tool, Gateway or MCP: a function `Tools` does not name is withheld from the model, and a call it asks for anyway is answered as a function that does not exist and never runs; a declaration-only tool, such as a handoff's, passes unnamed, since the client never invokes it and hands its call back to the run's caller; the call after the last allowed iteration offers no tool, so the run ends with the model's answer.
- **A guardrail is called before and after every call to the model**, and a refusal stops the call or the tool calls its response asked for; the Kit refuses nothing itself.
- **The Kit keeps no session and no memory**: they live in the application's stores, through `IAgentSessionStore` and its context providers.
- **The Host owns its MCP clients**: created with its services and disposed with them, which ends a stdio server's process and an HTTP server's session; no run ends one.
- **One resilience pipeline**: the provider's HTTP client is the Host's, with nothing added.
- **The options are checked before the Host serves**, not at the first run, and one provider serves the Host: no switch at run time.

## Limits

- Chat completions only, one model per Host. Embeddings are a later ticket of spec #115 (#138).
- Of an MCP server's tools, resources and prompts, the Kit gives a run its tools only. A server whose session ends on its own, as when its process crashes, is not connected again until the Host restarts: its listings throw, and a call of a tool listed from it fails as a function that throws does, the model told the call failed.
- Each tool call is its own Operation and commits on its own: a run that fails later, or whose handler then fails, undoes no call that already committed.
- The reducers are experimental in Microsoft.Extensions.AI 10.11 (`MEAI001`), and a streamed call is not streamed while a guardrail is registered.
- The model decides which tools to call and with what arguments; the Kit decides only what each call may do.

## Testing

- The Proving Sample's `AiTests` and `AgentRunTests` run the application on a database of their own with its chat client pointed at `ChatStub`, an OpenAI-compatible chat completions server in the test process on a loopback port, which answers each request with the replies a test scripts (text or tool calls, streamed when the request asks for a stream, or chosen from each request's body when agents call the model at once) and records every request: the tools offered and what each call returned. No hosted provider key is used in any test.
- `AgentRunTests` runs each agent that calls a tool inside an authorized Operation, through a test Module's query that hands the test its context, and asserts each call's context as `AiTests` does; a guardrail registered on a Host of its own, and scheduled work on a Host with a database and a `ChatStub` of its own.
- The one real probe, `AiTests.An_ollama_model_in_a_container_completes_a_tool_call_with_no_hosted_key`, runs the sample's agent against Ollama in a container (`Testcontainers.Ollama`, `ollama/ollama:0.40.6`, the model `qwen2.5:0.5b`) only with `POURYAKIT_OLLAMA_PROBE=1`, and is skipped otherwise. With the same variable, `AppHostTests` starts the sample's AppHost with its local agent, the same image and model through `AddPouryaKitOllama` ([aspire.md](aspire.md#a-local-agent)), and asks the agent over HTTP.
- `McpTests` gives the application a stdio MCP server, the test assembly `ModularMonolith.Tests.McpServer` started with `dotnet`, and an HTTP one in the test process that keeps sessions, and runs their tools beside a Gateway tool against `ChatStub`; disposing a Host of its own shows the stdio process gone and the HTTP session ended. No public MCP server is used in any test.
- Observe a tool call's context as the sample's tests do: an `IAuthorizer<T>` of the test assembly for the tool's operation records the `OperationContext` it runs with.

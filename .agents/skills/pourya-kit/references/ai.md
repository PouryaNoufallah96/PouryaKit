# AI

`PouryaKit.AI`: the Host's one chat client, `IChatClient` of Microsoft.Extensions.AI, built from validated options for OpenAI, Azure OpenAI or Ollama through the OpenAI client alone, and the Gateway tool, which offers a Module's command or query to a model and runs each call as an Operation through the Module's Gateway. It references `PouryaKit.Cqrs` and `PouryaKit.Modularity`. Namespace `PouryaKit.AI`. Agent runs, sessions, workflows, memory, MCP tools and embeddings are not built yet.

- [Registering](#registering)
- [A Gateway tool](#a-gateway-tool)
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
- `PouryaKitAIOptions.Provider` (`AIProvider`): `OpenAI` (the default; `Endpoint` may stay null for OpenAI's own API), `AzureOpenAI` (`Endpoint` is the resource's v1 endpoint, such as `https://my-resource.openai.azure.com/openai/v1/`, and `Model` the deployment's name) or `Ollama` (`Endpoint` is its OpenAI-compatible API, `http://<host>:11434/v1`, and no key). Each goes through the OpenAI client's chat completions API: no OllamaSharp and no `Azure.AI.OpenAI` (spec #115's A1).
- `ApiKey` is required for OpenAI and Azure OpenAI. Set it from user secrets or the environment, as `PouryaKit__AI__ApiKey`, never in a committed file; the Kit never logs it.
- `MaximumIterationsPerRun`, 10 by default, caps how many times a run calls the model and invokes the tools it asks for; `Tools` is the allow-list, the names of the tools a run may be given, empty by default.
- The Host fails when it starts, naming the option, when the provider is not an `AIProvider` value (a number such as `99` binds), `Model` is blank, `Endpoint` is not an absolute HTTP or HTTPS address or is missing for Azure OpenAI or Ollama, `ApiKey` is missing for OpenAI or Azure OpenAI, the cap is below 1, or a tool name is blank.
- The client sends through the HTTP client named `AIServiceCollectionExtensions.HttpClientName` (`PouryaKit.AI`). The package adds no resilience handler and turns the OpenAI client's own retries off, so the client keeps whatever the Host's defaults give every client: with the service defaults ([host-recipes.md](host-recipes.md)), their one pipeline, which retries safe methods only, so a chat completion, a POST, is sent once. That pipeline's attempt timeout is 10 seconds; give the client its own limits by replacing it, as for any client: `builder.Services.AddHttpClient(AIServiceCollectionExtensions.HttpClientName).RemoveAllResilienceHandlers()`, then `AddPouryaKitResilience().Configure(...)` ([web.md](web.md)). Adding a second pipeline instead is refused when the client is created.
- The client's pipeline, outermost first: the allow-list, function invocation with the cap, OpenTelemetry with sensitive data off, and logging. The telemetry step writes a span and duration and token metrics for each call to the model, on the activity source and meter Experimental.Microsoft.Extensions.AI, which the service defaults' tracing and metrics export ([host-recipes.md](host-recipes.md#what-each-recipe-does)); a Host with an exporter of its own adds that name.

## A Gateway tool

A run is one `GetResponseAsync` or `GetStreamingResponseAsync` call of the Host's `IChatClient`, given its tools in `ChatOptions.Tools`. `Tool` on a Module's `IGatewayProcessor<TModule>` makes a command or query a Gateway tool for one run's Operation Context. The model's arguments are read with the Kit's JSON into an arguments record, whose schema the model is given, and mapped explicitly to the operation:

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

## What the Kit guarantees

- **A tool call is an Operation through the Module's Gateway under the run's continued context**, trusted only when the run executes inside the authorized Operation the context belongs to.
- **A failed Result is a failed tool result**, never an exception to the model.
- **The allow-list and the cap hold for every run** of the Host's client: a tool `Tools` does not name is withheld from the model, and a call it asks for anyway is answered as a function that does not exist and never runs; the call after the last allowed iteration offers no tool, so the run ends with the model's answer.
- **One resilience pipeline**: the provider's HTTP client is the Host's, with nothing added.
- **The options are checked before the Host serves**, not at the first run, and one provider serves the Host: no switch at run time.

## Limits

- Chat completions only, one model per Host. Agent runs, sessions, workflows, a chat reducer, memory, a guardrail hook, MCP tools, embeddings and scheduled agent work are later tickets of spec #115 (#136 to #139).
- Each tool call is its own Operation and commits on its own: a run that fails later, or whose handler then fails, undoes no call that already committed.
- The model decides which tools to call and with what arguments; the Kit decides only what each call may do.

## Testing

- The Proving Sample's `AiTests` run the application on a database of its own with its chat client pointed at `ChatStub`, an OpenAI-compatible chat completions server in the test process on a loopback port, which answers each request with the replies a test scripts (text or tool calls) and records every request: the tools offered and what each call returned. No hosted provider key is used in any test.
- The one real probe, `AiTests.An_ollama_model_in_a_container_completes_a_tool_call_with_no_hosted_key`, runs the sample's agent against Ollama in a container (`Testcontainers.Ollama`, `ollama/ollama:0.40.6`, the model `qwen2.5:0.5b`) only with `POURYAKIT_OLLAMA_PROBE=1`, and is skipped otherwise.
- Observe a tool call's context as the sample's tests do: an `IAuthorizer<T>` of the test assembly for the tool's operation records the `OperationContext` it runs with.

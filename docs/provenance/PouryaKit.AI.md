# PouryaKit.AI provenance

Adopted implementations in `PouryaKit.AI`. Format and rules: [README](README.md).

The options validation in `src/PouryaKit.AI/AIServiceCollectionExtensions.cs` follows the Kit's own options pattern (`AddOptions`, `Validate`, `ValidateOnStart`, as `PouryaKitMailKitOptions` does), and the one-client composition check (`src/PouryaKit.AI/OneChatClientCheck.cs`) the Kit's `ICompositionCheck` shape of `OneEmailSenderCheck`, so neither has a record here. The allow-list (`src/PouryaKit.AI/ToolAllowList.cs`) and the Gateway tool (`src/PouryaKit.AI/GatewayTool.cs`, `src/PouryaKit.AI/GatewayToolExtensions.cs`) are new to the Kit, decided by spec #115's AI decision (ticket #135).

## Package dependencies

Checked on nuget.org on 2026-10-18 (ticket #135); licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002).

- `Microsoft.Extensions.AI` 10.11.0, `MIT`, the latest stable, with a `net10.0` asset: `IChatClient`'s pipeline (`ChatClientBuilder`, function invocation, OpenTelemetry, logging). It brings `Microsoft.Extensions.AI.Abstractions` 10.11.0, `Microsoft.Extensions.Caching.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions` and `System.Numerics.Tensors` 10.0.13, all `MIT`, so the closure holds those abstractions at 10.0.13 beside the Kit's 10.0.12 line.
- `Microsoft.Extensions.AI.OpenAI` 10.11.0, `MIT`, the latest stable: `AsIChatClient()` over the OpenAI client. It brings `OpenAI` 2.14.0, `MIT`, the latest stable, and through it `System.ClientModel` 1.15.0 and `System.Memory.Data` 10.0.9, both `MIT`.
- `Microsoft.Extensions.Http` 10.0.12, `MIT`, the Kit's line (10.0.13 is the latest), under the standing rule of plan section C: the named HTTP client the provider's client sends through, so the Host's defaults for every client apply to it. It brings the configuration, diagnostics and logging packages of the same line, all `MIT`.
- `Microsoft.Extensions.Options` 10.0.12, `MIT`, under the standing rule, for the options the Host binds and the Kit validates at start.

No `OllamaSharp`, which declares a licence file and no expression, so `LicenceTests` would refuse it, and no `Azure.AI.OpenAI` (spec #115's A1); no resilience package. `PackageGraph` keeps all of them out of the closure. `Microsoft.Agents.AI` is not referenced: #135 needs only the chat client and its function invocation. The test project's `Testcontainers.Ollama` 4.15.0 (`MIT`, the Kit's Testcontainers line; 4.16.0 is the latest) is recorded in [the Proving Sample's record](ModularMonolith.md). Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## The chat client's pipeline

- **Kit files:** `src/PouryaKit.AI/AIServiceCollectionExtensions.cs`, `src/PouryaKit.AI/PouryaKitAIOptions.cs`, `src/PouryaKit.AI/AIProvider.cs`
- **Upstream:** Ultrabusiness (`ultrabusiness_server_microservices`, a private repository with no public URL)
- **Commit:** a85eacd (the plan's I.3 pin)
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.AI/Extensions/AIServiceExtensions.cs` (lines 106 to 140: a provider switch over configuration; lines 149 to 173: `new OpenAIClient(...).GetChatClient(model).AsIChatClient()` with `UseFunctionInvocation()`, `UseOpenTelemetry(configure: otel => otel.EnableSensitiveData = false)` and `UseLogging()`); read at the local checkout's working tree, at commit `cd13e30ce4cf74825ffc6e9335906fb2d112fe00`, not at `a85eacd`
- **Licence:** unconfirmed
- **Adoption:** pattern
- **Ticket:** #135
- **Intentional differences:**
  - One construction for every provider: upstream builds Azure OpenAI through `Azure.AI.OpenAI`'s `AzureOpenAIClient` and Ollama through `OllamaSharp`'s `OllamaApiClient`; the Kit reaches Azure OpenAI's v1 endpoint and Ollama's OpenAI-compatible API with the `OpenAI` client, its endpoint and key from options.
  - The provider is validated when the Host starts, naming the option; upstream throws its own `AIException` at registration for an unknown provider or a missing key or endpoint.
  - The OpenAI client sends through the named HTTP client of the Host's factory, with its own retries off, so the Host's one resilience pipeline is the only one; upstream uses the client's default transport and retry policy.
  - The allow-list sits outside function invocation, and the iteration cap comes from options; upstream passes the tools an agent's options list and keeps the default cap.
  - One `IChatClient` per Host, checked when the container is built; no `Enabled` flag, prompt templates or on-the-fly tool selector (excluded by plan I.4).

## Tools over a Module's Gateway

- **Kit files:** `src/PouryaKit.AI/GatewayToolExtensions.cs`, `src/PouryaKit.AI/GatewayTool.cs`
- **Upstream:** Ultrabusiness (`ultrabusiness_server_microservices`, a private repository with no public URL)
- **Commit:** a85eacd (the plan's I.3 pin)
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.AI/Services/ToolsFactoryBase.cs` (a base class whose `BuildTools` returns `AIFunctionFactory.Create` over a service's methods, cached for the factory's life); read at the local checkout's working tree, at commit `cd13e30ce4cf74825ffc6e9335906fb2d112fe00`
- **Licence:** unconfirmed
- **Adoption:** pattern
- **Ticket:** #135
- **Intentional differences:**
  - A tool runs a Module's command or query through its Gateway, as an Operation with the run's context continued for each call, so validation, the authorizers and the Unit of Work apply; upstream's tools call repositories directly, outside any Operation.
  - A tool is made per run, bound to its Operation Context, never cached across runs; its arguments are a record the model's JSON is read into with the Kit's JSON, mapped explicitly to the operation.
  - A failed `Result` returns to the model as its Errors; upstream's tools return what the service returns, serialized, and let an exception reach function invocation.

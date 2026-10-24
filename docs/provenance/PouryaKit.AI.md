# PouryaKit.AI provenance

Adopted implementations in `PouryaKit.AI`. Format and rules: [README](README.md).

The MCP clients (`src/PouryaKit.AI/IMcpServers.cs`, `src/PouryaKit.AI/McpServerConnections.cs`, `src/PouryaKit.AI/PouryaKitMcpServerOptions.cs`, ticket #137) are new to the Kit, decided by plan I.4, over the SDK's client as it ships. The options validation in `src/PouryaKit.AI/AIServiceCollectionExtensions.cs` follows the Kit's own options pattern (`AddOptions`, `Validate`, `ValidateOnStart`, as `PouryaKitMailKitOptions` does), and the guardrails' composition check (`src/PouryaKit.AI/SingletonGuardrailsCheck.cs`) the Kit's `ICompositionCheck` shape, while the one-client check is Cqrs's `OneProviderCheck<IChatClient>` (`src/PouryaKit.Cqrs/Composition/OneProviderCheck.cs`), so none has a record here. The allow-list (`src/PouryaKit.AI/ToolAllowList.cs`) and the Gateway tool (`src/PouryaKit.AI/GatewayTool.cs`, `src/PouryaKit.AI/GatewayToolExtensions.cs`) are new to the Kit, decided by spec #115's AI decision (ticket #135). So are the guardrail step (`src/PouryaKit.AI/IChatGuardrail.cs`, `src/PouryaKit.AI/ChatGuardrails.cs`) and the session port (`src/PouryaKit.AI/IAgentSessionStore.cs`, `src/PouryaKit.AI/AgentSessionExtensions.cs`), decided by plan I.4 and ticket #136; agent runs, workflows, chat history and context providers are the Agent Framework's, used as it ships them. The embedding generator (`AddPouryaKitEmbeddings`, ticket #138) is built on the same OpenAI client as the chat client's pipeline below, with Microsoft.Extensions.AI's `EmbeddingGeneratorBuilder`, its OpenTelemetry and its logging, and has no record of its own.

## Package dependencies

Checked on nuget.org on 2026-10-18 (ticket #135); licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002).

- `Microsoft.Extensions.AI` 10.11.0, `MIT`, the latest stable, with a `net10.0` asset: `IChatClient`'s pipeline (`ChatClientBuilder`, function invocation, OpenTelemetry, logging). It brings `Microsoft.Extensions.AI.Abstractions` 10.11.0, `Microsoft.Extensions.Caching.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions` and `System.Numerics.Tensors` 10.0.13, all `MIT`, so the closure holds those abstractions at 10.0.13 beside the Kit's 10.0.12 line.
- `Microsoft.Extensions.AI.OpenAI` 10.11.0, `MIT`, the latest stable: `AsIChatClient()` over the OpenAI client. It brings `OpenAI` 2.14.0, `MIT`, the latest stable, and through it `System.ClientModel` 1.15.0 and `System.Memory.Data` 10.0.9, both `MIT`.
- `Microsoft.Extensions.Http` 10.0.12, `MIT`, the Kit's line (10.0.13 is the latest), under the standing rule of plan section C: the named HTTP client the provider's client sends through, so the Host's defaults for every client apply to it. It brings the configuration, diagnostics and logging packages of the same line, all `MIT`.
- `Microsoft.Extensions.Options` 10.0.12, `MIT`, under the standing rule, for the options the Host binds and the Kit validates at start.
- `Microsoft.Agents.AI` 1.25.0, `MIT`, the latest stable, with a `net10.0` asset (ticket #136, checked on nuget.org on 2026-10-18): the agent over the Host's chat client, its plain, streaming and structured runs, sessions and their serialization, chat history and context providers; plan I.4 and the owner's 2026-10-06 answer name the Microsoft Agent Framework for the agent capabilities. It brings `Microsoft.Agents.AI.Abstractions` 1.25.0, `Microsoft.Extensions.AI.Evaluation` 10.10.0, `Microsoft.Extensions.Compliance.Abstractions` 10.10.0, `Microsoft.Extensions.VectorData.Abstractions` 10.10.0, `Microsoft.Extensions.FileSystemGlobbing` 10.0.12, `Microsoft.Extensions.ObjectPool` 10.0.12 and `Microsoft.ML.Tokenizers` 2.0.0, all `MIT`, and through the tokenizers `Google.Protobuf` 3.30.2 (`BSD-3-Clause`), held at the Kit's 3.36.2 where a Host also takes gRPC.
- `Microsoft.Agents.AI.Workflows` 1.25.0, `MIT`, the latest stable, the same line: sequential, concurrent and handoff workflows of agents. It brings `OpenTelemetry.Api` 1.18.0 (`Apache-2.0`), below the service defaults' 1.19 line, which a Host composing them takes instead.
- `ModelContextProtocol.Core` 2.2.0, `Apache-2.0`, the latest stable, with a `net10.0` asset (ticket #137, checked on nuget.org on 2026-10-18): the official MCP C# SDK's client (`McpClient`, its stdio and HTTP transports, `McpClientTool`), of the Model Context Protocol project (`modelcontextprotocol/csharp-sdk`, commit `6fa3825973949a9c4f0cd8af344e15a8db09dc35`, as its nuspec records). On `net10.0` it asks for `Microsoft.Extensions.AI.Abstractions` 10.8.3 and `Microsoft.Extensions.Logging.Abstractions` 10.0.10, which the closure already holds at 10.11.0 and 10.0.13, so it adds no other package. The `ModelContextProtocol` 2.2.0 that spec #115 and plan I.4 name adds hosting and dependency-injection extensions for an MCP server, and `Microsoft.Extensions.Hosting.Abstractions` with them, which a client does not use; the Proving Sample's tests take it, through `ModelContextProtocol.AspNetCore`, for their test servers only ([the Proving Sample's record](ModularMonolith.md)).

No `OllamaSharp`, which declares a licence file and no expression, so `LicenceTests` would refuse it, and no `Azure.AI.OpenAI` (spec #115's A1); no resilience package. `PackageGraph` keeps all of them out of the closure. The Agent Framework's hosting package (`Microsoft.Agents.AI.Hosting`), a prerelease, is not referenced. The test project's `Testcontainers.Ollama` 4.15.0 (`MIT`, the Kit's Testcontainers line; 4.16.0 is the latest) is recorded in [the Proving Sample's record](ModularMonolith.md). Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## The chat client's pipeline

- **Kit files:** `src/PouryaKit.AI/AIServiceCollectionExtensions.cs`, `src/PouryaKit.AI/PouryaKitAIOptions.cs`, `src/PouryaKit.AI/AIProvider.cs`
- **Upstream:** Ultrabusiness (`ultrabusiness_server_microservices`, a private repository with no public URL)
- **Commit:** a85eacd (the plan's I.3 pin)
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.AI/Extensions/AIServiceExtensions.cs` (lines 106 to 140: a provider switch over configuration; lines 149 to 173: `new OpenAIClient(...).GetChatClient(model).AsIChatClient()` with `UseFunctionInvocation()`, `UseOpenTelemetry(configure: otel => otel.EnableSensitiveData = false)` and `UseLogging()`); read at commit `cd13e30ce4cf74825ffc6e9335906fb2d112fe00`, where the cited file is byte-identical to the pin `a85eacd` (`git diff a85eacd cd13e30c -- <path>` is empty, checked 2026-10-18)
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
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.AI/Services/ToolsFactoryBase.cs` (a base class whose `BuildTools` returns `AIFunctionFactory.Create` over a service's methods, cached for the factory's life); read at commit `cd13e30ce4cf74825ffc6e9335906fb2d112fe00`, where the cited file is byte-identical to the pin `a85eacd` (`git diff` empty, checked 2026-10-18)
- **Licence:** unconfirmed
- **Adoption:** pattern
- **Ticket:** #135
- **Intentional differences:**
  - A tool runs a Module's command or query through its Gateway, as an Operation with the run's context continued for each call, so validation, the authorizers and the Unit of Work apply; upstream's tools call repositories directly, outside any Operation.
  - A tool is made per run, bound to its Operation Context, never cached across runs; its arguments are a record the model's JSON is read into with the Kit's JSON, mapped explicitly to the operation.
  - A failed `Result` returns to the model as its Errors; upstream's tools return what the service returns, serialized, and let an exception reach function invocation.

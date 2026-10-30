![PouryaKit.AI](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.AI.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.AI?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.AI)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.AI

The Host's one chat client, for OpenAI, Azure OpenAI or Ollama, built from validated options. A run of that client may call a Module's command or query as a Gateway tool: each call runs through the Module's Gateway as an Operation of its own, with the run's Operation Context continued, so validation, the authorizers and the tenant requirement apply to it. Over the same client, agents of the Microsoft Agent Framework run plain, streamed or for a typed answer.

## Install

```bash
dotnet add package PouryaKit.AI --prerelease
```

## When to use it

- A Feature answers in natural language, or lets a model look things up through the Module's own queries.
- You compose the Host's chat client for a hosted provider or a local Ollama.
- A run needs a Module's command or query, or an MCP server's tools.
- An agent keeps a conversation's session and memory per Actor, or a workflow runs several agents.
- You check every call to the model with a guardrail.

It builds on [`PouryaKit.Modularity`](https://www.nuget.org/packages/PouryaKit.Modularity), whose Gateway its tools call. [`PouryaKit.AI.Postgres`](https://www.nuget.org/packages/PouryaKit.AI.Postgres) adds semantic search.

## Quick start

Register the Host's one client and bind its options, as the Proving Sample does:

```csharp
builder.Services.AddPouryaKitAI();
builder.Services.Configure<PouryaKitAIOptions>(builder.Configuration.GetSection("PouryaKit:AI"));
builder.Services.AddSingleton<IChatGuardrail, CardNumberGuardrail>();
```

Turn a query into a Gateway tool, in the handler that builds the run:

```csharp
var getProduct = catalog.Tool(
    "GetProduct",
    "Finds one product of the catalog by its id: its id, its category's id and its name.",
    context,
    (ProductToFind product) => new GetProductQuery(product.ProductId)
);
```

## What you get

- One `IChatClient` per Host, wrapped in its allow-list, iteration cap and the guardrails the Host registers.
- Gateway tools that run as Operations, under the run's Operation Context.
- MCP tools of the servers the Host's options name, under the same allow-list.
- Agents and workflows over that client, with sessions and memory in your application's stores.
- No resilience of its own on the provider's client, so a Host whose defaults add one pipeline to every client keeps exactly that one.

> [!WARNING]
> The model decides what to call; the Kit decides what each call may do. A run inside an authorized Operation gives its tool calls that Operation's authority, so give such a run only the tools its Operation may run.

## Learn more

- [AI](https://pouryakit.vercel.app/docs/capabilities/ai)
- [Gateway and MCP tools](https://pouryakit.vercel.app/docs/concepts/gateway-and-mcp-tools)
- [A Gateway tool](https://pouryakit.vercel.app/docs/capabilities/ai#a-gateway-tool)
- [Agent runs](https://pouryakit.vercel.app/docs/capabilities/ai#agent-runs)
- [Guardrails](https://pouryakit.vercel.app/docs/capabilities/ai#guardrails)

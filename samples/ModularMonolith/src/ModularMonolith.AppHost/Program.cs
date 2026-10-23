using ModularMonolith.AppHost;
using PouryaKit.Aspire;

// The Proving Sample's local infrastructure, matching its composition: ModularMonolith.Host caches in Redis and sends over
// RabbitMQ, so Inventory runs in Inventory.Host, which receives over RabbitMQ and caches nothing. Each Host takes a
// resource as the connection string its composition reads; one it is not given turns that part of it off. Email and files
// are not among them: ModularMonolith.Host always sends mail to the SMTP server and stores files in the S3 bucket its
// settings name, a local Papercut on port 2525 and a local RustFS on port 9000 with the bucket modular-monolith, which the
// Kit does not create. This AppHost provisions neither, so a developer runs both, and without the store a report job
// cannot store its file.
var builder = DistributedApplication.CreateBuilder(AppHostOptions.Create(args));

var postgres = builder.AddPouryaKitPostgres("postgres");
var cache = builder.AddPouryaKitRedis("cache");
var broker = builder.AddPouryaKitRabbitMq("broker");
var shop = postgres.AddDatabase("modular-monolith-db", "modular_monolith");
var inventory = postgres.AddDatabase("inventory-db", "inventory");

// ModularMonolith.Host refuses to start without a bearer issuer, audience and signing key: the key is the base64 value of
// the AppHost parameter bearer-signing-key (user secrets, or Parameters__bearer-signing-key), which signs a caller's token.
const string Issuer = "modular-monolith-apphost";
var signingKey = builder.AddParameter("bearer-signing-key", secret: true);

builder
    .AddProject<Projects.Inventory_Host>("inventory")
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/alive")
    .WithReference(inventory, "app")
    .WithReference(broker)
    .WaitFor(inventory)
    .WaitFor(broker);

var shopHost = builder
    .AddProject<Projects.ModularMonolith_Host>("modular-monolith")
    .WithHttpEndpoint()
    .WithReference(shop, "app")
    .WithReference(cache)
    .WithReference(broker)
    .WithEnvironment("Authentication__Schemes__Bearer__ValidIssuer", Issuer)
    .WithEnvironment("Authentication__Schemes__Bearer__ValidAudiences__0", "modular-monolith")
    .WithEnvironment("Authentication__Schemes__Bearer__SigningKeys__0__Issuer", Issuer)
    .WithEnvironment("Authentication__Schemes__Bearer__SigningKeys__0__Value", signingKey)
    .WaitFor(shop)
    .WaitFor(cache)
    .WaitFor(broker);

// ModularMonolith.Host's agent, AskAboutProducts, calls the model its PouryaKit:AI section names. Only an AppHost run with
// LocalAgent=true (an argument, or the environment variable LocalAgent) adds Ollama and points the agent at it: the image
// and the model are large, and without them the Host starts all the same.
if (bool.TryParse(builder.Configuration["LocalAgent"], out var localAgent) && localAgent)
{
    const string Model = "qwen2.5:0.5b";
    var ollama = builder.AddPouryaKitOllama("ollama", Model);
    shopHost
        .WithEnvironment("PouryaKit__AI__Provider", "Ollama")
        .WithEnvironment("PouryaKit__AI__Endpoint", ReferenceExpression.Create($"{ollama.GetEndpoint("http")}/v1"))
        .WithEnvironment("PouryaKit__AI__Model", Model)
        .WaitFor(ollama);
}

builder.Build().Run();

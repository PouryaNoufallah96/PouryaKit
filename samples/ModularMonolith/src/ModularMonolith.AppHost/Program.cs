using ModularMonolith.AppHost;
using PouryaKit.Aspire;

// The Proving Sample's local infrastructure, matching its composition: ModularMonolith.Host caches in Redis and sends over
// RabbitMQ, so Inventory runs in Inventory.Host, which receives over RabbitMQ and caches nothing. Each Host takes a
// resource as the connection string its composition reads; one it is not given turns that part of it off.
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

builder
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

builder.Build().Run();

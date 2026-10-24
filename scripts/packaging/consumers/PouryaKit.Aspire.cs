using Aspire.Hosting;
using PouryaKit.Aspire;

// What an AppHost calls on the packed package: PostgreSQL with a Host's database, Redis and RabbitMQ, each on its pinned
// image with a named volume. Compiled, never run.
var builder = DistributedApplication.CreateBuilder(args);
builder.AddPouryaKitPostgres("postgres").AddDatabase("app");
builder.AddPouryaKitRedis("cache");
builder.AddPouryaKitRabbitMq("broker");
Console.WriteLine(builder.Resources.Count);

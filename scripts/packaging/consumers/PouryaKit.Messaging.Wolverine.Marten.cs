using Microsoft.Extensions.Hosting;
using PouryaKit.Messaging.Wolverine.Marten;

// What a Host of Stream Modules calls on the packed package: its one Marten store on the message store. Compiled, never
// run: a real Host also composes the Modules whose Streams it keeps.
var builder = Host.CreateApplicationBuilder([
    "--ConnectionStrings:app=Host=localhost;Database=app",
]);
builder.UsePouryaKitMarten(connectionName: "app");
Console.WriteLine(builder.Services.Count);

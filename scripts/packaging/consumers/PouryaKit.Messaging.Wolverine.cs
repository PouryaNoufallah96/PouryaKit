using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;

System.Type[] types = [typeof(IReceipts)];

// The packed Messaging.Wolverine's recurring command, declared under its system Actor and given its cadence as a
// Composition Root does.
var services = new ServiceCollection();
services
    .AddPouryaKitRecurringCommand("consumer.tick", systemActor: "package-consumer", occurrence => new TickCommand(occurrence))
    .Configure(options => options.Every = TimeSpan.FromHours(1));
// A declaration the Host's composition makes on a Module's behalf names the Module.
services.AddPouryaKitDelayedCommand<ArchiveCommand>("consumer.archive", typeof(ConsumerModule));
Console.WriteLine(types.Length + services.Count);

internal sealed record TickCommand(DateTimeOffset Occurrence) : Command;

internal sealed record ArchiveCommand(Guid Id) : Command;

internal sealed class ConsumerModule;

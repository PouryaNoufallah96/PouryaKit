using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Observability;

System.Type[] types = [typeof(OperationTelemetry)];

// The packed Observability, registered as a Host would, after the dispatcher it wraps: the dispatcher's registration
// becomes the observing one, under the source a Host's exporter listens to.
var services = new ServiceCollection().AddPouryaKitCqrs().AddPouryaKitObservability();
var dispatcher = services.Single(descriptor => descriptor.ServiceType == typeof(IDispatcher));
Console.WriteLine(types.Length + (dispatcher.ImplementationFactory is not null && OperationTelemetry.SourceName == "PouryaKit" ? 1 : 0));

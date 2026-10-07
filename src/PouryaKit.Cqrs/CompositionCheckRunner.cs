using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PouryaKit.Cqrs;

// The composition checks ran when the Kit's container was built, which registered the HostComposition. Without it, a
// later ConfigureContainer built the container with another factory, so neither the validation nor the checks ran;
// StartingAsync runs before any hosted service's StartAsync, Wolverine's and the web server's included, so such a Host
// serves nothing and delivers nothing.
internal sealed class CompositionCheckRunner(IServiceProvider services) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) =>
        services.GetService<HostComposition>() is not null
            ? Task.CompletedTask
            : throw new InvalidOperationException(
                "The Host's container was built by another service provider factory than the Kit's: a ConfigureContainer call after AddPouryaKitCompositionChecks, which AddPouryaKitModules calls, replaced it. The container was not validated when it was built (every registration constructible, no singleton that depends on a scoped service), and the Kit's composition checks did not run on the Host's registrations. Remove that ConfigureContainer call, or call AddPouryaKitCompositionChecks after it."
            );

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
